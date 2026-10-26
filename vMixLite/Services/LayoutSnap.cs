using System;
using System.Collections.Generic;

namespace Cutlance.Services
{
    /// <summary>
    /// 智能吸附的纯计算部分（老板 2026-10-18）：
    /// 「拖到画布边缘或另一块边缘 6px 内自动对齐、显示青色参考线」。
    ///
    /// 这里只做数学，不碰任何 UI —— 输入是「元素的一条边（或中心）在画布上的归一化位置」，
    /// 输出是「需要偏移多少 + 吸到了哪条线」。这样既能给预览框用，也能给别人用，且好测。
    ///
    /// 三个吸附点：左/上边、中心、右/下边 —— 任意一个靠近对齐线都算命中。
    /// 命中时取「位移最小」的那个，避免同时有多条候选线时来回跳。
    /// </summary>
    public static class LayoutSnap
    {
        /// <summary>默认吸附阈值（DIP / 屏幕像素）。6px 是手感与"不打架"的折中。</summary>
        public const double DefaultTolerance = 6.0;

        /// <summary>
        /// 沿一个轴求吸附量。
        /// </summary>
        /// <param name="start">元素起始边（归一化，0–1）</param>
        /// <param name="length">元素长度（归一化）</param>
        /// <param name="targets">候选对齐线（归一化）</param>
        /// <param name="tolerance">容差（归一化，调用方用 6px ÷ 画布宽/高 换算）</param>
        /// <param name="line">命中的那条对齐线（没命中 = null），用于画参考线</param>
        /// <returns>需要加到 start 上的偏移量（没命中 = 0）</returns>
        public static double Solve(double start, double length, IReadOnlyList<double> targets,
                                   double tolerance, out double? line)
        {
            line = null;
            if (targets == null || targets.Count == 0) return 0;

            // 三个吸附点：起始边、中心、结束边
            double[] probes = { start, start + length / 2.0, start + length };

            double bestAbs = double.MaxValue;
            double bestDelta = 0;
            double? bestLine = null;

            foreach (double p in probes)
            {
                foreach (double t in targets)
                {
                    double d = t - p;
                    double ad = Math.Abs(d);
                    if (ad > tolerance) continue;
                    // 严格小于：多个候选打平时保留先遇到的（线序稳定 → 不会左右乱跳）
                    if (ad < bestAbs - 1e-9) { bestAbs = ad; bestDelta = d; bestLine = t; }
                }
            }

            line = bestLine;
            return bestLine == null ? 0 : bestDelta;
        }

        /// <summary>
        /// 构造「画框」的标准对齐线：四边 + 中心 + 三分线。
        /// 三分线放在这里是因为构图本来就按三分法，拖到那儿自动吸住比手动对齐省事。
        /// </summary>
        public static List<double> FrameLines(double step = 1.0 / 3.0)
        {
            var list = new List<double> { 0.0, 1.0 };
            if (step > 0.01 && step < 0.5)
            {
                for (double v = step; v < 1.0 - 1e-6; v += step) list.Add(v);
            }
            list.Add(0.5);
            list.Sort();
            return list;
        }
    }
}
