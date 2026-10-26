using System;

namespace Cutlance.Models
{
    /// <summary>
    /// 「精调面板」的统一操作对象 —— 预览槽（<see cref="PreviewSlot"/>）与输出图层（<see cref="Layer"/>）
    /// 都实现它，于是同一个面板既能调预览也能调输出，不必写两套。
    ///
    /// ⚠ 两边的缩放口径仍然不同，由接口吃掉差异：
    ///   · 预览槽 WidthFrac = ScaleX（本来就直接是占框比例，1 = 铺满）；
    ///   · 输出图层 WidthFrac = 0.5 × ScaleX（图层基准尺寸是画布的 1/2，2 = 铺满）。
    ///   面板只认 WidthFrac/HeightFrac，永远 1.00 = 正好铺满，不会看晕。
    /// </summary>
    public interface IFrameItem
    {
        /// <summary>面板标题行显示的名字（含来源名）。</summary>
        string PanelTitle { get; }

        /// <summary>底层输入源（旋转/裁切要用它，作用于源）。可能为空。</summary>
        InputSource? Source { get; }

        /// <summary>归一化左上角（0–1，相对画布宽/高）。允许超出（取景框语义）。</summary>
        double X { get; set; }
        double Y { get; set; }

        /// <summary>元素宽占画布宽的比例（1.00 = 正好铺满整个框）。</summary>
        double WidthFrac { get; set; }

        /// <summary>元素高占画布高的比例（1.00 = 正好铺满整个框）。</summary>
        double HeightFrac { get; set; }

        /// <summary>锁定宽高比：改一边时另一边按当前比例跟着变。</summary>
        bool AspectLock { get; set; }

        /// <summary>描边粗细（0 = 无边框）。</summary>
        double BorderThickness { get; set; }

        /// <summary>描边圆角。</summary>
        double BorderRadius { get; set; }

        /// <summary>描边颜色（"#AARRGGBB"）。</summary>
        string BorderColor { get; set; }

        /// <summary>true = 预览槽；false = 输出图层。</summary>
        bool IsPreviewItem { get; }

        /// <summary>
        /// 不透明度 0–1（1 = 完全不透明）。2026-10-24 老板要求。
        /// 默认 1：预览槽不参与透明度（预览只是取景，把它调半透明没有意义），
        /// 只有 <see cref="Layer"/> 会覆盖它。
        /// </summary>
        double OpacityFrac { get => 1.0; set { } }

        /// <summary>该对象是否支持改不透明度（面板据此置灰滑杆）。</summary>
        bool SupportsOpacity => false;

        /// <summary>该对象的缩放范围（占框比例），面板按它钳制输入。</summary>
        double MinFrac { get; }
        double MaxFrac { get; }
    }

    /// <summary>精调面板里的一次编辑动作（宿主据此决定要重算什么）。</summary>
    public enum GeoEdit
    {
        /// <summary>位置（X / Y）。</summary>
        Position,
        /// <summary>尺寸（宽 / 高 / 锁比例）。</summary>
        Size,
        /// <summary>边框（粗细 / 圆角 / 颜色）。</summary>
        Border,
        /// <summary>重置为铺满整框。</summary>
        Reset,
        /// <summary>旋转（作用于源：0/90/180/270）。</summary>
        Rotation,
        /// <summary>裁切（作用于源：X1/Y1/X2/Y2 百分比）。</summary>
        Crop,
        /// <summary>不透明度（2026-10-24）：只重推合成器几何，不重开媒体。</summary>
        Opacity,
        /// <summary>去隔行开关（2026-10-24）：作用于源，要重开这一路媒体（预处理链要换）。</summary>
        Deinterlace
    }
}
