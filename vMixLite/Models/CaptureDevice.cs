using System;
using System.Collections.Generic;
using System.Windows;

namespace Cutlance.Models
{
    /// <summary>采集设备类型（用于在面板里分类显示）。</summary>
    public enum CaptureDeviceKind
    {
        CaptureCard,    // 采集卡（HDMI / SDI 输入）
        Camera,         // 摄像头 / 摄像机（UVC）
        VirtualDevice,  // 虚拟设备（OBS / vMix / NDI 等软件设备）
        Audio,          // 音频设备
        Unknown
    }

    /// <summary>
    /// 一路 DirectShow 采集设备。
    ///
    /// ⚠ 为什么不用 WMI 的 PNPClass 枚举（原 GetCameraDevices 的做法）：
    ///   实测本机 DirectShow 视频设备共 10 个，而 WMI 条件 PNPClass='Camera' OR 'Image'
    ///   只能列出 1 个（内置摄像头）。PCIe 内置采集卡、专业 SDI 卡、部分 UVC 卡的
    ///   PNPClass 不在 Camera/Image 里，会被整个漏掉 —— 这就是「采集卡加不进来」的根因。
    ///   ffmpeg 的 -list_devices 直接问 DirectShow，是权威枚举。
    /// </summary>
    public sealed class CaptureDevice
    {
        public string Name { get; set; } = "";
        public string AltName { get; set; } = "";      // ffmpeg 给出的设备路径（\?\usb#vid_…）
        public CaptureDeviceKind Kind { get; set; } = CaptureDeviceKind.Unknown;
        public bool IsVideo { get; set; }

        /// <summary>
        /// 这一路是否已经在输入列表里（多卡管理用）。
        /// 采集设备是独占的 —— 同一路开两次，第二次必然打不开，所以面板里要标出来。
        /// </summary>
        public bool IsUsed { get; set; }

        public Visibility UsedVisibility => IsUsed ? Visibility.Visible : Visibility.Collapsed;

        public string KindText => Kind switch
        {
            CaptureDeviceKind.CaptureCard => "采集卡",
            CaptureDeviceKind.Camera => "摄像头",
            CaptureDeviceKind.VirtualDevice => "虚拟设备",
            CaptureDeviceKind.Audio => "音频设备",
            _ => "设备"
        };

        /// <summary>列表里显示的一行文字（名称 + 类型标注）。</summary>
        public string Display => $"{Name}   [{KindText}]";

        public override string ToString() => Name;
    }

    /// <summary>
    /// 采集设备支持的一种「信号格式」= 分辨率 × 像素格式 × 帧率范围。
    /// 来源于 ffmpeg -list_options 的真实设备能力枚举，不是写死的预设表。
    /// </summary>
    public sealed class CaptureFormat
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public string PixelFormat { get; set; } = "";
        public double FpsMin { get; set; }
        public double FpsMax { get; set; }

        /// <summary>
        /// true = 「自动（跟随输入信号）」这一行：不强制分辨率/帧率，让设备按当前进来的信号自动协商。
        ///
        /// 为什么必须有这一行（本机实测教训）：
        ///   设备能力表里的最高档是 2560×1600@30，但当时线进来的信号是 1920×1080@60。
        ///   若照能力表默认挑最高档，就会拿一个「当前没有被喂进来」的格式去要求设备，
        ///   结果通常是采不到画面。所以默认必须是「自动」，让设备自己报它能给什么。
        /// </summary>
        public bool IsAuto { get; set; }

        public string ResolutionText
            => IsAuto ? "★ 自动（跟随输入信号）"
                      : (Width > 0 ? $"{Width} × {Height}" : "设备默认");

        public string FpsText
        {
            get
            {
                if (IsAuto) return "自动";
                if (FpsMin <= 0) return "—";
                if (Math.Abs(FpsMax - FpsMin) < 0.01) return $"{FpsMin:0.##}";
                return $"{FpsMin:0.##} ~ {FpsMax:0.##}";
            }
        }

        /// <summary>像素格式统一大写显示（MJPG / YUY2 / NV12 …）。</summary>
        public string PixelText => IsAuto
            ? "自动"
            : (string.IsNullOrWhiteSpace(PixelFormat) ? "—" : PixelFormat.ToUpperInvariant());

        /// <summary>界面上一句话摘要。</summary>
        public string Summary => IsAuto
            ? "自动（不强制分辨率/帧率，按当前输入信号协商）"
            : $"{ResolutionText}   ·   {PixelText}   ·   {FpsText} fps";

        /// <summary>用于去重的键（同一分辨率+格式+帧率只留一条）。</summary>
        public string Key => IsAuto ? "__AUTO__" : $"{Width}x{Height}|{PixelFormat}|{FpsMin:0.##}-{FpsMax:0.##}";
    }

    /// <summary>
    /// 「现在这根线正送进来什么」——由 ffmpeg 实际打开设备一小段时间读出来的真实信号。
    ///
    /// 与 <see cref="CaptureFormat"/>（设备**声称**支持哪些格式）是两码事：
    /// 能力表是一张菜单，实际信号才是端上来的那道菜。采不到画面的根因常常就出在这里 ——
    /// 拿菜单上最贵的一道去点，后厨未必有货。
    /// </summary>
    public sealed class CaptureSignalInfo
    {
        public bool HasSignal { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public double Fps { get; set; }
        public string PixelFormat { get; set; } = "";   // yuyv422 / nv12 …
        public string FourCc { get; set; } = "";        // YUY2 / MJPG …
        /// <summary>
        /// 实测是否为隔行信号。**三态**：
        ///   true  = 实测到隔行行结构
        ///   false = 实测为逐行
        ///   null  = 判不出来（画面纯色/静止无内容/采样不足）—— 与 false 是两码事，
        ///           调用方必须区别对待，不能把「不知道」当成「不是」。
        ///
        /// ⚠ 为什么不是直接从 ffmpeg 日志里 grep 关键字（2026-10-16 真机复测挖出的旧 Bug）：
        ///   dshow 采 rawvideo 时 ffmpeg **不输出任何场序标记** —— 输入行形如
        ///   `yuyv422(tv, bt470bg/bt709/unknown), 1920x1080, 60 fps`，压根没有 interlaced。
        ///   唯一能找到的 `progressive` 出现在 `Output #0` 行，那是 rawvideo 编码器
        ///   拿不到场序时的**默认假设**，不是信号属性。照它判 → 永远返回「逐行」，
        ///   真接上 1080i 源就是静默拉丝（面板还写着「逐行」）。
        ///   所以改成**看像素**：真隔行帧的奇偶行来自不同时刻的两个场，
        ///   「跨场相邻行」的像素差会显著大于「同场相邻行」的像素差。
        /// </summary>
        public bool? Interlaced { get; set; }

        /// <summary>
        /// 隔行判据的实测比值 R = 跨场行差 / 同场行差（0 = 没测）。
        /// 实机标定（2026-10-16）：已知逐行样本 0.80、本机实拍逐行 0.53、合成隔行 1.33~1.36。
        /// 面板会把它显示出来，用户能自己看判据离分界线有多远。
        /// </summary>
        public double InterlaceRatio { get; set; }

        /// <summary>参与隔行判定的有效帧数（画面纯色或无内容时为 0 → 判不出来）。</summary>
        public int InterlaceFrames { get; set; }

        public string Error { get; set; } = "";
        public string RawLine { get; set; } = "";

        /// <summary>场序的一句话说法（三态，面板与状态栏共用）。</summary>
        public string InterlaceText => Interlaced switch
        {
            true => "隔行（需去隔行）",
            false => "逐行",
            _ => "场序未能判定"
        };

        /// <summary>界面上一句话摘要。无信号时返回原因。</summary>
        public string Summary
        {
            get
            {
                if (!HasSignal)
                    return string.IsNullOrWhiteSpace(Error) ? "未检测到信号" : Error;
                var pix = string.IsNullOrWhiteSpace(FourCc) ? PixelFormat : FourCc;
                return $"{Width} × {Height}　·　{pix.ToUpperInvariant()}　·　{Fps:0.##} fps　·　{InterlaceText}";
            }
        }
    }

    /// <summary>一次设备枚举的结果（含遇到的错误，便于界面如实提示）。</summary>
    public sealed class CaptureEnumeration
    {
        public List<CaptureDevice> Devices { get; } = new();
        public string Error { get; set; } = "";
        public string RawLog { get; set; } = "";

        public bool HasVideo => Devices.Exists(d => d.IsVideo);
    }
}
