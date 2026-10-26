using System;
using System.ComponentModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Cutlance.Models
{
    /// <summary>叠加层类型：文字标题 / 图片（透明 PNG 最适合）。</summary>
    public enum OverlayType { Text, Image }

    /// <summary>
    /// 单个叠加通道（对标 vMix 29 的 8 路叠加通道，固定 8 个、编号 1–8）。
    /// 位置用百分比（0–100）表示，便于自适应不同分辨率。
    /// 每个通道按编号分配一个专属“通道色”，用于 OVERLAYS 面板高亮与编辑区配色。
    /// </summary>
    public sealed class OverlayItem : INotifyPropertyChanged
    {
        // 仿 vMix 各叠加通道配色（红/蓝/绿/橙/紫/青/棕/粉）
        private static readonly string[] Palette =
        {
            "#FFE57373", "#FF64B5F6", "#FF81C784", "#FFFFB74D",
            "#FFBA68C8", "#FF4DB6AC", "#FFA1887F", "#FFF06292"
        };

        private bool _visible = true;
        private bool _hasContent = false;
        private string _text = "文字标题";
        private double _xPct = 50;
        private double _yPct = 90;
        private int _fontSize = 36;
        private string _colorHex = "#FFFFFFFF";

        private int _channelIndex;

        /// <summary>
        /// 通道序号（1–8，固定 8 通道，与 vMix 一致）。
        /// ⚠ 可写且带变更通知：老板 2026-10-17 要求叠加层能上下拖动调层级，
        ///   拖动后按新顺序重排编号（位置 1 = 最上层），列表里的号与通道色都要跟着刷新 ——
        ///   所以这里不能像以前那样写成只读属性。
        /// </summary>
        public int ChannelIndex
        {
            get => _channelIndex;
            set { if (_channelIndex != value) { _channelIndex = value; Raise(); } }
        }

        /// <summary>是否已填充内容（空通道不渲染、不计数）。true 后才算“ON”。</summary>
        public bool HasContent
        {
            get => _hasContent;
            set { if (_hasContent != value) { _hasContent = value; Raise(); } }
        }

        /// <summary>专属通道色笔刷（按通道号轮询配色），用于面板高亮与编辑区配色。</summary>
        public Brush ChannelBrush =>
            new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                Palette[(ChannelIndex - 1 + Palette.Length) % Palette.Length]));

        /// <summary>通道是否“上线”（有内容且可见）。用于面板高亮判定。</summary>
        public bool IsOn => HasContent && Visible;

        public string Id { get; } = Guid.NewGuid().ToString("N");
        public OverlayType Type { get; set; } = OverlayType.Text;
        public string? ImagePath { get; set; }

        /// <summary>
        /// 字幕条的「实时画面」覆盖（老板 2026-10-19 22:57：「移动字幕条时同步在输出框里显示移动后效果」）。
        ///
        /// 样式窗里拖滑块时，主窗把重渲染好的位图直接挂在这里 → 输出框当场跟着动。
        /// 为什么不让样式窗落盘一个新的 PNG 再换 ImagePath：
        ///   · 拖一次滑块写一个文件，一分钟能写几百张垃圾图；
        ///   · VLC / BitmapImage 换源有延迟，跟手感很差。
        ///
        /// ⚠ 故意**不发 PropertyChanged**：那个通知接的是「SyncOverlays() 整树重建」，
        ///   每 50ms 重建一次叠加树画面会闪。实时换图走 MainWindow 里按 Tag 定位的局部替换。
        /// ⚠ 应用 / 取消 / 翻页时必须清回 null —— 否则会一直压着磁盘上的正式图。
        /// </summary>
        public BitmapSource? LiveImage { get; set; }

        public bool Visible
        {
            get => _visible;
            set { if (_visible != value) { _visible = value; Raise(); } }
        }
        public string Text
        {
            get => _text;
            set { if (_text != value) { _text = value; Raise(); } }
        }
        public double XPct
        {
            get => _xPct;
            set { if (_xPct != value) { _xPct = value; Raise(); } }
        }
        public double YPct
        {
            get => _yPct;
            set { if (_yPct != value) { _yPct = value; Raise(); } }
        }
        public int FontSize
        {
            get => _fontSize;
            set { if (_fontSize != value) { _fontSize = value; Raise(); } }
        }
        public string ColorHex
        {
            get => _colorHex;
            set { if (_colorHex != value) { _colorHex = value; Raise(); } }
        }

        // —— 文字字体（模块5：字体族 / 粗体 / 斜体）——
        private string _fontFamily = "微软雅黑";
        private bool _bold;
        private bool _italic;
        public string FontFamilyName
        {
            get => _fontFamily;
            set { if (_fontFamily != value) { _fontFamily = value; Raise(); } }
        }
        public bool Bold
        {
            get => _bold;
            set { if (_bold != value) { _bold = value; Raise(); } }
        }
        public bool Italic
        {
            get => _italic;
            set { if (_italic != value) { _italic = value; Raise(); } }
        }

        // —— 图片叠加：整体大小（百分比，100 = 最多铺满画布并保持比例；老板 2026-10-18 第 7 条）——
        private double _imageScale = 100;

        /// <summary>
        /// 图片整体缩放百分比（10–300）。100 = 按画布大小等比铺满（不裁切），
        /// 小于 100 缩小、大于 100 放大后仍保持宽高比。
        /// 只对 <see cref="OverlayType.Image"/> 生效，文字叠加忽略。
        /// </summary>
        public double ImageScale
        {
            get => _imageScale;
            set { if (Math.Abs(_imageScale - value) > 0.01) { _imageScale = value; Raise(); } }
        }

        /// <summary>列表里显示的简称；空通道显示“(空)”。</summary>
        public string DisplayName =>
            !HasContent ? "(空)"
            : Type == OverlayType.Text
                ? "文字：" + Text
                : "图片：" + (ImagePath == null ? "?" : Path.GetFileName(ImagePath));

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));

        /// <summary>
        /// UIA / 调试器里显示的可读名字。
        /// ⚠ 不重写的话，ListBox 列表项的自动化 Name 会退化成「Cutlance.Models.OverlayItem」，
        ///   读屏软件和自动化脚本都读不出一行到底是哪路通道。屏幕显示不受影响（走 DataTemplate）。
        /// </summary>
        public override string ToString() => "通道 " + ChannelIndex + " " + DisplayName;
    }
}
