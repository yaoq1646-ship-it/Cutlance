using System.Collections.Generic;

namespace Cutlance.Models
{
    /// <summary>
    /// 工程文件（*.cutlance）：把「当前调用的所有文件状态」整体保存下来，下次直接载入还原。
    ///
    /// 保存范围（= 当前调用的全部素材与设置）：
    ///   · 输入源清单  —— 文件路径 / 摄像头设备与采集参数 / 桌面捕获
    ///   · 输出图层    —— 每层的来源、位置、缩放、层级、可见性、音频路由
    ///   · 叠加层      —— 8 路叠加通道（文字内容 / 图片路径 / 字体 / 颜色 / 位置）
    ///   · 音频混音    —— 四路设备选择、音量、静音
    ///   · 画面效果    —— 调色 / 美白 / 磨皮 / 瘦身 / 拉长
    ///   · 人体测算    —— 美学比例测算的输入数据（身高 / 三围 / 坐高）
    ///   · 输出设置    —— 引擎 / 编码器 / 分辨率 / 容器 / 码率 / RTMP / 录制目录等
    ///
    /// 格式为 JSON（人可读、可手改、可版本管理），后缀名 .cutlance。
    /// </summary>
    public sealed class ProjectFile
    {
        /// <summary>固定标识（载入时校验，防止误开别的 json）。</summary>
        public string App { get; set; } = "Cutlance";

        /// <summary>工程格式版本。</summary>
        public int Version { get; set; } = 1;

        /// <summary>保存时间（仅作记录）。</summary>
        public string SavedAt { get; set; } = "";

        public List<ProjectInput> Inputs { get; set; } = new();
        public List<ProjectLayer> Layers { get; set; } = new();
        public List<ProjectOverlay> Overlays { get; set; } = new();
        public List<ProjectAudio> Audio { get; set; } = new();

        /// <summary>
        /// 预览框里摆着的画面（2026-10-23 老板要的：「多选一起进预览 … 保存工程，下次打开直接回到上次的设置」）。
        /// 单路预览也是这里的一条（X/Y=0、Scale=1）。
        /// ⚠ 老工程没有这一段 → null → 预览框留空，与升级前行为完全一致（不会凭空多出画面）。
        /// </summary>
        public List<ProjectPreviewSlot>? Preview { get; set; }

        /// <summary>输出总线压缩器（2026-10-23）。老工程缺这一段 → null → 按「关闭」处理。</summary>
        public ProjectCompressor? Compressor { get; set; }

        /// <summary>播放列表（模块 A）：项的顺序即播出顺序，SourceIndex 指向 Inputs 下标。</summary>
        public List<ProjectPlayItem> PlayList { get; set; } = new();

        /// <summary>播放列表是否循环（播完最后一项回到第 1 项）。</summary>
        public bool PlayListLoop { get; set; }

        /// <summary>倒计时 / 触发器配置（模块 B）。</summary>
        public ProjectCountdown Countdown { get; set; } = new();

        /// <summary>
        /// 右栏「倒计时牌」（2026-10-24）。老工程没有这一段 → null → 按默认（1 小时 / 字号 120 / 不叠加），
        /// 与升级前行为一致。
        /// </summary>
        public ProjectClock? Clock { get; set; }
        public ProjectFx Fx { get; set; } = new();
        public ProjectBody Body { get; set; } = new();
        public ProjectOutput Output { get; set; } = new();

        /// <summary>
        /// 界面状态（2026-10-23 老板「都存入」）：右栏折叠状态 + 当时选中的是第几格。
        /// 老工程没有这一段 → 按默认（正常展开、不选中），与升级前一致。
        /// </summary>
        public ProjectUi Ui { get; set; } = new();
    }

    /// <summary>输入源（含模块6 的摄像头采集参数）。</summary>
    public sealed class ProjectInput
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "File";     // File / Camera / Desktop / Network / Ndi
        public string Location { get; set; } = "";     // 文件全路径 / dshow 设备名 / screen:// / 网络流 URL / NDI 源名
        public int CamWidth { get; set; }
        public int CamHeight { get; set; }
        public int CamFps { get; set; }
        public string CamChroma { get; set; } = "";
        public bool CamAudio { get; set; } = true;
        // 模块E：采集卡 / 摄像机信号
        public string CamAudioDevice { get; set; } = "";   // 指定音频设备名（空 = 系统默认）
        public string CamLink { get; set; } = "Auto";      // Auto / VlcDirect / Ffmpeg
        // 模块E B-2：去隔行 / 低延迟
        public bool? CamInterlaced { get; set; }               // 加入时实测到的是否隔行（null = 未能判定）
        public string CamDeinterlace { get; set; } = "Auto";   // Auto / On / Off
        public bool CamLowLatency { get; set; }                // 低延迟档（采集与网络流通用）

        /// <summary>
        /// 通用去隔行开关（2026-10-24）：文件 / 网络流等也适用，勾上就走 yadif 预处理。
        /// 老工程没有这个字段 → false（不去隔行），画面与升级前一致。
        /// </summary>
        public bool Deinterlace { get; set; }
        // 模块D：网络流输入（RTSP / SRT / RTMP / HTTP / RIST / UDP）
        public string NetProtocol { get; set; } = "Auto";      // Auto / Rtsp / Rtmp / Http / Srt / Rist / Udp / Ndi
        public string NetRtspTransport { get; set; } = "Auto"; // Auto / Tcp / Udp
        public int NetSrtLatency { get; set; } = 120;          // SRT 重传缓冲窗口（毫秒）
        public string NetSrtMode { get; set; } = "Caller";     // Caller / Listener
        public string NetSrtPassphrase { get; set; } = "";     // SRT 加密口令（空 = 不加密）
        public int NetTimeoutMs { get; set; } = 5000;          // 连接/读取超时（毫秒）
        // 模块C：输入级裁切 / 旋转 / 镜像
        public int CropTop { get; set; }
        public int CropBottom { get; set; }
        public int CropLeft { get; set; }
        public int CropRight { get; set; }
        // 百分比裁切（精调面板，2026-10-18）：UsePctCrop=true 时这四项生效，上面四项被忽略
        public bool UsePctCrop { get; set; }
        public double CropPctLeft { get; set; }
        public double CropPctTop { get; set; }
        public double CropPctRight { get; set; }
        public double CropPctBottom { get; set; }
        public int Rotate { get; set; }
        public bool FlipH { get; set; }
        public bool FlipV { get; set; }
        // —— 办公文档输入（老板 2026-10-19）：表格按打印分页导出的每页图片 ——
        /// <summary>每页图片的绝对路径（空 = 不是文档输入）。</summary>
        public List<string> OfficePages { get; set; } = new();
        /// <summary>当前第几页（0 基）。</summary>
        public int OfficePageIndex { get; set; }
        /// <summary>原始文档路径（.xlsx / .pptx）。</summary>
        public string OfficeSourcePath { get; set; } = "";
        // —— 屏幕区域抓取（PPT 抓放映窗口）——
        public int CaptureLeft { get; set; }
        public int CaptureTop { get; set; }
        public int CaptureWidth { get; set; }
        public int CaptureHeight { get; set; }
        /// <summary>抓屏同时要采的录音设备（dshow 名，通常是一路回环）；空 = 只采画面。</summary>
        public string CaptureAudioDevice { get; set; } = "";
        // 模块C A-2：色键抠像 + 背景合成
        public bool ChromaEnabled { get; set; }
        public string ChromaKeyColor { get; set; } = "0x00B140";
        public double ChromaSimilarity { get; set; } = 0.05;
        public double ChromaBlend { get; set; } = 0.02;
        public string BgType { get; set; } = "Color";      // Color / Image / Video
        public string BgColor { get; set; } = "0x1E1E1E";
        public int BgWidth { get; set; } = 1920;
        public int BgHeight { get; set; } = 1080;
        public string BgPath { get; set; } = "";
    }

    /// <summary>输出图层；SourceIndex 指向 ProjectFile.Inputs 的下标（-1 = 空图层）。</summary>
    public sealed class ProjectLayer
    {
        public int SourceIndex { get; set; } = -1;
        public double X { get; set; }
        public double Y { get; set; }
        /// <summary>宽度缩放（老字段，兼容老工程；同时等于 ScaleX）。</summary>
        public double Scale { get; set; } = 1;
        /// <summary>宽度缩放（新字段；0 = 老工程没写，回落到 Scale）。</summary>
        public double ScaleX { get; set; }
        /// <summary>高度缩放（新字段；0 = 老工程没写，回落到 Scale）。</summary>
        public double ScaleY { get; set; }
        public int Z { get; set; } = 1;
        public bool Visible { get; set; } = true;
        public bool IsAudioSource { get; set; }

        /// <summary>
        /// 这一格的声音大小百分比（2026-10-22；0 = 老工程没写，读回来当 100 原音量）。
        /// 注意 0 在这里是合法值（= 静音），所以不能像 ScaleX 那样用 0 当"没写"，
        /// 老工程靠 <see cref="Volume"/> 的默认值 100 兜住 —— 反序列化时字段缺失就不赋值。
        /// </summary>
        public int Volume { get; set; } = 100;

        /// <summary>画面组号（2026-10-22；0 = 老工程，按同一套布局处理）。</summary>
        public int GroupId { get; set; }

        /// <summary>
        /// 这一格在输出画面里的不透明度 0–100（2026-10-24）。
        /// 默认 100 = 完全不透明；老工程缺这个字段 → 反序列化保留默认值 100，画面与升级前一致。
        /// </summary>
        public int Opacity { get; set; } = 100;

        /// <summary>
        /// 这一格在混音里的角色档："Main"/"Secondary"/"Background"（2026-10-23）。
        /// 老工程没有这个字段 → 空串 → 按主音处理（不衰减），升级后音量不变。
        /// </summary>
        public string AudioRole { get; set; } = "";

        // —— 精调面板（2026-10-18）——
        public bool AspectLock { get; set; } = true;
        public double BorderThickness { get; set; }
        public double BorderRadius { get; set; }

        /// <summary>
        /// 阴影扩散粗细（2026-10-25；0 = 无阴影，与老工程缺字段时的表现一致）。
        /// </summary>
        public double Shadow { get; set; }

        /// <summary>阴影浓淡 0–100（2026-10-25）。默认 60，与 <c>Layer.ShadowOpacity</c> 出厂值一致。</summary>
        public int ShadowOpacity { get; set; } = 60;

        /// <summary>
        /// 填充方式（2026-10-25 异形屏）：0 等比内接 / 1 铺满裁切 / 2 拉伸铺满。
        /// 老工程缺这个字段 → 0，画面与升级前完全一致。
        /// </summary>
        public int FitMode { get; set; }
        public string BorderColor { get; set; } = "#FF33C6F5";
    }

    /// <summary>
    /// 预览框里的一路（= <see cref="Cutlance.Models.PreviewSlot"/> 的可存版本）。
    ///
    /// ⚠ 缩放口径与 <see cref="ProjectLayer"/> **不同**（沿用内部的既有约定，不做二次换算）：
    ///   预览槽的 ScaleX 直接就是「元素宽 ÷ 画布宽」（1 = 铺满），
    ///   而输出图层的 Scale 是「图层宽 = 画布宽 × 0.5 × Scale」（2 = 铺满）。
    ///   所以这里原样存预览侧的 ScaleX / ScaleY，读回来也原样塞回预览槽，
    ///   两边互不折算 —— 折算只发生在「推上输出」那一刻（ProgramLayout.ToLayerScale）。
    /// </summary>
    public sealed class ProjectPreviewSlot
    {
        /// <summary>指向 ProjectFile.Inputs 的下标（-1 = 无效，载入时跳过）。</summary>
        public int SourceIndex { get; set; } = -1;
        public double X { get; set; }
        public double Y { get; set; }
        public double ScaleX { get; set; } = 1;
        public double ScaleY { get; set; } = 1;
        public int Z { get; set; } = 1;
        public bool AspectLock { get; set; } = true;
        public double BorderThickness { get; set; }
        public double BorderRadius { get; set; }
        public string BorderColor { get; set; } = "#FF33C6F5";
    }

    /// <summary>一路叠加通道（固定 8 路，按 ChannelIndex 还原）。</summary>
    public sealed class ProjectOverlay
    {
        public int ChannelIndex { get; set; }
        public bool HasContent { get; set; }
        public bool Visible { get; set; } = true;
        public string Type { get; set; } = "Text";     // Text / Image
        public string Text { get; set; } = "";
        public string? ImagePath { get; set; }
        public double XPct { get; set; } = 50;
        public double YPct { get; set; } = 90;
        public int FontSize { get; set; } = 36;
        public string ColorHex { get; set; } = "#FFFFFFFF";
        public string FontFamilyName { get; set; } = "微软雅黑";
        public bool Bold { get; set; }
        public bool Italic { get; set; }
        /// <summary>图片整体缩放百分比（100 = 等比铺满画布；老板 2026-10-18 第 7 条）。</summary>
        public double ImageScale { get; set; } = 100;
    }

    /// <summary>
    /// 播放列表项（模块 A）。
    /// SourceIndex 指向 ProjectFile.Inputs 的下标（-1 = 无效项，载入时跳过）。
    /// DurationSec &lt;= 0 表示「播完自动下一项」。
    /// </summary>
    public sealed class ProjectPlayItem
    {
        public int SourceIndex { get; set; } = -1;
        public double DurationSec { get; set; }
    }

    /// <summary>倒计时 / 触发器配置（模块 B）。Action 存 CountdownAction 的整数值。</summary>
    public sealed class ProjectCountdown
    {
        /// <summary>计时方式（CountdownMode 的整数值）：0 = 按时长，1 = 到指定时刻。</summary>
        public int Mode { get; set; }
        /// <summary>到指定时刻模式的目标时刻文本（如 21:00 / 2026-10-20 21:00:00）。</summary>
        public string TargetTimeText { get; set; } = "21:00";
        public double DurationSec { get; set; } = 60;
        public int Action { get; set; }
        public int TargetIndex { get; set; } = 1;
        /// <summary>弹框提醒的正文（Action = 9 时用）。</summary>
        public string RemindText { get; set; } = "请录制推流";
        public bool Repeat { get; set; }
        public int DisplayOverlayChannel { get; set; }
        public string DisplayFormat { get; set; } = "倒计时 {t}";
    }

    /// <summary>
    /// 右栏「倒计时牌」（2026-10-24）。只存「设定值 / 叠加字号 / 是否叠加」这三样，
    /// 不存「当前读数」—— 重开工程时读数必然等于设定值（回到起点），存个半截的读数只会误导。
    /// </summary>
    public sealed class ProjectClock
    {
        /// <summary>设定时长（秒），默认 3600 = 一小时。</summary>
        public double SetSeconds { get; set; } = 3600;

        /// <summary>叠加到画面上的字号（1080p 基准像素，40–300）。</summary>
        public double FontSize { get; set; } = 120;

        /// <summary>是否已经打开「叠加」（数字进输出画面）。</summary>
        public bool OverlayOn { get; set; }

        /// <summary>数字中心在画面上的归一化位置（0.5 / 0.5 = 正中）。老工程缺这段 → 居中。</summary>
        public double X { get; set; } = 0.5;
        public double Y { get; set; } = 0.5;

        // ── 样式（2026-10-24 老板「做字体/颜色自定义」）──
        // 老工程缺这些字段 → 空串 / false，由上层填出厂值（Consolas、粗体、白字、半透黑底）。

        /// <summary>字体名（系统字体）。空 = 出厂 Consolas。</summary>
        public string FontFamily { get; set; } = "";

        /// <summary>粗体 / 斜体。用可空：老工程没有这两个字段 → null → 保留出厂值（粗体）。</summary>
        public bool? Bold { get; set; }
        public bool? Italic { get; set; }

        /// <summary>文字颜色 / 超时颜色 / 底板颜色（#AARRGGBB）。空 = 出厂值。</summary>
        public string TextColor { get; set; } = "";
        public string NegativeColor { get; set; } = "";
        public string BackColor { get; set; } = "";
    }

    /// <summary>一路音频混音通道。</summary>
    public sealed class ProjectAudio
    {
        public int Index { get; set; }
        public string DeviceName { get; set; } = "";
        public int Volume { get; set; } = 100;
        public bool Muted { get; set; }

        /// <summary>角色档："main" / "secondary" / "background"；老工程没有该字段时按主音处理。</summary>
        public string Role { get; set; } = "";

        /// <summary>
        /// 这一路自己的压缩档（2026-10-24）："Off" / "Light" / "Medium" / "Strong"。
        /// 老工程没有这个字段 → 空串 → 关闭，输出与升级前完全一致。
        /// </summary>
        public string Compress { get; set; } = "";
    }

    /// <summary>
    /// 输出总线压缩器（2026-10-23）。老工程没有这一段 → Level 为空 → 按「关闭」处理，
    /// 也就是老工程的输出与以前一模一样，不会因为升级突然被压。
    /// </summary>
    public sealed class ProjectCompressor
    {
        /// <summary>档位："off" / "light" / "medium" / "strong"。</summary>
        public string Level { get; set; } = "off";
        public double ThresholdDb { get; set; } = -18;
        public double Ratio { get; set; } = 3;
        public double MakeupDb { get; set; } = 2;
    }

    /// <summary>
    /// 界面状态（2026-10-23）。只存"一眼看得出是设置"的东西，不存临时态
    /// （比如鼠标拖到哪、预览框里选中的是哪一路 —— 那些每次都要重新点，存了反而误导）。
    /// </summary>
    public sealed class ProjectUi
    {
        /// <summary>
        /// 右栏「图层区放到最大」（= 上面叠加层的三行收起）。
        /// 老工程没有这个字段 → false = 正常展开，与升级前一致。
        /// </summary>
        public bool LayersMaximized { get; set; }

        /// <summary>保存时选中的是输出框第几格（0 基，-1 = 没选）。换格 / 精调都指着这一格。</summary>
        public int SelectedLayerIndex { get; set; } = -1;
    }

    /// <summary>画面效果参数。</summary>
    public sealed class ProjectFx
    {
        public double Brightness { get; set; }
        public double Contrast { get; set; } = 1;
        public double Saturation { get; set; } = 1;
        public double Gamma { get; set; } = 1;
        public double Temperature { get; set; }
        public int Whiten { get; set; }
        public int Smooth { get; set; }
        public int Slim { get; set; }
        public int Stretch { get; set; }
        // —— 美体·分段局部形变（模块10）——
        public bool SegmentedBody { get; set; } = true;
        public double WaistY { get; set; } = 46;
        public double HipY { get; set; } = 62;
        public int SlimSeg { get; set; }
        public int LegSeg { get; set; }
        public double WarpCenterX { get; set; } = 50;
        public double WarpWidth { get; set; } = 70;
    }

    /// <summary>美学比例测算的输入数据（单位：厘米）。</summary>
    public sealed class ProjectBody
    {
        public double Height { get; set; } = 170;
        public double NavelHeight { get; set; } = 103;
        public double SittingHeight { get; set; } = 90;
        public double Waist { get; set; } = 72;
        public double Hip { get; set; } = 95;
        public bool IsFemale { get; set; } = true;
        public bool UseManouvrierForLeg { get; set; }
    }

    /// <summary>输出总线设置。</summary>
    public sealed class ProjectOutput
    {
        public string Engine { get; set; } = "ffmpeg";      // ffmpeg / vlc
        public string Encoder { get; set; } = "nvenc";      // nvenc / qsv / software
        public string Container { get; set; } = "mkv";      // mkv / mp4

        /// <summary>
        /// 输出帧率（= 合成器出帧帧率 = ffmpeg 的 -r）。
        /// 2026-10-21 由 int 改 double：25 / 29.97 / 30 / 50 / 59.94 / 60 都要能存，
        /// int 装不下 29.97 与 59.94（以前这两档只能靠 SynthFps 绕）。
        /// </summary>
        public double Fps { get; set; } = 30;

        /// <summary>
        /// 切换台（节目合成器）的帧率；0 = 跟随 <see cref="Fps"/>。
        /// 单独存一份是因为 Fps 是 int，装不下 29.97 / 59.94 这两档。
        /// 老工程没有这个字段 → 反序列化后是 0，正好是「跟随」语义，无需迁移。
        /// </summary>
        public double SynthFps { get; set; }
        public int BitrateKbps { get; set; } = 6000;
        public int Width { get; set; } = 1920;
        public int Height { get; set; } = 1080;
        public string RtmpUrl { get; set; } = "";
        public string RecordFolder { get; set; } = "";
        public bool EnableStream { get; set; } = true;
        public bool EnableRecord { get; set; } = true;
        public bool IncludeMic { get; set; } = true;
        public bool HardwareDecoding { get; set; } = true;

        // —— 模块F：NDI 输出 ——
        /// <summary>是否把节目画面同时广播成一个 NDI 源（供局域网里别的软件/机器取用）。</summary>
        public bool NdiOutputEnabled { get; set; }

        /// <summary>NDI 输出源名（别的机器上看到的名字）。空 = 用默认名 "Cutlance (主机名)"。</summary>
        public string NdiOutputName { get; set; } = "";

        /// <summary>
        /// NDI 输出的帧率（0 = 跟随输出设置里的 Fps）。
        /// NDI 是逐帧广播、不做码率控制，帧率越高网络占用越大，一般与播出帧率一致即可。
        /// </summary>
        public int NdiOutputFps { get; set; }

        /// <summary>
        /// NDI 源发现的「额外目标 IP」（逗号分隔）。本机地址已自动包含（127.0.0.1 + 各网卡）；
        /// 这里填局域网里**别的机器**的 IP，用于 mDNS 不通时改走单播发现（见 NdiService.BuildExtraIps）。
        /// </summary>
        public string NdiExtraIps { get; set; } = "";
    }
}
