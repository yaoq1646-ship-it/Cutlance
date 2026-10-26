using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Cutlance.Models
{
    /// <summary>
    /// 画面效果：调色 / 美颜（美白·磨皮）/ 美体（分段局部形变 · 整幅近似形变）。
    /// 全部由 FFmpeg 滤镜链落地，作用于「播出框」整幅画面（抓屏取样），
    /// 因此效果会同时进入推流与录制文件。按老板要求仅在编码器=英伟达 NVENC 时启用。
    ///
    /// 滤镜映射：
    ///   调色  → eq(brightness/contrast/saturation/gamma) + colorbalance(色温)
    ///   美白  → eq 提亮 + 轻微降对比
    ///   磨皮  → gblur(sigma)
    ///   分段局部形变（默认）→ remap 坐标映射：腰段横向压窄 + 腿段纵向拉长，头胸不动
    ///   整幅近似（可选）    → 瘦身 lenscorrection(k1 负值)；拉长 scale 垂直放大 + crop 裁回
    /// </summary>
    public sealed class VideoFx
    {
        // —— 调色 ——
        public double Brightness { get; set; } = 0;    // -0.30 ~ +0.30（eq brightness 增量）
        public double Contrast { get; set; } = 1;      // 0.50 ~ 2.00 倍
        public double Saturation { get; set; } = 1;    // 0.00 ~ 2.50 倍
        public double Gamma { get; set; } = 1;         // 0.50 ~ 2.50 倍
        public double Temperature { get; set; } = 0;   // -1（冷/偏蓝） ~ +1（暖/偏黄）

        // —— 美颜（0 = 关闭，10 = 最强）——
        public int Whiten { get; set; } = 0;           // 美白
        public int Smooth { get; set; } = 0;           // 磨皮

        // —— 美体·整幅近似（0 = 关闭，10 = 最强；近似形变，非液化级）——
        public int Slim { get; set; } = 0;             // 瘦身（整幅桶形）
        public int Stretch { get; set; } = 0;          // 拉长（整幅垂直放大）

        // —— 美体·分段局部形变（模块10，默认方式）——
        // 用 FFmpeg remap 做坐标映射：腰段（腰线→臀线）整段横向压窄、腿段（臀线以下）纵向拉长，
        // 头部与胸部保持原样，过渡带羽化，所以是"真的分段"，不是整幅近似。
        public bool SegmentedBody { get; set; } = true;    // true = 分段局部，false = 整幅近似

        // —— 腰线 / 臀线位置（画面高度 %，从顶部算）——
        //
        // 老板 2026-10-22：竖屏要单独一套默认值。
        //
        // 为什么不能共用一套：这两个数原本按「人物顶天立地、头顶 0%、脚底 100%」构图定的，
        // 而人体比例是：腰最细处（肚脐）≈ 身高 38–42%（从头顶算），胯最宽 ≈ 身高 50–53%。
        // 竖屏直播的人物通常比横屏更大更靠下（常见「121」构图：顶部 1/4 留白、中部半身占 1/2），
        // 同一组百分比在两个方向上落点差很多。
        //
        // 竖屏值 52 / 66 的来源（2026-10-22 实测，见 `验证截图/p17_腰臀线三组对比.png`）：
        //   用 `竖屏_裁剪.mp4`（1080×1920，走秀人物占画高约 78%）渲染三组对照 ——
        //     46/62 → 红线压在胸下，腰段实际收的是「胸下到胯」，腰没收到；
        //     52/64 → 红线正压裙腰，蓝线在裙摆最宽处，最贴合；
        //     62/68 → 已经压到大腿根，收的不是腰是大腿。
        //   取 52 / 66（臀线比实测 64 低 2 个点，兼顾常见七分身构图人物更大）。
        public const double WaistY_Landscape = 46;         // 横屏出厂值（已过 7/7 回归，别乱动）
        public const double HipY_Landscape = 62;
        public const double WaistY_Portrait = 52;          // 竖屏出厂值
        public const double HipY_Portrait = 66;

        public double WaistY { get; set; } = WaistY_Landscape;
        public double HipY { get; set; } = HipY_Landscape;

        /// <summary>该方向对应的出厂腰/臀线。</summary>
        public static (double Waist, double Hip) LineDefaults(bool portrait)
            => portrait ? (WaistY_Portrait, HipY_Portrait) : (WaistY_Landscape, HipY_Landscape);

        /// <summary>
        /// 腰臀线是否还停在某一套出厂值上（= 用户没手动调过）。
        /// 两套都算「没动过」，这样从横屏切到竖屏时能干净地换过去。
        /// </summary>
        public bool LinesAtFactoryPreset =>
            (Math.Abs(WaistY - WaistY_Landscape) < 0.01 && Math.Abs(HipY - HipY_Landscape) < 0.01) ||
            (Math.Abs(WaistY - WaistY_Portrait) < 0.01 && Math.Abs(HipY - HipY_Portrait) < 0.01);

        /// <summary>
        /// 按输出方向套用出厂腰/臀线。**只对「没手动调过」的画面生效**，
        /// 用户自己拖过的值一律不覆盖（否则调好的参数会被静默改掉）。
        /// 返回是否真的改了。
        /// </summary>
        public bool ApplyOrientationDefaults(bool portrait, bool force = false)
        {
            if (!force && !LinesAtFactoryPreset) return false;
            var (w, h) = LineDefaults(portrait);
            bool changed = Math.Abs(WaistY - w) > 0.01 || Math.Abs(HipY - h) > 0.01;
            WaistY = w;
            HipY = h;
            return changed;
        }
        public int SlimSeg { get; set; } = 0;              // 腰段横向收缩强度 0~10
        public int LegSeg { get; set; } = 0;               // 腿段纵向拉伸强度 0~10
        public double WarpCenterX { get; set; } = 50;      // 形变作用中心（画面宽度 %）
        public double WarpWidth { get; set; } = 70;        // 形变作用宽度（画面宽度 %）

        /// <summary>
        /// 节目内容在播出框里的**有效宽高比**（宽 ÷ 高），0 = 未知（按整帧处理）。
        ///
        /// 老板 2026-10-18 第 4 条：「美颜美体要根据进来的信号适应竖屏」。
        /// 播出框是 1080×1920 竖屏、而信号是 16:9 横屏时，画面只占中间一条，
        /// 上下各一大块黑边；而腰线/臀线原本按**整帧**高度百分比算，
        /// 46% 会落到黑边上或落错位置 —— 形变就整段错位。
        ///
        /// 有了这个值就能把腰线/臀线、作用中心/宽度都映射进「真正有画面的那条带」，
        /// 黑边永远不动（黑边被拉长/压窄会露出接缝，比不动更难看）。
        /// 该值不落工程文件，每次开始输出时由主窗按当前节目内容算一次。
        /// </summary>
        public double ContentAspect { get; set; } = 0;

        /// <summary>
        /// 算出播出框内「真正有画面」的矩形（像素）：x, y, w, h。
        /// <paramref name="aspect"/> ≤ 0 或与帧比例一致时返回整帧。
        /// </summary>
        public static (double X, double Y, double W, double H) ActiveRect(int outW, int outH, double aspect)
        {
            double fw = outW, fh = outH;
            if (aspect <= 0.01 || outW < 8 || outH < 8) return (0, 0, fw, fh);

            double frameAspect = (double)outW / outH;
            if (Math.Abs(aspect - frameAspect) < 0.01) return (0, 0, fw, fh);

            if (aspect < frameAspect)
            {
                // 内容比框更"瘦"（如竖屏框里放横屏信号）→ 上下留黑边
                double aw = outH * aspect;
                return ((outW - aw) / 2, 0, aw, fh);
            }
            else
            {
                // 内容比框更"宽" → 左右留黑边
                double ah = outW / aspect;
                return (0, (outH - ah) / 2, fw, ah);
            }
        }

        /// <summary>当前播出框里的有效画面矩形（用本对象的 ContentAspect）。</summary>
        public (double X, double Y, double W, double H) ActiveRect(int outW, int outH) =>
            ActiveRect(outW, outH, ContentAspect);

        /// <summary>当前是否需要 remap 坐标映射（分段模式且腰段/腿段强度非 0）。</summary>
        public bool WarpActive => SegmentedBody && (SlimSeg > 0 || LegSeg > 0);

        /// <summary>是否设置了任何非默认效果。</summary>
        public bool Any =>
            Math.Abs(Brightness) > 0.0001 || Math.Abs(Contrast - 1) > 0.0001 ||
            Math.Abs(Saturation - 1) > 0.0001 || Math.Abs(Gamma - 1) > 0.0001 ||
            Math.Abs(Temperature) > 0.0001 || Whiten > 0 || Smooth > 0 ||
            Slim > 0 || Stretch > 0 || WarpActive;

        public void Reset()
        {
            Brightness = 0; Contrast = 1; Saturation = 1; Gamma = 1; Temperature = 0;
            Whiten = 0; Smooth = 0; Slim = 0; Stretch = 0;
            SlimSeg = 0; LegSeg = 0;
            WaistY = 46; HipY = 62; WarpCenterX = 50; WarpWidth = 70;
            SegmentedBody = true;
        }

        /// <summary>界面/状态栏用的一句话摘要。</summary>
        public string Summary
        {
            get
            {
                if (!Any) return "未启用画面效果";
                var parts = new List<string>();
                if (Math.Abs(Brightness) > 0.0001) parts.Add($"亮度{Brightness * 100:+0;-0;0}");
                if (Math.Abs(Contrast - 1) > 0.0001) parts.Add($"对比{Contrast * 100:0}%");
                if (Math.Abs(Saturation - 1) > 0.0001) parts.Add($"饱和{Saturation * 100:0}%");
                if (Math.Abs(Gamma - 1) > 0.0001) parts.Add($"伽马{Gamma:0.00}");
                if (Math.Abs(Temperature) > 0.0001) parts.Add(Temperature > 0 ? "偏暖" : "偏冷");
                if (Whiten > 0) parts.Add($"美白{Whiten}");
                if (Smooth > 0) parts.Add($"磨皮{Smooth}");
                if (SegmentedBody)
                {
                    if (SlimSeg > 0) parts.Add($"腰段收缩{SlimSeg}");
                    if (LegSeg > 0) parts.Add($"腿段拉伸{LegSeg}");
                }
                else
                {
                    if (Slim > 0) parts.Add($"瘦身{Slim}");
                    if (Stretch > 0) parts.Add($"拉长{Stretch}");
                }
                return string.Join(" · ", parts);
            }
        }

        private static string F(double v) =>
            v.ToString("0.####", CultureInfo.InvariantCulture);

        /// <summary>
        /// 生成「缩放 + 整幅近似形变 + 美颜 + 调色」的逗号滤镜链（不含 remap 分段形变）。
        /// 第一个元素固定是把画面缩放到输出分辨率的 scale。
        /// </summary>
        private List<string> ChainParts(int outW, int outH)
        {
            var chain = new List<string> { $"scale={outW}:{outH}" };

            // ① 美体·拉长（仅整幅模式）：垂直放大后从上下各裁一半，画面高度回到 outH（人显高）
            if (!SegmentedBody && Stretch > 0)
            {
                double k = Stretch / 10.0 * 0.20;              // 最多 +20%
                int bigH = (int)Math.Round(outH * (1 + k));
                if (bigH > outH)
                {
                    chain.Add($"scale={outW}:{bigH}");
                    chain.Add($"crop={outW}:{outH}:0:{(bigH - outH) / 2}");
                }
            }

            // ② 美体·瘦身（仅整幅模式）：桶形畸变（k1 负值）→ 中心区域被压缩，视觉更瘦
            if (!SegmentedBody && Slim > 0)
            {
                double k1 = -0.02 * Slim;                      // 最多 -0.20
                chain.Add($"lenscorrection=cx=0.5:cy=0.5:k1={F(k1)}:k2=0");
            }

            // ③ 美颜·磨皮：轻微高斯模糊抹平皮肤细节
            // ⚠ sigma 必须随「有效画面高度」缩放：竖屏 1920 高比横屏 1080 高多出 78% 像素，
            //   固定 sigma 在竖屏下几乎看不出磨皮（老板 2026-10-18 第 4 条：要适应竖屏）。
            if (Smooth > 0)
            {
                double ah = ActiveRect(outW, outH).H;
                double k = Math.Clamp(ah / 1080.0, 0.6, 2.0);
                chain.Add($"gblur=sigma={F(0.25 * Smooth * k)}");
            }

            // ④ 调色：eq（含美白增量）+ colorbalance（色温）
            double brightness = Brightness + 0.010 * Whiten;   // 美白 = 提亮
            double contrast = Contrast * (1 - 0.008 * Whiten); // 美白略微降对比，肤色更透
            if (Math.Abs(brightness) > 0.0001 || Math.Abs(contrast - 1) > 0.0001 ||
                Math.Abs(Saturation - 1) > 0.0001 || Math.Abs(Gamma - 1) > 0.0001)
            {
                chain.Add($"eq=brightness={F(brightness)}:contrast={F(contrast)}:" +
                          $"saturation={F(Saturation)}:gamma={F(Gamma)}");
            }
            if (Math.Abs(Temperature) > 0.0001)
            {
                double t = 0.15 * Temperature;                 // 暖 +：加红减蓝
                chain.Add($"colorbalance=rm={F(t)}:bm={F(-t)}");
            }

            return chain;
        }

        /// <summary>
        /// 生成 FFmpeg 视频滤镜链（-vf 内容，无 remap 分段形变时使用）。已含缩放到输出分辨率的 scale。
        /// </summary>
        public string BuildVideoFilter(int outW, int outH) =>
            string.Join(",", ChainParts(outW, outH));

        /// <summary>
        /// 生成完整的视频滤镜图（-filter_complex 片段），返回形如 "[0:v]scale=…[vout]" 的一段。
        ///
        /// 分段局部形变（模块10）需要用 remap 滤镜：它的第 2 / 3 个输入是坐标映射图，
        /// 因此调用方必须把映射图作为额外的视频输入喂进来，并把它们的标签（如 "[1:v]"、"[2:v]"）传进来。
        /// </summary>
        public string BuildVideoGraph(int outW, int outH, string srcLabel, string dstLabel,
            string? xmapLabel = null, string? ymapLabel = null)
        {
            var parts = ChainParts(outW, outH);

            if (!WarpActive || string.IsNullOrWhiteSpace(xmapLabel) || string.IsNullOrWhiteSpace(ymapLabel))
                return srcLabel + string.Join(",", parts) + dstLabel;

            var sb = new System.Text.StringBuilder();
            sb.Append(srcLabel).Append(parts[0]).Append("[vs];");                 // 先缩放到输出分辨率
            sb.Append("[vs]").Append(xmapLabel).Append(ymapLabel).Append("remap[w];");  // 分段局部形变
            var rest = parts.Skip(1).ToList();
            sb.Append("[w]").Append(rest.Count > 0 ? string.Join(",", rest) : "null").Append(dstLabel);
            return sb.ToString();
        }
    }
}
