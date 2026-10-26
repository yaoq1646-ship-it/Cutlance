using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using Cutlance.Services;

namespace Cutlance
{
    /// <summary>「输出设置」对话框：配置并启停 RTMP 推流 / 本地录制。</summary>
    public partial class OutputWindow : Window
    {
        private readonly StreamService _svc = StreamService.Instance;
        private readonly VideoEngine _engine = VideoEngine.Instance;

        public event EventHandler? StartRequested;
        public event EventHandler? StopRequested;

        // 每秒心跳：刷新「已运行」计时（StatusChanged 仅在启停时触发一次，不足以走秒）
        private readonly DispatcherTimer _heartbeat = new();

        public OutputWindow()
        {
            InitializeComponent();
            WorkFrame.Attach(this);   // 统一：置顶 + 抓屏排除 + 底部「保存/关闭」+ Esc/Ctrl+S

            // 790 DIP 在小逻辑屏上会超出 → 收敛到可用区（根节点已是 ScrollViewer，内容可滚）
            WindowFit.FitToWorkArea(this);

            UrlBox.Text = _svc.RtmpUrl;
            FolderBox.Text = _svc.RecordFolder;
            StreamChk.IsChecked = _svc.EnableStream;
            RecChk.IsChecked = _svc.EnableRecord;
            SelectSegment(_svc.RecordSegmentSeconds);
            MicChk.IsChecked = _svc.IncludeMic;
            HwDecodeChk.IsChecked = _engine.HardwareDecoding;
            SelectEncoder(_svc.Encoder);
            SelectContainer(_svc.Container);
            SelectFps(_svc.Fps);
            SelectBitrate(_svc.BitrateKbps);

            // 输出分辨率（横屏/竖屏）已整体搬到主界面「视图 → 输出分辨率（重启生效）」
            // 子菜单（2026-10-20 老板要求）：本窗不再提供入口，避免同一个功能两处可改。

            // HDMI 输出目标显示器列表（默认优先选一块非主屏，即 HDMI 外接屏）
            foreach (var m in DisplayService.GetMonitors())
                HdmiMonitorCombo.Items.Add(new ComboBoxItem { Content = m.ToString(), Tag = m.Index });
            if (HdmiMonitorCombo.Items.Count > 0)
                HdmiMonitorCombo.SelectedIndex = HdmiMonitorCombo.Items.Count > 1 ? 1 : 0;

            // 输出引擎（模块8）：FFmpeg 默认（支持四路混音与美颜美体），可切回 LibVLC 兼容模式
            SelectEngine(_svc.OutputEngine);
            RefreshEngineHint();

            // NDI 输出（模块F）
            NdiChk.IsChecked = _svc.EnableNdi;
            NdiNameBox.Text = _svc.NdiSourceName;
            SelectNdiFps(_svc.NdiFps);

            _svc.StatusChanged += OnStatus;
            _svc.Error += OnError;
            Closed += (_, _) =>
            {
                _svc.StatusChanged -= OnStatus;
                _svc.Error -= OnError;
                _heartbeat.Stop();
                // 老板 10-18 要求：这个界面一关，HDMI 输出就跟着停，绝不留下没人管的副屏全屏画面。
                // 所以无条件发停止请求（原来只在 HdmiRunning==true 时才发，而打开窗口时该标志可能是旧值，
                //  导致"界面关了、输出还在，必须再点一次按钮才关"）。MainWindow 侧没在输出时会直接返回。
                try { HdmiStopRequested?.Invoke(this, EventArgs.Empty); } catch { }
            };
            _heartbeat.Interval = TimeSpan.FromSeconds(1);
            _heartbeat.Tick += (_, _) => RefreshUi();
            _heartbeat.Start();
            RefreshUi();
            TrySelfTestUi();
            TrySaveContentShot();
            TryBitrateMenuSelfTest();
        }

        /// <summary>
        /// 自检钩子 `CUTLANCE_BITRATE_MENU=1`：把「参考 ▾」真点开，把菜单项逐条打进日志。
        ///
        /// 为什么不在外部用 UIA 点：`--offscreen` 下 UIA 拿不到 WPF 内部控件、也点不动按钮
        /// （本项目的老坑，见 MEMORY）。而且菜单是 Popup，PrintWindow 抓回来是白的 —— 
        /// 读 Items 反而是最可靠的判据：项数、顺序、每一档的数值与说明都能核。
        /// </summary>
        private void TryBitrateMenuSelfTest()
        {
            if (Environment.GetEnvironmentVariable("CUTLANCE_BITRATE_MENU") != "1") return;

            Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(600);
                try
                {
                    BitrateRefBtn_Click(BitrateRefBtn, new RoutedEventArgs());
                    await Task.Delay(500);

                    if (_bitrateMenu == null) { ErrorLog.Info("[自检] 参考菜单：没弹出来"); return; }

                    var tips = new List<string>();
                    foreach (var o in _bitrateMenu.Items)
                    {
                        if (o is MenuItem mi && mi.Tag is int kbps) tips.Add($"{kbps}｜{mi.Header}");
                        else if (o is Separator) tips.Add("———");
                        else if (o is MenuItem m2) tips.Add("(说明)" + m2.Header);
                    }
                    ErrorLog.Info($"[自检] 参考菜单 共 {_bitrateMenu.Items.Count} 项，已弹出={_bitrateMenu.IsOpen}");
                    foreach (var t in tips) ErrorLog.Info("[自检] 码率档 " + t);

                    _bitrateMenu.IsOpen = false;
                }
                catch (Exception ex)
                {
                    ErrorLog.Info("[自检] 参考菜单测试失败：" + ex.Message);
                }
            });
        }

        /// <summary>
        /// 自检钩子：`CUTLANCE_OUTCFG_PNG=&lt;绝对路径&gt;` → 把窗口**内容**直接渲染成 PNG。
        ///
        /// 为什么不外部 PrintWindow 截：工作框都做了抓屏排除（WDA_EXCLUDEFROMCAPTURE），
        /// 而且自动化时窗口摆在屏幕外，DWM 不给屏幕外窗口合成内容 ——
        /// 两条加一起，PrintWindow 抓回来永远是纯白（2026-10-21 实测两次都是白图）。
        /// 这里用 RenderTargetBitmap 读**自己的可视树**，跟窗口在不在屏幕上、有没有被排除都无关。
        /// 代价是不含标题栏 —— 验证版式够用。
        /// </summary>
        private void TrySaveContentShot()
        {
            var path = Environment.GetEnvironmentVariable("CUTLANCE_OUTCFG_PNG");
            if (string.IsNullOrWhiteSpace(path)) return;

            Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(700);   // 等布局稳定（字体度量、换行都要算完）
                try
                {
                    if (Content is not FrameworkElement root) return;
                    root.UpdateLayout();
                    int w = (int)Math.Ceiling(root.ActualWidth);
                    int h = (int)Math.Ceiling(root.ActualHeight);
                    if (w <= 0 || h <= 0) { ErrorLog.Info("[自检] 截图失败：内容尺寸为 0"); return; }

                    var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                        w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    rtb.Render(root);

                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                    using (var fs = File.Create(path)) enc.Save(fs);
                    ErrorLog.Info($"[自检] 窗口内容已存成 PNG：{path}（{w}×{h}）");
                }
                catch (Exception ex)
                {
                    ErrorLog.Info("[自检] 窗口截图失败：" + ex.Message);
                }
            });
        }

        /// <summary>
        /// 自检钩子：`CUTLANCE_OUTCFG_UI="25,9000,mp4"` → 把三个下拉拨到指定档，
        /// 再走**点「开始推流」时那条真实写回路径**（ApplyUiToService），把结果打进日志。
        ///
        /// 为什么需要：`--offscreen` 下 UIA 拿不到控件、点不动下拉，外部脚本没法验证
        /// 「界面上选的」到底有没有写进服务。这个钩子把最后一跳补上，判据看日志里的三个值。
        /// </summary>
        private void TrySelfTestUi()
        {
            var spec = Environment.GetEnvironmentVariable("CUTLANCE_OUTCFG_UI");
            if (string.IsNullOrWhiteSpace(spec)) return;
            try
            {
                var parts = spec.Split(',');
                if (parts.Length >= 1 && double.TryParse(parts[0].Trim(),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var fps))
                    SelectFps(fps);
                if (parts.Length >= 2)
                {
                    // 码率改成手填之后（2026-10-21 第 2 条），这里也接受「非法原文」：
                    // 传 "abc" / "99999" 时会原样塞进输入框，再走 SelectedBitrate() 的校验，
                    // 用来验证「填错不会静默改成别的档位、也不会把非法值下发给 ffmpeg」。
                    var raw = parts[1].Trim();
                    if (int.TryParse(raw, out var br)) SelectBitrate(br);
                    else BitrateBox.Text = raw;
                }
                if (parts.Length >= 3) SelectContainer(parts[2].Trim());

                string C(object? o) => o is ComboBoxItem ci ? ci.Content?.ToString() ?? "?" : "(空)";
                var uiPick = $"帧率[{C(FpsCombo.SelectedItem)}] 码率[{BitrateBox.Text}] "
                           + $"容器[{C(ContainerCombo.SelectedItem)}]";
                // 注意：故意**不**调 StartRequested —— 这里只验「界面 → 服务」这一跳
                ApplyUiToService();
                // 末尾再读一次输入框：手填值经过校验后会被回写（99999 → 50000、abc → 用原值），
                // 这一项专门用来证明「界面显示的 = 实际下发的」。
                ErrorLog.Info($"[自检] 输出设置窗写回：界面={uiPick} → 服务 Fps={_svc.Fps:0.##} "
                            + $"码率={_svc.BitrateKbps} 容器={_svc.Container} SynthFps={_svc.SynthFps:0.##} "
                            + $"| 回写后界面码率=[{BitrateBox.Text}]");
            }
            catch (Exception ex)
            {
                ErrorLog.Info("[自检] 输出设置窗写回失败：" + ex.Message);
            }
        }

        private void ToggleBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!_svc.IsRunning)
            {
                ApplyUiToService();
                StartRequested?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                StopRequested?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// 把本窗所有控件的当前值写进 StreamService（点「开始推流/录制」时调）。
        /// 抽成独立方法是为了能被自检钩子复用 —— 验证「界面上选的」和「真正下发给 ffmpeg 的」是同一个值。
        /// </summary>
        private void ApplyUiToService()
        {
            _svc.RtmpUrl = UrlBox.Text.Trim();
            _svc.RecordFolder = FolderBox.Text.Trim();
            _svc.EnableStream = StreamChk.IsChecked == true;
            _svc.EnableRecord = RecChk.IsChecked == true;
            _svc.RecordSegmentSeconds = SelectedSegment();
            _svc.IncludeMic = MicChk.IsChecked == true;
            _engine.SetHardwareDecoding(HwDecodeChk.IsChecked == true);
            _svc.Encoder = SelectedEncoder();
            // 输出帧率 / 码率 / 容器（2026-10-21）：开始前统一落进服务，保证下发给 ffmpeg 的就是界面上选的
            _svc.Fps = SelectedFps();
            _svc.SynthFps = _svc.Fps;      // 切换台帧率与输出帧率保持同一档（避免两套值互相覆盖）
            // 码率是手填的（2026-10-21 第 2 条）：校验后的**真值**要写回输入框。
            // 否则会出现「框里写 99999、实际下发 50000」或「框里写 abc、实际用 6000」——
            // 老板最反感的就是这种界面与实际不一致的静默改档。
            int br = SelectedBitrate();
            _svc.BitrateKbps = br;
            if ((BitrateBox.Text ?? "").Trim() != br.ToString()) BitrateBox.Text = br.ToString();
            _svc.Container = SelectedContainer();
            // NDI 输出（模块F）
            _svc.EnableNdi = NdiChk.IsChecked == true;
            _svc.NdiSourceName = NdiNameBox.Text.Trim();
            _svc.NdiFps = SelectedNdiFps();
        }

        /// <summary>选择录制文件保存目录（.NET 8 的 WPF 文件夹选择对话框）。</summary>
        private void BrowseBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new OpenFolderDialog
                {
                    Title = "选择录制文件保存目录",
                    Multiselect = false
                };
                var cur = FolderBox.Text?.Trim();
                if (!string.IsNullOrWhiteSpace(cur) && Directory.Exists(cur))
                    dlg.InitialDirectory = cur;
                if (dlg.ShowDialog(this) == true)
                {
                    FolderBox.Text = dlg.FolderName;
                    _svc.RecordFolder = dlg.FolderName;
                    StatusLbl.Text = "录制目录已设为：" + dlg.FolderName;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("选择目录失败：\n" + ex.Message, "Cutlance",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>HDMI 输出目标显示器索引（MainWindow 读取）。</summary>
        public int HdmiMonitorIndex => HdmiMonitorCombo.SelectedIndex < 0 ? 0 : HdmiMonitorCombo.SelectedIndex;

        /// <summary>
        /// 版式探针：两列实际高度 + 滚动区可视高。给离屏自检用（UIA 在 --offscreen 下取不到元素），
        /// 用来机器判定「左列下方那块空白」到底有多少、改完是否真的收掉了。
        /// </summary>
        public string DescribeLayout()
        {
            // 两列 StackPanel 在 Grid 里会被拉满行高 → ActualHeight 恒相等，量不出「下面的空白」。
            // 真正要看的是**最后一个子元素的下缘**：它到窗口底还有多少，就是那块空白。
            double lBottom = LastBottom(ColLeft);
            double rBottom = LastBottom(ColRight);
            double scrollable = -1, viewport = -1;
            try
            {
                // WorkFrame 会在内容外面再套一层 DockPanel（带「保存/关闭」底栏），
                // 所以 ScrollViewer 不一定就是 Content —— 往下找。
                var sv = FindScroll(this);
                if (sv != null)
                {
                    scrollable = sv.ScrollableHeight;
                    viewport = sv.ViewportHeight;
                }
            }
            catch { }
            return $"左列内容底={lBottom:0.#} 右列内容底={rBottom:0.#} 差={Math.Abs(lBottom - rBottom):0.#} " +
                   $"可滚距离={scrollable:0.#} 可视高={viewport:0.#} 窗高={ActualHeight:0.#}" +
                   $" 麦克风勾[可用={MicChk?.IsEnabled} 勾选={MicChk?.IsChecked} 说明={(MicHint?.Text ?? "")}]" +
                   " | 明细 " + Dump(ColLeft, "L") + " " + Dump(ColRight, "R");
        }

        /// <summary>把一列里每个子元素的高度打成一行，便于按块重排（离屏自检用）。</summary>
        private static string Dump(Panel? p, string tag)
        {
            if (p == null) return tag + "=null";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < p.Children.Count; i++)
            {
                if (p.Children[i] is not FrameworkElement fe) continue;
                string name = fe.Name;
                if (string.IsNullOrEmpty(name))
                {
                    name = fe switch
                    {
                        GroupBox g => "组:" + (g.Header?.ToString() ?? "?"),
                        CheckBox c => "勾:" + (c.Content as string ?? (c.Content as TextBlock)?.Text ?? "?"),
                        TextBlock t => "文:" + (t.Text?.Length > 12 ? t.Text.Substring(0, 12) : t.Text),
                        StackPanel sp => "横排(" + sp.Children.Count + ")",
                        _ => fe.GetType().Name
                    };
                }
                sb.Append($"{tag}{i}[{name}]={fe.ActualHeight:0.#}  ");
            }
            return sb.ToString();
        }

        /// <summary>在可视树里找第一个 ScrollViewer（WorkFrame 套了 DockPanel，Content 未必就是它）。</summary>
        private static ScrollViewer? FindScroll(DependencyObject root)
        {
            if (root is ScrollViewer sv) return sv;
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var hit = FindScroll(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
                if (hit != null) return hit;
            }
            return null;
        }

        /// <summary>某列最后一个子元素的下缘（相对本窗口，DIP）。取不到返回 -1。</summary>
        private double LastBottom(Panel? p)        {
            try
            {
                if (p == null || p.Children.Count == 0) return -1;
                var last = p.Children[p.Children.Count - 1] as FrameworkElement;
                if (last == null) return -1;
                return last.TranslatePoint(new Point(0, last.ActualHeight), this).Y;
            }
            catch { return -1; }
        }

        /// <summary>HDMI 输出是否正在运行（由 MainWindow 回填，驱动按钮显示）。</summary>
        public bool HdmiRunning { get; private set; }

        public event EventHandler? HdmiStopRequested;

        // 老板 2026-10-18：界面上的开关按钮已撤（统一到「流」菜单勾选），
        // 原来的 HdmiBtn_Click 与 HdmiStartRequested 一并删除 —— 开启入口只在「流」菜单。
        // HdmiStopRequested 保留：关闭本窗口时仍要无条件停掉 HDMI，避免留下没人管的副屏画面。

        /// <summary>MainWindow 处理完起停后回填状态，刷新状态文字。</summary>
        public void SetHdmiRunning(bool running, string? note = null)
        {
            HdmiRunning = running;
            // 老板 2026-10-18：开关入口已收到「流」菜单，这里只保留状态显示
            HdmiStatus.Text = note ?? (running ? "● 运行中：正在镜像播出框到所选显示器" : "未开启");
            HdmiStatus.Foreground = new System.Windows.Media.SolidColorBrush(running
                ? System.Windows.Media.Colors.LightGreen
                : System.Windows.Media.Color.FromRgb(0x9E, 0x9E, 0x9E));
        }

        /// <summary>输出引擎下拉切换。</summary>
        private void EngineCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // ⚠ XAML 解析期若下拉项带 IsSelected="True"，会在这里就触发 SelectionChanged，
            // 而 EngineHint / MixerBtn / FxBtn 可能尚未创建（字段为 null）→ NullReferenceException，
            // 直接表现为「打开输出设置就报错」。这里先判空返回；XAML 里也已去掉 IsSelected。
            if (EngineHint == null || MixerBtn == null || FxBtn == null) return;

            if (EngineCombo.SelectedItem is ComboBoxItem it && it.Tag is string tag)
            {
                _svc.OutputEngine = tag;
                RefreshEngineHint();
            }
        }

        private void SelectEngine(string engine)
        {
            foreach (ComboBoxItem it in EngineCombo.Items)
                if ((it.Tag as string)?.Equals(engine, StringComparison.OrdinalIgnoreCase) == true)
                { EngineCombo.SelectedItem = it; return; }
            if (EngineCombo.Items.Count > 0) EngineCombo.SelectedIndex = 0;
        }

        private void RefreshEngineHint()
        {
            if (EngineHint == null) return;   // XAML 尚未解析完（见 EngineCombo_SelectionChanged 的说明）
            var vlc = _svc.OutputEngine.Equals("vlc", StringComparison.OrdinalIgnoreCase);
            var path = FfmpegOutput.FfmpegPath;
            if (vlc)
            {
                EngineHint.Text = "当前：LibVLC 兼容模式（抓屏 + sout）。只支持单路麦克风，不支持四路混音与画面效果。";
            }
            else if (!FfmpegOutput.IsAvailable)
            {
                EngineHint.Text = "⚠ 未找到 ffmpeg.exe（已探测 " + path + "），将自动退回 LibVLC 兼容模式。";
            }
            else
            {
                EngineHint.Text = "当前：FFmpeg 引擎 · " + path +
                                  "（合成器喂帧 + 多路音频 amix + 滤镜 + NVENC 硬编）";
            }
            if (MixerBtn != null) MixerBtn.IsEnabled = !vlc;
            if (FxBtn != null) FxBtn.IsEnabled = !vlc;

            // 「带麦克风声音」只在 LibVLC 兼容引擎里被读取（StreamService.StartVlc）；
            // FFmpeg 引擎的音频走音频混音台（最多八路、各自音量/静音），这个勾完全不参与。
            // 以前它在默认引擎下也照常勾着、点了没任何反应 = 假开关（审计 A2）→ 按引擎置灰并写明原因。
            // 说明文字放在独立 TextBlock（MicHint）里：主题的 CheckBox 模板是横向 StackPanel，
            // 内容拿到无限宽 → 塞在 Content 里的长文字不会换行，会被列边界裁掉。
            if (MicChk != null && MicHint != null)
            {
                MicChk.IsEnabled = vlc;
                if (vlc)
                {
                    MicChk.ToolTip = "LibVLC 兼容引擎额外抓一路系统默认麦克风混进推流/录制";
                    MicHint.Text = "LibVLC 引擎会另抓一路系统默认麦克风混进推流 / 录制。";
                }
                else
                {
                    MicChk.ToolTip = "FFmpeg 引擎的音频走「音频混音台」，这个勾不参与编码，因此已置灰";
                    MicHint.Text = "当前 FFmpeg 引擎不适用（已置灰）：音频请用上方「音频混音台」，最多八路、各自音量与静音。";
                }
            }
        }

        private void MixerBtn_Click(object sender, RoutedEventArgs e) => (Owner as MainWindow)?.OpenAudioMixer();

        private void FxBtn_Click(object sender, RoutedEventArgs e) => (Owner as MainWindow)?.OpenVideoFx();

        // 输出分辨率（横屏/竖屏）+「立即重启」已搬到主界面「视图 → 输出分辨率」子菜单，
        // 本窗口不再持有相关控件与逻辑（2026-10-20 老板要求：功能集中到一个入口）。

        // ===================== 分段录制 =====================

        /// <summary>
        /// 下拉切换：只在录制**没在跑**的时候才写回配置。
        /// 录到一半改段长，ffmpeg 那个进程是按启动参数走的，改了也不会生效 ——
        /// 与其让人以为"我设了却没切段"，不如直接告诉他这次录制不生效。
        /// </summary>
        private void SegCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SegCombo == null || _svc == null) return;   // XAML 解析期就会触发（铁律 13）
            if (_svc.IsRunning && RecChk?.IsChecked == true)
            {
                StatusLbl.Text = "分段设置将在下次开始录制时生效（当前录制沿用启动时的段长）";
                return;
            }
            _svc.RecordSegmentSeconds = SelectedSegment();
        }

        private double SelectedSegment()
        {
            if (SegCombo?.SelectedItem is ComboBoxItem it && it.Tag is string tag &&
                double.TryParse(tag, out var sec) && sec > 0)
                return sec;
            return 0;
        }

        private void SelectSegment(double seconds)
        {
            string tag = ((int)Math.Round(seconds)).ToString();
            foreach (ComboBoxItem it in SegCombo.Items)
                if ((it.Tag as string) == tag) { SegCombo.SelectedItem = it; return; }
            if (SegCombo.Items.Count > 0) SegCombo.SelectedIndex = 0;   // 默认「不分段」
        }

        /// <summary>勾选「播放硬件解码」即时切换（下次播放/重载生效）。</summary>
        private void HwDecodeChk_Click(object sender, RoutedEventArgs e)
        {
            _engine.SetHardwareDecoding(HwDecodeChk.IsChecked == true);
        }

        private string SelectedEncoder()
        {
            if (EncoderCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
                return tag;
            return "software";
        }

        private void SelectEncoder(string enc)
        {
            foreach (ComboBoxItem item in EncoderCombo.Items)
            {
                if ((item.Tag as string)?.Equals(enc, StringComparison.OrdinalIgnoreCase) == true)
                {
                    EncoderCombo.SelectedItem = item;
                    break;
                }
            }
        }

        // ==================== 输出帧率 / 码率 / 容器（2026-10-21） ====================

        private double SelectedFps()
        {
            if (FpsCombo.SelectedItem is ComboBoxItem it && it.Tag is string tag
                && double.TryParse(tag, System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out var v)
                && v > 0) return v;
            return _svc.Fps > 0 ? _svc.Fps : 30;
        }

        private void SelectFps(double fps)
        {
            // 找工作区里最接近的一档（29.97 这类要按数值比，不能按字符串比）
            ComboBoxItem? best = null;
            double bestDiff = double.MaxValue;
            foreach (ComboBoxItem it in FpsCombo.Items)
            {
                if (it.Tag is not string tag) continue;
                if (!double.TryParse(tag, System.Globalization.NumberStyles.Float,
                                     System.Globalization.CultureInfo.InvariantCulture, out var v)) continue;
                double diff = Math.Abs(v - fps);
                if (diff < bestDiff) { bestDiff = diff; best = it; }
            }
            if (best != null) FpsCombo.SelectedItem = best;
            else if (FpsCombo.Items.Count > 0) FpsCombo.SelectedIndex = 2;   // 兜底 30
        }

        /// <summary>
        /// 读界面上的码率 —— 老板 2026-10-21 第 2 条改成**手填**，所以这里做范围钳制：
        /// 500~50000 kbps。填了非数字/超范围不静默改成别的值，而是退回服务里上一次有效的值，
        /// 并在工具提示那行说明（以前「填 9000 被悄悄换成 8000」就是老板反感的那种静默改档）。
        /// </summary>
        private int SelectedBitrate()
        {
            var raw = (BitrateBox.Text ?? "").Trim();
            // 允许带单位写「6000k」「6M」这种
            raw = raw.Replace("kbps", "", StringComparison.OrdinalIgnoreCase)
                     .Replace("k", "", StringComparison.OrdinalIgnoreCase).Trim();
            if (raw.EndsWith("M", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(raw.Substring(0, raw.Length - 1), out var m))
                return ClampBitrate((int)Math.Round(m * 1000));

            if (int.TryParse(raw, out var v)) return ClampBitrate(v);

            int fallback = _svc.BitrateKbps > 0 ? _svc.BitrateKbps : 6000;
            return fallback;
        }

        private static int ClampBitrate(int v)
        {
            if (v < 500) return 500;
            if (v > 50000) return 50000;
            return v;
        }

        /// <summary>把码率写进输入框。注意：不再「找最接近的档位」—— 手填多少就是多少。</summary>
        private void SelectBitrate(int kbps)
        {
            if (kbps <= 0) kbps = 6000;
            BitrateBox.Text = ClampBitrate(kbps).ToString();
        }

        /// <summary>
        /// 「参考 ▾」：列出常用直播码率档位，点一下直接填进输入框。
        /// 老板 2026-10-21 第 2 条要「给出一般直播参考大小段数值」——
        /// 放右键菜单而不是新加一行说明，是因为这个窗口高度已经没有余量（两列都贴到边）。
        /// </summary>
        /// <summary>「参考 ▾」那次弹出的菜单（自检钩子要读它，所以留个字段）。</summary>
        private ContextMenu? _bitrateMenu;

        private void BitrateRefBtn_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu
            {
                PlacementTarget = BitrateRefBtn,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
            };
            _bitrateMenu = menu;

            // 档位 = 码率 + 适用场景（数值口径：H.264 直播，网页平台/局域网都按这个量级）
            var presets = new (int Kbps, string Text)[]
            {
                (2000, "2000 — 720p30 低带宽 / 网络差时的保底"),
                (3000, "3000 — 720p30 普通 或 1080p30 纯课件（画面几乎不动）"),
                (4500, "4500 — 1080p30 普通直播（人物 + 少量变化）"),
                (6000, "6000 — 1080p30 高清（推荐起点，多数平台都吃）"),
                (8000, "8000 — 1080p30 高动态 / 1080p60 普通"),
                (10000, "10000 — 1080p60 高动态，或局域网内网直播"),
                (15000, "15000 — 局域网 / 内网高画质（公网直播基本用不上）"),
            };

            foreach (var (kbps, text) in presets)
            {
                var mi = new MenuItem { Header = text, Tag = kbps };
                mi.Click += (_, _) =>
                {
                    BitrateBox.Text = kbps.ToString();
                    StatusLbl.Foreground = (TryFindResource("Brush.Text.Secondary")
                                            as System.Windows.Media.Brush) ?? StatusLbl.Foreground;
                    StatusLbl.Text = $"码率已填 {kbps} kbps —— 点「开始推流 / 录制」时生效。";
                };
                menu.Items.Add(mi);
            }

            menu.Items.Add(new Separator());
            // 这行是只读说明（IsHitTestVisible=false，避免看起来像能点）
            menu.Items.Add(new MenuItem
            {
                Header = "经验规则：码率不要超过你「上行带宽」的 70%。",
                IsHitTestVisible = false,
                IsEnabled = true
            });
            menu.Items.Add(new MenuItem
            {
                Header = "上行 10Mbps → 码率别过 7000；上行 20Mbps → 别过 14000。",
                IsHitTestVisible = false,
                IsEnabled = true
            });

            menu.IsOpen = true;
        }

        private string SelectedContainer()
        {
            if (ContainerCombo.SelectedItem is ComboBoxItem it && it.Tag is string tag)
                return tag;
            return "mkv";
        }

        private void SelectContainer(string c)
        {
            foreach (ComboBoxItem it in ContainerCombo.Items)
            {
                if ((it.Tag as string)?.Equals(c, StringComparison.OrdinalIgnoreCase) == true)
                {
                    ContainerCombo.SelectedItem = it;
                    return;
                }
            }
            if (ContainerCombo.Items.Count > 0) ContainerCombo.SelectedIndex = 0;
        }

        // ==================== NDI 输出（模块F） ====================

        private void NdiChk_Click(object sender, RoutedEventArgs e)
        {
            _svc.EnableNdi = NdiChk.IsChecked == true;
            RefreshUi();
        }

        private int SelectedNdiFps()
        {
            if (NdiFpsCombo.SelectedItem is ComboBoxItem it && it.Tag is string tag
                && int.TryParse(tag, out var v)) return v;
            return 0;   // 0 = 跟随输出帧率
        }

        private void SelectNdiFps(int fps)
        {
            var tag = fps.ToString();
            foreach (ComboBoxItem it in NdiFpsCombo.Items)
                if ((it.Tag as string) == tag) { NdiFpsCombo.SelectedItem = it; return; }
            if (NdiFpsCombo.Items.Count > 0) NdiFpsCombo.SelectedIndex = 0;
        }

        private void RefreshNdiStatus()
        {
            if (NdiStatus == null) return;
            var ndi = NdiOutputService.Instance;
            if (ndi.IsRunning)
            {
                NdiStatus.Text = $"● 广播中：{ndi.SourceName}　{ndi.Width}×{ndi.Height}@{ndi.Fps}"
                               + $"　已发 {ndi.FramesSent} 帧　接收端 {ndi.Connections}";
                NdiStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Colors.LightGreen);
            }
            else if (ndi.IsStarting)
            {
                // 首次建发送器要 12~60 秒（mDNS 注册 + 网卡枚举），这期间界面不卡但不能说"没开"
                NdiStatus.Text = "● 正在启动…（首次可能要十几秒，别重复点）";
                NdiStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
            }
            else if (_svc.EnableNdi)
            {
                NdiStatus.Text = NdiService.Instance.IsAvailable
                    ? "已勾选 —— 点下方按钮即开始广播"
                    : "⚠ NDI 运行时不可用：" + NdiService.Instance.LastError;
                NdiStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                    NdiService.Instance.IsAvailable
                        ? System.Windows.Media.Color.FromRgb(0x9E, 0x9E, 0x9E)
                        : System.Windows.Media.Colors.OrangeRed);
            }
            else
            {
                NdiStatus.Text = "未开启";
                NdiStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x9E, 0x9E, 0x9E));
            }
        }

        private void OnStatus() => Dispatcher.Invoke(RefreshUi);

        private void OnError(string msg) => Dispatcher.Invoke(() =>
        {
            StatusLbl.Text = "⚠ " + msg;
            StatusLbl.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.OrangeRed);
        });

        private void RefreshUi()
        {
            RefreshNdiStatus();

            if (_svc.IsRunning)
            {
                ToggleBtn.Content = "停止";
                var el = _svc.StartedAt.HasValue
                    ? (DateTime.Now - _svc.StartedAt.Value).ToString(@"hh\:mm\:ss") : "";
                var ndiPart = NdiOutputService.Instance.IsRunning ? " | NDI 开" : "";
                StatusLbl.Text = $"● 运行中：推流 {(_svc.EnableStream ? "开" : "关")} | " +
                                 $"录制 {(_svc.EnableRecord ? "开" : "关")}{ndiPart} | 已运行 {el}";
                StatusLbl.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.LightGreen);
            }
            else
            {
                // 只勾了 NDI 时按钮文案要跟着变，否则用户以为点下去只会推流
                var anyStream = _svc.EnableStream || _svc.EnableRecord;
                ToggleBtn.Content = anyStream ? "开始推流 / 录制" : "开始 NDI 输出";
                StatusLbl.Text = "就绪";
                StatusLbl.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Gray);
            }
        }
    }
}
