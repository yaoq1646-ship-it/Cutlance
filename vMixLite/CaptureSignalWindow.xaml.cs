using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Cutlance.Models;
using Cutlance.Services;

namespace Cutlance
{
    /// <summary>
    /// 「输入信号」面板（模块E）。
    ///
    /// 流程（老板 2026-10-14 定的）：
    ///   ① 先展示「输入信号的格式」→ 看清楚 → ② 定采集设置（音频 / 链路）→ 点「添加为输入」。
    ///
    /// 这里要分清两个概念，它们是两码事：
    ///   · <b>当前输入信号</b>（面板上那条青色行）：把这根线现在真正送进来的东西读出来。
    ///     本机实测：能力表最高 2560×1600@30，实际进来的是 1920×1080@60 逐行。
    ///   · <b>支持的信号格式</b>（右侧列表）：设备声称能接受哪些档位 —— 是一张菜单。
    /// 采不到画面的经典根因就是「照菜单点了道后厨没有的菜」，所以格式表首行是「自动」。
    ///
    /// 设备与格式均来自 ffmpeg 的真实枚举（见 CaptureDeviceService），
    /// 不是 WMI 猜的、也不是写死的预设表 —— 采集卡才列得出来、格式才准。
    ///
    /// 音频自动匹配：采集卡的 HDMI 内嵌音频在 DirectShow 里是**独立设备**，
    /// 且命名带视频设备名（本机实测：视频 `GATHER CAM1` ↔ 音频 `数字音频接口 (GATHER CAM1)`）。
    /// 所以按「音频名包含视频名」自动配对，用户仍可在下拉里改。
    /// </summary>
    public partial class CaptureSignalWindow : Window
    {
        private bool _scanning;
        private List<CaptureDevice> _allDevices = new();

        /// <summary>用于丢弃过期的探测结果（用户连续换设备时，旧结果不许覆盖新的）。</summary>
        private int _probeToken;

        /// <summary>当前选中的视频设备。</summary>
        public CaptureDevice? SelectedDevice { get; private set; }

        /// <summary>当前选中的信号格式（决定分辨率 / 帧率）。</summary>
        public CaptureFormat? SelectedFormat { get; private set; }

        /// <summary>当前展示的格式列表（保留给后续扩展用）。</summary>
        public List<CaptureFormat> SelectedFormats { get; private set; } = new();

        /// <summary>选定的采集链路。</summary>
        public CameraLink SelectedLink { get; private set; } = CameraLink.Ffmpeg;

        /// <summary>选定的去隔行档位（模块E B-2）。</summary>
        public DeinterlaceMode SelectedDeinterlace { get; private set; } = DeinterlaceMode.Auto;

        /// <summary>选定的延迟档（模块E B-2）：true = 低延迟。</summary>
        public bool SelectedLowLatency { get; private set; }

        /// <summary>
        /// 实测到的当前信号是否隔行（1080i）——写进输入，去隔行「自动」档靠它判定。
        /// **三态**：true = 隔行 ／ false = 逐行 ／ null = 判不出来（还没探测、或画面无内容）。
        /// </summary>
        public bool? SignalInterlaced { get; private set; }

        /// <summary>选定的音频项（null = 未选；NoAudio = 不采集音频）。</summary>
        public AudioChoice? SelectedAudio { get; private set; }

        /// <summary>
        /// 多卡管理：本次要添加的设备（Ctrl/Shift 多选时可一次加多路采集卡）。
        /// 始终只含视频设备 —— 音频设备是给视频设备配的，不能单独当一路输入。
        /// </summary>
        public List<CaptureDevice> SelectedDevices { get; private set; } = new();

        /// <summary>
        /// 已经在输入列表里的设备名（主窗口打开面板前传入）。
        /// 采集设备独占，同一路加两次第二次必然打不开 → 列表里要标「已在输入列表中」。
        /// </summary>
        public List<string> UsedDeviceNames { get; set; } = new();

        /// <summary>最近一次实测到的当前信号（估算带宽时用，选「自动」时没有固定档位可算）。</summary>
        private CaptureSignalInfo? _lastSignal;

        /// <summary>
        /// 每台设备实测到的「是否隔行」缓存（模块E B-2）。
        /// 存在的理由：Ctrl 多选批量添加时，面板右侧只展示「最后选中那一路」的实测结果，
        /// 其余几路如果不各自记住，添加时就会把 1080i 机位当逐行处理（画面拉丝）。
        /// </summary>
        private readonly Dictionary<string, bool?> _interlacedByDevice =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 该设备实测到的信号是否隔行。
        /// ⚠ 返回 null 表示「这一路没实测过」—— 与 false（实测逐行）是两码事，
        ///   调用方必须区别对待，不能把「不知道」当成「不是」。
        /// </summary>
        public bool? GetInterlaced(string deviceName)
            => _interlacedByDevice.TryGetValue(deviceName ?? "", out var v) ? v : null;

        // ==================== 下拉项 ====================

        /// <summary>音频下拉项。</summary>
        public sealed class AudioChoice
        {
            public string Label { get; set; } = "";
            public string DeviceName { get; set; } = "";   // 空 = 系统默认
            public bool NoAudio { get; set; }              // 「不采集音频」
            public bool AutoMatched { get; set; }          // 自动配对到的那一路
            public override string ToString() => Label;
        }

        private sealed class LinkChoice
        {
            public string Label { get; set; } = "";
            public CameraLink Value { get; set; }
            public override string ToString() => Label;
        }

        private sealed class DeintChoice
        {
            public string Label { get; set; } = "";
            public DeinterlaceMode Value { get; set; }
            public override string ToString() => Label;
        }

        private sealed class LatencyChoice
        {
            public string Label { get; set; } = "";
            public bool Low { get; set; }
            public override string ToString() => Label;
        }

        private sealed class BgTypeChoice
        {
            public string Label { get; set; } = "";
            public ChromaBackground Value { get; set; }
            public override string ToString() => Label;
        }

        // ==================== 模块C A-2：抠像换背景的选定值 ====================

        /// <summary>是否启用抠像。</summary>
        public bool SelectedChromaEnabled { get; private set; }

        /// <summary>键色（0xRRGGBB）。下拉/手输的最终值以输入框为准。</summary>
        public string SelectedChromaKeyColor { get; private set; } = "0x00B140";

        /// <summary>相似度（归一化平方色差，实测推荐 0.05）。</summary>
        public double SelectedChromaSimilarity { get; private set; } = 0.05;

        /// <summary>混合度（实测推荐 0.02）。</summary>
        public double SelectedChromaBlend { get; private set; } = 0.02;

        /// <summary>背景类型（纯色 / 图片 / 视频）。</summary>
        public ChromaBackground SelectedBgType { get; private set; } = ChromaBackground.Color;

        /// <summary>纯色背景色。</summary>
        public string SelectedBgColor { get; private set; } = "0x1E1E1E";

        /// <summary>图片 / 视频背景路径。</summary>
        public string SelectedBgPath { get; private set; } = "";

        /// <summary>合成画布尺寸（背景铺满这个尺寸，前景缩放到它）。</summary>
        public int SelectedBgWidth { get; private set; } = 1920;
        public int SelectedBgHeight { get; private set; } = 1080;

        private static readonly Brush SignalOk = new SolidColorBrush(Color.FromRgb(0x6B, 0xE0, 0x8A));
        private static readonly Brush SignalWarn = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x65));
        private static readonly Brush SignalIdle = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));

        public CaptureSignalWindow()
        {
            InitializeComponent();
            WorkFrame.Attach(this);   // 统一：置顶 + 抓屏排除 + 底部「保存/关闭」+ Esc/Ctrl+S

            // 900 DIP 是按大屏定的；1080p@150% 这类逻辑可用高只有 ~670 DIP，
            // 不收敛就会顶出屏幕、底部按钮拿不回来（内容由 XAML 里的 ScrollViewer 滚动）
            WindowFit.FitToWorkArea(this);

            BuildCombos();
            Loaded += async (_, _) => await ScanAsync();
        }

        /// <summary>填充三个下拉：采集链路 / 去隔行 / 延迟档。</summary>
        private void BuildCombos()
        {
            LinkCombo.Items.Add(new LinkChoice
            {
                Value = CameraLink.VlcDirect,
                Label = "VLC 直连（延迟最低 ~100ms，高码率信号可能丢帧）"
            });
            LinkCombo.Items.Add(new LinkChoice
            {
                Value = CameraLink.Ffmpeg,
                Label = "ffmpeg 预处理（稳定不丢帧，+约 460ms 延迟）"
            });
            LinkCombo.SelectedIndex = 1;

            // 去隔行：默认「自动」—— 只有实测到隔行信号才启用（1080i 才需要，1080p 开了反而糊）
            DeintCombo.Items.Add(new DeintChoice
            {
                Value = DeinterlaceMode.Auto,
                Label = "自动（检测到隔行才启用）"
            });
            DeintCombo.Items.Add(new DeintChoice
            {
                Value = DeinterlaceMode.On,
                Label = "强制开启（信号确定是 1080i）"
            });
            DeintCombo.Items.Add(new DeintChoice
            {
                Value = DeinterlaceMode.Off,
                Label = "关闭（不用）"
            });
            DeintCombo.SelectedIndex = 0;

            LatencyCombo.Items.Add(new LatencyChoice
            {
                Low = false,
                Label = "标准（抗抖动优先）"
            });
            LatencyCombo.Items.Add(new LatencyChoice
            {
                Low = true,
                Label = "低延迟（约再省 300ms，抖动时更易丢帧）"
            });
            LatencyCombo.SelectedIndex = 0;

            BuildChromaCombos();
        }

        /// <summary>抠像区的下拉（背景类型）与初始状态。</summary>
        private void BuildChromaCombos()
        {
            BgTypeCombo.Items.Add(new BgTypeChoice { Value = ChromaBackground.Color, Label = "纯色背景" });
            BgTypeCombo.Items.Add(new BgTypeChoice { Value = ChromaBackground.Image, Label = "图片背景（jpg / png / bmp）" });
            BgTypeCombo.Items.Add(new BgTypeChoice { Value = ChromaBackground.Video, Label = "视频背景（mp4 / mkv，自动循环）" });
            BgTypeCombo.SelectedIndex = 0;

            SimText.Text = SimSlider.Value.ToString("0.###");
            BlendText.Text = BlendSlider.Value.ToString("0.###");
            ChromaPanel.IsEnabled = false;
            UpdateChromaHint();
        }

        // ==================== 扫描设备 ====================

        private async System.Threading.Tasks.Task ScanAsync()
        {
            if (_scanning) return;
            _scanning = true;
            try
            {
                DeviceList.ItemsSource = null;
                FormatList.ItemsSource = null;
                AudioCombo.ItemsSource = null;
                SelectedFormats = new();
                SelectedFormat = null;
                SelectedDevice = null;
                SignalInterlaced = null;   // 三态：重置为「尚未判定」，不能记成 false（那等于谎称逐行）
                BtnAdd.IsEnabled = false;
                SetSignal("（选中设备后自动检测）", SignalIdle);
                UpdateDeintHint();
                ShowFormatHint("请先在左侧选择一个采集设备…");
                SetStatus("正在扫描采集设备…（首次扫描需 1~3 秒）", false);

                var res = await CaptureDeviceService.EnumerateDevicesAsync();

                if (res.Devices.Count == 0)
                {
                    _allDevices = new();
                    var why = string.IsNullOrWhiteSpace(res.Error) ? "未检测到任何采集设备" : res.Error;
                    SetStatus("⚠ " + why, true);
                    ShowFormatHint(why + "\n\n排查建议：\n" +
                                   "· HDMI 采集卡是否插好、驱动是否已安装\n" +
                                   "· 设备是否被其他程序独占（OBS / vMix / 会议软件 / 相机应用）\n" +
                                   "· 设备管理器中是否被禁用或标记异常");
                    return;
                }

                // 标注「已在输入列表中」——采集设备独占，重复添加只会得到一个打不开的输入
                foreach (var d in res.Devices)
                    d.IsUsed = UsedDeviceNames.Contains(d.Name);

                _allDevices = res.Devices;
                DeviceList.ItemsSource = res.Devices;

                int video = res.Devices.FindAll(d => d.IsVideo).Count;
                int card = res.Devices.FindAll(d => d.Kind == CaptureDeviceKind.CaptureCard).Count;
                int virt = res.Devices.FindAll(d => d.Kind == CaptureDeviceKind.VirtualDevice).Count;

                SetStatus($"检测到 {res.Devices.Count} 个设备：视频 {video} 个" +
                          $"（采集卡 {card} · 虚拟设备 {virt}）、音频 {res.Devices.Count - video} 个" +
                          "　—　选中左侧设备即可查看它的当前信号与支持格式");

                // 默认选中第一个采集卡（有的话），否则第一个摄像头，少一次点击
                CaptureDevice? pick = res.Devices.Find(d => d.IsVideo && d.Kind == CaptureDeviceKind.CaptureCard)
                                   ?? res.Devices.Find(d => d.IsVideo && d.Kind == CaptureDeviceKind.Camera)
                                   ?? res.Devices.Find(d => d.IsVideo);
                if (pick != null) DeviceList.SelectedItem = pick;
            }
            catch (Exception ex)
            {
                SetStatus("⚠ 扫描失败：" + ex.Message, true);
                ShowFormatHint("扫描失败：" + ex.Message);
            }
            finally { _scanning = false; }
        }

        // ==================== 选中设备 → 测信号 + 列格式 + 配音频 ====================

        private async void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 多卡管理：SelectedDevices = 全部选中的视频设备；
            // 右侧的信号/格式面板只展示「最后选中」的那一个（一次只能看一路的细节）。
            SelectedDevices = DeviceList.SelectedItems
                                  .OfType<CaptureDevice>()
                                  .Where(d => d.IsVideo)
                                  .ToList();

            var dev = SelectedDevices.Count > 0
                ? SelectedDevices[SelectedDevices.Count - 1]
                : DeviceList.SelectedItem as CaptureDevice;
            if (dev == null) return;

            SelectedDevice = dev.IsVideo ? dev : null;
            _lastSignal = null;
            SignalInterlaced = null;
            UpdateDeintHint();

            if (!dev.IsVideo)
            {
                _probeToken++;                      // 让在飞的探测结果作废
                SelectedFormats = new();
                SelectedFormat = null;
                FormatList.ItemsSource = null;
                AudioCombo.ItemsSource = null;
                BtnAdd.IsEnabled = false;
                SetSignal("（这是音频设备，没有画面信号）", SignalIdle);
                SettingHint.Text = "这是音频设备。请在上面的设备列表里选一个「视频」设备（采集卡 / 摄像头），"
                                 + "它会自动把配套的音频设备匹配到下拉里。";
                ShowFormatHint($"「{dev.Name}」是音频设备，没有视频信号格式。\n\n" +
                               "它会在选中配套的视频设备时被自动匹配（本机命名规律：\n「数字音频接口 (采集卡名)」）。");
                SetStatus($"已选：{dev.Name}（音频设备，需配合视频设备使用）");
                return;
            }

            // 音频自动匹配 + 链路默认值
            BuildAudioCombo(dev);
            SetDefaultLink(dev);

            FormatList.ItemsSource = null;
            ShowFormatHint($"正在读取「{dev.Name}」的信号格式…");
            SetStatus($"正在读取「{dev.Name}」的信号格式…", false);

            var token = ++_probeToken;

            try
            {
                var fmts = await CaptureDeviceService.EnumerateFormatsAsync(dev.Name);

                // 期间用户又点了别的设备 → 丢弃这次结果，避免串台。
                // ⚠ 废弃判定只能用 token —— 多选（Ctrl+click）时 WPF 的 SelectedItem
                //   仍停在最初那一项（实测：SelectedItems 变了但 SelectedItem 不动），
                //   拿它比对必然误判成「已切换设备」，右侧永远停在「正在读取」。
                if (token != _probeToken || !DeviceList.Items.Contains(dev))
                {
                    ShowFormatHint("已切换设备，请重新选择查看。");
                    return;
                }

                if (fmts.Count == 0)
                {
                    SelectedFormats = new();
                    SelectedFormat = null;
                    var why = string.IsNullOrWhiteSpace(CaptureDeviceService.LastError)
                        ? "设备没有返回任何信号格式。可能原因：\n" +
                          "· 设备正被其他程序独占（OBS / vMix / 会议软件 / 相机应用）\n" +
                          "· HDMI / SDI 线未接或信号源未开机（采集卡常见）\n" +
                          "· 驱动异常"
                        : CaptureDeviceService.LastError;
                    ShowFormatHint(why);
                    SetStatus("⚠ " + (why.Split('\n')[0]), true);
                    return;
                }

                // 首行固定是「自动」：不强制分辨率/帧率，按当前输入信号协商。
                // 这是默认值 —— 照能力表挑最高档反而可能采不到画面（见 CaptureFormat.IsAuto 注释）。
                var list = new List<CaptureFormat> { new CaptureFormat { IsAuto = true } };
                list.AddRange(fmts);

                SelectedFormats = list;
                FormatList.ItemsSource = list;
                ShowFormatList();
                FormatList.SelectedIndex = 0;       // 默认「自动」

                var fpsMax = 0.0;
                foreach (var f in fmts) if (f.FpsMax > fpsMax) fpsMax = f.FpsMax;

                SetStatus($"「{dev.Name}」支持 {fmts.Count} 种格式（最高 {fmts[0].ResolutionText}" +
                          $"、{fpsMax:0.##} fps）　—　默认已选「自动」，选中一行后点「添加为输入」");
            }
            catch (Exception ex)
            {
                if (token != _probeToken) return;
                ShowFormatHint("读取格式失败：" + ex.Message);
                SetStatus("⚠ 读取格式失败：" + ex.Message, true);
            }
            finally
            {
                // 「当前输入信号」探测放在格式枚举【之后】串行执行。
                // ⚠ 不能并行：DirectShow 设备独占，两个 ffmpeg 同时打开同一设备会互锁 ——
                //   实测 CAM2 卡死在「正在读取」就是它俩抢设备（命令行分别跑都正常，一起跑就锁）。
                if (token == _probeToken) _ = ProbeSignalAsync(dev, token);
            }
        }

        /// <summary>实际打开设备采样 1 秒，报出「这根线现在送进来的是什么」。</summary>
        private async System.Threading.Tasks.Task ProbeSignalAsync(CaptureDevice dev, int token)
        {
            SetSignal("检测中…", SignalIdle);
            BtnProbe.IsEnabled = false;
            try
            {
                var info = await CaptureDeviceService.ProbeLiveSignalAsync(dev.Name);
                if (token != _probeToken) return;   // 早已换设备 → 丢弃

                if (info.HasSignal)
                {
                    _lastSignal = info;      // 留着给带宽估算用（「自动」档没有固定档位可算）
                    SignalInterlaced = info.Interlaced;   // 去隔行「自动」档的判据
                    _interlacedByDevice[dev.Name] = info.Interlaced;   // 多选批量添加时逐路取用
                    UpdateDeintHint();
                    UpdateBandwidth();
                    SetSignal(info.Summary, SignalOk);
                    // 场序三态各给一句人话：隔行要说清「已自动启用」；判不出来必须**明说并给出路**，
                    // 不能沉默 —— 沉默正是老 Bug 把 1080i 当逐行时用户能看到的全部信息。
                    var ilaceNote = info.Interlaced switch
                    {
                        true => "　（隔行信号，去隔行默认已自动启用）",
                        false => "",
                        // 「判不出来」有两种成因，给用户的下一步动作完全不同，必须分开说：
                        //   R=0 → 画面没内容（纯色/未接信号）；0.95~1.15 → 判据模糊。
                        // 混成一句「落在灰区」会让用户对着黑屏去调去隔行，白费功夫。
                        _ => info.InterlaceRatio <= 0
                             ? "　（画面是纯色或没有内容，无从判断场序 —— 请先确认这根线真的有信号进来）"
                             : $"　（场序未能判定：判据 R={info.InterlaceRatio:0.00} 落在灰区。"
                               + "若画面有横向拉丝，请把去隔行手动切到「开」）"
                    };
                    SetStatus($"当前输入信号：{info.Summary}　—　格式表默认「自动」即可，不必照最高档强制" + ilaceNote);
                }
                else
                {
                    SignalInterlaced = null;   // 三态：重置为「尚未判定」，不能记成 false（那等于谎称逐行）
                    // 没测到信号 = 无从判断隔行与否 → 不留缓存，添加时按「未实测」如实提示
                    _interlacedByDevice.Remove(dev.Name);
                    UpdateDeintHint();
                    SetSignal(info.Summary, SignalWarn);
                }
            }
            finally
            {
                if (token == _probeToken) BtnProbe.IsEnabled = true;
            }
        }

        private async void Probe_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedDevice == null)
            {
                SetStatus("⚠ 请先在左侧选择一个视频设备", true);
                return;
            }
            await ProbeSignalAsync(SelectedDevice, ++_probeToken);
        }

        /// <summary>
        /// 按选中的视频设备重建音频下拉，并自动匹配配套音频。
        /// 匹配规则：音频设备名**包含**视频设备名（本机实测 `数字音频接口 (GATHER CAM1)` ⊃ `GATHER CAM1`）。
        /// </summary>
        private void BuildAudioCombo(CaptureDevice videoDev)
        {
            var items = new List<AudioChoice>
            {
                new AudioChoice { Label = "（不采集音频）", NoAudio = true },
                new AudioChoice { Label = "（系统默认音频设备）", DeviceName = "" }
            };

            AudioChoice? matched = null;
            foreach (var d in _allDevices)
            {
                if (d.IsVideo) continue;
                if (d.Kind != CaptureDeviceKind.Audio) continue;
                if (string.IsNullOrWhiteSpace(d.Name)) continue;

                bool hit = !string.IsNullOrWhiteSpace(videoDev.Name)
                           && d.Name.IndexOf(videoDev.Name, StringComparison.OrdinalIgnoreCase) >= 0;

                var choice = new AudioChoice
                {
                    DeviceName = d.Name,
                    AutoMatched = hit,
                    Label = hit ? $"★ {d.Name}　（与设备同名，自动匹配）" : d.Name
                };
                items.Add(choice);
                if (hit && matched == null) matched = choice;
            }

            // 重建时先摘掉事件，避免 SelectionChanged 在填充过程中乱跑
            AudioCombo.SelectionChanged -= AudioCombo_SelectionChanged;
            AudioCombo.ItemsSource = items;
            if (matched != null) AudioCombo.SelectedItem = matched;
            else AudioCombo.SelectedItem = items[1];      // 无匹配 → 系统默认
            AudioCombo.SelectionChanged += AudioCombo_SelectionChanged;

            SelectedAudio = AudioCombo.SelectedItem as AudioChoice;

            SettingHint.Text = matched != null
                ? $"已自动匹配到该设备的配套音频：「{matched.DeviceName}」。如不需要采集声音，可在下拉里选「（不采集音频）」。"
                : $"未找到与「{videoDev.Name}」同名的音频设备（普通摄像头常见）。"
                + "若摄像机是走 HDMI 进采集卡，音频会随采集卡一起进来（届时会显示为「数字音频接口 (采集卡名)」）。";
        }

        /// <summary>采集卡默认走 ffmpeg 预处理（1080p60 无压缩信号需控缓冲）；摄像头默认 VLC 直连（低延迟）。</summary>
        private void SetDefaultLink(CaptureDevice dev)
        {
            var want = dev.Kind == CaptureDeviceKind.CaptureCard ? CameraLink.Ffmpeg : CameraLink.VlcDirect;
            foreach (var o in LinkCombo.Items)
                if (o is LinkChoice lc && lc.Value == want) { LinkCombo.SelectedItem = o; break; }
            if (LinkCombo.SelectedItem is LinkChoice cur) SelectedLink = cur.Value;
        }

        // ==================== 下拉 / 格式选中 ====================

        private void AudioCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SelectedAudio = AudioCombo.SelectedItem as AudioChoice;
        }

        private void LinkCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LinkCombo.SelectedItem is LinkChoice lc) SelectedLink = lc.Value;
            UpdateDeintHint();      // 直连链路下去隔行只是尽力而为 → 提示可能不生效
        }

        private void DeintCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DeintCombo.SelectedItem is DeintChoice d) SelectedDeinterlace = d.Value;
            UpdateDeintHint();
        }

        private void LatencyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LatencyCombo.SelectedItem is LatencyChoice l) SelectedLowLatency = l.Low;
        }

        // ==================== 模块C A-2：抠像换背景 ====================

        private void Chroma_Toggled(object sender, RoutedEventArgs e)
        {
            if (ChromaPanel == null) return;
            bool on = ChkChroma.IsChecked == true;
            ChromaPanel.IsEnabled = on;

            // 抠像是 ffmpeg 预处理层做的，VLC 直连链路下**不可能**生效 ——
            // 与其让用户配完发现没反应，不如勾上的瞬间就把链路切过去，并说明为什么。
            if (on && SelectedLink == CameraLink.VlcDirect)
            {
                foreach (var o in LinkCombo.Items)
                    if (o is LinkChoice lc && lc.Value == CameraLink.Ffmpeg) { LinkCombo.SelectedItem = o; break; }
                if (LinkCombo.SelectedItem is LinkChoice cur) SelectedLink = cur.Value;
                SetStatus("抠像需要 ffmpeg 预处理链路，已把「采集链路」自动切到 ffmpeg 预处理");
            }
            UpdateChromaHint();
        }

        /// <summary>键色预设按钮 → 填进手输框（输入框始终是唯一真相源，避免下拉与框不同步）。</summary>
        private void KeySwatch_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string hex) KeyColorHex.Text = hex;
        }

        private void BgSwatch_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string hex) BgColorHex.Text = hex;
        }

        private void BgType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (BgTypeCombo.SelectedItem is not BgTypeChoice c) return;
            bool color = c.Value == ChromaBackground.Color;
            BgColorPanel.Visibility = color ? Visibility.Visible : Visibility.Collapsed;
            BgFilePanel.Visibility = color ? Visibility.Collapsed : Visibility.Visible;
            UpdateChromaHint();
        }

        private void Sim_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (SimText != null) SimText.Text = e.NewValue.ToString("0.###");
        }

        private void Blend_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (BlendText != null) BlendText.Text = e.NewValue.ToString("0.###");
        }

        /// <summary>背景文件选择：按当前背景类型过滤扩展名。</summary>
        private void BgBrowse_Click(object sender, RoutedEventArgs e)
        {
            bool video = BgTypeCombo.SelectedItem is BgTypeChoice c && c.Value == ChromaBackground.Video;
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = video ? "选择背景视频" : "选择背景图片",
                Filter = video
                    ? "视频文件|*.mp4;*.mkv;*.mov;*.avi;*.wmv;*.webm|所有文件|*.*"
                    : "图片文件|*.jpg;*.jpeg;*.png;*.bmp;*.webp|所有文件|*.*",
                CheckFileExists = true
            };
            if (dlg.ShowDialog(this) == true) BgPathBox.Text = dlg.FileName;
            UpdateChromaHint();
        }

        /// <summary>
        /// 抠像提示行：把「启用状态 + 背景素材是否齐备 + 链路是否合适」合成一句话。
        /// 抠像是"配了但没反应"最容易发生的功能（链路错、背景没选、参数太狠），所以每个坑都要说出来。
        /// </summary>
        private void UpdateChromaHint()
        {
            if (ChromaHint == null) return;

            if (ChkChroma.IsChecked != true)
            {
                ChromaHint.Foreground = new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x6E));
                ChromaHint.Text = "抠像必须走 ffmpeg 预处理链路。勾选后会先把绿幕/蓝幕抠掉，"
                                + "再把背景合成进这一路画面（预览框与输出都能看到效果）。";
                return;
            }

            bool isFile = BgTypeCombo.SelectedItem is BgTypeChoice c && c.Value != ChromaBackground.Color;
            if (isFile && string.IsNullOrWhiteSpace(BgPathBox.Text))
            {
                ChromaHint.Foreground = SignalWarn;
                ChromaHint.Text = "⚠ 还没选背景文件 —— 现在添加的话抠像不会生效（会按原始画面播出）。"
                                + "点「浏览…」选一张图片或一段视频。";
                return;
            }

            ChromaHint.Foreground = SignalOk;
            ChromaHint.Text = "✓ 抠像已启用：添加后可先看预览框；绿边残留就把「相似度」调大一点，"
                            + "主体边缘被抠掉就调小；边缘发虚（半透明）就把「混合度」调小。";
        }

        /// <summary>
        /// 去隔行提示行：把「实测结果 + 当前档位 + 链路」三者合成一句话。
        /// 隔行信号 + VLC 直连是唯一一个「可能不生效」的组合，必须明说。
        /// **三态**：判不出来时尤其不能沉默 —— 要说清「程序不替你猜」并给出路（手动切「开」）。
        /// </summary>
        private void UpdateDeintHint()
        {
            if (DeintHint == null) return;

            var gray = new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x6E));

            if (SignalInterlaced == false)
            {
                DeintHint.Foreground = gray;
                DeintHint.Text = "实测为逐行信号 → 去隔行保持关闭。对逐行信号做去隔行反而会糊掉垂直细节，"
                               + "所以程序只在该路确实隔行时才自动启用。";
                return;
            }

            if (SignalInterlaced == null)
            {
                DeintHint.Foreground = gray;
                DeintHint.Text = "场序未能判定（画面可能是纯色、静止无内容，或判据落在灰区）——"
                               + "程序不会替你猜：自动档下保持不启用，免得给逐行信号白糊一层。"
                               + "若画面里有横向拉丝（运动物体边缘出现梳齿），请把档位手动切到「强制开启」。";
                return;
            }

            // 到这里 SignalInterlaced == true
            if (SelectedLink == CameraLink.VlcDirect)
            {
                DeintHint.Foreground = SignalWarn;
                DeintHint.Text = "⚠ 实测到隔行信号（1080i），但当前选的是「VLC 直连」——"
                               + "直连链路下发去隔行只能寄望 VLC 自己，未必生效。建议把「采集链路」改成"
                               + "「ffmpeg 预处理」，去隔行能真正做进画面。";
                return;
            }

            DeintHint.Foreground = SignalOk;
            DeintHint.Text = "✓ 实测到隔行信号（1080i）："
                           + (SelectedDeinterlace == DeinterlaceMode.Off
                                ? "但当前档位是「关闭」—— 画面会拉丝，建议改回「自动」或「强制开启」。"
                                : "已自动启用去隔行（yadif），画面平滑不拉丝。");
        }

        private void FormatList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SelectedFormat = FormatList.SelectedItem as CaptureFormat;
            UpdateAddButton();
            UpdateBandwidth();
            if (SelectedFormat != null)
                SetStatus($"已选格式：{SelectedFormat.Summary}　—　点右下「添加为输入」加入输入列表");
        }

        /// <summary>按钮文案随选中路数变：选了 3 路就写「添加 3 路为输入」。</summary>
        private void UpdateAddButton()
        {
            int n = SelectedDevices.Count;
            BtnAdd.IsEnabled = SelectedFormat != null && n > 0;
            BtnAdd.Content = n > 1 ? $"添加 {n} 路为输入" : "添加为输入";
        }

        // ==================== 多卡管理：USB 带宽估算 ====================

        /// <summary>每像素字节数（无压缩格式按位深算，压缩格式按经验值粗估）。</summary>
        private static double BytesPerPixel(string pixelFormat)
        {
            var s = (pixelFormat ?? "").ToLowerInvariant();
            if (s.Contains("mjpg") || s.Contains("mjpeg") || s.Contains("h264") || s.Contains("hevc"))
                return 0.5;                                   // 压缩格式，粗估
            if (s.Contains("nv12") || s.Contains("420")) return 1.5;
            if (s.Contains("rgb24") || s.Contains("bgr24")) return 3;
            return 2;                                         // yuyv422 / uyvy / yuy2 等无压缩 16bpp 家族
        }

        /// <summary>
        /// 估算选中路数要吃掉的 USB 带宽。
        /// 实测教训：1080p60 yuyv422 单路就 ≈2 Gbps，USB3 实际可用约 3 Gbps，
        /// 四路卡全开 1080p60 是带不动的（会掉设备/降帧），所以这里提前算给用户看。
        /// </summary>
        private void UpdateBandwidth()
        {
            BandwidthText.Visibility = Visibility.Collapsed;
            if (SelectedFormat == null) return;

            int n = Math.Max(1, SelectedDevices.Count);
            double w, h, fps;
            string pix;

            if (SelectedFormat.IsAuto)
            {
                if (_lastSignal == null || !_lastSignal.HasSignal) return;   // 还没测出来就不猜
                w = _lastSignal.Width; h = _lastSignal.Height;
                fps = _lastSignal.Fps; pix = _lastSignal.PixelFormat;
            }
            else
            {
                w = SelectedFormat.Width; h = SelectedFormat.Height;
                fps = SelectedFormat.FpsMax > 0 ? SelectedFormat.FpsMax : 30;
                pix = SelectedFormat.PixelFormat;
            }

            if (w <= 0 || h <= 0) return;

            double one = w * h * (fps > 0 ? fps : 30) * BytesPerPixel(pix) * 8 / 1e9;   // Gbps
            double total = one * n;

            var txt = $"带宽估算：单路 ≈ {one:0.##} Gbps"
                      + (n > 1 ? $"　·　{n} 路合计 ≈ {total:0.##} Gbps" : "");

            if (total > 2.8)
            {
                txt += "　⚠ 超过 USB3 实际可用带宽（≈3 Gbps），同开可能掉设备或降帧，建议降分辨率/帧率";
                BandwidthText.Foreground = SignalWarn;
            }
            else if (total > 1.8)
            {
                txt += "　（已接近 USB3 带宽上限，多路同开需留意）";
                BandwidthText.Foreground = SignalIdle;
            }
            else
            {
                BandwidthText.Foreground = SignalIdle;
            }

            BandwidthText.Text = txt;
            BandwidthText.Visibility = Visibility.Visible;
        }

        // ==================== 按钮 ====================

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            // 多选为空时回落到单选（SelectedItem）
            if (SelectedDevices.Count == 0 && SelectedDevice != null)
                SelectedDevices = new List<CaptureDevice> { SelectedDevice };

            // 音频设备不能单独当一路输入（它是给视频设备配的）
            SelectedDevices = SelectedDevices.Where(d => d.IsVideo).ToList();

            if (SelectedDevices.Count == 0)
            {
                SetStatus("⚠ 请先在左侧选择至少一个视频设备（采集卡 / 摄像头）", true);
                return;
            }
            if (SelectedFormat == null)
            {
                SetStatus("⚠ 请先在上方格式表里选中一行（决定分辨率与帧率）", true);
                return;
            }

            var dup = SelectedDevices.Where(d => d.IsUsed).Select(d => d.Name).ToList();
            if (dup.Count > 0)
            {
                SetStatus($"⚠ 已跳过 {dup.Count} 路重复设备：" + string.Join("、", dup)
                          + "（采集设备独占，同一路只能有一个输入）", true);
                SelectedDevices = SelectedDevices.Where(d => !d.IsUsed).ToList();
                if (SelectedDevices.Count == 0) return;
            }

            if (LinkCombo.SelectedItem is LinkChoice lc) SelectedLink = lc.Value;
            if (AudioCombo.SelectedItem is AudioChoice ac) SelectedAudio = ac;
            if (DeintCombo.SelectedItem is DeintChoice dc) SelectedDeinterlace = dc.Value;
            if (LatencyCombo.SelectedItem is LatencyChoice lt) SelectedLowLatency = lt.Low;

            // 模块C A-2：抠像参数
            SelectedChromaEnabled = ChkChroma.IsChecked == true;
            SelectedChromaKeyColor = (KeyColorHex.Text ?? "").Trim();
            SelectedChromaSimilarity = SimSlider.Value;
            SelectedChromaBlend = BlendSlider.Value;
            if (BgTypeCombo.SelectedItem is BgTypeChoice bc) SelectedBgType = bc.Value;
            SelectedBgColor = (BgColorHex.Text ?? "").Trim();
            SelectedBgPath = (BgPathBox.Text ?? "").Trim();
            SelectedBgWidth = ParseIntOr(BgWBox.Text, 1920);
            SelectedBgHeight = ParseIntOr(BgHBox.Text, 1080);

            // 「启用了抠像但背景是空的」是最典型的"配了没反应" —— 当场拦住，别让它静默降级播出。
            if (SelectedChromaEnabled && SelectedBgType != ChromaBackground.Color
                && string.IsNullOrWhiteSpace(SelectedBgPath))
            {
                SetStatus("⚠ 已启用抠像，但背景文件还没选 —— 请点「浏览…」选一张图片或一段视频，"
                          + "或把「背景类型」改回「纯色」", true);
                return;
            }

            DialogResult = true;
        }

        private static int ParseIntOr(string? s, int fallback)
            => int.TryParse((s ?? "").Trim(), out var v) && v > 0 ? v : fallback;

        private async void Rescan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        // ==================== 界面辅助 ====================

        /// <summary>信号行：文字与状态灯同色同步（灯是给一眼扫的人看的，文字是给要细节的人看的）。</summary>
        private void SetSignal(string text, Brush brush)
        {
            SignalText.Text = text;
            SignalText.Foreground = brush;
            SignalDot.Fill = brush;
        }

        private void SetStatus(string text, bool warn = false)
        {
            StatusLine.Text = text;
            StatusLine.Foreground = warn
                ? System.Windows.Media.Brushes.OrangeRed
                : new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x6E, 0x6E, 0x6E));
        }

        private void ShowFormatHint(string text)
        {
            FormatHint.Text = text;
            FormatHint.Visibility = Visibility.Visible;
            FormatList.Visibility = Visibility.Collapsed;
        }

        private void ShowFormatList()
        {
            FormatHint.Visibility = Visibility.Collapsed;
            FormatList.Visibility = Visibility.Visible;
        }
    }
}
