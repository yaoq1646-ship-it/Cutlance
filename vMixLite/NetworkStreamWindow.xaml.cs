using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Cutlance.Models;
using Cutlance.Services;

namespace Cutlance
{
    /// <summary>
    /// 「网络流输入」面板（模块D：RTSP / SRT / RTMP / HTTP(HLS) / RIST / UDP）。
    ///
    /// 设计取向 —— 网络流和采集卡有一个根本差别：<b>采集卡插错了能看见（设备列表里没有）,
    /// 网络流地址错了什么都看不见（就是黑屏）</b>。所以这个面板的重点不是"配置项多"，
    /// 而是**把"黑屏"背后的原因提前变成一句人话**：
    ///   · 粘进来立刻回显「认成了什么协议、主机端口、路径」→ 粘错/漏前缀马上看得见；
    ///   · 「测试连接」真连一次读回分辨率/编码/码率 → 区分「地址错 / 源没开 / 被墙挡」；
    ///   · 认不出来或不支持的协议（如 NDI）直接说明原因，而不是让你添加一个注定黑屏的输入。
    ///
    /// 批量：粘贴框支持多行，一次把整个机位的地址表粘进来，添加时逐条建输入。
    /// </summary>
    public partial class NetworkStreamWindow : Window
    {
        // ==================== 对外输出（主窗口据此构造 InputSource）====================

        /// <summary>要添加的地址（已清洗、已去重，按粘贴顺序）。</summary>
        public List<string> Urls { get; private set; } = new();

        public NetworkProtocol SelectedProtocol { get; private set; } = NetworkProtocol.Auto;
        public RtspTransport SelectedRtspTransport { get; private set; } = RtspTransport.Auto;
        public int SrtLatency { get; private set; } = 120;
        public SrtMode SelectedSrtMode { get; private set; } = SrtMode.Caller;
        public string SrtPassphrase { get; private set; } = "";
        public int TimeoutMs { get; private set; } = 5000;
        public CameraLink SelectedLink { get; private set; } = CameraLink.Ffmpeg;
        public bool SelectedLowLatency { get; private set; }
        public DeinterlaceMode SelectedDeinterlace { get; private set; } = DeinterlaceMode.Auto;

        /// <summary>主窗口已有的地址（用于标注/拦截重复添加）。</summary>
        public List<string> UsedUrls { get; set; } = new();

        /// <summary>某条地址实测到的是否隔行（去隔行「自动」档的判据）。null = 没测过。</summary>
        public bool? GetInterlaced(string url)
            => _interlacedByUrl.TryGetValue(url ?? "", out var v) ? v : null;

        // ==================== 内部状态 ====================

        private bool _ready;                              // 控件都建好了才允许联动
        private readonly DispatcherTimer _debounce;
        private CancellationTokenSource? _probeCts;
        private readonly Dictionary<string, bool> _interlacedByUrl =
            new(StringComparer.OrdinalIgnoreCase);

        // ==================== 下拉项 ====================

        private sealed class ProtoChoice
        {
            public string Label { get; set; } = "";
            public NetworkProtocol Value { get; set; }
            public override string ToString() => Label;
        }
        private sealed class RtspChoice
        {
            public string Label { get; set; } = "";
            public RtspTransport Value { get; set; }
            public override string ToString() => Label;
        }
        private sealed class SrtModeChoice
        {
            public string Label { get; set; } = "";
            public SrtMode Value { get; set; }
            public override string ToString() => Label;
        }
        private sealed class LinkChoice
        {
            public string Label { get; set; } = "";
            public CameraLink Value { get; set; }
            public override string ToString() => Label;
        }
        private sealed class LatencyChoice
        {
            public string Label { get; set; } = "";
            public bool Low { get; set; }
            public override string ToString() => Label;
        }
        private sealed class DeintChoice
        {
            public string Label { get; set; } = "";
            public DeinterlaceMode Value { get; set; }
            public override string ToString() => Label;
        }

        /// <summary>收藏项（持久化到 %APPDATA%\Cutlance\net-sources.json）。</summary>
        public sealed class Bookmark
        {
            public string Url { get; set; } = "";
            public string Display { get; set; } = "";
            public string LastUsed { get; set; } = "";
            public int UseCount { get; set; }
        }

        private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0xE0, 0x8A));
        private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x65));
        private static readonly Brush IdleBrush = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));

        public NetworkStreamWindow()
        {
            InitializeComponent();
            WorkFrame.Attach(this);   // 统一：置顶 + 抓屏排除 + 底部「保存/关闭」+ Esc/Ctrl+S

            // 820 DIP 在小逻辑屏（1080p@150% ≈ 670 DIP 可用）会顶出屏幕 → 收敛到可用区
            WindowFit.FitToWorkArea(this);

            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            _debounce.Tick += (_, _) => { _debounce.Stop(); UpdateParse(); };

            BuildCombos();
            LoadBookmarks();

            _ready = true;
            UpdateParamEnabled();
            UpdateParse();
        }

        /// <summary>填充六个下拉。</summary>
        private void BuildCombos()
        {
            CmbProtocol.Items.Add(new ProtoChoice { Value = NetworkProtocol.Auto, Label = "自动（按地址前缀判定）" });
            CmbProtocol.Items.Add(new ProtoChoice { Value = NetworkProtocol.Rtsp, Label = "RTSP（摄像机 / NVR 直播）" });
            CmbProtocol.Items.Add(new ProtoChoice { Value = NetworkProtocol.Srt, Label = "SRT（低延迟可靠传输）" });
            CmbProtocol.Items.Add(new ProtoChoice { Value = NetworkProtocol.Rtmp, Label = "RTMP（推流平台）" });
            CmbProtocol.Items.Add(new ProtoChoice { Value = NetworkProtocol.Http, Label = "HTTP / HLS（m3u8、直链）" });
            CmbProtocol.Items.Add(new ProtoChoice { Value = NetworkProtocol.Rist, Label = "RIST（广播级传输）" });
            CmbProtocol.Items.Add(new ProtoChoice { Value = NetworkProtocol.Udp, Label = "UDP / RTP（组播裸流）" });
            CmbProtocol.SelectedIndex = 0;

            CmbRtspTransport.Items.Add(new RtspChoice { Value = RtspTransport.Auto, Label = "自动（交给 ffmpeg 协商）" });
            CmbRtspTransport.Items.Add(new RtspChoice { Value = RtspTransport.Tcp, Label = "TCP（跨网段/丢包网络推荐）" });
            CmbRtspTransport.Items.Add(new RtspChoice { Value = RtspTransport.Udp, Label = "UDP（内网低延迟）" });
            CmbRtspTransport.SelectedIndex = 0;

            CmbSrtMode.Items.Add(new SrtModeChoice { Value = SrtMode.Caller, Label = "caller（主动连对方）" });
            CmbSrtMode.Items.Add(new SrtModeChoice { Value = SrtMode.Listener, Label = "listener（本机监听等推）" });
            CmbSrtMode.SelectedIndex = 0;

            CmbLink.Items.Add(new LinkChoice
            {
                Value = CameraLink.Ffmpeg,
                Label = "ffmpeg 预处理（推荐：单连接 + 断流重连 + 状态灯）"
            });
            CmbLink.Items.Add(new LinkChoice
            {
                Value = CameraLink.VlcDirect,
                Label = "VLC 直连（延迟略低，但预览+输出会占 2 路连接）"
            });
            CmbLink.SelectedIndex = 0;

            CmbLatency.Items.Add(new LatencyChoice { Low = false, Label = "标准（抗抖动优先）" });
            CmbLatency.Items.Add(new LatencyChoice { Low = true, Label = "低延迟（约再省 300ms，抖动时更易花屏）" });
            CmbLatency.SelectedIndex = 0;

            CmbDeint.Items.Add(new DeintChoice { Value = DeinterlaceMode.Auto, Label = "自动（测到隔行才启用）" });
            CmbDeint.Items.Add(new DeintChoice { Value = DeinterlaceMode.On, Label = "强制开启（确定是 1080i）" });
            CmbDeint.Items.Add(new DeintChoice { Value = DeinterlaceMode.Off, Label = "关闭（不用）" });
            CmbDeint.SelectedIndex = 0;
        }

        // ==================== 地址解析回显 ====================

        private void TxtUrl_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_ready) return;
            _debounce.Stop();
            _debounce.Start();
        }

        /// <summary>当前粘贴框里的全部地址（逐行清洗、去空行、按顺序去重）。</summary>
        private List<string> CurrentUrls()
        {
            var raw = TxtUrl.Text ?? "";
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var line in raw.Split('\n'))
            {
                var u = NetworkStreamService.Normalize(line);
                if (u.Length == 0) continue;
                if (!seen.Add(u)) continue;      // 同一批里重复的只留一条
                list.Add(u);
            }
            return list;
        }

        /// <summary>
        /// 解析第一行并回显。多行时额外说明「共几条」，因为参数与预检都是按第一条演示的 ——
        /// 不说清楚会让人以为后面几条被忽略了。
        /// </summary>
        private void UpdateParse()
        {
            var urls = CurrentUrls();
            // ⚠ 参数可用性必须【先】更新：它与协议绑定，而协议正是这一步识别出来的。
            //   踩过：只在「参数下拉变化」时更新，结果粘进一个 srt:// 后 SRT 那一行还是灰的 ——
            //   明明识别成了 SRT，却要用户先去动一下别的控件才"醒过来"。
            UpdateParamEnabled(urls);

            BtnAdd.IsEnabled = false;

            if (urls.Count == 0)
            {
                SetProto("等待输入…", IdleBrush);
                UrlDetail.Text = "粘贴地址后，这里会显示协议、主机端口、路径与是否带鉴权。";
                UrlWarn.Visibility = Visibility.Collapsed;
                BtnProbe.IsEnabled = false;
                BtnProbeAll.IsEnabled = false;
                SetStatus("就绪");
                return;
            }

            var first = urls[0];
            var info = NetworkStreamService.Parse(first);
            var manual = SelectedProtocol;

            // 用户手动指定了协议时，以手动的为准来显示（否则会与下面的下拉自相矛盾）
            var effective = manual != NetworkProtocol.Auto ? manual : info.Protocol;

            if (effective == NetworkProtocol.Ndi || effective == NetworkProtocol.Auto)
            {
                SetProto(effective == NetworkProtocol.Ndi ? "NDI（不支持）" : "未识别",
                         WarnBrush);
                UrlDetail.Text = info.Host.Length > 0
                    ? $"主机 {info.HostPort}" + (info.Path.Length > 0 ? $" · 路径 {info.Path}" : "")
                    : first;
                UrlWarn.Text = NetworkStreamService.UnsupportedReason(effective);
                UrlWarn.Visibility = Visibility.Visible;
                BtnProbe.IsEnabled = false;
                BtnProbeAll.IsEnabled = false;
                SetStatus("⚠ 该协议当前不可用，详见上方说明", true);
                return;
            }

            SetProto(NetworkStreamService.ProtocolText(effective), OkBrush);

            var parts = new List<string>();
            if (info.Host.Length > 0) parts.Add("主机 " + info.HostPort);
            if (info.Port == 0) parts.Add("未写端口（按协议默认端口）");
            if (info.Path.Length > 0) parts.Add("路径 " + info.Path);
            if (info.IsMulticast) parts.Add("组播接收（@ 前缀 = 绑定本机网卡）");
            else if (IsMulticastHost(info.Host)) parts.Add("组播地址（224~239 段）");
            if (info.HasCredentials) parts.Add("地址含用户名密码（界面不回显明文）");
            if (info.Query.Length > 0) parts.Add("已带参数 " + info.Query);
            if (urls.Count > 1) parts.Add($"本框共 {urls.Count} 条地址");

            UrlDetail.Text = string.Join(" · ", parts);
            UrlWarn.Visibility = Visibility.Collapsed;

            BtnProbe.IsEnabled = true;
            BtnProbeAll.IsEnabled = urls.Count > 0;
            RefreshAddButton();

            // 已在输入列表里的地址提前说清楚 —— 别等用户点了「添加」才发现被静默跳过
            int dup = urls.Count(u => UsedUrls.Contains(u, StringComparer.OrdinalIgnoreCase));
            var dupText = dup > 0 ? $"　·　其中 {dup} 条已在输入列表中（添加时会跳过）" : "";

            if (info.Port == 0 && effective != NetworkProtocol.Http)
                SetStatus($"识别为 {NetworkStreamService.ProtocolText(effective)}，但地址里没写端口 —— "
                          + "多数网络流需要显式端口（RTSP 常见 554/8554，SRT 常见 9000+）" + dupText, true);
            else
                SetStatus($"识别为 {NetworkStreamService.ProtocolText(effective)}"
                          + (urls.Count > 1 ? $"（共 {urls.Count} 条）" : "")
                          + dupText
                          + "　—　建议先「测试第一行地址」确认真能连上");
        }

        /// <summary>IPv4 组播地址段（224.0.0.0 ~ 239.255.255.255）。用来提示「这是组播，不是单播」。</summary>
        private static bool IsMulticastHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;
            var seg = host.Split('.');
            if (seg.Length != 4) return false;
            return int.TryParse(seg[0], out var a) && a >= 224 && a <= 239;
        }

        private void SetProto(string text, Brush brush)
        {
            ProtoText.Text = text;
            ProtoText.Foreground = brush;
            ProtoDot.Fill = brush;
        }

        /// <summary>按当前协议启用/禁用参数控件 —— 灰掉的控件也要能点开看清说明。</summary>
        private void UpdateParamEnabled() => UpdateParamEnabled(CurrentUrls());

        private void UpdateParamEnabled(List<string> urls)
        {
            if (ParamHint == null) return;          // 构造期控件尚未就绪

            var proto = SelectedProtocol;
            if (proto == NetworkProtocol.Auto)
                proto = urls.Count > 0 ? NetworkStreamService.DetectProtocol(urls[0]) : NetworkProtocol.Auto;

            bool isRtsp = proto == NetworkProtocol.Rtsp;
            bool isSrt = proto == NetworkProtocol.Srt;

            CmbRtspTransport.IsEnabled = isRtsp;
            TxtSrtLatency.IsEnabled = isSrt;
            CmbSrtMode.IsEnabled = isSrt;
            TxtSrtPassphrase.IsEnabled = isSrt;

            if (isSrt)
                ParamHint.Text = "SRT 参数已启用。latency 是重传缓冲窗口：内网 120ms 够用，跨境/4G 回传常要 200~500ms。";
            else if (isRtsp)
                ParamHint.Text = "RTSP 参数已启用。承载方式建议先试「自动」，花屏再改 TCP。";
            else
                ParamHint.Text = "当前协议没有额外传输参数 —— SRT 参数与 RTSP 承载只在对应协议下启用（灰掉的控件说明它此刻不生效）。";
        }

        private void Param_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;

            if (CmbProtocol.SelectedItem is ProtoChoice pc) SelectedProtocol = pc.Value;
            if (CmbRtspTransport.SelectedItem is RtspChoice rc) SelectedRtspTransport = rc.Value;
            if (CmbSrtMode.SelectedItem is SrtModeChoice sc) SelectedSrtMode = sc.Value;
            if (CmbLink.SelectedItem is LinkChoice lc) SelectedLink = lc.Value;
            if (CmbLatency.SelectedItem is LatencyChoice lt) SelectedLowLatency = lt.Low;
            if (CmbDeint.SelectedItem is DeintChoice dc) SelectedDeinterlace = dc.Value;

            SrtPassphrase = (TxtSrtPassphrase.Text ?? "").Trim();
            SrtLatency = ParseInt(TxtSrtLatency.Text, 120, 20, 8000);
            TimeoutMs = ParseInt(TxtTimeout.Text, 5000, 500, 120000);

            // 用户手选了协议 → 立刻重算回显与参数可用性（UpdateParse 内部会一并刷新参数区）
            UpdateParse();

            LinkHint.Text = SelectedLink == CameraLink.VlcDirect
                ? "⚠ 已选 VLC 直连：预览框与输出图层会各自建立一条连接（占 2 路），"
                  + "源的并发上限低时第二路会连不上；且直连链路没有自动重连与状态灯。"
                : "推荐保持「ffmpeg 预处理」—— 网络源大多限制并发连接，直连时「预览 + 输出同看一路」会把源连爆。";
        }

        private static int ParseInt(string s, int def, int min, int max)
            => int.TryParse((s ?? "").Trim(), out var v) && v >= min && v <= max ? v : def;

        // ==================== 预检（测试连接）====================

        /// <summary>把当前面板上的参数固化成一个临时 InputSource，供预检使用。</summary>
        private InputSource BuildTempSource(string url)
        {
            return new InputSource
            {
                Type = SourceType.Network,
                Location = url,
                NetProtocol = SelectedProtocol,
                NetRtspTransport = SelectedRtspTransport,
                NetSrtLatency = SrtLatency,
                NetSrtMode = SelectedSrtMode,
                NetSrtPassphrase = SrtPassphrase,
                NetTimeoutMs = TimeoutMs
            };
        }

        private async void Probe_Click(object sender, RoutedEventArgs e)
        {
            var urls = CurrentUrls();
            if (urls.Count == 0) { SetStatus("⚠ 先粘一个地址", true); return; }
            await ProbeOneAsync(urls[0], 1, 1);
        }

        private async void ProbeAll_Click(object sender, RoutedEventArgs e)
        {
            var urls = CurrentUrls();
            if (urls.Count == 0) { SetStatus("⚠ 先粘一个地址", true); return; }

            // 串行而非并发：多个地址可能是同一个源的不同通道，同时连会把源挤掉；
            // 而且并发探测的日志会交错，出问题时根本读不出哪条对应哪条。
            for (int i = 0; i < urls.Count; i++)
                await ProbeOneAsync(urls[i], i + 1, urls.Count);
        }

        private async Task ProbeOneAsync(string url, int index, int total)
        {
            _probeCts?.Cancel();
            var cts = new CancellationTokenSource();
            _probeCts = cts;

            BtnProbe.IsEnabled = false;
            BtnProbeAll.IsEnabled = false;
            var tag = total > 1 ? $"（第 {index}/{total} 条）" : "";
            SetProbe($"正在连接{tag}…（最多等 {TimeoutMs + 3000} ms）", IdleBrush);

            try
            {
                var src = BuildTempSource(url);
                var r = await NetworkStreamService.ProbeAsync(src, cts.Token);
                if (cts.IsCancellationRequested) return;

                if (r.Ok)
                {
                    _interlacedByUrl[url] = r.Interlaced;
                    SetProbe($"✓ 连上了{tag}：{r.Summary}", OkBrush);
                    SetStatus("✓ 预检通过 —— 分辨率与编码已读到，可以直接「添加为输入」");
                    // 隔行流自动把去隔行切到「自动」（默认值），并提示
                    if (r.Interlaced)
                        SetStatus("✓ 预检通过 —— 检测到隔行信号，去隔行「自动」档会在添加后自动启用（yadif）");
                }
                else
                {
                    _interlacedByUrl.Remove(url);
                    SetProbe($"✗ 失败{tag}：{r.Error}", WarnBrush);
                    SetStatus("⚠ 预检失败，详见上方原因；也可以「照样添加」，程序会持续重连直到源上线", true);
                }
            }
            catch (Exception ex)
            {
                SetProbe("✗ 预检异常：" + ex.Message, WarnBrush);
            }
            finally
            {
                if (!cts.IsCancellationRequested)
                {
                    BtnProbe.IsEnabled = true;
                    BtnProbeAll.IsEnabled = true;
                }
                if (ReferenceEquals(_probeCts, cts)) _probeCts = null;
                cts.Dispose();
            }
        }

        private void SetProbe(string text, Brush brush)
        {
            ProbeText.Text = text;
            ProbeText.Foreground = brush;
        }

        // ==================== 收藏 / 最近使用 ====================

        private static string BookmarkPath
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cutlance");
                return Path.Combine(dir, "net-sources.json");
            }
        }

        private List<Bookmark> _bookmarks = new();

        private void LoadBookmarks()
        {
            try
            {
                var p = BookmarkPath;
                if (File.Exists(p))
                {
                    var json = File.ReadAllText(p);
                    _bookmarks = JsonSerializer.Deserialize<List<Bookmark>>(json) ?? new();
                }
            }
            catch { _bookmarks = new(); }
            RefreshBookmarks();
        }

        private void SaveBookmarks()
        {
            try
            {
                var p = BookmarkPath;
                var dir = Path.GetDirectoryName(p);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(p, JsonSerializer.Serialize(_bookmarks,
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* 收藏写不进去不该影响主流程 */ }
        }

        private void RefreshBookmarks()
        {
            var list = _bookmarks
                .OrderByDescending(b => b.LastUsed)     // 最近用的排前面
                .ToList();
            BookmarkList.ItemsSource = null;
            BookmarkList.ItemsSource = list;
            BookmarkHint.Text = list.Count == 0
                ? "还没有收藏。粘好一个能用的地址后点「☆ 收藏当前地址」，下次双击就能填进来。"
                : $"共 {list.Count} 条，双击填入地址框。";
        }

        private void Bookmark_Click(object sender, RoutedEventArgs e)
        {
            var urls = CurrentUrls();
            if (urls.Count == 0) { SetStatus("⚠ 先粘一个地址再收藏", true); return; }

            var url = urls[0];
            var info = NetworkStreamService.Parse(url);
            var existing = _bookmarks.FirstOrDefault(b => string.Equals(b.Url, url, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.LastUsed = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                SetStatus("该地址已在收藏里，已刷新使用时间");
            }
            else
            {
                _bookmarks.Add(new Bookmark
                {
                    Url = url,
                    Display = $"{NetworkStreamService.ProtocolText(info.Protocol)} · {info.HostPort}",
                    LastUsed = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                    UseCount = 1
                });
                SetStatus($"已收藏：{info.HostPort}");
            }
            SaveBookmarks();
            RefreshBookmarks();
        }

        private void DelBookmark_Click(object sender, RoutedEventArgs e)
        {
            if (BookmarkList.SelectedItem is not Bookmark b)
            {
                SetStatus("⚠ 先在列表里选一条", true);
                return;
            }
            _bookmarks.RemoveAll(x => string.Equals(x.Url, b.Url, StringComparison.OrdinalIgnoreCase));
            SaveBookmarks();
            RefreshBookmarks();
            SetStatus("已删除该收藏");
        }

        private void BookmarkList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (BookmarkList.SelectedItem is not Bookmark b) return;
            TxtUrl.Text = b.Url;
            b.LastUsed = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            b.UseCount++;
            SaveBookmarks();
            SetStatus($"已填入：{b.Url}　—　点「测试第一行地址」确认源还在线");
        }

        // ==================== 添加 / 关闭 ====================

        private void RefreshAddButton()
        {
            var urls = CurrentUrls();
            int n = 0;
            foreach (var u in urls)
            {
                var proto = SelectedProtocol != NetworkProtocol.Auto
                    ? SelectedProtocol
                    : NetworkStreamService.DetectProtocol(u);
                if (proto == NetworkProtocol.Auto || proto == NetworkProtocol.Ndi) continue;
                if (UsedUrls.Contains(u, StringComparer.OrdinalIgnoreCase)) continue;
                n++;
            }
            BtnAdd.IsEnabled = n > 0;
            BtnAdd.Content = n > 1 ? $"添加 {n} 条为输入" : "添加为输入";
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            var all = CurrentUrls();
            if (all.Count == 0) { SetStatus("⚠ 先粘一个地址", true); return; }

            // 固化成参数（后面的校验与主窗口构造都依赖这些字段已更新）
            Param_Changed(this, new RoutedEventArgs());

            var ok = new List<string>();
            var bad = new List<string>();

            foreach (var u in all)
            {
                var proto = SelectedProtocol != NetworkProtocol.Auto
                    ? SelectedProtocol
                    : NetworkStreamService.DetectProtocol(u);

                if (proto == NetworkProtocol.Auto || proto == NetworkProtocol.Ndi)
                {
                    bad.Add(u);
                    continue;
                }
                if (UsedUrls.Contains(u, StringComparer.OrdinalIgnoreCase))
                {
                    bad.Add(u);
                    continue;
                }
                ok.Add(u);
            }

            if (ok.Count == 0)
            {
                SetStatus($"⚠ 没有可添加的地址：{bad.Count} 条被跳过（协议不支持 / 已在输入列表中）", true);
                return;
            }

            Urls = ok;
            DialogResult = true;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            _probeCts?.Cancel();
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            _probeCts?.Cancel();
            _debounce.Stop();
            base.OnClosed(e);
        }

        // ==================== 界面辅助 ====================

        private void SetStatus(string text, bool warn = false)
        {
            StatusLine.Text = text;
            StatusLine.Foreground = warn ? WarnBrush : new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x6E));
        }
    }
}
