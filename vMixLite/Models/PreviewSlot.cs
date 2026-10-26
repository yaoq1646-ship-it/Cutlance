using System;
using System.Windows;
using LibVLCSharp.Shared;

namespace Cutlance.Models
{
    /// <summary>
    /// 预览槽（模块5 扩展：多路预览组合）。
    ///
    /// 老板 2026-10-17 的第 ③ 条：
    /// 「点击多个文件进预览框，应该是拉动不同文件都可以按自设大小和位置进行排列，
    ///   点击切换等按键可以所有画面一起输出到输出框。」
    ///
    /// ── 与输出图层 <see cref="Layer"/> 的关系 ──
    ///   · 两者都各有**独立 MediaPlayer**（预览组不会抢输出组的解码器），
    ///     所以「预览里摆好几路 → 一键推上输出」不会打断预览；
    ///   · 位置口径相同：都是归一化的左上角（0–1，相对画布宽/高）；
    ///   · ⚠ **缩放口径不同**：预览槽的 <see cref="ScaleX"/> 直接就是「元素宽 ÷ 画布宽」，
    ///     而 Layer 是「图层宽 = 画布宽 × 0.5 × ScaleX」。
    ///     于是同名转换关系是：`LayerScale = 2 × SlotScale`（推上输出时必须按这个换，否则大小会差一倍）。
    ///
    /// 2026-10-18 精调面板：宽高改成**可以分别设**（ScaleX / ScaleY），
    /// 于是需要「锁比例」来维持以前那种等比行为（默认锁着，与老行为一致）。
    /// </summary>
    public sealed class PreviewSlot : IFrameItem
    {
        public InputSource? Source { get; set; }

        /// <summary>专属播放器（由 VideoEngine 创建，走预览音频轨、默认静音）。</summary>
        public MediaPlayer? Player { get; set; }

        /// <summary>
        /// 该槽在音频总线上的轨道名（形如 "preview:3"）。
        /// ⚠ 必须记下来：槽被移除时要 <c>AudioBus.RemoveTrack(TrackKey)</c>，
        ///   否则每加一路就在总线上留一条永不回收的空轨道（反复摆预览会让轨道表无限膨胀）。
        /// </summary>
        public string? TrackKey { get; set; }

        /// <summary>
        /// 浮层里的选中框（金色描边）—— 只在被选中时显示边框，未选中时全透明。
        /// 与 <see cref="View"/> 分开是因为 View 是 Win32 子窗口宿主（HwndHost），
        /// 描边画上去看不见；必须画在「贴在预览框上的独立顶层浮层」里才有效。
        /// </summary>
        public FrameworkElement? HitBox { get; set; }

        /// <summary>
        /// 浮层里的**用户描边**（精调面板设的粗细/圆角/颜色，一直在）。
        /// 与 HitBox（选中态金色框）分开放，两者可以同时存在、互不干扰。
        /// </summary>
        public FrameworkElement? BorderEl { get; set; }

        /// <summary>预览画布里的画面容器（Grid：VideoView）。</summary>
        public UIElement? View { get; set; }

        public double X { get; set; } = 0;        // 归一化左上角（相对画布宽）
        public double Y { get; set; } = 0;        // 归一化左上角（相对画布高）

        /// <summary>宽占框比例（1 = 正好铺满）。</summary>
        public double ScaleX { get; set; } = 1;

        /// <summary>高占框比例（1 = 正好铺满）。</summary>
        public double ScaleY { get; set; } = 1;

        /// <summary>
        /// 等比缩放别名：读 = 宽度比例；写 = 宽高一起设。
        /// 老代码（滚轮缩放、分屏布局、推上输出×2）都按「一个数」用，这里保持兼容。
        /// </summary>
        public double Scale
        {
            get => ScaleX;
            set { ScaleX = value; ScaleY = value; }
        }

        // ==================== 几何换算（2026-10-18 起）====================
        //
        // 老板批准的「VLC Scale 方案」：**窗口不长大，画面在窗口里放大**。
        // 所以对外报「元素该占多大」时一律走 ViewScale*（>1 的部分已经交给 VLC 了），
        // 而 Zoom 是交给 VLC 的放大倍数。
        //
        // ⚠ 所有算元素矩形的地方（渲染、命中、选中框、吸附、精调面板）都必须用 ViewScale*，
        //   直接拿 ScaleX 去算会让选中框/命中区比真实画面大出一圈（4× 时大 4 倍）。

        /// <summary>元素实际宽度占框比例（上限 1：超出的放大由 VLC 负责）。</summary>
        public double ViewScaleX => Math.Min(ScaleX, 1.0);

        /// <summary>元素实际高度占框比例（上限 1）。</summary>
        public double ViewScaleY => Math.Min(ScaleY, 1.0);

        /// <summary>交给 VLC 的画面放大倍数（1 = 不放大）。</summary>
        public double Zoom => Math.Max(1.0, Math.Max(ScaleX, ScaleY));

        public int Z { get; set; } = 1;

        // —— 精调面板（2026-10-18）——
        /// <summary>锁宽高比（默认锁：与以前等比行为一致）。</summary>
        public bool AspectLock { get; set; } = true;
        public double BorderThickness { get; set; }
        public double BorderRadius { get; set; }
        public string BorderColor { get; set; } = "#FF33C6F5";

        public string DisplayName => Source?.Name ?? "空";

        // ==================== IFrameItem ====================
        public string PanelTitle => $"预览 · {(Source == null ? "空" : Source.Name)}";
        public double WidthFrac { get => ScaleX; set => ScaleX = value; }
        public double HeightFrac { get => ScaleY; set => ScaleY = value; }
        public bool IsPreviewItem => true;
        public double MinFrac => 0.1;
        public double MaxFrac => 4.0;
    }
}
