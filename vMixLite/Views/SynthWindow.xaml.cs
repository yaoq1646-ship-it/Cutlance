using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cutlance.Models;
using Cutlance.Services;
using LibVLCSharp.Shared;
using SynthPoc.Engine;
using SynthPoc.Output;
// 产品自己的音频总线（带声卡输出 + 推流供流），与 POC 里那份 SynthPoc.Engine.AudioBus 是两套。
// 切换台必须用产品这份 —— 否则声卡里听不到节目声，录制也接不上主链路的音频管道。
using ProdAudio = Cutlance.Services.Audio;

namespace Cutlance.Views
{
    /// <summary>
    /// P4：监视窗 + 切换条（产品里的内存合成切换台）。
    ///
    /// 核心差别（为什么值得重做一套）：
    ///   老链路每一路都是 VLC 自己的 HwndHost 子窗口，叠加上去的是独立顶层 Popup，
    ///   所谓"合成"是 DWM 在屏幕上拼出来的 —— 内存里根本没有这张成品画面。
    ///   所以输出只能 gdigrab 抓屏：窗口一被盖住、一最小化、一换缩放，输出就没了。
    ///   本窗口每一路走 vmem 回调把帧收进我们自己的内存，CPU 合成出一张真实存在的成品帧，
    ///   推流/录制/全屏都从这张帧出去，跟屏幕再无关系。
    ///
    /// 这一版（P4 第一刀）只做「切换台 + 监看 + 输出」，工程里的图层/字幕/画中画还在老界面，
    /// 后续一版一版搬过来。这样老板日常导播流程不受影响，可以两个窗口对照着用。
    /// </summary>
    public partial class SynthWindow : Window
    {
        // 成品帧分辨率。跟主界面节目输出保持一致（1080p）。
        private const int CanvasW = 1920;
        private const int CanvasH = 1080;

        private const int PvwW = 480;
        private const int PvwH = 270;

        private readonly List<InputSource> _sources = new();
        private readonly List<VmemInput> _feeds = new();
        private readonly List<Button> _tiles = new();
        private readonly List<double> _durItems = new() { 0.3, 0.5, 0.8, 1.0, 1.5, 2.0, 3.0 };

        // 节目帧率：广播行业 PAL 是 25、网络常见 30、体育/游戏 50-60。
        // 老版本这里写死 30（合成节拍 + 录制 + 统计），工程设成 25 时
        // 会一边按 30 出帧一边按 25 写文件 —— 文件时长和实际差 20%，已改成可配。
        private readonly List<double> _fpsItems = new() { 25.0, 29.97, 30.0, 50.0, 59.94, 60.0 };

        // 录制分段：只有一个维度 —— 到时间自动封包并接着录下一个文件。
        // 0 = 不分段（一个文件录到手动停）。只保留时长，是因为 ffmpeg 8.x 的
        // segment 分段器已经没有 segment_size 参数了，「按容量」只能按码率折算成时间，
        // 折出来的段大小误差 ±10%，与其给个不准的选项，不如只给准的那个。
        private readonly List<double> _segHours = new() { 0, 0.25, 0.5, 1.0, 2.0, 4.0 };

        private double _fps = 30.0;
        private double _segHour = 1.0;

        private LibVLC? _vlc;
        private Compositor? _comp;
        private Switcher? _sw;
        // 录制会话（2026-10-25 两套录制合并）：起 ffmpeg / 音频管道 / 看门狗 / 停止清理
        // 全在 Services.RecordSession 里，切换台不再自己攒一套。
        private Services.RecordSession? _session;
        private SynthPoc.Output.FullscreenOutput? _fs;

        // 产品音频总线上的轨道：一路输入一条（key = synth:N），音量大小由 Switcher 推过来
        private readonly Dictionary<VmemInput, ProdAudio.AudioBus.Track> _prodTracks = new();

        // ── 工程图层与叠加（P4 第二步）──
        // 字幕/图片走 WPF 离屏渲染成位图；画中画图层走各自的 vmem 解码 + 合成器图层。
        private readonly ObservableCollection<OverlayItem>? _overlays;
        private readonly ObservableCollection<Layer>? _layers;
        private SynthOverlayRenderer? _ovRender;
        private System.Windows.Threading.DispatcherTimer? _syncTimer;
        private readonly Dictionary<Layer, VmemInput> _layerFeeds = new();
        private int _lastLayerCount = -1;

        private WriteableBitmap? _pgmBmp;
        private WriteableBitmap? _pvwBmp;
        private byte[]? _pvwBuf;

        private Thread? _thread;
        private volatile bool _running;
        private int _uploadPending;
        private long _frames;
        private long _pvwTick;
        private double _autoDur = 1.0;   // 转场时长（合成线程只读这个字段，绝不读 UI 控件）
        private int _lastPgm = -1;
        private string _status = "正在启动…";
        private string _recDir = "";

        private DateTime _startedAt = DateTime.Now;

        public SynthWindow(ObservableCollection<InputSource> inputs, double fps = 30,
                           ObservableCollection<OverlayItem>? overlays = null,
                           ObservableCollection<Layer>? layers = null)
        {
            InitializeComponent();

            // 最高权限（老板 2026-10-21 第 1 条）：置顶 + 不透明 + 抓屏排除 + Esc 关闭。
            // 切换台以前漏装 —— 它是最常开着的工作窗，播出框全屏置顶时会被压住。
            // 用 Pin 不用 Attach：切换台自带「刷新/关闭」那一行，硬塞「保存」会挤动版式。
            WorkFrame.Pin(this);

            _fps = fps > 0 ? fps : 30.0;
            // 开窗时把实际采用的帧率记一笔：验证「工程里记的帧率重启后还在」靠这条日志。
            ErrorLog.Info($"[切换台] 启动帧率 {_fps:0.##}fps（来自工程 SynthFps={StreamService.Instance.SynthFps:0.##}）");
            _overlays = overlays;
            _layers = layers;

            // 能播的才进切换条：NDI 走的是另一条链路（主程序自己的接收器），
            // VideoEngine.CreateMedia 暂时不认，先跳过而不是让它抛异常把整个窗口废掉。
            foreach (var s in inputs)
            {
                if (s.Type == SourceType.Ndi) continue;
                if (string.IsNullOrWhiteSpace(s.Location)) continue;
                _sources.Add(s);
            }

            DurCombo.ItemsSource = _durItems.Select(d => d.ToString("0.0") + " 秒").ToList();
            DurCombo.SelectedIndex = 3;
            // ⚠ 转场时长必须缓存成字段：合成线程要用它，而 ComboBox 是 UI 控件，
            //   在合成线程读 SelectedIndex 会抛「调用线程无法访问此对象」（实测踩过，
            //   表现为 AUTO 一点反应都没有，日志里一个 InvalidOperationException）。
            DurCombo.SelectionChanged += (s, e) => _autoDur = CurrentDur();

            InitFpsCombo();
            InitSegCombo();

            CutBtn.Click += (s, e) => DoCut();
            AutoBtn.Click += (s, e) => DoAuto();
            RecBtn.Click += (s, e) => ToggleRecord();
            FsBtn.Click += (s, e) => ToggleFullscreen();
            CloseBtn.Click += (s, e) => Close();

            Loaded += (s, e) => StartEngine();
            Closing += (s, e) => Shutdown();
            PreviewKeyDown += OnKey;
        }

        private void InitFpsCombo()
        {
            // 离屏自检钩子：CUTLANCE_SYNTH_FPS=25 直接指定帧率。
            // 窗口在屏幕外时 UIA 点不了下拉，而"25fps 到底对不对"必须能复现地量出来。
            string? env = Environment.GetEnvironmentVariable("CUTLANCE_SYNTH_FPS");
            if (double.TryParse(env, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double ef) && ef > 0)
                _fps = ef;

            FpsCombo.ItemsSource = _fpsItems.Select(f => f.ToString("0.##") + " fps").ToList();
            // 容差必须小于 29.97 与 30 的间距（0.03），否则传 30 会先命中 29.97，
            // 界面显示 29.97、内部却按 30 跑 —— 这种"看着对、实际错"最难查。
            int idx = _fpsItems.FindIndex(f => Math.Abs(f - _fps) < 0.001);
            FpsCombo.SelectedIndex = idx >= 0 ? idx : 2;   // 找不到就用 30
            FpsCombo.SelectionChanged += (s, e) => OnFpsChanged();

            // 自检钩子：CUTLANCE_SYNTH_FPS_PICK=25 模拟"老板从下拉里挑了一档"。
            // 必须放在 SelectionChanged 接线**之后**，才能走真实路径（含写回工程）——
            // 用来验证「改过的帧率关窗再开还在」，而不是只验证初始化值。
            string? pick = Environment.GetEnvironmentVariable("CUTLANCE_SYNTH_FPS_PICK");
            if (double.TryParse(pick, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double pf) && pf > 0)
            {
                int pi = _fpsItems.FindIndex(f => Math.Abs(f - pf) < 0.001);
                if (pi >= 0) FpsCombo.SelectedIndex = pi;   // → OnFpsChanged
            }
        }

        private void InitSegCombo()
        {
            string? env = Environment.GetEnvironmentVariable("CUTLANCE_SYNTH_SEG");
            double envSeg = -1;
            if (double.TryParse(env, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double es) && es >= 0)
                envSeg = es;

            SegCombo.ItemsSource = _segHours
                .Select(h => h <= 0 ? "不分段"
                            : h < 1 ? (h * 60).ToString("0") + " 分钟"
                                    : h.ToString("0.##") + " 小时")
                .ToList();
            // 默认档跟产品走（2026-10-25 两套录制合并）：产品在输出设置里选的分段时长，
            // 这里自动选中同一档 —— 否则同一台机器在切换台录和在主界面录，段长不一样（审计 B3）。
            // 0 小时 = 不分段，正好对上产品里「不分段」那一档。
            double prodSegH = Math.Max(0, Services.StreamService.Instance.RecordSegmentSeconds) / 3600.0;
            int idx = _segHours.FindIndex(h => Math.Abs(h - prodSegH) < 1e-6);
            SegCombo.SelectedIndex = idx >= 0 ? idx : 3;   // 产品那个值不在选项里 → 退回 1 小时
            // SelectionChanged 是在下面才接线的，所以这里得手动把 _segHour 同步过来
            _segHour = _segHours[Math.Max(0, SegCombo.SelectedIndex)];

            if (envSeg >= 0) _segHour = envSeg;    // 钩子给的值不在选项里也用（自检用）
            SegCombo.SelectionChanged += (s, e) => OnSegChanged();
        }

        private void OnSegChanged()
        {
            int i = SegCombo.SelectedIndex;
            _segHour = (i >= 0 && i < _segHours.Count) ? _segHours[i] : 0;
            if (_session != null && _status.StartsWith("录制", StringComparison.Ordinal))
                StatText.Text = _status = $"录制中（每段 {DescribeSeg()}，改设置请重开录制）→ " + _recDir;
        }

        /// <summary>
        /// 换帧率。合成节拍是按帧率算的距离，改它必须让合成线程用新节拍重跑，
        /// 否则只是界面数字变了、实际还按老帧率出帧（这正是之前 25fps 出错的地方）。
        /// 正在录制时不动录制 —— 停掉再重开由老板决定，免得把一段好好的录像切碎。
        /// </summary>
        private void OnFpsChanged()
        {
            int i = FpsCombo.SelectedIndex;
            if (i < 0 || i >= _fpsItems.Count) return;
            double f = _fpsItems[i];
            if (Math.Abs(f - _fps) < 0.001) return;
            _fps = f;

            // 写回工程（老板 2026-10-21：以前只改窗口内局部字段，关窗再开就变回 30）。
            // 存进 StreamService 而不是直接存盘：工程在退出时/点保存时统一落盘，
            // 这里只把「当前档位」记进服务，避免开窗期间反复写工程文件。
            // ⚠ 要同时写 Fps：输出（ffmpeg 的 -r）和主窗合成器都只读 Fps，
            //   只写 SynthFps 的话「切换台 25、推流录制还是 30」，时间戳会错位（2026-10-21 实测）。
            try
            {
                StreamService.Instance.Fps = _fps;
                StreamService.Instance.SynthFps = _fps;   // 老字段保留，工程向后兼容
            }
            catch { }
            ErrorLog.Info($"[切换台] 帧率改为 {_fps:0.##}fps（已写回 Fps={StreamService.Instance.Fps:0.##} / SynthFps={StreamService.Instance.SynthFps:0.##}）");

            if (_running) RestartCompose();
            UpdateStats();
        }

        private void RestartCompose()
        {
            try
            {
                _running = false;
                _thread?.Join(1500);
            }
            catch { }

            _lastPgm = -1;
            _startedAt = DateTime.Now;
            Interlocked.Exchange(ref _frames, 0);

            _running = true;
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "SynthWindowCompose",
                Priority = ThreadPriority.AboveNormal
            };
            _thread.Start();
            ErrorLog.Info($"[切换台] 合成节拍已切到 {_fps:0.##}fps");
        }

        // ───────────────────── 自动切换脚本（验证用） ─────────────────────
        //
        // 环境变量 CUTLANCE_SYNTH_PLAN="3:cut:2;6:auto:3"，含义：第 3 秒硬切到 2 号输入、
        // 第 6 秒溶解到 3 号输入。格式与 POC 的 --plan 一致。
        //
        // 为什么要有这个：窗口在屏幕外（--offscreen）时发不了键鼠，UIA 点按钮也不一定稳；
        // 而"切换是否真的生效"必须能复现地验证 —— 跑一次就知道 CUT/AUTO 有没有接对。
        private readonly List<(double At, string Kind, int Idx)> _plan = new();
        private int _planNext;

        private void LoadPlan()
        {
            string? raw = Environment.GetEnvironmentVariable("CUTLANCE_SYNTH_PLAN");
            if (string.IsNullOrWhiteSpace(raw)) return;

            foreach (var item in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = item.Split(':');
                if (p.Length < 3) continue;
                if (!double.TryParse(p[0], out double at)) continue;
                if (!int.TryParse(p[2], out int idx)) continue;
                _plan.Add((at, p[1].Trim().ToLowerInvariant(), idx - 1));
            }
            _plan.Sort((a, b) => a.At.CompareTo(b.At));
            if (_plan.Count > 0)
                ErrorLog.Info($"[切换台] 已载入自动切换脚本 {_plan.Count} 步");
        }

        private void RunPlan()
        {
            if (_planNext >= _plan.Count) return;
            double t = (DateTime.Now - _startedAt).TotalSeconds;
            while (_planNext < _plan.Count && t >= _plan[_planNext].At)
            {
                var step = _plan[_planNext++];
                if (step.Idx < 0 || step.Idx >= _feeds.Count) continue;

                if (step.Kind == "cut")
                {
                    _sw?.Cut(step.Idx);
                    ErrorLog.Info($"[切换台] 脚本第 {t:0.0}s CUT → #{step.Idx + 1}");
                }
                else
                {
                    _sw?.Auto(step.Idx, _autoDur);
                    ErrorLog.Info($"[切换台] 脚本第 {t:0.0}s AUTO → #{step.Idx + 1}（{_autoDur:0.0}s）");
                }

                Dispatcher.BeginInvoke(new Action(RefreshBadges));
            }
        }

        // ───────────────────────── 启动 ─────────────────────────

        private void StartEngine()
        {
            if (_sources.Count == 0)
            {
                _status = "工程里还没有可用输入，先在主界面添加素材";
                StatText.Text = _status;
                return;
            }

            try
            {
                _vlc = VideoEngine.Instance.LibVLC;

                _pgmBmp = new WriteableBitmap(CanvasW, CanvasH, 96, 96, PixelFormats.Bgr32, null);
                PgmImage.Source = _pgmBmp;

                _pvwBuf = new byte[PvwW * PvwH * 4];
                _pvwBmp = new WriteableBitmap(PvwW, PvwH, 96, 96, PixelFormats.Bgr32, null);
                PvwImage.Source = _pvwBmp;

                _comp = new Compositor(CanvasW, CanvasH) { ProgramMode = true };
                _sw = new Switcher { AudioGain = 1f, FollowProgram = true };
                _sw.Switched += msg =>
                {
                    try { ErrorLog.Info("[切换台] " + msg); } catch { }
                };

                // 音频：节目那一路出声音（声卡 + 录制/推流），其余路静音。
                // 音量大小时刻由 Switcher 算（硬切 150ms 交叉淡化、AUTO 跟转场同步），
                // 这里只把「某一路该多大声」落到产品音频总线的轨道上。
                var aud = ProdAudio.AudioBus.Instance;
                aud.EnsureStarted();
                SilenceLegacyProgram(aud);
                _sw.SetFeedAudio = (feed, vol, mute) =>
                {
                    if (_prodTracks.TryGetValue(feed, out var t)) { t.Volume = vol; t.Mute = mute; }
                };

                // 每一路：媒体交给主程序唯一的构造入口，尺寸能探到就按源尺寸出帧（不变形）
                for (int i = 0; i < _sources.Count; i++)
                {
                    var src = _sources[i];
                    try
                    {
                        var media = VideoEngine.Instance.CreateMedia(src);
                        var (w, h) = ProbeSize(media, src);
                        if (w <= 0 || h <= 0) { w = CanvasW; h = CanvasH; }

                        var track = aud.EnsureTrack("synth:" + (i + 1));
                        track.ToSoundCard = true;    // 导播要听得见节目声
                        track.ToStream = true;       // 同时进录制/推流
                        track.Mute = true;
                        track.Volume = 0f;

                        var feed = new VmemInput(_vlc, media, w, h, src.Name, null,
                            player => ProdAudio.VlcAudioTap.Attach(player, track));
                        _prodTracks[feed] = track;
                        _feeds.Add(feed);
                        _sw.Add(feed, null);
                    }
                    catch (Exception ex)
                    {
                        ErrorLog.Info($"[切换台] 输入「{src.Name}」准备失败：{ex.Message}");
                    }
                }

                if (_feeds.Count == 0)
                {
                    _status = "没有一路能播（看日志）";
                    StatText.Text = _status;
                    return;
                }

                BuildTiles();

                // 叠加（字幕/图片）：WPF 离屏渲染成位图交给合成器
                _ovRender = new SynthOverlayRenderer(CanvasW, CanvasH);
                // 图层（画中画）：每层一路 vmem 解码，按几何贴到成品帧上
                BuildLayerFeeds();

                _sw.SetProgram(0);
                _sw.SetPreview(_feeds.Count > 1 ? 1 : 0);
                _sw.Tick();

                foreach (var f in _feeds)
                {
                    if (!f.Start())
                        ErrorLog.Info($"[切换台] {f.Name} 启动失败：{f.Error}");
                }

                _comp.Pgm = _feeds[0];
                _startedAt = DateTime.Now;
                RefreshBadges();

                LoadPlan();

                _running = true;
                _thread = new Thread(Loop)
                {
                    IsBackground = true,
                    Name = "SynthWindowCompose",
                    Priority = ThreadPriority.AboveNormal
                };
                _thread.Start();

                _status = $"已启动 {_feeds.Count} 路（{_fps:0.##}fps）";
                StatText.Text = _status;

                StartSyncTimer();
                StartSelfTestHooks();
            }
            catch (Exception ex)
            {
                _status = "启动失败：" + ex.Message;
                StatText.Text = _status;
                ErrorLog.Info("[切换台] 启动异常：" + ex);
            }
        }

        /// <summary>
        /// 把工程里的图层（画中画）接进合成器：每层一路 vmem 解码，按归一化几何摆到成品帧上。
        ///
        /// 几何口径必须与主界面 RenderLayer 完全一致（宽 = 画布宽 × 0.5 × ViewScaleX，
        /// 位置 = X × 画布宽）—— 口径不一致的后果是同一个工程在老界面和切换台里
        /// 画中画大小/位置不同，导播切出去就露馅。
        /// </summary>
        private void BuildLayerFeeds()
        {
            if (_comp == null || _vlc == null) return;

            foreach (var f in _layerFeeds.Values) { try { f.Dispose(); } catch { } }
            _layerFeeds.Clear();
            _comp.Layers.Clear();

            if (_layers == null) { _lastLayerCount = 0; return; }
            _lastLayerCount = _layers.Count;

            int ok = 0;
            foreach (var L in _layers)
            {
                if (L.Source == null || string.IsNullOrWhiteSpace(L.Source.Location)) continue;
                if (L.Source.Type == SourceType.Ndi) continue;   // NDI 走另一条链路，暂时跳过

                try
                {
                    var media = VideoEngine.Instance.CreateMedia(L.Source);
                    var (w, h) = ProbeSize(media, L.Source);
                    if (w <= 0 || h <= 0) { w = CanvasW; h = CanvasH; }

                    // 图层声音先不接：画中画大多数是"看着的"，声音仍由节目那一路提供，
                    // 接上会有两路同时出声（老板第一版就是被这个坑到过）。
                    var feed = new VmemInput(_vlc, media, w, h, L.DisplayName);
                    feed.Start();
                    _layerFeeds[L] = feed;

                    _comp.Layers.Add(new Compositor.Layer
                    {
                        Feed = feed,
                        Label = L.DisplayName,
                        X = (int)Math.Round(L.X * CanvasW),
                        Y = (int)Math.Round(L.Y * CanvasH),
                        W = Math.Max(1, (int)Math.Round(CanvasW * 0.5 * L.ViewScaleX)),
                        H = Math.Max(1, (int)Math.Round(CanvasH * 0.5 * L.ViewScaleY)),
                        Z = L.Z,
                        Visible = L.Visible,
                        Letterbox = true
                    });
                    ok++;
                }
                catch (Exception ex)
                {
                    ErrorLog.Info($"[切换台] 图层「{L.DisplayName}」接入失败：{ex.Message}");
                }
            }

            if (_layers.Count > 0)
                ErrorLog.Info($"[切换台] 图层接入 {ok}/{_layers.Count}（画中画，声音仍跟节目）");
        }

        /// <summary>
        /// 500ms 同步一次工程状态：叠加内容、图层几何/可见性、图层增删。
        /// 为什么是轮询而不是订阅 PropertyChanged：老板在老界面拖图层/改字幕时
        /// 会每帧改几何，订阅会收到成百上千次通知，轮询 500ms 反而更稳、更省。
        /// </summary>
        private void StartSyncTimer()
        {
            if (_syncTimer != null) return;
            _syncTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _syncTimer.Tick += (s, e) => SyncProjectState();
            _syncTimer.Start();
        }

        private void SyncProjectState()
        {
            if (_comp == null) return;

            try
            {
                if (_ovRender != null && _overlays != null)
                {
                    int n = _ovRender.Sync(_overlays, _comp);
                    if (n > 0) ErrorLog.Info($"[切换台] 叠加层重渲染 {n} 个（共 {_ovRender.ActiveCount} 个在线）");
                }
            }
            catch (Exception ex) { ErrorLog.Info("[切换台] 叠加同步失败：" + ex.Message); }

            try
            {
                if (_layers != null && _layers.Count != _lastLayerCount) BuildLayerFeeds();

                foreach (var kv in _layerFeeds)
                {
                    var L = kv.Key;
                    for (int i = 0; i < _comp.Layers.Count; i++)
                    {
                        var cl = _comp.Layers[i];
                        if (cl.Feed != kv.Value) continue;
                        cl.X = (int)Math.Round(L.X * CanvasW);
                        cl.Y = (int)Math.Round(L.Y * CanvasH);
                        cl.W = Math.Max(1, (int)Math.Round(CanvasW * 0.5 * L.ViewScaleX));
                        cl.H = Math.Max(1, (int)Math.Round(CanvasH * 0.5 * L.ViewScaleY));
                        cl.Z = L.Z;
                        cl.Visible = L.Visible;
                        break;
                    }
                }
            }
            catch (Exception ex) { ErrorLog.Info("[切换台] 图层同步失败：" + ex.Message); }
        }

        /// <summary>
        /// 自检钩子（只在设了环境变量时动作，正常使用零影响）：
        ///   CUTLANCE_SYNTH_REC=1      窗口一起来就开始录制（不用点按钮）
        ///   CUTLANCE_SYNTH_EXIT=秒    到点自动关窗（跑完自己收尾，不留进程）
        /// </summary>
        private void StartSelfTestHooks()
        {
            if (Environment.GetEnvironmentVariable("CUTLANCE_SYNTH_REC") == "1")
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { ToggleRecord(); } catch (Exception ex) { ErrorLog.Info("[自检] 自动开录失败：" + ex.Message); }
                }));
            }

            string? exit = Environment.GetEnvironmentVariable("CUTLANCE_SYNTH_EXIT");
            if (double.TryParse(exit, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double sec) && sec > 0)
            {
                var tm = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(sec)
                };
                tm.Tick += (s, e) =>
                {
                    tm.Stop();
                    ErrorLog.Info($"[自检] 到 {sec:0.#} 秒，自动关窗。统计：{StatText.Text.Replace('\n', ' ')}");
                    Close();
                };
                tm.Start();
            }
        }

        /// <summary>
        /// 切换台接管声音之前，先把老链路那几路音轨静音。
        /// 不静音的话：主界面正在播节目时打开切换台，同一路素材会被解两遍、
        /// 两套声音叠在声卡里（听起来像回声）。老链路自己会按需要重设这几个轨道，不用我们恢复。
        /// </summary>
        private static void SilenceLegacyProgram(ProdAudio.AudioBus aud)
        {
            foreach (var key in new[] { "program", "preview", "transition" })
            {
                try
                {
                    var t = aud.FindTrack(key);
                    if (t == null) continue;
                    t.Mute = true;
                    t.Volume = 0f;
                }
                catch { }
            }
        }

        /// <summary>
        /// 探一下源分辨率。只对本地文件做（Parse 很快）；
        /// 摄像头/网络流/桌面不去 Parse —— 那等于先拉一遍流，启动要等好几秒。
        /// </summary>
        private static (int w, int h) ProbeSize(Media media, InputSource src)
        {
            if (src.Type != SourceType.File) return (0, 0);
            try
            {
                var task = media.Parse(MediaParseOptions.ParseLocal);
                try { task?.Wait(4000); } catch { }
                foreach (var t in media.Tracks)
                    if (t.TrackType == TrackType.Video)
                        return ((int)t.Data.Video.Width, (int)t.Data.Video.Height);
            }
            catch { }
            return (0, 0);
        }

        private void BuildTiles()
        {
            InputBar.Children.Clear();
            _tiles.Clear();

            for (int i = 0; i < _feeds.Count; i++)
            {
                int idx = i;
                var feed = _feeds[i];

                var num = new TextBlock
                {
                    Text = (i + 1).ToString(),
                    FontSize = 15,
                    FontWeight = FontWeights.Bold,
                    Foreground = (Brush)FindResource("Brush.Text")
                };
                var name = new TextBlock
                {
                    Text = ShortName(feed.Name),
                    FontSize = 11,
                    Foreground = (Brush)FindResource("Brush.Text.Secondary"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 96
                };

                var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                sp.Children.Add(num);
                sp.Children.Add(name);

                var b = new Button
                {
                    Content = sp,
                    Width = 112,
                    Height = 56,
                    Margin = new Thickness(0, 0, 6, 0),
                    Padding = new Thickness(8, 4, 8, 4),
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Tag = idx
                };
                // 无障碍 / 自动化：按钮里放的是 StackPanel，UIA 的 Name 会是空串，
                // 显式给一个固定名字，脚本才能按名字点到它（铁律 34）
                System.Windows.Automation.AutomationProperties.SetName(b, "输入" + (i + 1));
                b.Click += (s, e) => SetPreview(idx);
                ToolTipService.SetToolTip(b,
                    $"{feed.Name}\n{feed.Width}×{feed.Height}\n点一下送到预监，双击直接切出");

                b.MouseDoubleClick += (s, e) => { SetPreview(idx); DoCut(); };

                InputBar.Children.Add(b);
                _tiles.Add(b);
            }
        }

        private static string ShortName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "输入";
            return name.Length > 14 ? name.Substring(0, 14) + "…" : name;
        }

        // ───────────────────────── 合成循环 ─────────────────────────

        private void Loop()
        {
            var sw = Stopwatch.StartNew();
            long next = 0;
            // 每次进循环按当前帧率算节拍 —— 换帧率会重启这个线程，不会用到旧的间隔
            double frameTicks = Stopwatch.Frequency / Math.Max(1.0, _fps);

            while (_running)
            {
                try
                {
                    long now = sw.ElapsedTicks;
                    if (now >= next)
                    {
                        next += (long)frameTicks;
                        // 落后太多就重新对齐，别为了补帧把 CPU 打满
                        if (now - next > Stopwatch.Frequency / 4) next = now + (long)frameTicks;

                        _sw?.Tick();
                        RunPlan();
                        SyncCompositor();
                        _comp?.Compose();
                        Interlocked.Increment(ref _frames);

                        // 成品帧一帧分发多路：录制/推流（ffmpeg 管道）在 swap 锁内拷走，
                        // 全屏窗走自己的 STA 线程，预览走 UI 线程 —— 三条互不拖累。
                        // ⚠ 漏掉 _bus.Push 的后果：ffmpeg 起来了、文件建了，但一帧都没收到，
                        //   文件里只剩 48 字节的容器头（第一版就是这么假"录制成功"的）。
                        if (_session != null)
                        {
                            var sess = _session;
                            _comp.WithFront((ptr, stride) => sess.Push(ptr, stride));
                        }

                        // 全屏输出走自己的 STA 线程窗口，只吃成品帧，跟主界面卡不卡无关
                        if (_fs != null && _comp != null)
                        {
                            var fs = _fs;
                            _comp.WithFront((ptr, stride) => { try { fs.Present(ptr, stride); } catch { } });
                        }

                        if (Interlocked.Exchange(ref _uploadPending, 1) == 0)
                        {
                            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render,
                                new Action(UploadPreview));
                        }
                    }
                    else
                    {
                        // 1ms 粒度轮询：Thread.Sleep(1) 在 Windows 上默认 15.6ms，
                        // 直接把 30fps 打成 23.5fps（P1 的坑）
                        Thread.Sleep(1);
                    }
                }
                catch (Exception ex)
                {
                    ErrorLog.Info("[切换台] 合成异常：" + ex.Message);
                    Thread.Sleep(50);
                }
            }
        }

        private void UploadPreview()
        {
            Interlocked.Exchange(ref _uploadPending, 0);
            if (_comp == null || _pgmBmp == null) return;

            try
            {
                _comp.WithFront((ptr, stride) =>
                {
                    _pgmBmp.Lock();
                    _pgmBmp.WritePixels(new Int32Rect(0, 0, CanvasW, CanvasH), ptr, stride * CanvasH, stride);
                    _pgmBmp.AddDirtyRect(new Int32Rect(0, 0, CanvasW, CanvasH));
                    _pgmBmp.Unlock();
                });
            }
            catch { }

            // 预监小窗 10fps 就够，别跟节目窗抢 UI 线程
            long now = Environment.TickCount64;
            if (now - _pvwTick < 100) { UpdateStats(); return; }
            _pvwTick = now;

            try
            {
                int pi = _sw?.Preview ?? 0;
                if (pi < 0 || pi >= _feeds.Count) return;
                var f = _feeds[pi];
                var p = f.Acquire();
                if (p == IntPtr.Zero || _pvwBuf == null || _pvwBmp == null) return;

                Thumbnail.Render(p, f.Width, f.Height, f.Stride, _pvwBuf, PvwW, PvwH);
                _pvwBmp.Lock();
                _pvwBmp.WritePixels(new Int32Rect(0, 0, PvwW, PvwH), _pvwBuf, PvwW * 4, 0);
                _pvwBmp.AddDirtyRect(new Int32Rect(0, 0, PvwW, PvwH));
                _pvwBmp.Unlock();
            }
            catch { }

            UpdateStats();
        }

        private void UpdateStats()
        {
            if (_comp == null) return;
            double secs = Math.Max(0.001, (DateTime.Now - _startedAt).TotalSeconds);
            double fps = Interlocked.Read(ref _frames) / secs;
            long drop = 0;
            foreach (var f in _feeds) drop += f.Dropped;

            int audible = 0;
            foreach (var t in _prodTracks.Values)
                if (!t.Mute && t.Volume > 0f) audible++;

            StatText.Text = $"{fps:0.0} / {_fps:0.##} fps · 合成 {_comp.LastComposeMs:0.0} ms\n" +
                            $"{_feeds.Count} 路 · 解码丢帧 {drop}\n" +
                            (audible > 0 ? $"● 出声 {audible} 路（跟随节目）" : "○ 静音");

            var parts = new List<string>();
            if (_session != null) parts.Add("● 录制中");
            if (_fs != null) parts.Add("全屏输出中");
            OutText.Text = parts.Count > 0 ? string.Join(" · ", parts) : "仅监看（未输出）";
        }

        // ───────────────────────── 切换 ─────────────────────────

        private void SetPreview(int idx)
        {
            if (_sw == null || idx < 0 || idx >= _feeds.Count) return;
            _sw.SetPreview(idx);
            RefreshBadges();
        }

        /// <summary>
        /// 每帧把切换台状态喂给合成器。
        /// 转场期间 Program 还停在旧的那一路（Switcher 到转场结束才翻），
        /// 所以「正在淡入的那一路」要单独作为 PgmB 送进去，Mix 就是它的不透明度。
        /// </summary>
        private void SyncCompositor()
        {
            if (_sw == null || _comp == null) return;
            int p = _sw.Program;
            if (p < 0 || p >= _feeds.Count) return;

            // 转场是合成线程在推进的，Program 会在某一帧突然翻到目标上；
            // 界面上的胜负标记只有 UI 线程能改，所以发现变化就异步刷一次
            // （不加这句的现象：画面已经切过去了，但「节目」红标还留在上一个按钮上）。
            if (p != _lastPgm)
            {
                _lastPgm = p;
                Dispatcher.BeginInvoke(new Action(RefreshBadges));
            }

            _comp.Pgm = _feeds[p];
            if (_sw.InTransition)
            {
                int to = _sw.To;
                _comp.PgmB = (to >= 0 && to < _feeds.Count) ? _feeds[to] : null;
                _comp.Mix = (float)_sw.Mix;
            }
            else
            {
                _comp.PgmB = null;
                _comp.Mix = 1f;
            }
        }

        private void DoCut()
        {
            if (_sw == null) return;
            _sw.Cut(_sw.Preview);
            SyncProgram();
            RefreshBadges();
        }

        private void DoAuto()
        {
            if (_sw == null) return;
            _sw.Auto(_sw.Preview, CurrentDur());
            RefreshBadges();
        }

        /// <summary>当前选中的转场时长（只能在 UI 线程调用）。</summary>
        private double CurrentDur()
        {
            int di = DurCombo.SelectedIndex;
            return (di >= 0 && di < _durItems.Count) ? _durItems[di] : 1.0;
        }

        private void SyncProgram()
        {
            if (_sw == null || _comp == null) return;
            int pi = _sw.Program;
            if (pi >= 0 && pi < _feeds.Count) _comp.Pgm = _feeds[pi];
            // 转场中由 Switcher 推进 Mix，合成器按比例混 A/B
            PgmName.Text = pi >= 0 && pi < _feeds.Count ? _feeds[pi].Name : "";
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            if (_sw == null) return;

            if (e.Key == Key.Enter || e.Key == Key.Space) { DoCut(); e.Handled = true; return; }
            if (e.Key == Key.A) { DoAuto(); e.Handled = true; return; }

            int n = -1;
            if (e.Key >= Key.D1 && e.Key <= Key.D9) n = e.Key - Key.D1;
            if (e.Key >= Key.NumPad1 && e.Key <= Key.NumPad9) n = e.Key - Key.NumPad1;
            if (n >= 0 && n < _feeds.Count) { SetPreview(n); e.Handled = true; }
        }

        /// <summary>把「谁是节目、谁是预监」画到开关上（红=节目，绿=预监）。</summary>
        private void RefreshBadges()
        {
            if (_sw == null) return;
            int pgm = _sw.Program, pvw = _sw.Preview;

            for (int i = 0; i < _tiles.Count; i++)
            {
                var b = _tiles[i];
                if (i == pgm)
                {
                    b.Background = (Brush)FindResource("Brush.Pgm");
                    b.Foreground = Brushes.White;
                }
                else if (i == pvw)
                {
                    b.Background = (Brush)FindResource("Brush.Pvw");
                    b.Foreground = Brushes.White;
                }
                else
                {
                    b.ClearValue(Button.BackgroundProperty);
                    b.ClearValue(Button.ForegroundProperty);
                }
            }

            if (pgm >= 0 && pgm < _feeds.Count) PgmName.Text = _feeds[pgm].Name;
            if (pvw >= 0 && pvw < _feeds.Count) PvwName.Text = _feeds[pvw].Name;
        }

        // ───────────────────────── 输出 ─────────────────────────

        private void ToggleRecord()
        {
            // 停止
            if (_session != null)
            {
                var s0 = _session;
                _session = null;          // 先置空：合成线程看到的立刻是"没在录"
                try { s0.Stop(); } catch { }
                try { s0.Dispose(); } catch { }
                RecBtn.Content = "● 录制";
                _status = "录制已停止 → " + _recDir;
                StatText.Text = _status;
                UpdateStats();
                return;
            }

            // 开录
            var svc = Services.StreamService.Instance;
            var opt = new Services.RecordSessionOptions
            {
                Width = CanvasW,
                Height = CanvasH,
                Fps = _fps,
                BitrateKbps = Math.Max(1000, svc.BitrateKbps),
                SegmentHours = _segHour,
                Dir = svc.RecordFolder,                       // 与产品同一个目录
                Prefix = "切换台",
                Extension = (svc.Container ?? "mkv").Trim().ToLowerInvariant() == "mp4" ? ".mp4" : ".mkv",
                WithAudio = true
            };

            var sess = Services.RecordSession.Start(opt, out string? err);
            if (sess == null)
            {
                // 导播中不弹模态框（会挡住播出画面）—— 只写状态栏 + 日志
                _status = "录制启动失败：" + (err ?? "未知原因");
                StatText.Text = _status;
                ErrorLog.Info("[切换台] " + _status);
                UpdateStats();
                return;
            }

            _session = sess;
            _recDir = sess.Dir;
            RecBtn.Content = "■ 停止";
            _status = $"录制中（{_fps:0.##}fps · {DescribeSeg()} · 带音频）→ {sess.Dir}";
            StatText.Text = _status;
            UpdateStats();
        }

        private string DescribeSeg()
        {
            if (_segHour <= 0) return "不分段";
            double sec = _segHour * 3600;
            if (sec < 60) return $"{sec:0} 秒一段";
            if (_segHour < 1) return $"{_segHour * 60:0} 分钟一段";
            return $"{_segHour:0.##} 小时一段";
        }

        private void ToggleFullscreen()
        {
            if (_fs != null)
            {
                try { _fs.Dispose(); } catch { }
                _fs = null;
                UpdateStats();
                return;
            }

            try
            {
                // 默认打到第二块屏（monitorIndex=1）；只有一块屏时会 Open 失败并给提示
                int mon = 1;
                _fs = new SynthPoc.Output.FullscreenOutput(CanvasW, CanvasH, mon, false, null);
                if (!_fs.Open())
                {
                    _fs = null;
                    MessageBox.Show(this, "没有第二块屏幕可输出。接上大屏后再试。", "Cutlance",
                                    MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                _fs = null;
                MessageBox.Show(this, "全屏输出失败：" + ex.Message);
            }
            UpdateStats();
        }

        private static string FfmpegPath()
        {
            // 跟主程序一致：优先程序目录旁的 ffmpeg，其次环境里的
            string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
            return File.Exists(local) ? local : @"C:\FFMPEG\bin\ffmpeg.exe";
        }

        // ───────────────────────── 收尾 ─────────────────────────

        private void Shutdown()
        {
            _running = false;
            try { _thread?.Join(1500); } catch { }

            try { _syncTimer?.Stop(); } catch { }
            try { _ovRender?.Dispose(); } catch { }
            foreach (var f in _layerFeeds.Values) { try { f.Dispose(); } catch { } }
            _layerFeeds.Clear();

            // 录制会话收尾（2026-10-25 合并）：Stop 里已按「编码器 → 看门狗 → 总线 → 音频管道」
            // 的正确顺序收干净，这里不用再单独 Dispose 那几个对象。
            try { _session?.Stop(); } catch { }
            try { (_session as IDisposable)?.Dispose(); } catch { }
            _session = null;
            try { _fs?.Dispose(); } catch { }

            // 音频：先断开 ffmpeg 取流通道，再把本窗口的轨道全部静音并移除
            try { ProdAudio.AudioBus.Instance.StopPipe(); } catch { }
            foreach (var kv in _prodTracks)
            {
                try { kv.Value.Mute = true; kv.Value.Volume = 0f; } catch { }
                try { ProdAudio.AudioBus.Instance.RemoveTrack(kv.Value.Key); } catch { }
            }
            _prodTracks.Clear();

            foreach (var f in _feeds) { try { f.Dispose(); } catch { } }
            _feeds.Clear();

            try { _comp?.Dispose(); } catch { }
        }
    }
}
