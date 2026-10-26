using System;
using System.Globalization;

namespace Cutlance.Models
{
    /// <summary>
    /// 人体美学比例测算（模块9）：按老板给定的三条美学标准，把「人体测量数据」
    /// 换算成美体滑块的建议值（瘦身 / 拉长）。
    ///
    /// 三条标准（A = 实际值，I = 理想值）：
    ///   ① 黄金比例（上下身比）：以肚脐为界，下身长 / 身高 ≈ 0.618（等价于 上身/下身 ≈ 0.618）
    ///   ② 腰臀比 WHR：腰围 / 臀围 ≈ 0.70（女性；男性取 0.90）
    ///   ③ 腿身比（马氏躯干腿长指数）：(身高 − 坐高) / 坐高 × 100 ∈ [74.4, 94.4]
    ///      老板口径：腿长占身高 ≈ 54% ~ 57%（本模型两口径并列显示）
    ///
    /// 建议值映射（经验尺度，见下方常量；数值越大表示需要越强的形变）：
    ///   拉长 ← 腿身比偏差 与 黄金比例偏差 加权
    ///   瘦身 ← 腰臀比偏差
    ///
    /// ⚠ 诚实说明：当前美体是「整幅画面」形变，垂直拉伸/水平压缩会同时作用于全身，
    ///   因此**不能精确改变人体解剖比例**，建议值是「视觉改善强度」而非「达标换算值」。
    ///   若要真正改变腰臀比 / 腿身比，需改用分段局部形变（腰段压缩、腿段拉伸），
    ///   那是另一个档次的实现（需手动标定腰线位置）。
    /// </summary>
    public sealed class BodyProportion
    {
        // ================= 理想值常量 =================
        public const double IdealLowerRatio = 0.618;      // 黄金比例：下身 / 身高
        public const double IdealWhrFemale = 0.70;        // 女性理想腰臀比
        public const double IdealWhrMale = 0.90;          // 男性理想腰臀比
        public const double ManouvrierMin = 74.4;         // 马氏指数理想区间下沿
        public const double ManouvrierMax = 94.4;         // 马氏指数理想区间上沿
        public const double LegRatioMin = 0.54;           // 腿长占身高理想下沿
        public const double LegRatioMax = 0.57;           // 腿长占身高理想上沿
        public const double LowerRatioTolerance = 0.02;   // 黄金比例达标容差 ±2%
        public const double WhrTolerance = 0.03;          // 腰臀比达标容差 ±0.03

        // 建议值映射尺度：偏差达到该值时建议值为满档（10）
        private const double LegRatioFullScale = 0.10;    // 腿身比差 10 个百分点 → 满档
        private const double ManouvrierFullScale = 20;    // 马氏指数差 20 → 满档
        private const double GoldenFullScale = 0.06;      // 黄金比例差 0.06 → 满档
        private const double WhrFullScale = 0.15;         // 腰臀比差 0.15 → 满档
        private const double StretchWeightLeg = 0.6;      // 综合拉长权重：腿身比
        private const double StretchWeightGolden = 0.4;   // 综合拉长权重：黄金比例

        // ================= 输入（单位：厘米）=================
        public double Height { get; set; } = 170;         // 身高
        public double NavelHeight { get; set; } = 103;    // 肚脐高（脚底 → 肚脐）= 下身长
        public double SittingHeight { get; set; } = 90;   // 坐高（马氏指数必需）
        public double Waist { get; set; } = 72;           // 腰围
        public double Hip { get; set; } = 95;             // 臀围
        public bool IsFemale { get; set; } = true;        // 性别（决定腰臀比理想值）

        /// <summary>
        /// 腿身比建议值采用的口径：
        ///   false → 「腿长占身高 54%~57%」（老板口径；数值直观，标准偏严）
        ///   true  → 「马氏躯干腿长指数 74.4~94.4」（学术标准；由身高与坐高直接算出）
        /// 说明：两个口径并不等价——马氏 74.4~94.4 换算成身高占比约 42.7%~48.6%，
        ///       而 54%~57% 按身高占比是明显更高的标准，故本模型同时展示、可切换取用。
        /// </summary>
        public bool UseManouvrierForLeg { get; set; }

        // ================= ① 黄金比例 =================
        /// <summary>下身长 / 身高（理想 0.618）。</summary>
        public double LowerRatio => Height > 0 ? NavelHeight / Height : 0;

        /// <summary>上/下身比值 = 上身 / 下身（理想 0.618）。</summary>
        public double UpperLowerRatio =>
            NavelHeight > 0 ? Math.Max(0, Height - NavelHeight) / NavelHeight : 0;

        /// <summary>黄金比例状态：-1 偏短，0 达标，+1 偏长。</summary>
        public int GoldenState =>
            LowerRatio <= 0 ? 0 :
            LowerRatio < IdealLowerRatio - LowerRatioTolerance ? -1 :
            LowerRatio > IdealLowerRatio + LowerRatioTolerance ? 1 : 0;

        public string GoldenStateText => GoldenState switch
        {
            -1 => $"下身偏短（差 {((IdealLowerRatio - LowerRatio) / IdealLowerRatio * 100):0.0}%）",
            1 => "下身偏长（优于标准）",
            _ => "达标（视觉协调）"
        };

        // ================= ② 腰臀比 =================
        /// <summary>腰臀比 = 腰围 / 臀围。</summary>
        public double Whr => Hip > 0 ? Waist / Hip : 0;

        /// <summary>当前性别对应的理想腰臀比。</summary>
        public double IdealWhr => IsFemale ? IdealWhrFemale : IdealWhrMale;

        /// <summary>腰臀比状态：-1 偏低（曲线感弱），0 达标，+1 偏高。</summary>
        public int WhrState =>
            Whr <= 0 ? 0 :
            Whr < IdealWhr - WhrTolerance ? -1 :
            Whr > IdealWhr + WhrTolerance ? 1 : 0;

        public string WhrStateText => WhrState switch
        {
            1 => $"偏高（超 {((Whr - IdealWhr) / IdealWhr * 100):0.0}%，建议收腰）",
            -1 => "偏低（曲线感偏弱，无需瘦身）",
            _ => "达标（曲线与健康兼顾）"
        };

        // ================= ③ 腿身比 / 马氏指数 =================
        /// <summary>腿长 = 身高 − 坐高。</summary>
        public double LegLength => Math.Max(0, Height - SittingHeight);

        /// <summary>马氏躯干腿长指数 = 腿长 / 坐高 × 100（理想 74.4 ~ 94.4）。</summary>
        public double Manouvrier => SittingHeight > 0 ? LegLength / SittingHeight * 100 : 0;

        /// <summary>腿长占身高比例（老板口径，理想 54% ~ 57%）。</summary>
        public double LegRatio => Height > 0 ? LegLength / Height : 0;

        /// <summary>马氏躯干腿长指数分级（Manouvrier 标准分级）。</summary>
        public string ManouvrierGrade
        {
            get
            {
                double m = Manouvrier;
                if (m <= 0) return "—";
                if (m < 74.9) return "超短腿型";
                if (m < 80.0) return "短腿型";
                if (m < 85.0) return "亚短腿型";
                if (m < 90.0) return "中腿型";
                if (m < 95.0) return "亚长腿型";
                if (m < 100.0) return "长腿型";
                return "超长腿型";
            }
        }

        /// <summary>腿身比状态：-1 偏短，0 达标，+1 偏长。</summary>
        public int LegState =>
            LegRatio <= 0 ? 0 :
            LegRatio < LegRatioMin ? -1 :
            LegRatio > LegRatioMax ? 1 : 0;

        public string LegStateText => LegState switch
        {
            -1 => "偏短（建议拉长）",
            1 => "偏长（优于标准）",
            _ => "达标（理想区间）"
        };

        // ================= 建议值（0 ~ 10，直接对应美体滑块）=================

        /// <summary>仅按「腿长占身高 54%~57%」口径换算的建议拉长强度。</summary>
        public int SuggestStretchByLegRatioOnly
        {
            get
            {
                double target = (LegRatioMin + LegRatioMax) / 2;   // 中值 0.555
                double gap = target - LegRatio;
                return gap <= 0 ? 0 : Clamp10(gap / LegRatioFullScale * 10);
            }
        }

        /// <summary>仅按「马氏躯干腿长指数 74.4~94.4」口径换算的建议拉长强度。</summary>
        public int SuggestStretchByManouvrierOnly
        {
            get
            {
                double target = (ManouvrierMin + ManouvrierMax) / 2;   // 84.4
                double gap = target - Manouvrier;
                return gap <= 0 ? 0 : Clamp10(gap / ManouvrierFullScale * 10);
            }
        }

        /// <summary>按当前所选口径（UseManouvrierForLeg）的建议拉长强度。</summary>
        public int SuggestStretchByLeg =>
            UseManouvrierForLeg ? SuggestStretchByManouvrierOnly : SuggestStretchByLegRatioOnly;

        /// <summary>由「黄金比例（上下身比）」偏差换算的建议拉长强度。</summary>
        public int SuggestStretchByGolden
        {
            get
            {
                double gap = IdealLowerRatio - LowerRatio;
                return gap <= 0 ? 0 : Clamp10(gap / GoldenFullScale * 10);
            }
        }

        /// <summary>综合建议拉长 = 腿身比 × 0.6 + 黄金比例 × 0.4（一个形变需同时兼顾两项）。</summary>
        public int SuggestStretch
        {
            get
            {
                double v = SuggestStretchByLeg * StretchWeightLeg +
                           SuggestStretchByGolden * StretchWeightGolden;
                return Clamp10(v);
            }
        }

        /// <summary>由「腰臀比」偏差换算的建议瘦身强度。</summary>
        public int SuggestSlim
        {
            get
            {
                double gap = Whr - IdealWhr;
                return gap <= 0 ? 0 : Clamp10(gap / WhrFullScale * 10);
            }
        }

        /// <summary>是否存在可给出的建议（任一项不达标）。</summary>
        public bool HasSuggestion => SuggestSlim > 0 || SuggestStretch > 0;

        /// <summary>一句话建议摘要（状态栏 / 窗口提示用）。</summary>
        public string SuggestSummary
        {
            get
            {
                if (!HasSuggestion) return "各项比例均达标，无需美体形变";
                var parts = new System.Collections.Generic.List<string>();
                if (SuggestSlim > 0) parts.Add($"瘦身 {SuggestSlim}");
                if (SuggestStretch > 0) parts.Add($"拉长 {SuggestStretch}");
                return "建议：" + string.Join(" · ", parts);
            }
        }

        /// <summary>输入是否完整可用。</summary>
        public bool IsValid =>
            Height > 50 && Height < 260 && NavelHeight > 20 && NavelHeight <= Height &&
            SittingHeight > 20 && SittingHeight < Height && Hip > 20 && Waist > 20;

        private static int Clamp10(double v) =>
            (int)Math.Clamp(Math.Round(v, MidpointRounding.AwayFromZero), 0, 10);

        private static string F(double v, string fmt) => v.ToString(fmt, CultureInfo.InvariantCulture);
    }
}
