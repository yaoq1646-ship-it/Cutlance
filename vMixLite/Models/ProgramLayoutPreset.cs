using System.Collections.Generic;

namespace Cutlance.Models
{
    /// <summary>
    /// 输出布局预设（P3，2026-10-22）：「把输出框现在这一整套摆法起个名存下来，一键切回来」。
    ///
    /// ── 与已有的 LayoutPreset（.vpreset）有什么不同 ──
    /// · .vpreset 存的是**预览框**里那几路的位置与大小，口径是"占框比例"（1.00 = 铺满预览框），
    ///   用途是排版复用，不绑定"哪一格播什么"。
    /// · 本类存的是**输出框**那一整套槽位：几何 + 层级 + 描边 + **每一格绑定的输入**，
    ///   口径是图层口径（2.00 = 铺满画布）。用途是直播中一键换整套版面。
    /// 两者互不影响，不要合并进同一个文件/同一个列表（口径不同，混用必然算错）。
    ///
    /// ── 为什么绑定要记三项 ──
    /// 输入编号（#N）会随工程重新载入而变化，名字可能被改，路径最稳定但采集设备没有路径。
    /// 所以按「路径 → 编号+名字 → 名字」三级认，全认不到就把那一格留空（几何仍然保留），
    /// 状态栏报数。空着比塌掉好：导出时该有一块 PPT 的地方还是应该有一块黑。
    /// </summary>
    public sealed class ProgramLayoutPreset
    {
        public string Name { get; set; } = "";

        /// <summary>保存时间（仅记录，界面可显示"什么时候存的"）。</summary>
        public string SavedAt { get; set; } = "";

        /// <summary>
        /// 这套布局自带的转场类型（2026-10-22 老板要求）。存枚举名 "Cut"/"Fade"/"AlphaFade"。
        /// 为什么用字符串而不是枚举：以后加新转场、或读到旧版本文件里没有的值时，
        /// 解析失败可以安全退回 Cut，而枚举反序列化失败会直接抛。
        /// </summary>
        public string Transition { get; set; } = "Cut";

        /// <summary>转场时长（秒）。四宫格这类硬切更利落，换主讲人适合 0.6–1.0s。</summary>
        public double TransitionSeconds { get; set; } = 1.0;

        // ── 导出文件用的头（2026-10-22）──
        /// <summary>写文件的程序名。只认 "Cutlance"，别的值当作不是本程序的布局文件。</summary>
        public string App { get; set; } = "Cutlance";

        /// <summary>文件格式版本。加字段时递增，读旧文件靠缺省值兼容。</summary>
        public int Version { get; set; } = 1;

        /// <summary>整套槽位（顺序即列表顺序，层级看每格自己的 Z）。</summary>
        public List<ProgramPresetCell> Cells { get; set; } = new();

        /// <summary>
        /// 混音台的八路角色档快照（2026-10-23）。
        /// 为什么进布局：一套版面往往对应一套声音 —— "双人对话"是两支麦都出声、"主讲满屏"只留主讲。
        /// 老布局文件没这一段（null）→ **不动混音台**，绝不猜着把别人的声音关掉。
        /// </summary>
        public List<ProgramPresetMixerChannel>? Mixer { get; set; }

        /// <summary>输出总线压缩档快照（2026-10-23）。null（老文件）= 不动压缩档。</summary>
        public ProgramPresetCompressor? Compressor { get; set; }

        /// <summary>
        /// 倒计时牌的显示样式快照（2026-10-24）。
        /// 只存「长什么样、摆在哪、是否叠加」——**不存设定时长**：
        /// 时长是这一场的业务参数，切布局顺手把它改回 10 分钟会把正在用的倒计时弄乱。
        /// null（老布局）= 一个字都不动。
        /// </summary>
        public ProgramPresetClock? Clock { get; set; }
    }

    /// <summary>布局预设里的倒计时牌样式（2026-10-24）。</summary>
    public sealed class ProgramPresetClock
    {
        /// <summary>叠加字号（1080p 基准 40–300）。</summary>
        public double FontSize { get; set; } = 120;

        /// <summary>字体名（空 = 出厂 Consolas）。</summary>
        public string FontFamily { get; set; } = "";

        /// <summary>粗体 / 斜体（可空：老文件没有就不动）。</summary>
        public bool? Bold { get; set; }
        public bool? Italic { get; set; }

        /// <summary>文字色 / 底板色（#AARRGGBB，空 = 不动）。</summary>
        public string TextColor { get; set; } = "";
        public string BackColor { get; set; } = "";

        /// <summary>数字中心位置（归一化 0–1，0.5/0.5 = 正中）。</summary>
        public double X { get; set; } = 0.5;
        public double Y { get; set; } = 0.5;

        /// <summary>切到这套布局时倒计时是否叠加进输出画面。</summary>
        public bool OverlayOn { get; set; }
    }

    /// <summary>
    /// 布局预设里的一路混音通道（2026-10-23）。
    /// 按**设备名**认领：换工程 / 换机器后设备名不变，编号会变，所以不记编号。
    /// </summary>
    public sealed class ProgramPresetMixerChannel
    {
        public string DeviceName { get; set; } = "";

        /// <summary>"main" / "secondary" / "background"。</summary>
        public string Role { get; set; } = "main";

        /// <summary>滑杆百分比（0–200）。</summary>
        public int Volume { get; set; } = 100;

        public bool Muted { get; set; }

        /// <summary>
        /// 这一路自己的压缩档（2026-10-24）："Off"/"Light"/"Medium"/"Strong"。
        /// 老布局没有 → 空串 → 关闭（不动这一路）。
        /// </summary>
        public string Compress { get; set; } = "";
    }

    /// <summary>布局预设里的压缩档快照。字段与 AudioCompressor 一一对应（存字符串，坏值可安全退回关闭）。</summary>
    public sealed class ProgramPresetCompressor
    {
        public string Level { get; set; } = "off";
        public double ThresholdDb { get; set; } = -18;
        public double Ratio { get; set; } = 3;
        public double MakeupDb { get; set; } = 2;
    }

    /// <summary>输出布局预设里的一格。几何口径 = 输出图层口径（2.00 = 铺满画布）。</summary>
    public sealed class ProgramPresetCell
    {
        /// <summary>输入编号（左侧列表里的 #N）—— 与名字一起作第二级匹配依据。</summary>
        public int SourceNumber { get; set; }

        /// <summary>输入名 —— 第二/三级匹配依据。</summary>
        public string SourceName { get; set; } = "";

        /// <summary>素材路径 / 设备标识 —— 首选匹配依据（最稳）。</summary>
        public string SourcePath { get; set; } = "";

        /// <summary>左上角（归一化 0–1，相对画布宽/高）。</summary>
        public double X { get; set; }
        public double Y { get; set; }

        /// <summary>缩放（输出图层口径：2.00 = 正好铺满画布）。</summary>
        public double ScaleX { get; set; } = 2.0;
        public double ScaleY { get; set; } = 2.0;

        /// <summary>层级（越大越靠上）。</summary>
        public int Z { get; set; } = 1;

        public bool AspectLock { get; set; } = true;
        public double BorderThickness { get; set; }
        public double BorderRadius { get; set; }
        public string BorderColor { get; set; } = "#FF33C6F5";

        /// <summary>
        /// 这一格的不透明度 0–100（2026-10-24）。默认 100 = 不透明；
        /// 老布局文件没有这个字段 → 保留默认 100，切过去的画面与升级前一致。
        /// </summary>
        public int Opacity { get; set; } = 100;

        /// <summary>阴影扩散粗细（2026-10-25；0 = 无阴影，老布局缺字段时的表现）。</summary>
        public double Shadow { get; set; }

        /// <summary>阴影浓淡 0–100（2026-10-25）。</summary>
        public int ShadowOpacity { get; set; } = 60;

        /// <summary>填充方式（2026-10-25 异形屏）：0 等比内接 / 1 铺满裁切 / 2 拉伸铺满。老布局 → 0。</summary>
        public int FitMode { get; set; }

        /// <summary>这一格是不是出声的那一格（一般只有一格）。</summary>
        public bool IsAudioSource { get; set; }

        /// <summary>
        /// 这一格的声音大小百分比（0–150，2026-10-22 老板要求）。
        /// 存"值"而不是"相对值"：切布局时整组原样还原，包括"这场我特意把某格压到 80%"。
        /// 老布局文件没这个字段 → 反序列化默认 100，正好是原音量，不用特判。
        /// </summary>
        public int Volume { get; set; } = 100;

        /// <summary>
        /// 这一格在混音里演什么角色（"Main"/"Secondary"/"Background"，2026-10-23）。
        /// 用字符串存而不是枚举：布局 JSON 让人看得懂，而且以后加档位时老文件不会解析失败。
        /// 老布局没有这个字段 → 空串 → 按主音处理（不衰减），切过去音量与以前一致。
        /// </summary>
        public string AudioRole { get; set; } = "";
    }
}
