using System.Collections.Generic;
using System.ComponentModel;

namespace Cutlance.Models
{
    public enum SourceType
    {
        File,     // 本地视频文件
        Camera,   // 摄像头 / 采集卡（DirectShow，dshow://）
        Desktop,  // 桌面捕获（screen://）
        Network,  // 网络流（RTSP / SRT / RTMP / HTTP(HLS) / RIST / UDP，模块D）
        Ndi       // NDI 源（模块F：.NET 自写接收器收帧 → ffmpeg 编码 → 组播 → VLC）
    }

    /// <summary>
    /// 网络流协议（模块D）。
    ///
    /// 本机实测的可用性（这台机器上的 ffmpeg 8.1.1 + VLC 3.0.21）：
    ///   · ffmpeg 侧：rtsp / srt / rist / rtmp(s) / http(s) / udp / rtp 全部编进去了
    ///     （buildconf 有 --enable-libsrt 与 --enable-librist，协议表里逐个可见）
    ///   · VLC 侧：plugins/access 下有 liblive555(RTSP) / libaccess_srt / librist /
    ///     libhttp(s) / librtp / libudp，同样齐全
    ///   · ⚠ NDI **两边都没有** —— 它不是「没配好」，是根本没编进去（ffmpeg 官方因许可
    ///     冲突主动移除了 NDI，VLC 3.0 也没带 NDI 插件）。
    ///     NDI 已改由**独立输入类型**支持（见 SourceType.Ndi / 模块F）：走 NDI 官方 SDK 的
    ///     .NET 接收器自己收帧 → 喂 ffmpeg 编码 → 组播 → VLC，完全绕开两边的 NDI 能力缺口。
    ///     所以网络流面板识别到 ndi:// 时会引导用户改用「+NDI」按钮，而不是收下再黑屏。
    /// </summary>
    public enum NetworkProtocol
    {
        Auto,   // 按 URL 前缀自动判定（默认）
        Rtsp,   // rtsp://  rtspu://（UDP 承载）
        Rtmp,   // rtmp://  rtmps://
        Http,   // http://  https://（含 .m3u8 / HLS、mp4 直链、FLV over HTTP）
        Srt,    // srt://  （RTP over SRT，可带 passphrase 加密）
        Rist,   // rist://
        Udp,    // udp://  rtp://  （裸 MPEG-TS / RTP 承载）
        Ndi     // ndi://  —— 当前版本不支持（见上面的说明），面板会明确告知
    }

    /// <summary>RTSP 的承载传输方式（RTSP 独有的坑：UDP 在丢包网络上会花屏，TCP 更稳但延迟略高）。</summary>
    public enum RtspTransport
    {
        Auto,   // 交给 ffmpeg 协商（默认）
        Tcp,    // 强制 TCP 承载（跨网段/丢包网络推荐）
        Udp     // 强制 UDP 承载（低延迟，内网稳定时可用）
    }

    /// <summary>SRT 的连接方向。caller = 主动连对方（拉流常用）；listener = 本机监听等对方推。</summary>
    public enum SrtMode
    {
        Caller,     // 主动连接（默认）
        Listener    // 监听等待
    }

    /// <summary>
    /// 摄像头 / 采集卡的采集链路选择（模块E）。
    ///
    /// 两条链路的实测差异（本机 GATHER 四路采集卡，1080p60 yuyv422）：
    ///   · <b>VLC 直连</b>：dshow:// 直接播，延迟最低（约 100ms），但 VLC 的 dshow 模块
    ///     **没有采集缓冲参数**，1080p60 无压缩信号（单帧 4.1MB）容易丢帧。
    ///   · <b>ffmpeg 预处理</b>：过一层常驻 ffmpeg（可调 rtbufsize / 分辨率 / 帧率），
    ///     编码成 h264 再喂给 VLC，稳定不丢帧，代价是 +约 460ms 延迟。
    /// </summary>
    public enum CameraLink
    {
        Auto,        // 有几何处理走 ffmpeg，否则 VLC 直连（默认，兼容量大）
        VlcDirect,   // 强制 VLC 直连 dshow（低延迟优先）
        Ffmpeg       // 强制走 ffmpeg 预处理层（稳定性优先，采集卡推荐）
    }

    /// <summary>
    /// 去隔行档位（模块E B-2）。
    ///
    /// 为什么要做：1080i / 1080i50（隔行）信号逐行播出时会出现「梳状拉丝」——
    /// 快速横移或摇镜头时画面边缘一层层锯齿。必须做去隔行（yadif）才干净。
    /// 但反过来，逐行（1080p）信号若也去隔行，会把垂直细节插值糊掉。
    /// 本机当前信号是 1080p60 逐行，所以默认「自动」= 只在该信号确实隔行时才启用。
    /// </summary>
    public enum DeinterlaceMode
    {
        Auto,   // 面板实测到「隔行」才启用（默认）
        On,     // 强制启用
        Off     // 强制关闭
    }

    /// <summary>
    /// 抠像后的背景类型（模块C A-2）。
    ///
    /// ⚠ 架构硬约束（本项目的根本限制，别再试「透出下层图层」）：
    ///   图层合成 = 在 WPF 画布里堆多个 VideoView（HwndHost 原生子窗口），
    ///   原生子窗口之间**没有逐像素 alpha 合成**。所以抠像**不可能**让下层图层透出来。
    ///   等价且可行的做法：在 FFmpeg 里把背景**合成进这一路画面**（本枚举就是背景从哪来）。
    /// </summary>
    public enum ChromaBackground
    {
        Color,   // 纯色（默认）：最常用，绿幕换成演播室灰/黑/蓝
        Image,   // 静态图片（jpg/png/bmp）：虚拟背景板
        Video    // 视频文件（mp4/mkv…，自动循环）：动态背景
    }

    /// <summary>采集输入的信号状态（主窗口输入输入项上常显的状态灯）。</summary>
    public enum CaptureStatus
    {
        Unknown,        // 尚未取过画面 / 状态未知
        Live,           // 有信号，采集链路在跑
        Reconnecting,   // 断流，正在按退避重连
        Down,           // 断了且已放弃重连，或预处理起不来（已回落原始画面）
        Direct          // VLC 直连：无自动监测，不谎报状态
    }

    /// <summary>
    /// 输入源（模块1）：文件 / 摄像头 / 桌面。
    /// Location 约定：
    ///   File    → 本地文件完整路径
    ///   Camera  → dshow 设备名（空 = 系统默认设备）
    ///   Desktop → 固定 "screen://"
    /// Number    → 显示编号（vMix 风格固定编号，删除不重排）
    /// IsPreview / IsProgram → 左栏缩略图高亮（蓝=预览 / 红=输出）
    /// </summary>
    public class InputSource : INotifyPropertyChanged
    {
        public string Name { get; set; } = "";
        public SourceType Type { get; set; }
        public string Location { get; set; } = "";
        public int Number { get; set; } = 0;

        /// <summary>
        /// 改名（老板 2026-10-20：左侧输入项右键「改名」）。
        /// 只改**显示名** —— 不动磁盘文件、不动设备名。
        /// ⚠ Name 原本是自动属性（不会发变更通知），改名后左侧列表 / 预览标题 / 图层列表
        ///   不会自己刷新，所以这里必须补一次 Raise；列表那边还有 Items.Refresh() 兜底。
        /// </summary>
        public void Rename(string name)
        {
            Name = name ?? "";
            Raise(nameof(Name));
        }

        // —— 办公文档输入（老板 2026-10-19：PPT / 表格进预览，快捷键翻页）——
        //
        // 表格（Excel）：按 Excel 自己的**打印分页**把每一页导出成 PNG，`OfficePages` 存这些图片的绝对路径，
        //   `Location` 始终指向当前页那张 —— 这样媒体构造（CreateFileMedia）不用改，
        //   PNG 本来就是被支持的静态图片输入（`:image-duration=-1` 一直显示）。
        //   翻页 = 换 Location 指向下一页 + ReplayInput 重建媒体。
        //
        // PPT：走「抓放映窗口」，`Location` 是 screen:// 那套区域抓屏，不填 OfficePages。
        public List<string> OfficePages { get; set; } = new();

        /// <summary>当前显示第几页（0 基，对应 <see cref="OfficePages"/> 下标）。</summary>
        public int OfficePageIndex { get; set; }

        /// <summary>原始文档路径（.xlsx / .pptx），仅供显示与工程保存，不参与播放。</summary>
        public string OfficeSourcePath { get; set; } = "";

        // —— 屏幕区域抓取（PPT 抓放映窗口用）——
        // PPT 放映窗口是全屏的，抓"那一块屏幕区域"就等于抓到放映画面。
        // 全部为**物理像素**；Width/Height 都为 0 时表示抓整屏（普通桌面捕获）。
        public int CaptureLeft { get; set; }
        public int CaptureTop { get; set; }
        public int CaptureWidth { get; set; }
        public int CaptureHeight { get; set; }

        /// <summary>
        /// 桌面抓屏同时要采的录音设备（dshow 设备名）；空 = 只采画面。
        ///
        /// 为什么会需要它：抓屏（screen://）本身只有画面，PPT 里的音效 / 内嵌视频声音
        /// 进不来直播。要出声得挂一路**回环**录音设备（VB-Audio Cable / 立体声混音 之类），
        /// 把系统播放设备指到它，再从它采。没有回环设备就只能抓画面。
        /// </summary>
        public string CaptureAudioDevice { get; set; } = "";

        // —— 摄像头采集参数（模块6：对标 OBS/vMix 的采集信号设定）——
        public int CamWidth { get; set; } = 0;       // 0 = 设备默认分辨率
        public int CamHeight { get; set; } = 0;
        public int CamFps { get; set; } = 0;         // 0 = 设备默认帧率
        public string CamChroma { get; set; } = "";  // MJPG / YUY2 / NV12 …（空 = 设备默认格式）
        public bool CamAudio { get; set; } = true;   // 是否同时采集该摄像头音频

        // —— 模块E：采集卡 / 摄像机信号 ——
        /// <summary>
        /// 指定的音频设备名（如采集卡的「数字音频接口 (GATHER CAM1)」；空 = 系统默认音频设备）。
        /// VLC 直连时写进 :dshow-adev=，ffmpeg 链路时写进 audio="…" —— 两条链路都靠它点准设备。
        /// </summary>
        public string CamAudioDevice { get; set; } = "";

        /// <summary>采集链路：VLC 直连 / ffmpeg 预处理（见 CameraLink 说明）。</summary>
        public CameraLink CamLink { get; set; } = CameraLink.Auto;

        // —— 模块E B-2：信号状态 / 去隔行 / 低延迟 ——

        /// <summary>
        /// 加入输入时面板**实测**到的当前信号是否为隔行（1080i）。**三态**：
        ///   true = 实测隔行 ／ false = 实测逐行 ／ null = 判不出来（画面纯色、采样不足等）。
        /// 去隔行「自动」档只在 `== true` 时启用 —— null 与 false 都不启用，
        /// 因为对逐行信号做 yadif 反而会糊掉垂直细节。判不出来时面板会明确提示用户手动确认，
        /// 而不是悄悄当成「逐行」（那正是 2026-10-16 复测挖出的老 Bug 的后果）。
        /// </summary>
        public bool? CamInterlaced { get; set; }

        /// <summary>去隔行档位（默认自动：只有实测到隔行才启用）。</summary>
        public DeinterlaceMode CamDeinterlace { get; set; } = DeinterlaceMode.Auto;

        private bool _deinterlace;

        /// <summary>
        /// 通用「去隔行」开关（2026-10-24 老板要的 1080i 开关）。
        ///
        /// 为什么另立一个字段、不复用 <see cref="CamDeinterlace"/>：
        ///   那个是**采集卡**专用的档位（带「自动 = 检测到隔行才开」的语义，靠采集信号探测）。
        ///   文件 / 网络流这类没有"探测信号"环节，需要的是老板自己看画面决定的一个勾。
        ///   两者语义不同，但最终都汇总到 <see cref="NeedDeinterlace"/> 这一个判据上。
        ///
        /// 落在哪：走预处理链时 ffmpeg 加 `yadif=mode=send_frame`（1080i50 出来是 50fps 逐行）；
        ///   采集卡在 VLC 直连时退回 VLC 自己的 `:deinterlace=1`（质量次一些，见 VideoEngine）。
        /// </summary>
        public bool Deinterlace
        {
            get => _deinterlace;
            set { if (_deinterlace != value) { _deinterlace = value; Raise(nameof(Deinterlace)); } }
        }

        /// <summary>
        /// 低延迟档。开启后压 VLC 播放缓冲（120→60ms）并关掉时钟抖动补偿、
        /// 关键帧间隔 1s→0.25s，端到端大约再省 250~350ms；
        /// 代价是网络/解码抖动时更容易丢帧，弱机或信号不稳时不要开。
        ///
        /// ⚠ 这个字段对<b>采集输入和网络流输入通用</b>（模块D 复用它，没另开一份）：
        ///   两者都是「接一路实时信号进来」，延迟档要调的东西完全一样 ——
        ///   缓冲、时钟、GOP，没有哪个参数是采集独有或网络独有的。另开字段只会两处同步改、改漏一处。
        /// </summary>
        public bool CamLowLatency { get; set; }

        // ==================== 模块D：网络流输入（RTSP / SRT / RTMP / HTTP / RIST / UDP） ====================
        //
        // 为什么另立一个输入类型、而不是塞进 File：
        //   ① 生命周期完全不同 —— 文件有「播完」这个正常终点，网络流没有；断流对文件是结束、
        //      对直播是故障（要重连）。混在一起会让看门狗分不清该不该重连。
        //   ② 状态灯要对它常显（和采集卡一样，直播流的死活得一眼看见）。
        //   ③ URL 不是路径，不能走进「文件是否存在」那套校验。

        /// <summary>
        /// 协议（默认 Auto = 按 URL 前缀自动判定）。
        ///
        /// 为什么要让用户能手动指定：有些源给的 URL 是「不带协议的裸地址」或者走非标端口，
        /// 光看前缀认不出来。给了手动档，至少不会因为识别不出来而无法使用。
        /// </summary>
        public NetworkProtocol NetProtocol { get; set; } = NetworkProtocol.Auto;

        /// <summary>RTSP 承载方式（仅 RTSP 生效）。丢包网络上是花屏与稳定的分水岭。</summary>
        public RtspTransport NetRtspTransport { get; set; } = RtspTransport.Auto;

        /// <summary>
        /// SRT 的 latency（毫秒，仅 SRT 生效）。
        /// 这是 SRT 的**重传缓冲窗口**：设太小则丢包来不及重传（花屏），设太大则延迟高。
        /// 默认 120ms —— 内网/同城够用；跨境或 4G 回传常见要 200~500。
        /// </summary>
        public int NetSrtLatency { get; set; } = 120;

        /// <summary>SRT 连接方向（仅 SRT 生效）。拉别人的流用 caller，等别人推过来用 listener。</summary>
        public SrtMode NetSrtMode { get; set; } = SrtMode.Caller;

        /// <summary>
        /// SRT 加密口令（仅 SRT 生效，空 = 不加密）。
        /// ⚠ 会明文写进工程文件 —— 面板上对这个框做了掩码显示，但工程文件是 JSON，别用它当密钥保险箱。
        /// </summary>
        public string NetSrtPassphrase { get; set; } = "";

        /// <summary>
        /// 连接/读取超时（毫秒，默认 5000）。
        /// 为什么要暴露它：默认超时对「源暂时不在线」这个最常见场景是致命的 ——
        /// 拉不动的源会一直挂着等，用户看到的是「黑屏且不知道要等多久」。
        /// 给短一点（如 3000）能让失败更快暴露，重连也就能更快开始。
        /// </summary>
        public int NetTimeoutMs { get; set; } = 5000;

        /// <summary>识别出来的协议（面板显示 / 输入名用）。Auto 时按 Location 前缀实时判定。</summary>
        public NetworkProtocol EffectiveProtocol =>
            NetProtocol != NetworkProtocol.Auto
                ? NetProtocol
                : Services.NetworkStreamService.DetectProtocol(Location);

        /// <summary>本路最终是否真的启用去隔行：通用开关 或 采集档位任一成立即可。</summary>
        public bool NeedDeinterlace =>
            Deinterlace ||
            CamDeinterlace == DeinterlaceMode.On ||
            (CamDeinterlace == DeinterlaceMode.Auto && CamInterlaced == true);

        /// <summary>去隔行的一句话说明（状态栏/提示用）。</summary>
        public string DeinterlaceSummary => Deinterlace
            ? "去隔行 开（手动）"
            : CamDeinterlace switch
            {
                DeinterlaceMode.On => "去隔行 强制开",
                DeinterlaceMode.Off => "去隔行 关",
                _ => CamInterlaced switch
                {
                    true => "去隔行 自动（已实测到隔行信号 → 启用）",
                    false => "去隔行 自动（实测逐行 → 未启用）",
                    _ => "去隔行 自动（场序未能判定 → 保守不启用；画面有横丝请手动切「开」）"
                }
            };

        // —— 模块C：输入级裁切 / 旋转 / 镜像 ——
        // 由 VLC 原生 video-filter 链实现（见 VideoEngine.CreateMedia），
        // 所以对「所有播放该输入的地方」——预览框、输出图层、播放列表——统一生效。
        // 这正是「输入级」的含义：同一个输入在哪儿播，都带同样的裁切与朝向。
        // 典型用途：绿幕素材先裁掉边缘溢出的绿边，再做色键。
        public int CropTop { get; set; } = 0;      // 各边裁切像素（0 = 不裁）
        public int CropBottom { get; set; } = 0;
        public int CropLeft { get; set; } = 0;
        public int CropRight { get; set; } = 0;
        public int Rotate { get; set; } = 0;       // 顺时针 0 / 90 / 180 / 270（度）
        public bool FlipH { get; set; }            // 水平镜像
        public bool FlipV { get; set; }            // 垂直镜像

        // —— 百分比裁切（精调面板，2026-10-18）——
        //
        // 为什么要有第二套裁切：面板里填的是「X1/Y1/X2/Y2」四个百分比（取景思路），
        // 而得先知道源分辨率才能换算成像素 —— 文件类输入事先并不知道。
        // 所以百分比这条路直接把表达式交给 ffmpeg（crop=iw*0.1:...），由它在解码后按实际尺寸算，
        // 不需要任何探测。两条路是同一件事的两种表述，用 UsePctCrop 二选一：
        //   · UsePctCrop = true  → 百分比字段生效，像素字段被忽略；
        //   · UsePctCrop = false → 老行为（像素字段），「输入裁切/旋转」窗口用的就是它。
        public bool UsePctCrop { get; set; }
        public double CropPctLeft { get; set; }     // 左裁掉源宽的百分之几（0–90）
        public double CropPctTop { get; set; }
        public double CropPctRight { get; set; }
        public double CropPctBottom { get; set; }

        private bool HasPctCrop => CropPctLeft > 0.01 || CropPctTop > 0.01
                                   || CropPctRight > 0.01 || CropPctBottom > 0.01;

        public bool HasCrop => UsePctCrop
            ? HasPctCrop
            : CropTop > 0 || CropBottom > 0 || CropLeft > 0 || CropRight > 0;

        public bool HasGeometry => HasCrop || Rotate != 0 || FlipH || FlipV;

        // ==================== 模块C A-2：色键抠像 + 背景合成 ====================
        //
        // 做什么：把画面里的纯色背景（绿幕/蓝幕）抠掉，换成另一张背景（纯色/图片/视频）。
        // 怎么实现：不是「透出下层图层」（见 ChromaBackground 的架构硬约束说明），
        //          而是在 FFmpeg 里把背景**合成进这一路画面**，再作为整体喂给 VLC。
        //
        // 参数为什么是这三个（本机实测结论，别照搬网上教程的"百分比"直觉）：
        //   · ffmpeg 的 similarity / blend 是**归一化平方色差**，不是百分比。0x00B140 绿
        //     对白色的平方距离约 0.083 —— 所以网上常见的 similarity=0.25 会把主体一起抠掉。
        //   · 实测可用值：similarity≈0.05 / blend≈0.02（残留绿边与误抠主体都很少）。

        /// <summary>是否启用抠像换背景。</summary>
        public bool ChromaEnabled { get; set; }

        /// <summary>
        /// 键色（要抠掉的颜色），ffmpeg 0xRRGGBB 格式。
        /// 默认 0x00B140 = 标准数字绿幕色（比纯绿 0x00FF00 更接近实际绿布）。
        /// </summary>
        public string ChromaKeyColor { get; set; } = "0x00B140";

        /// <summary>
        /// 相似度（归一化平方色差）：越大抠得越狠。实测 0.05 左右可用。
        /// 太大 → 主体（尤其与键色接近的衣物/肤色边缘）被一起抠掉；太小 → 绿边残留。
        /// </summary>
        public double ChromaSimilarity { get; set; } = 0.05;

        /// <summary>
        /// 混合度：键色附近的过渡带宽度，用来软化边缘。实测 0.02 左右可用。
        /// 太大会让主体边缘半透明（"鬼影"）。
        /// </summary>
        public double ChromaBlend { get; set; } = 0.02;

        /// <summary>背景类型（纯色 / 图片 / 视频）。</summary>
        public ChromaBackground BgType { get; set; } = ChromaBackground.Color;

        /// <summary>纯色背景色（ffmpeg 0xRRGGBB）。默认演播室深灰。</summary>
        public string BgColor { get; set; } = "0x1E1E1E";

        /// <summary>
        /// 纯色背景的尺寸。为什么要用户给：lavfi 的 color 源必须有一个确定尺寸，
        /// 没法"跟随前景"。默认 1920×1080 覆盖最常见的直播输出规格；
        /// 前景小于它时居中叠放（四周留背景色），大于它才可能被裁。
        /// </summary>
        public int BgWidth { get; set; } = 1920;
        public int BgHeight { get; set; } = 1080;

        /// <summary>图片 / 视频背景的文件路径（纯色背景时忽略）。</summary>
        public string BgPath { get; set; } = "";

        /// <summary>背景素材是否齐备（纯色恒 true；图片/视频要求有路径）。</summary>
        public bool HasChromaBackground =>
            BgType == ChromaBackground.Color ||
            !string.IsNullOrWhiteSpace(BgPath);

        /// <summary>本路是否真的要做抠像合成（启用 + 背景可用）。</summary>
        public bool NeedChromaKey => ChromaEnabled && HasChromaBackground;

        /// <summary>抠像配置的一句话摘要（状态栏/输入名用）。</summary>
        public string ChromaSummary
        {
            get
            {
                if (!ChromaEnabled) return "抠像 关";
                if (!HasChromaBackground) return "抠像 开（缺背景素材，未生效）";
                var bg = BgType switch
                {
                    ChromaBackground.Color => $"纯色 {BgColor} {BgWidth}×{BgHeight}",
                    ChromaBackground.Image => "图片 " + System.IO.Path.GetFileName(BgPath),
                    _ => "视频 " + System.IO.Path.GetFileName(BgPath)
                };
                return $"抠像 开 · 键色 {ChromaKeyColor} · 相似度 {ChromaSimilarity:0.###} · 背景 {bg}";
            }
        }

        /// <summary>
        /// 本路是否有任何需要 FFmpeg 逐帧处理的视频效果（几何 或 抠像）。
        /// 用途：非实时输入（文件/桌面）是否需要走预处理层，就看它。
        /// </summary>
        public bool NeedsVideoProcess => HasGeometry || NeedChromaKey;

        /// <summary>界面/状态栏用的一句话摘要。</summary>
        public string GeometrySummary
        {
            get
            {
                if (!HasGeometry) return "原始画面";
                var parts = new System.Collections.Generic.List<string>();
                if (UsePctCrop && HasPctCrop)
                    parts.Add($"裁切 L{CropPctLeft:0.#}% T{CropPctTop:0.#}% R{CropPctRight:0.#}% B{CropPctBottom:0.#}%");
                else if (!UsePctCrop && HasCrop)
                    parts.Add($"裁切 上{CropTop} 下{CropBottom} 左{CropLeft} 右{CropRight}");
                if (Rotate != 0) parts.Add($"旋转 {Rotate}°");
                if (FlipH) parts.Add("水平镜像");
                if (FlipV) parts.Add("垂直镜像");
                return string.Join(" · ", parts);
            }
        }

        private bool _isPreview;
        private bool _isProgram;
        private bool _isPicked;

        public bool IsPreview
        {
            get => _isPreview;
            set { if (_isPreview != value) { _isPreview = value; Raise(nameof(IsPreview)); } }
        }
        public bool IsProgram
        {
            get => _isProgram;
            set { if (_isProgram != value) { _isProgram = value; Raise(nameof(IsProgram)); } }
        }

        /// <summary>
        /// 多选标记（Ctrl+点击输入项切换）。
        /// 用途：把「几个文件的画面一起切到输出框」——勾好几个输入后一次铺成分屏。
        /// 纯界面态，不写进工程文件。
        /// </summary>
        public bool IsPicked
        {
            get => _isPicked;
            set { if (_isPicked != value) { _isPicked = value; Raise(nameof(IsPicked)); } }
        }

        // ==================== 模块E B-2：信号状态灯（主窗口常显） ====================
        //
        // 为什么放在输入输入项上：采集链路的死活（USB 抖动、信号源断电、设备被抢走）
        // 恰恰是导播最需要一眼看到的东西，不该逼人开面板去查。灯常显，出问题立刻看得见。

        private CaptureStatus _camStatus = CaptureStatus.Unknown;

        public CaptureStatus CamStatus
        {
            get => _camStatus;
            set
            {
                if (_camStatus == value) return;
                _camStatus = value;
                Raise(nameof(CamStatus));
                Raise(nameof(CamStatusText));
                Raise(nameof(CamStatusBrush));
            }
        }

        public string CamStatusText => CamStatus switch
        {
            CaptureStatus.Live => "信号正常",
            CaptureStatus.Reconnecting => "重连中",
            CaptureStatus.Down => "无信号",
            CaptureStatus.Direct => "直连",
            _ => "未启动"
        };

        public System.Windows.Media.Brush CamStatusBrush => CamStatus switch
        {
            CaptureStatus.Live => StLive,
            CaptureStatus.Reconnecting => StReconnect,
            CaptureStatus.Down => StDown,
            CaptureStatus.Direct => StDirect,
            _ => StIdle
        };

        /// <summary>
        /// 状态灯只对「实时输入」显示 —— 文件/桌面没有「信号」这个概念。
        /// 采集卡与网络流都要常显：两者的死活都是导播最需要一眼看到的东西
        /// （USB 抖动 / 信号源断电 / 网络断了 / 推流端掉线），不该逼人开面板去查。
        /// </summary>
        public System.Windows.Visibility CamStatusVisibility =>
            IsLiveInput
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;

        /// <summary>
        /// 是不是「接一路实时信号」的输入（采集 / 网络流 / NDI）。
        /// 判据的用处：① 状态灯是否常显；② 断流要不要自动重连；
        /// ③ 低延迟档/去隔行这些实时链路参数是否适用。
        /// </summary>
        public bool IsLiveInput => Type == SourceType.Camera
                                || Type == SourceType.Network
                                || Type == SourceType.Ndi;

        private static readonly System.Windows.Media.Brush StLive = MakeBrush(0x6B, 0xE0, 0x8A);
        private static readonly System.Windows.Media.Brush StReconnect = MakeBrush(0xFF, 0xC1, 0x07);
        private static readonly System.Windows.Media.Brush StDown = MakeBrush(0xFF, 0x52, 0x52);
        private static readonly System.Windows.Media.Brush StDirect = MakeBrush(0x4F, 0xC3, 0xF7);
        private static readonly System.Windows.Media.Brush StIdle = MakeBrush(0x7A, 0x7A, 0x7A);

        /// <summary>Freeze 过的静态画刷：绑定在列表里被反复取用，冻结后不参与变更通知、开销最低。</summary>
        private static System.Windows.Media.Brush MakeBrush(byte r, byte g, byte b)
        {
            var br = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }

        public string TypeShort => Type switch
        {
            SourceType.File => "FILE",
            SourceType.Camera => "CAM",
            SourceType.Desktop => "SCR",
            SourceType.Network => "NET",
            SourceType.Ndi => "NDI",
            _ => "?"
        };

        /// <summary>NDI 源名（模块F）。Location 存的就是完整 NDI 源名，形如 "PC-XXX (Camera 1)"。</summary>
        public string NdiSourceName => Location;

        /// <summary>NDI 输入的一句话摘要（输入名/状态栏用）。非 NDI 返回空串。</summary>
        public string NdiSummary => Type != SourceType.Ndi ? "" : "NDI 源 " + Location;

        /// <summary>网络流的一句话协议标签（输入名 / 状态栏用）。非网络流返回空串。</summary>
        public string NetProtocolText => Type != SourceType.Network ? "" : EffectiveProtocol switch
        {
            NetworkProtocol.Rtsp => "RTSP",
            NetworkProtocol.Rtmp => "RTMP",
            NetworkProtocol.Http => "HTTP",
            NetworkProtocol.Srt => "SRT",
            NetworkProtocol.Rist => "RIST",
            NetworkProtocol.Udp => "UDP",
            NetworkProtocol.Ndi => "NDI（不支持）",
            _ => "未知协议"
        };

        public override string ToString() => $"[{TypeShort}] {Name}";

        // ==================== 上播音频记忆（2026-10-23 老板要求）====================
        //
        // 问题：同一批素材推上输出 → 调好主次 → 再清空重推一次，主次又被打回
        //      「第一格主音、其余次音 100%」，白调一遍。
        // 做法：按**输入源**记住上次上播时的角色档与音量，重推同一路时沿用。
        //
        // ⚠ 只活在本次运行内，**不写进工程文件**：工程里存的是"这一格"的设置，
        //   这里存的是"这一路素材"的习惯值，两者语义不同，混在一起会让换工程串味。
        // ⚠ 只有真的上过播（推上输出）才会写，预览/拖进拖出都不会污染记忆。

        /// <summary>上次上播时的角色档（HasAudioMemory=false 时不作数）。</summary>
        public AudioRole LastAudioRole { get; set; } = AudioRole.Secondary;

        /// <summary>上次上播时的格音量百分比（0–150）。</summary>
        public int LastAudioVolume { get; set; } = 100;

        /// <summary>这一路有没有"上次上播"的记忆（第一次推上输出时是 false，推完置 true）。</summary>
        public bool HasAudioMemory { get; set; }

        /// <summary>把某格当前的音频设置记到它的输入源上（推上输出 / 混音台改动时调）。</summary>
        public void RememberAudio(AudioRole role, int volume)
        {
            LastAudioRole = role;
            LastAudioVolume = volume;
            HasAudioMemory = true;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }
}
