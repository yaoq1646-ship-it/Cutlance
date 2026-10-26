using System;
using LibVLCSharp.Shared;

namespace Cutlance.Services
{
    /// <summary>
    /// 画面内放大（老板 2026-10-18 批准的「VLC Scale 方案」）。
    ///
    /// ── 为什么不能用「把 HwndHost 元素撑大」来放大 ──
    ///   2026-10-18 实测：元素从 646×1154 撑到 2584×4616 时，WPF 侧确实变大了，
    ///   但 VLC 的 Direct3D11 输出窗**不会跟着重新排版**（实测 2× 时画面只按 0.75 倍
    ///   画在子窗底部，4× 时只露出画面最底下一条）。表现为「数值变了、画面却糊/缺一块」。
    ///   根因在 vout 的 D3D 交换链按创建时的尺寸固定，改窗口不重建。
    ///
    /// ── 所以改成 ──
    ///   窗口尺寸**永远不超过取景框**（元素只按 ≤1 的那部分缩小），
    ///   放大一律走 VLC 自己的 scale：画面在同一个窗口里被放大，
    ///   超出窗口的部分由窗口边界天然裁掉 —— 正好就是「取景框」语义，
    ///   还顺带省掉 4× 时 2584×4616 那么大一块 D3D 表面。
    ///
    /// scale 取值：0 = 自动（铺满窗口，保持比例），>1 = 按倍数放大。
    /// </summary>
    public static class VlcZoom
    {
        /// <summary>把缩放倍数落到播放器上（1 = 不放大）。异常一律吞掉：画面问题不该让导播崩。</summary>
        public static void Apply(MediaPlayer? player, double zoom)
        {
            if (player == null) return;
            try
            {
                float z = (float)Math.Clamp(zoom, 1.0, 8.0);
                float want = z <= 1.001f ? 0f : z;   // 0 = 交给 VLC 自动铺满
                if (Math.Abs(player.Scale - want) > 0.001f) player.Scale = want;
            }
            catch { /* 老 VLC / 非视频轨：忽略 */ }
        }
    }
}
