using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using SynthPoc.Engine;
using SynthPoc.Output;

namespace SynthPoc
{
    /// <summary>
    /// P1 验证宿主：N 路 vmem 出帧 → CPU 合成一帧成品 → WriteableBitmap 预览。
    ///
    /// 与 Cutlance 现有链路的关键差别：
    ///   预览不再是 HwndHost（原生子窗），而是 WPF 自绘的位图。
    ///   → 成品帧在内存里真实存在，窗口被遮挡 / 最小化 / 关闭都不影响出画。
    /// </summary>
    internal sealed class MainWindow : Window
    {
        private readonly Options _opt;

        private LibVLC? _vlc;
        private Compositor? _comp;
        private readonly List<VmemInput> _feeds = new();
        private readonly Dictionary<VmemInput, long> _lastFeedFrames = new();

        private WriteableBitmap? _bmp;
        private OutputBus? _bus;
        private FullscreenOutput? _fs;
        private Image _preview = null!;
        private TextBlock _stats = null!;

        // ── P3 预监/节目 ────────────────────────────────────────────────────
        private Switcher? _sw;
        private WriteableBitmap? _pvwBmp;
        private byte[] _pvwBuf = Array.Empty<byte>();
        private const int ThumbW = 384, ThumbH = 216;
        private readonly Button[] _inButtons = new Button[16];
        private TextBlock? _pgmLabel, _pvwLabel;
        private DispatcherTimer? _pvwTick;
        private int _lastProgram = -1, _lastPreview = -1;

        // ── 录制上限 ────────────────────────────────────────────────────────
        private RecordWatchdog? _recWatch;
        private string _recStopNote = string.Empty;

        private Thread? _synthThread;
        private volatile bool _running;
        private int _presentPending;

        private long _composeFrames;
        private long _presentCount;
        private readonly double[] _costs = new double[1200];
        private int _costCount;
        private int _costIdx;
        private double _costMax;

        private readonly Stopwatch _life = Stopwatch.StartNew();
        private long _synthStartTicks;
        private long _synthEndTicks;

        /// <summary>
        /// 合成线程真正跑了多久。
        /// 起点 = 合成线程开跑（不含窗口初始化那 0.5 秒）；
        /// 终点 = 停合成那一刻（不含 ffmpeg 收尾那 1~2 秒，否则帧率会被算成 29.8 冤枉掉帧）。
        /// </summary>
        private double SynthSeconds
        {
            get
            {
                if (_synthStartTicks == 0) return Math.Max(0.001, _life.Elapsed.TotalSeconds);
                long end = _synthEndTicks != 0 ? _synthEndTicks : Stopwatch.GetTimestamp();
                return Math.Max(0.001, (end - _synthStartTicks) / (double)Stopwatch.Frequency);
            }
        }

        /// <summary>本进程实际吃了几个核（从开始播放算起）。</summary>
        private double CpuCores
        {
            get
            {
                try
                {
                    _proc.Refresh();
                    double wall = Math.Max(0.1, _life.Elapsed.TotalSeconds - _cpuT0);
                    return (_proc.TotalProcessorTime - _cpu0).TotalSeconds / wall;
                }
                catch { return 0; }
            }
        }

        private readonly Process _proc = Process.GetCurrentProcess();
        private TimeSpan _cpu0;
        private double _cpuT0;

        private DispatcherTimer? _tick;
        private bool _minimized;
        private bool _frozen;
        private bool _shutdown;
        private long _droppedTotal;
        private long _audioBlocksAtStop;

        public MainWindow(Options opt)
        {
            _opt = opt;

            Title = opt.IsPgmMode
                ? $"SynthPoc P3 · 预监/节目 · {opt.Count} 路 · {opt.Width}x{opt.Height}@{opt.Fps}"
                : $"SynthPoc P1 · {opt.Count} 路合成 · {opt.Width}x{opt.Height}@{opt.Fps}";
            Width = 1280;
            Height = 820;
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14));
            WindowStartupLocation = WindowStartupLocation.Manual;
            ShowActivated = !opt.NoActivate;
            ShowInTaskbar = !opt.Offscreen;

            var area = SystemParameters.WorkArea;
            if (opt.Offscreen)
            {
                Left = -4000;
                Top = 80;
            }
            else
            {
                Left = area.X + (area.Width - Width) / 2;
                Top = area.Y + (area.Height - Height) / 2;
            }

            BuildUi();
            Loaded += OnLoaded;
        }

        private void BuildUi()
        {
            var root = new Grid();
            if (_opt.IsPgmMode) BuildPgmUi(root);
            else BuildGridUi(root);
            Content = root;
        }

        /// <summary>P1/P2 的回归界面：N 路拼成一张多画面，用来验证解码/合成/输出。</summary>
        private void BuildGridUi(Grid root)
        {
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var stage = new Border { Background = Brushes.Black, Margin = new Thickness(0) };
            _preview = new Image { Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(_preview, BitmapScalingMode.Linear);
            stage.Child = _preview;
            Grid.SetRow(stage, 0);
            root.Children.Add(stage);

            _stats = new TextBlock
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(0xD8, 0xD8, 0xD8)),
                Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x0B)),
                Padding = new Thickness(14, 10, 14, 12),
                Text = "初始化中…"
            };
            Grid.SetRow(_stats, 1);
            root.Children.Add(_stats);
        }

        /// <summary>
        /// P3 界面：左边节目画面（大），右边预监（小），底下输入排 + CUT / AUTO。
        /// 逻辑跟 vMix 一致：点编号 → 送进预监；CUT = 硬切进节目；AUTO = 溶解进节目。
        /// </summary>
        private void BuildPgmUi(Grid root)
        {
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var stage = new Grid();
            stage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            stage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(404) });

            _preview = new Image { Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(_preview, BitmapScalingMode.Linear);

            _pgmLabel = new TextBlock
            {
                Text = "节目 PGM  #1",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromArgb(0xC8, 0xB0, 0x18, 0x24)),
                Padding = new Thickness(10, 5, 10, 5),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(10)
            };
            AutomationProperties.SetAutomationId(_pgmLabel, "PgmLabel");

            var pgmHost = new Grid { Background = Brushes.Black, Margin = new Thickness(0, 0, 8, 0) };
            pgmHost.Children.Add(_preview);
            pgmHost.Children.Add(_pgmLabel);
            Grid.SetColumn(pgmHost, 0);
            stage.Children.Add(pgmHost);

            // ── 右栏：预监 ────────────────────────────────────────────────
            var right = new StackPanel { Margin = new Thickness(4, 0, 0, 0) };

            right.Children.Add(new TextBlock
            {
                Text = "预监 PVW · 下一步要切出去的画面",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xA6)),
                Margin = new Thickness(0, 2, 0, 6)
            });

            _pvwBmp = new WriteableBitmap(ThumbW, ThumbH, 96, 96, PixelFormats.Bgr32, null);
            var pvwImg = new Image { Source = _pvwBmp, Stretch = Stretch.Uniform, Height = ThumbH };
            RenderOptions.SetBitmapScalingMode(pvwImg, BitmapScalingMode.Linear);

            var pvwHost = new Border
            {
                Background = Brushes.Black,
                Child = pvwImg,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)),
                BorderThickness = new Thickness(2)
            };
            right.Children.Add(pvwHost);

            _pvwLabel = new TextBlock
            {
                Text = "预监 PVW  #2",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xDC, 0x9C)),
                Margin = new Thickness(0, 8, 0, 0)
            };
            AutomationProperties.SetAutomationId(_pvwLabel, "PvwLabel");
            right.Children.Add(_pvwLabel);

            right.Children.Add(new TextBlock
            {
                Text = "点下面的编号把它送进预监，再按 CUT 硬切 / AUTO 溶解。\n" +
                       "快捷键：数字键选预监，回车硬切，A 键自动转场。",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x7D, 0x83)),
                Margin = new Thickness(0, 10, 0, 0)
            });

            Grid.SetColumn(right, 1);
            stage.Children.Add(right);
            Grid.SetRow(stage, 0);
            root.Children.Add(stage);

            // ── 输入排 + CUT / AUTO ───────────────────────────────────────
            var bar = new WrapPanel { Margin = new Thickness(10, 10, 10, 6) };

            int n = Math.Min(_inButtons.Length, Math.Max(1, _opt.Count));
            for (int i = 0; i < n; i++)
            {
                int ch = i;
                var btn = new Button
                {
                    Content = (i + 1).ToString(),
                    Width = 52,
                    Height = 36,
                    Margin = new Thickness(0, 0, 6, 6),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 15,
                    Foreground = Brushes.White,
                    Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A))
                };
                AutomationProperties.SetAutomationId(btn, "In" + (i + 1));
                AutomationProperties.SetName(btn, "输入" + (i + 1));
                btn.Click += (s, e) => { _sw?.SetPreview(ch); SyncBadges(); };
                _inButtons[i] = btn;
                bar.Children.Add(btn);
            }

            var cut = new Button
            {
                Content = "CUT",
                Width = 88,
                Height = 36,
                Margin = new Thickness(14, 0, 8, 6),
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromRgb(0xB0, 0x18, 0x24))
            };
            AutomationProperties.SetAutomationId(cut, "CutBtn");
            AutomationProperties.SetName(cut, "硬切 CUT");
            cut.Click += (s, e) => { DoCut(); };
            bar.Children.Add(cut);

            var auto = new Button
            {
                Content = $"AUTO {_opt.AutoDuration:0.0}s",
                Width = 112,
                Height = 36,
                Margin = new Thickness(0, 0, 6, 6),
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x5E, 0x20))
            };
            AutomationProperties.SetAutomationId(auto, "AutoBtn");
            AutomationProperties.SetName(auto, "溶解转场 AUTO");
            auto.Click += (s, e) => { DoAuto(); };
            bar.Children.Add(auto);

            Grid.SetRow(bar, 1);
            root.Children.Add(bar);

            _stats = new TextBlock
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(0xD8, 0xD8, 0xD8)),
                Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x0B)),
                Padding = new Thickness(14, 10, 14, 12),
                Text = "初始化中…"
            };
            Grid.SetRow(_stats, 2);
            root.Children.Add(_stats);

            PreviewKeyDown += OnPgmKeyDown;
        }

        private void OnPgmKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (_sw == null) return;

            int digit = -1;
            if (e.Key >= System.Windows.Input.Key.D1 && e.Key <= System.Windows.Input.Key.D9)
                digit = e.Key - System.Windows.Input.Key.D1;
            else if (e.Key >= System.Windows.Input.Key.NumPad1 && e.Key <= System.Windows.Input.Key.NumPad9)
                digit = e.Key - System.Windows.Input.Key.NumPad1;

            if (digit >= 0 && digit < _sw.Count)
            {
                _sw.SetPreview(digit);
                SyncBadges();
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.Enter || e.Key == System.Windows.Input.Key.Space)
            {
                DoCut();
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.A)
            {
                DoAuto();
                e.Handled = true;
            }
        }

        private void DoCut()
        {
            var sw = _sw;
            if (sw == null) return;
            sw.Cut(sw.Preview);
            SyncBadges();
        }

        private void DoAuto()
        {
            var sw = _sw;
            if (sw == null) return;
            sw.Auto(sw.Preview, _opt.AutoDuration);
            SyncBadges();
        }

        /// <summary>输入按钮的红/绿底色 = 谁是节目、谁是预监。</summary>
        private void SyncBadges()
        {
            var sw = _sw;
            if (sw == null) return;

            int pgm = sw.Program, pvw = sw.Preview;
            if (pgm == _lastProgram && pvw == _lastPreview) return;
            _lastProgram = pgm;
            _lastPreview = pvw;

            for (int i = 0; i < _inButtons.Length; i++)
            {
                var b = _inButtons[i];
                if (b == null) continue;

                if (i == pgm) b.Background = new SolidColorBrush(Color.FromRgb(0xB0, 0x18, 0x24));
                else if (i == pvw) b.Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x5E, 0x20));
                else b.Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
            }

            if (_pgmLabel != null) _pgmLabel.Text = $"节目 PGM  #{pgm + 1}";
            if (_pvwLabel != null) _pvwLabel.Text = $"预监 PVW  #{pvw + 1}";
        }

        /// <summary>预监缩略图：10fps 更新，只搬 384×216，不吃 UI 线程。</summary>
        private void OnPvwTick(object? sender, EventArgs e)
        {
            try
            {
                var sw = _sw;
                var bmp = _pvwBmp;
                if (sw == null || bmp == null) return;

                var feed = sw.PreviewFeed;
                if (feed == null) return;
                var src = feed.Acquire();
                if (src == IntPtr.Zero) return;

                if (_pvwBuf.Length != ThumbW * ThumbH * 4) _pvwBuf = new byte[ThumbW * ThumbH * 4];
                Thumbnail.Render(src, feed.Width, feed.Height, feed.Stride, _pvwBuf, ThumbW, ThumbH);
                // byte[] 重载的参数顺序是 (rect, buffer, stride, offset)——传错会溢出，界面全黑
                bmp.WritePixels(new Int32Rect(0, 0, ThumbW, ThumbH), _pvwBuf, ThumbW * 4, 0);
            }
            catch (Exception ex)
            {
                Trace.Log("预监缩略图异常: " + ex.Message);
            }
        }


        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Trace.Log("OnLoaded 开始");
            try
            {
                Core.Initialize();
            }
            catch (Exception ex)
            {
                Trace.Log("Core.Initialize 失败: " + ex);
                _stats.Text = "LibVLC 初始化失败：" + ex.Message;
                return;
            }
            Trace.Log("Core.Initialize 完成");

            var vlcOpts = new List<string>
            {
                "--no-video-title-show", "--quiet",
                // ⚠ 这里绝对不能有 --no-audio：那是**实例级**开关，一加全实例都不解码音频，
                //   光把 Media 上的 :no-audio 去掉没用（P2.5 实测：音频流在，但内容全是静音、码率 2kbps）。
                "--aout=amem",                 // 音频交给 amem 回调 → 汇入总线，不再直出声卡
                "--avcodec-hw=none",           // P1 走软解（POC 实测硬解在 vmem 下基本无收益）
                "--no-osd", "--no-snapshot-preview"
            };
            _vlc = new LibVLC(false, vlcOpts.ToArray());

            var (sw0, sh0) = Probe(_vlc, _opt.File);
            Trace.Log($"探针结果 {sw0}x{sh0}");
            if (sw0 <= 0 || sh0 <= 0)
            {
                _stats.Text = "探测不到视频轨：" + _opt.File;
                return;
            }

            _comp = new Compositor(_opt.Width, _opt.Height);
            var layout = _opt.IsPgmMode ? null : BuildLayout(_opt.Count, _opt.Width, _opt.Height);

            // P2.5：音频总线要先起 —— ffmpeg 需要拿到 TCP 端口才知道去哪里接音频
            if (_opt.WithAudio)
            {
                AudioBus.Instance.Start();
                Trace.Log("音频总线已启动: " + AudioBus.Instance.InputUrl);
            }

            // P3：切换台
            if (_opt.IsPgmMode)
            {
                _sw = new Switcher
                {
                    AudioGain = (float)_opt.AudioVolume,
                    FollowProgram = _opt.AudioFollowProgram
                };
                _sw.Switched += msg => Trace.Log("切换: " + msg);
                _comp.ProgramMode = true;
            }

            for (int i = 0; i < _opt.Count; i++)
            {
                // 每路给一个不同的起始时间，8 格画面各不相同，一眼能看出是不是真的 8 路独立解码
                string start = (i * 1.7).ToString("0.0", CultureInfo.InvariantCulture);

                // P2.5：每路挂一条音轨。
                // 多画面模式按路数等分音量；PVW/PGM 模式交给切换台（只有节目那一路出声）。
                AudioBus.Track? track = null;
                if (_opt.WithAudio)
                {
                    track = AudioBus.Instance.EnsureTrack("#" + (i + 1));
                    track.ToStream = true;
                    if (_opt.IsPgmMode)
                    {
                        track.Mute = true;
                        track.Volume = 0f;
                    }
                    else
                    {
                        track.Mute = false;
                        track.Volume = (float)Math.Max(0.02, _opt.AudioVolume / Math.Max(1, _opt.Count));
                    }
                }

                var feed = new VmemInput(_vlc, _opt.File, sw0, sh0, "#" + (i + 1), start,
                                         loop: true, audioTrack: track);
                _feeds.Add(feed);
                _lastFeedFrames[feed] = 0;

                if (_opt.IsPgmMode)
                {
                    _sw!.Add(feed, track);
                    continue;
                }

                var rect = layout![i];
                var layer = new Compositor.Layer
                {
                    Feed = feed,
                    Label = "#" + (i + 1),
                    X = rect.X, Y = rect.Y, W = rect.W, H = rect.H,
                    Z = i,
                    Debug = _opt.DebugOverlay,
                    DebugColor = Palette(i)
                };
                _comp.Layers.Add(layer);
            }

            if (_sw != null)
            {
                _sw.SetProgram(Math.Clamp(_opt.StartProgram, 0, _feeds.Count - 1));
                _sw.SetPreview(Math.Clamp(_opt.StartPreview, 0, _feeds.Count - 1));
                _sw.Tick();                       // 立刻把音频交给节目那一路，别等第一帧
                Trace.Log($"切换台就绪：节目 #{_sw.Program + 1}  预监 #{_sw.Preview + 1}  " +
                          $"转场 {_opt.AutoDuration:0.0}s  音频跟随节目={_sw.FollowProgram}");
            }


            _bmp = new WriteableBitmap(_opt.Width, _opt.Height, 96, 96, PixelFormats.Bgr32, null);
            _preview.Source = _bmp;
            Trace.Log("WriteableBitmap 就绪");

            foreach (var f in _feeds)
            {
                bool ok = f.Start();
                Trace.Log($"{f.Name} Start={ok} err={f.Error}");
                if (!ok) _stats.Text = f.Name + " 启动失败：" + f.Error;
            }

            StartOutputs();
            StartRecordWatchdog();

            _running = true;
            _synthThread = new Thread(SynthLoop)
            {
                IsBackground = true,
                Name = "SynthCompose",
                Priority = ThreadPriority.AboveNormal
            };
            _synthThread.Start();

            _proc.Refresh();
            _cpu0 = _proc.TotalProcessorTime;
            _cpuT0 = _life.Elapsed.TotalSeconds;

            _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, OnTick, Dispatcher);
            _tick.Start();

            if (_opt.IsPgmMode)
            {
                _pvwTick = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Render, OnPvwTick, Dispatcher);
                _pvwTick.Start();
                SyncBadges();
            }

            Title += "  [RUN]";
            Trace.Log("合成线程已启动");
        }

        /// <summary>
        /// P2：成品帧的三条出去的路——录制/推流（ffmpeg 管道）、全屏输出（独立线程窗口）、预览（主界面）。
        /// 三者互不依赖：推流断了预览照常，主界面卡了全屏输出照常。
        /// </summary>
        private void StartOutputs()
        {
            // ① 编码通路（可多条：边录边推）
            if (_opt.OutTargets.Count > 0)
            {
                // 音频：同一路混音结果要同时喂给「录制」和「推流」两条 ffmpeg，
                // 所以总线必须能同时服务多个客户端（见 AudioBus 的多客户端接受逻辑）。
                string? audioUrl = _opt.WithAudio ? AudioBus.Instance.InputUrl : null;
                if (_opt.WithAudio && string.IsNullOrEmpty(audioUrl))
                    Trace.Log("音频总线没有监听地址，本次输出将不带声音");

                _bus = new OutputBus();
                foreach (var target in _opt.OutTargets)
                {
                    try
                    {
                        if (!File.Exists(_opt.FfmpegPath))
                            throw new FileNotFoundException("找不到 ffmpeg", _opt.FfmpegPath);

                        // 分段录制：只对本地录制文件生效，推流不分段（推流没有"文件"可切）。
                        // 「按容量」在这里换算成「按时间」——ffmpeg 8.x 的 segment 分段器
                        // 已经没有 segment_size 选项了，只能按时间切，靠码率换算回来。
                        bool segStream = target.Trim().IndexOf("://", StringComparison.Ordinal) > 0;
                        double segSec = (!segStream && _opt.RecordLimits.Any)
                            ? _opt.RecordLimits.SegmentSecondsFor(_opt.BitrateKbps, audioUrl != null ? 128 : 0)
                            : 0;

                        var enc = _bus.Add(_opt.FfmpegPath, _opt.Width, _opt.Height, _opt.Fps,
                                           target, _opt.BitrateKbps, 0.30, audioUrl, segSec);
                        enc.Fatal += msg => Trace.Log($"ffmpeg 报错 [{enc.Target}]: {msg}");

                        Trace.Log($"编码通路已启动 → {target}（{enc.Muxer}，队列 {enc.QueueDepth} 帧，" +
                                  $"{_opt.Width}x{_opt.Height}@{_opt.Fps:0.###}，{_opt.BitrateKbps}kbps，" +
                                  (audioUrl != null ? "带音频" : "无音频") +
                                  (enc.Segmented ? $"，每段 {enc.SegmentSeconds:0} 秒后自动换新文件" : "") + "）");
                    }
                    catch (Exception ex)
                    {
                        Trace.Log($"编码通路启动失败 [{target}]: {ex}");
                        _stats.Text = "输出启动失败（" + target + "）：" + ex.Message;
                    }
                }
            }

            // ② 全屏输出（等价 vMix 的 Fullscreen 到第二屏）
            if (_opt.FullscreenMonitor >= 0)
            {
                try
                {
                    _fs = new FullscreenOutput(_opt.Width, _opt.Height, _opt.FullscreenMonitor,
                                               _opt.FullscreenTopmost, _opt.FsRect);
                    if (_fs.Open())
                        Trace.Log($"全屏输出已打开 → {_fs.MonitorName} {_fs.MonitorRect}");
                    else
                        Trace.Log("全屏输出打开失败（独立线程没起来）");
                }
                catch (Exception ex)
                {
                    Trace.Log("全屏输出异常: " + ex);
                }
            }
        }

        /// <summary>
        /// 录制分段看门狗（老板 2026-10-20 澄清版）。
        ///
        /// 第一轮原话：「录制应该增加按大小（单位：G）录制并保存或者按时间录制（小时）并保存的选项」，
        /// 当时实现成「到量就停止录制」；第二轮澄清：「达到某个容量或时间自动打包并开始录制新文件」。
        /// 所以现在是**分段录制**：切段由 ffmpeg 的 segment 分段器在同一进程内完成（无缝、不掉帧），
        /// 看门狗只负责盯目录、记录每段真实大小、把进度喂给状态栏。
        /// </summary>
        private void StartRecordWatchdog()
        {
            if (!_opt.RecordLimits.Any || _bus == null) return;

            var recCount = 0;
            foreach (var enc in _bus.Sinks) if (!enc.IsStream) recCount++;
            if (recCount == 0)
            {
                Trace.Log("设了分段录制，但这次没有本地录制通路（全是推流），分段不生效");
                return;
            }

            _recWatch = new RecordWatchdog(_bus, _opt.RecordLimits, _opt.Fps);
            _recWatch.SegmentRolled += (enc, seg) =>
            {
                string msg = $"📦 已封包：{Path.GetFileName(seg.Path)}（{seg.Bytes / 1048576.0:0.0} MB），继续录下一段";
                _recStopNote = msg;
                Trace.Log(msg);
                Dispatcher.BeginInvoke(new Action(() => { if (_stats != null) _stats.Text = msg; }));
            };
            _recWatch.Start();

            Trace.Log($"分段录制已启用：{_opt.RecordLimits.Describe()}");
        }

        private static (int w, int h) Probe(LibVLC vlc, string file)
        {
            try
            {
                using var m = new Media(vlc, file, FromType.FromPath);
                // Parse 是异步的：不等它完成就读 Tracks 一定是空数组（PocVmem 里被默认 1920x1080 掩盖了）
                var task = m.Parse(MediaParseOptions.ParseLocal);
                try { task?.Wait(8000); } catch (Exception ex) { Trace.Log("Parse 等待异常: " + ex.Message); }
                Trace.Log($"Parse 状态 {task?.Status} 轨道数 {m.Tracks.Length}");

                foreach (var t in m.Tracks)
                {
                    if (t.TrackType != TrackType.Video) continue;
                    return ((int)t.Data.Video.Width, (int)t.Data.Video.Height);
                }
            }
            catch (Exception ex) { Trace.Log("Probe 异常: " + ex); }
            return (0, 0);
        }

        private static (int X, int Y, int W, int H)[] BuildLayout(int n, int w, int h)
        {
            int cols, rows;
            if (n <= 1) { cols = 1; rows = 1; }
            else if (n <= 4) { cols = 2; rows = 2; }
            else if (n <= 6) { cols = 3; rows = 2; }
            else if (n <= 9) { cols = (n <= 8) ? 4 : 3; rows = (n <= 8) ? 2 : 3; }
            else { cols = 4; rows = (n + 3) / 4; }

            var list = new List<(int, int, int, int)>();
            int cw = w / cols, ch = h / rows;
            for (int i = 0; i < n; i++)
            {
                int c = i % cols, r = i / cols;
                if (r >= rows) break;
                list.Add((c * cw, r * ch, cw, ch));
            }
            while (list.Count < n) list.Add((0, 0, cw, ch));
            return list.ToArray();
        }

        private static uint Palette(int i)
        {
            uint[] c =
            {
                0xFF3B82F6u, 0xFF22C55Eu, 0xFFF59E0Bu, 0xFFEF4444u,
                0xFFA855F7u, 0xFF06B6D4u, 0xFF84CC16u, 0xFFEC4899u
            };
            return c[i % c.Length];
        }

        // ── 合成线程 ─────────────────────────────────────────────────────────
        private void SynthLoop()
        {
            // 用绝对时间基准（start + n × 周期）而不是逐帧累加：
            // 29.97 / 59.94 这类 NTSC 帧率周期是无限小数，累加会一直漂，绝对基准不会。
            double fps = _opt.Fps > 0 ? _opt.Fps : 25.0;
            double periodTicks = Stopwatch.Frequency / fps;
            long start = Stopwatch.GetTimestamp();
            _synthStartTicks = start;
            long n = 0;

            while (_running)
            {
                long due = start + (long)(n * periodTicks);
                long now = Stopwatch.GetTimestamp();

                if (due > now)
                {
                    double ms = (due - now) * 1000.0 / Stopwatch.Frequency;
                    // 1ms 定时器粒度下，用 Sleep(1) 反复醒来重算，累计误差 < 1ms；
                    // 最后 2ms 改成忙等，避免 Sleep 睡过头把整帧推后。
                    if (ms > 2.0) Thread.Sleep(1);
                    else Thread.SpinWait(120);
                    continue;
                }

                // 落后超过 3 帧：整体平移时间轴（不追帧，否则会连续猛合成把 CPU 打满）
                if (now - due > (long)(periodTicks * 3))
                {
                    long lag = now - due;
                    start += lag;
                    due += lag;
                    Trace.Log($"节拍落后 {lag * 1000.0 / Stopwatch.Frequency:0}ms，时间轴平移");
                }

                var comp = _comp;
                if (comp == null) break;

                double tSec = (now - start) / (double)Stopwatch.Frequency;
                RunPlan(tSec);

                // P3：先推进转场/音频淡化，再把「节目 + 转场中的下一路 + 混合比例」交给合成器
                var sw = _sw;
                if (sw != null)
                {
                    sw.Tick();
                    sw.GetFrame(out var pgmA, out var pgmB, out double mix);
                    comp.Pgm = pgmA;
                    comp.PgmB = pgmB;
                    comp.Mix = mix;
                }

                try
                {
                    comp.Compose();
                    RecordCost(comp.LastComposeMs);
                }
                catch (Exception ex)
                {
                    Trace.Log("Compose 抛异常: " + ex.Message);
                }

                // P2：同一帧同时喂 编码通路 + 全屏输出（预览在 Present 里另走一次）
                // 全部在 swap 锁内完成，保证三路拿到的是同一帧，天然同步。
                var bus = _bus;
                var fs = _fs;
                bool busAlive = bus != null && bus.AnyAlive;
                if (busAlive || fs != null)
                {
                    comp.WithFront((buf, stride) =>
                    {
                        if (busAlive) bus!.Push(buf, stride);
                        fs?.Present(buf, stride);
                    });
                }

                long seq = Interlocked.Increment(ref _composeFrames);
                if (seq == 1)
                    Trace.Log($"首帧合成完成 {comp.LastComposeMs:0.00}ms，目标 {fps:0.###}fps");
                n++;

                if (Interlocked.CompareExchange(ref _presentPending, 1, 0) == 0)
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(Present));
                }
            }
        }

        /// <summary>
        /// 跑预设的切换计划（自动化验证用，也方便演示）。
        /// 放在合成线程里按合成时间判，切换时刻的精度就是 1 帧，不受 UI 卡顿影响。
        /// </summary>
        private void RunPlan(double t)
        {
            var plan = _opt.Plan;
            if (plan.Count == 0) return;

            for (int i = 0; i < plan.Count; i++)
            {
                var cue = plan[i];
                if (cue.Done || t < cue.At) continue;
                cue.Done = true;

                var sw = _sw;
                if (sw == null) continue;

                if (string.Equals(cue.Action, "auto", StringComparison.OrdinalIgnoreCase))
                    sw.Auto(cue.Channel, _opt.AutoDuration);
                else
                    sw.Cut(cue.Channel);

                Dispatcher.BeginInvoke(new Action(SyncBadges));
            }
        }

        private void RecordCost(double ms)
        {
            if (_costCount < _costs.Length) _costCount++;
            _costs[_costIdx] = ms;
            _costIdx = (_costIdx + 1) % _costs.Length;
            if (ms > _costMax) _costMax = ms;
        }

        private void Present()
        {
            try
            {
                var comp = _comp;
                var bmp = _bmp;
                if (comp == null || bmp == null) return;
                comp.PresentTo((buf, stride) =>
                    bmp.WritePixels(new Int32Rect(0, 0, comp.Width, comp.Height), buf, stride * comp.Height, stride));
                if (Interlocked.Increment(ref _presentCount) == 1) Trace.Log("首帧呈现完成");
            }
            catch (Exception ex)
            {
                Trace.Log("Present 抛异常: " + ex);
            }
            finally
            {
                Volatile.Write(ref _presentPending, 0);
            }
        }

        // ── 统计 ─────────────────────────────────────────────────────────────
        private void OnTick(object? sender, EventArgs e)
        {
            if (!_running) return;

            double t = SynthSeconds;
            long frames = Interlocked.Read(ref _composeFrames);
            double realFps = frames / t;

            _proc.Refresh();
            double cores = CpuCores;
            double cpuPct = cores / Environment.ProcessorCount * 100.0;
            double mem = _proc.WorkingSet64 / 1048576.0;

            _droppedTotal = 0;
            foreach (var f in _feeds) _droppedTotal += f.Dropped;

            var (p50, p95) = Percentiles();

            var sb = new StringBuilder();
            sb.AppendLine($"输出 {_opt.Width}x{_opt.Height} @{_opt.Fps:0.00}fps    路数 {_feeds.Count}    " +
                          $"源 {_feeds[0].Width}x{_feeds[0].Height}    已跑 {t:0.0}s");
            sb.AppendLine($"合成 {realFps:0.00} fps   耗时 p50 {p50:0.00}ms / p95 {p95:0.00}ms / max {_costMax:0.0}ms");
            sb.AppendLine($"CPU {cores:0.00} 核（{cpuPct:0.0}% 整机）   内存 {mem:0} MB   解码丢帧 {_droppedTotal}   " +
                          $"窗口 {(WindowState == WindowState.Minimized ? "已最小化" : "正常")}");

            if (_bus != null)
            {
                foreach (var enc in _bus.Sinks)
                {
                    sb.AppendLine($"→ {ShortTarget(enc.Target)} [{enc.Muxer}]  写入 {enc.Written} 帧  " +
                                  $"丢 {enc.Dropped} 帧  " +
                                  (enc.HasExited ? "已退出(" + enc.ExitCode + ")" : "运行中"));
                }
            }

            if (_fs != null)
            {
                sb.AppendLine($"全屏输出 → {_fs.MonitorName}  已显示 {_fs.Shown} 帧  跳过 {_fs.Skipped} 帧");
            }

            if (_sw != null)
            {
                SyncBadges();
                sb.AppendLine($"节目 PGM #{_sw.Program + 1}   预监 PVW #{_sw.Preview + 1}   " +
                              (_sw.InTransition ? $"转场中 {_sw.Mix * 100:0}%" : "待命") +
                              $"   硬切 {_sw.Cuts} 次 / 转场 {_sw.Autos} 次");
            }

            if (_recStopNote.Length > 0) sb.AppendLine(_recStopNote);

            if (_opt.WithAudio)
            {
                var ab = AudioBus.Instance;
                sb.AppendLine($"音频 → 48kHz 立体声  送出 {ab.BlocksWritten} 块（20ms/块）  " +
                              $"ffmpeg 接入 {ab.ClientCount} 路  落后重对齐 {ab.BlocksDropped} 次");
            }

            var parts = new List<string>();
            foreach (var f in _feeds)
            {
                long cur = f.Frames;
                long prev = _lastFeedFrames.TryGetValue(f, out var p) ? p : 0;
                _lastFeedFrames[f] = cur;
                double fps = (cur - prev) / 0.5;
                parts.Add($"{f.Name} {fps:0.0}");
            }
            sb.Append("每路 fps: " + string.Join("  ", parts));

            _stats.Text = sb.ToString();

            if (_opt.MinimizeAfter > 0 && !_minimized && t >= _opt.MinimizeAfter && !_opt.Offscreen)
            {
                _minimized = true;
                WindowState = WindowState.Minimized;
                _stats.Text += "\n→ 已最小化，合成继续（这就是 P1 要证明的事）";
            }

            // 验证 P2 的隔离性：把 UI 线程硬卡 3 秒，看推流/全屏输出会不会跟着停
            if (_opt.FreezeUiAfter > 0 && !_frozen && t >= _opt.FreezeUiAfter)
            {
                _frozen = true;
                long before = Interlocked.Read(ref _composeFrames);
                long presentBefore = Interlocked.Read(ref _presentCount);
                Trace.Log($"开始冻结 UI 3 秒（此刻合成 {before} 帧，预览 {presentBefore} 帧）");
                Thread.Sleep(3000);
                Trace.Log($"UI 解冻（合成 {Interlocked.Read(ref _composeFrames)} 帧，" +
                          $"预览 {Interlocked.Read(ref _presentCount)} 帧）");
            }

            if (_opt.Seconds > 0 && t >= _opt.Seconds) Close();
        }

        /// <summary>状态栏放不下长路径/长 URL，截个尾巴便于辨认是哪个目标。</summary>
        private static string ShortTarget(string target)
        {
            string t = target;
            if (t.Length <= 46) return t;
            return "…" + t.Substring(t.Length - 45);
        }

        private (double p50, double p95) Percentiles()
        {
            int n = _costCount;
            if (n == 0) return (0, 0);
            var arr = new double[n];
            for (int i = 0; i < n; i++) arr[i] = _costs[i];
            Array.Sort(arr);
            return (arr[(int)(n * 0.50)], arr[Math.Min(n - 1, (int)(n * 0.95))]);
        }

        // ── 收尾 ─────────────────────────────────────────────────────────────
        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);
            Shutdown();
        }

        private void Shutdown()
        {
            if (_shutdown) return;
            _shutdown = true;
            Trace.Log("Shutdown 开始");
            _running = false;
            _synthEndTicks = Stopwatch.GetTimestamp();   // 帧率口径截止在这里，后面的收尾不算运行时长
            // 音频口径同样截止在这里：之后 ffmpeg 收尾还要几秒，混音线程仍在跑，
            // 不掐断的话报告会显示「音频比视频多 7 秒」，看起来像音画不同步（实际文件里是同步的）。
            _audioBlocksAtStop = AudioBus.Instance.BlocksWritten;
            try { _synthThread?.Join(2000); } catch { }
            try { _tick?.Stop(); } catch { }
            try { _pvwTick?.Stop(); } catch { }
            Trace.Log("合成线程已停");

            // 先收输出：关 stdin 让 ffmpeg 各自写完容器索引，再释放合成缓冲
            try { _bus?.Dispose(); } catch { }
            try { _fs?.Dispose(); } catch { }
            // 看门狗必须放在输出之后停 —— 最后一段的大小只有 ffmpeg 收尾写完 moov 才准，
            // 提前停会漏掉结尾那几段，报告里显示「只录了 3 段」而磁盘上其实有 5 个文件。
            try { _recWatch?.Dispose(); } catch { }
            // 音频总线放在 ffmpeg 之后停：让 ffmpeg 先正常收尾，别因为音频源提前断而报错
            try { AudioBus.Instance.Dispose(); } catch { }
            Trace.Log("输出与音频已停");

            if (!string.IsNullOrEmpty(_opt.DumpPath)) { Trace.Log("导出成品帧"); DumpFrame(_opt.DumpPath!); }
            if (!string.IsNullOrEmpty(_opt.ReportPath)) { Trace.Log("写报告"); WriteReport(_opt.ReportPath!); }

            Trace.Log("释放解码器");
            foreach (var f in _feeds) { try { f.Dispose(); } catch { } }
            _feeds.Clear();
            try { _comp?.Dispose(); } catch { }
            try { _vlc?.Dispose(); } catch { }
            Trace.Log("Shutdown 完成");
        }

        private void DumpFrame(string path)
        {
            var comp = _comp;
            if (comp == null) return;
            try
            {
                BitmapSource? shot = null;
                comp.PresentTo((buf, stride) =>
                    shot = BitmapSource.Create(comp.Width, comp.Height, 96, 96,
                                               PixelFormats.Bgr32, null, buf, stride * comp.Height, stride));
                if (shot == null) return;
                shot.Freeze();

                var dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir!);

                using var fs = File.Create(path);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(shot));
                enc.Save(fs);
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(path + ".err.txt", ex.ToString()); } catch { }
            }
        }

        private void WriteReport(string path)
        {
            try
            {
                double t = SynthSeconds;
                long frames = Interlocked.Read(ref _composeFrames);
                var (p50, p95) = Percentiles();
                double cores = CpuCores;

                var sb = new StringBuilder();
                sb.AppendLine("=== SynthPoc 合成 + 输出 实测 ===");
                sb.AppendLine("时间       : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("素材       : " + _opt.File);
                sb.AppendLine($"输出       : {_opt.Width}x{_opt.Height} @{_opt.Fps:0.00}fps");
                sb.AppendLine($"模式       : {(_opt.IsPgmMode ? "PVW/PGM 双栏（P3 切换台）" : "多画面拼接（P1/P2 回归）")}");
                sb.AppendLine($"路数       : {_feeds.Count}   源分辨率 {(_feeds.Count > 0 ? _feeds[0].Width + "x" + _feeds[0].Height : "-")}");
                sb.AppendLine();
                sb.AppendLine($"运行时长   : {t:0.0} s");
                sb.AppendLine($"合成帧数   : {frames}  → {frames / Math.Max(0.001, t):0.00} fps（目标 {_opt.Fps:0.00}）");
                sb.AppendLine($"合成耗时   : p50 {p50:0.00}ms / p95 {p95:0.00}ms / max {_costMax:0.0}ms");
                sb.AppendLine($"进程 CPU   : {cores:0.00} 核 / {Environment.ProcessorCount} 核 = {cores / Environment.ProcessorCount * 100:0.0}% 整机");
                sb.AppendLine($"内存       : {_proc.WorkingSet64 / 1048576.0:0} MB");
                sb.AppendLine($"解码丢帧   : {_droppedTotal}");
                sb.AppendLine($"窗口状态   : {(WindowState == WindowState.Minimized ? "已最小化（合成未停）" : "正常")}   离屏={_opt.Offscreen}");
                sb.AppendLine($"预览上传   : {Interlocked.Read(ref _presentCount)} 帧（走 UI 线程；界面卡住时这里才会掉）" +
                              (_opt.FreezeUiAfter > 0 ? $"   已验证 UI 冻结 {_opt.FreezeUiAfter:0}s" : ""));

                if (_bus != null)
                {
                    sb.AppendLine();
                    sb.AppendLine("输出通路（P2：内存成品帧 → ffmpeg 管道，不抓屏）：");

                    int idx = 0;
                    foreach (var enc in _bus.Sinks)
                    {
                        idx++;
                        sb.AppendLine($"  [{idx}] 目标 : {enc.Target}");
                        sb.AppendLine($"      容器 : {enc.Muxer}   队列深度 {enc.QueueDepth} 帧");
                        sb.AppendLine($"      推送 : {enc.Pushed} 帧 → 实际写入 {enc.Written} 帧，溢出丢 {enc.Dropped} 帧" +
                                      $"（送达率 {(enc.Pushed > 0 ? enc.Written * 100.0 / enc.Pushed : 0):0.0}%）");
                        sb.AppendLine($"      ffmpeg: 退出码 {enc.FinalExitCode}" +
                                      (enc.FinalExitCode == 0 ? "（正常收尾）" : "（异常，看下面的错误输出）"));
                        if (!string.IsNullOrEmpty(enc.StopReason))
                            sb.AppendLine($"      停止原因 : {enc.StopReason}（已收尾保存，推流未受影响）");

                        string err = enc.Stderr.Trim();
                        if (err.Length > 0)
                        {
                            if (err.Length > 800) err = err.Substring(err.Length - 800);
                            sb.AppendLine("      错误输出 : " + err.Replace("\r\n", "\n                 "));
                        }

                        try
                        {
                            if (!enc.IsStream && File.Exists(enc.Target))
                            {
                                var fi = new FileInfo(enc.Target);
                                sb.AppendLine($"      文件 : {fi.Length / 1048576.0:0.00} MB");
                            }
                        }
                        catch { }
                    }
                }

                if (_fs != null)
                {
                    sb.AppendLine();
                    sb.AppendLine("全屏输出（P2：独立线程窗口，不受主界面影响）：");
                    sb.AppendLine($"  显示器   : {_fs.MonitorName}   {_fs.MonitorRect}");
                    sb.AppendLine($"  已显示   : {_fs.Shown} 帧   跳过（跟不上）{_fs.Skipped} 帧");
                }

                if (_sw != null)
                {
                    sb.AppendLine();
                    sb.AppendLine("P3 切换台（PVW/PGM 双栏）：");
                    sb.AppendLine($"  当前节目 : #{_sw.Program + 1}      当前预监 : #{_sw.Preview + 1}");
                    sb.AppendLine($"  硬切     : {_sw.Cuts} 次      溶解转场 : {_sw.Autos} 次（每次 {_opt.AutoDuration:0.0}s）");
                    sb.AppendLine($"  转场帧数 : {_sw.TransitionFrames} 帧（转场期间照常逐帧出画，不存在黑屏空窗）");
                    sb.AppendLine($"  音频跟随 : {(_sw.FollowProgram ? "只出节目那一路，切换处 150ms 交叉淡化" : "多路混音（--mix-audio）")}");
                    sb.AppendLine("  切换事件 :");
                    var evs = _sw.Events;
                    if (evs.Count == 0) sb.AppendLine("    （无）");
                    foreach (var e in evs) sb.AppendLine("    " + e);
                }

                if (_recWatch != null)
                {
                    sb.AppendLine();
                    sb.AppendLine("分段录制（按大小 / 按时长，到量自动封包并开新文件）：");
                    sb.AppendLine($"  设置     : {_opt.RecordLimits.Describe()}");
                    foreach (var enc in _bus?.Sinks ?? new List<FfmpegEncoder>())
                    {
                        if (enc.IsStream || !enc.Segmented) continue;
                        sb.AppendLine($"  目标     : {enc.Target}");
                        sb.AppendLine($"  每段     : {enc.SegmentSeconds:0} 秒" +
                                      $"（约 {enc.SegmentSeconds * (enc.VideoBitrateKbps + enc.AudioBitrateKbps) * 125.0 / 1048576.0:0} MB）" +
                                      $"  文件模板 {Path.GetFileName(enc.SegmentPattern)}");
                    }
                    var segs = _recWatch.Segments;
                    if (segs.Count == 0)
                    {
                        sb.AppendLine("  状态     : 还没到量（第一段仍在写）");
                    }
                    else
                    {
                        foreach (var s in segs)
                        {
                            sb.AppendLine($"  第 {s.Index + 1} 段 {(s.Complete ? "已封包" : "写入中")}: {Path.GetFileName(s.Path)}  " +
                                          $"{s.Bytes / 1048576.0:0.0} MB");
                        }
                        sb.AppendLine($"  合计     : {segs.Count} 段 / {_recWatch.TotalBytes / 1048576.0:0.0} MB（推流不受影响，一直在跑）");
                    }
                }

                if (_opt.WithAudio)
                {
                    var ab = AudioBus.Instance;
                    long blocks = _audioBlocksAtStop > 0 ? _audioBlocksAtStop : ab.BlocksWritten;
                    double secs = blocks * 20.0 / 1000.0;
                    sb.AppendLine();
                    sb.AppendLine("音频（P2.5：各路线性混音 → TCP → ffmpeg）：");
                    sb.AppendLine($"  格式     : 48000 Hz / 立体声 / S16");
                    sb.AppendLine($"  送出     : {blocks} 块 × 20ms = {secs:0.00} 秒（截至停合成，不含收尾）");
                    sb.AppendLine($"  音画偏差 : {(secs - t):+0.00;-0.00;0.00} 秒（正 = 音频多，负 = 音频少）");
                }
                sb.AppendLine();
                sb.AppendLine("每路：");
                foreach (var f in _feeds)
                    sb.AppendLine($"  {f.Name}  出帧 {f.Frames}   丢 {f.Dropped}   错误 {(string.IsNullOrEmpty(f.Error) ? "-" : f.Error)}");
                sb.AppendLine("DONE");

                var dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir!);
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }
    }
}
