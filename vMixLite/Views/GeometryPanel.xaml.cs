using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Cutlance.Models;

namespace Cutlance.Views
{
    /// <summary>
    /// 就地精调面板（老板 2026-10-18）：
    /// 「选中某一路后，面板贴在该路旁边或右栏浮出，含 X / Y、宽 / 高、旋转、裁切 X1Y1X2Y2、
    ///   边框（颜色、粗细、圆角）、锁定宽高比、重置。默认停靠右栏，可拖出成浮动大窗。」
    ///
    /// 设计要点：
    ///   · 面板本身**不含任何渲染逻辑**，只读写 <see cref="IFrameItem"/> 上的属性，
    ///     然后抛 <see cref="Edited"/> 让宿主（MainWindow）去钳制 + 重绘。
    ///     这样同一个控件既能放在右栏、也能放进浮动窗口，不用写两套。
    ///   · 数值口径统一成「占画框百分比」（100 = 正好铺满）：预览槽与输出图层的内部缩放口径不同，
    ///     但面板上看到的数字含义完全一致。
    ///   · 旋转 / 裁切是**输入源级**的（视频画面是 Win32 子窗口，WPF 的 RotateTransform 对它无效），
    ///     所以这两个操作会改到「这一路输入」，预览与输出同步变化 —— 界面上有明确标注。
    /// </summary>
    public partial class GeometryPanel : UserControl
    {
        // ⚠ 初值必须是 true：XAML 解析期会真实触发事件（ComboRotate 的 IsSelected="True"
        //   就会在 InitializeComponent 里抛 SelectionChanged），此刻字段都还没准备好。
        private bool _loading = true;

        private IFrameItem? _target;

        // Refresh() 时记下"界面上显示的就是这些值"。
        // 用途：切换目标时控件会先失焦再重填，此时 LostFocus 会拿旧文字往新对象上写。
        // 只要文字与快照一致就说明用户没改过，直接跳过提交 —— 这一条挡掉了最隐蔽的一类串值。
        private string _snapX = "", _snapY = "", _snapW = "", _snapH = "", _snapBt = "", _snapBr = "";

        /// <summary>描边颜色循环盘（点击色块依次切换）。</summary>
        private static readonly string[] Palette =
        {
            "#FF33C6F5",   // 青（与预览框同色）
            "#FFFFFFFF",   // 白
            "#FFF0B93B",   // 金
            "#FFFF4D5E",   // 红
            "#FF000000"    // 黑
        };

        public GeometryPanel()
        {
            InitializeComponent();
            _loading = false;
            SetFieldsEnabled(false);
            UpdateSwatch();
        }

        /// <summary>数值或结构被改过（宿主据此钳制位置 + 重绘）。</summary>
        public event Action<GeoEdit>? Edited;

        /// <summary>点了「浮出」——宿主负责把面板搬到一个可移动的大窗口里。</summary>
        public event Action? PopOutRequested;

        /// <summary>展开/收起状态变了（宿主可据此调整行高或状态栏提示）。</summary>
        public event Action<bool>? CollapsedChanged;

        /// <summary>当前操作对象；设为 null 即"未选中"。</summary>
        public IFrameItem? Target
        {
            get => _target;
            set
            {
                bool wasEmpty = _target == null;
                _target = value;

                // 「选中某一路后面板浮出」：首次选中时自动展开。
                // 但如果用户主动收起了它，就别再自作聪明地弹回来（会被当成界面抽风）。
                if (value != null && wasEmpty && !_userCollapsed) SetExpanded(true);
                Refresh();
            }
        }

        private bool _userCollapsed;

        /// <summary>是否显示「浮出」按钮（浮动窗口里不需要再浮出）。</summary>
        public bool ShowPopOut
        {
            set => BtnPopOut.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>是否显示「收起」按钮（浮动窗口里固定展开）。</summary>
        public bool ShowCollapse
        {
            set => BtnCollapse.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>面板是否展开（收起后只剩标题行，不占用法版面）。</summary>
        public bool IsExpanded => Body.Visibility == Visibility.Visible;

        public void SetExpanded(bool on)
        {
            if (Body == null) return;
            Body.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (BtnCollapse != null) BtnCollapse.Content = on ? "收起" : "展开";
            CollapsedChanged?.Invoke(on);
        }

        /// <summary>旋转角度（度），供宿主应用到输入源。</summary>
        public int RotationDegrees
        {
            get
            {
                if (ComboRotate.SelectedItem is ComboBoxItem it && it.Tag is string t
                    && int.TryParse(t, out var deg)) return deg;
                return 0;
            }
        }

        /// <summary>裁切四边百分比（X1 左 / Y1 上 / X2 右 / Y2 下）。</summary>
        public (double L, double T, double R, double B) CropPercents
            => (Read(BoxCropL), Read(BoxCropT), Read(BoxCropR), Read(BoxCropB));

        /// <summary>从当前 <see cref="Target"/> 重新读一遍到界面上。</summary>
        public void Refresh()
        {
            _loading = true;
            try
            {
                bool on = _target != null;
                SetFieldsEnabled(on);
                TitleText.Text = on ? _target!.PanelTitle : "（未选中）";

                if (!on)
                {
                    foreach (var tb in new[] { BoxX, BoxY, BoxW, BoxH, BoxBorder, BoxRadius, BoxShadow, BoxShadowDeep })
                        if (tb != null) tb.Text = "";
                    if (RangeHint != null) RangeHint.Text = "范围 —（未选中）";
                    _snapX = _snapY = _snapW = _snapH = _snapBt = _snapBr = "";
                    return;
                }

                var t = _target!;
                // 最小/最大范围跟着目标类型走（预览槽与图层目前同为 10–400%，但别写死，
                // 以后某一边单独调范围时这里自动跟上）
                if (RangeHint != null)
                    RangeHint.Text = $"范围 {Pct(t.MinFrac)}–{Pct(t.MaxFrac)}%（100 = 正好铺满）";
                // 正在输入的框不覆盖（否则输到一半被打断）
                if (!BoxX.IsKeyboardFocusWithin) BoxX.Text = Pct(t.X);
                if (!BoxY.IsKeyboardFocusWithin) BoxY.Text = Pct(t.Y);
                if (!BoxW.IsKeyboardFocusWithin) BoxW.Text = Pct(t.WidthFrac);
                if (!BoxH.IsKeyboardFocusWithin) BoxH.Text = Pct(t.HeightFrac);
                if (!BoxBorder.IsKeyboardFocusWithin) BoxBorder.Text = t.BorderThickness.ToString("0.#");
                if (!BoxRadius.IsKeyboardFocusWithin) BoxRadius.Text = t.BorderRadius.ToString("0.#");

                // 阴影（2026-10-25）：只有输出图层有，预览槽不参与（取景框调阴影没意义）
                if (t is Layer lay)
                {
                    if (!BoxShadow.IsKeyboardFocusWithin) BoxShadow.Text = lay.Shadow.ToString("0.#");
                    if (!BoxShadowDeep.IsKeyboardFocusWithin) BoxShadowDeep.Text = lay.ShadowOpacity.ToString("0");
                    if (ShadowHint != null)
                        ShadowHint.Text = lay.Shadow <= 0 ? "0 = 无阴影" : "四周均匀";

                    // 填充方式（异形屏）：用 Tag 匹配，别按索引 —— 以后插一项就全错位
                    if (ComboFit != null)
                    {
                        foreach (ComboBoxItem it in ComboFit.Items)
                            if (it.Tag is string s && int.TryParse(s, out var fm) && fm == lay.FitMode)
                            { ComboFit.SelectedItem = it; break; }
                    }
                    if (FitHint != null)
                        FitHint.Text = lay.FitMode == 0 ? "留黑边" : "已铺满";
                }
                ChkLock.IsChecked = t.AspectLock;

                // 旋转：取源上的实际角度
                int rot = t.Source?.Rotate ?? 0;
                foreach (ComboBoxItem it in ComboRotate.Items)
                    if (it.Tag is string s && int.TryParse(s, out var d) && d == rot)
                    { ComboRotate.SelectedItem = it; break; }

                // 裁切：取源上的实际值
                var src = t.Source;
                if (!BoxCropL.IsKeyboardFocusWithin) BoxCropL.Text = Pct((src?.CropPctLeft ?? 0) / 100.0);
                if (!BoxCropT.IsKeyboardFocusWithin) BoxCropT.Text = Pct((src?.CropPctTop ?? 0) / 100.0);
                if (!BoxCropR.IsKeyboardFocusWithin) BoxCropR.Text = Pct((src?.CropPctRight ?? 0) / 100.0);
                if (!BoxCropB.IsKeyboardFocusWithin) BoxCropB.Text = Pct((src?.CropPctBottom ?? 0) / 100.0);

                // 没有输入源时旋转/裁切不可用（这两项作用于源）
                ComboRotate.IsEnabled = src != null;
                BtnCrop.IsEnabled = src != null;

                // 不透明度（2026-10-24）：预览槽不支持 → 滑杆置灰并显示 100%
                bool opOn = t.SupportsOpacity;
                SldOpacity.IsEnabled = opOn;
                if (!opOn) { SldOpacity.Value = 100; OpacityVal.Text = "—"; }
                else
                {
                    SldOpacity.Value = Math.Round(t.OpacityFrac * 100.0);
                    OpacityVal.Text = SldOpacity.Value <= 0 ? "全透明" : SldOpacity.Value.ToString("0") + "%";
                }

                // 去隔行（2026-10-24）：作用于源，没有源的槽位不可用
                if (ChkDeinterlace != null)
                {
                    ChkDeinterlace.IsEnabled = src != null;
                    ChkDeinterlace.IsChecked = src?.Deinterlace == true;
                    if (DeinterlaceHint != null)
                        DeinterlaceHint.Text = src?.NeedDeinterlace == true
                            ? "已启用（这一路走 yadif 逐行输出）"
                            : "1080i 有横丝才勾；逐行源勾了会变糊";
                }

                _snapX = BoxX.Text; _snapY = BoxY.Text;
                _snapW = BoxW.Text; _snapH = BoxH.Text;
                _snapBt = BoxBorder.Text; _snapBr = BoxRadius.Text;

                UpdateSwatch();
            }
            finally { _loading = false; }
        }

        // ==================== 内部 ====================

        private void SetFieldsEnabled(bool on)
        {
            foreach (var c in new Control?[]
                     { BoxX, BoxY, BoxW, BoxH, BoxBorder, BoxRadius, ChkLock, ComboRotate, BtnCrop, BtnReset, SldOpacity })
                if (c != null) c.IsEnabled = on;

            // 阴影 / 填充只对输出图层有效：预览槽上没有这两个概念（同「不透明度」的取舍）
            bool isLayer = on && _target is Layer;
            if (BoxShadow != null) BoxShadow.IsEnabled = isLayer;
            if (BoxShadowDeep != null) BoxShadowDeep.IsEnabled = isLayer;
            if (ComboFit != null) ComboFit.IsEnabled = isLayer;
        }

        private static string Pct(double frac) => (frac * 100.0).ToString("0.##", CultureInfo.CurrentCulture);

        private static double Read(TextBox tb)
        {
            if (tb == null) return 0;
            var s = (tb.Text ?? "").Trim().TrimEnd('%', '％');
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out var v)) return v;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
            return 0;
        }

        /// <summary>能解析才算数。空着/乱打的时候必须保持原值 —— 否则"清空后点别处"会把尺寸压成 0.1。</summary>
        private static bool TryRead(TextBox tb, out double v)
        {
            v = 0;
            if (tb == null) return false;
            var s = (tb.Text ?? "").Trim().TrimEnd('%', '％');
            if (s.Length == 0) return false;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out v)
                || double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        private void UpdateSwatch()
        {
            if (ColorSwatch == null) return;
            try { ColorSwatch.Background = new SolidColorBrush(ParseColor(_target?.BorderColor)); }
            catch { ColorSwatch.Background = Brushes.Transparent; }
        }

        private static Color ParseColor(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return Color.FromRgb(0x33, 0xC6, 0xF5);
            try { return (Color)ColorConverter.ConvertFromString(hex); }
            catch { return Color.FromRgb(0x33, 0xC6, 0xF5); }
        }

        /// <summary>把位置/尺寸/描边数字提交给目标，然后抛事件让宿主重绘。</summary>
        private void CommitNumbers()
        {
            if (_loading || _target == null) return;

            // 文字与上次刷新时一模一样 = 用户没改过（切换目标时的失焦也会走到这里）→ 不提交
            if (BoxX.Text == _snapX && BoxY.Text == _snapY && BoxW.Text == _snapW
                && BoxH.Text == _snapH && BoxBorder.Text == _snapBt && BoxRadius.Text == _snapBr)
                return;

            var t = _target;

            double oldW = t.WidthFrac, oldH = t.HeightFrac;
            double ratio = oldW > 1e-6 ? oldH / oldW : 1.0;

            // 解析失败的框保持原值（空白不等于 0）
            double x = TryRead(BoxX, out var vx) ? vx / 100.0 : t.X;
            double y = TryRead(BoxY, out var vy) ? vy / 100.0 : t.Y;
            double w = TryRead(BoxW, out var vw) ? Math.Clamp(vw / 100.0, t.MinFrac, t.MaxFrac) : oldW;
            double h = TryRead(BoxH, out var vh) ? Math.Clamp(vh / 100.0, t.MinFrac, t.MaxFrac) : oldH;
            double bt = TryRead(BoxBorder, out var vb) ? Math.Max(0, Math.Round(vb)) : t.BorderThickness;
            double br = TryRead(BoxRadius, out var vr) ? Math.Max(0, Math.Round(vr)) : t.BorderRadius;

            bool wChanged = Math.Abs(w - oldW) > 1e-9;
            bool hChanged = Math.Abs(h - oldH) > 1e-9;

            if (t.AspectLock && (wChanged || hChanged))
            {
                // 只改了一边 → 另一边按当前比例跟上；两边都改（清空重填）→ 以宽为准。
                // ⚠ 撞到上下限时必须**同时**把驱动边拉回来，否则会出现
                //   「宽被钳到 400%、高还按 1:9 算出 3600 然后再被钳到 400%」→ 比例悄悄变成 1:1。
                if (wChanged)
                {
                    h = w * ratio;
                    if (h > t.MaxFrac) { h = t.MaxFrac; w = h / ratio; }
                    else if (h < t.MinFrac) { h = t.MinFrac; w = h / ratio; }
                }
                else
                {
                    w = h / ratio;
                    if (w > t.MaxFrac) { w = t.MaxFrac; h = w * ratio; }
                    else if (w < t.MinFrac) { w = t.MinFrac; h = w * ratio; }
                }
                w = Math.Clamp(w, t.MinFrac, t.MaxFrac);
                h = Math.Clamp(h, t.MinFrac, t.MaxFrac);
            }

            t.X = x; t.Y = y;
            t.WidthFrac = w; t.HeightFrac = h;
            t.BorderThickness = bt; t.BorderRadius = br;

            // 钳制后必须回显：用户手输 6000% 而实际上限是 400% 时，框里要显示 400。
            // 否则会出现"看着 6000、实际 400"的错位，下一次微调完全对不上数。
            if (EchoClamped(BoxW, w)) _snapW = BoxW.Text;
            if (EchoClamped(BoxH, h)) _snapH = BoxH.Text;

            Edited?.Invoke((wChanged || hChanged) ? GeoEdit.Size : GeoEdit.Position);
        }

        /// <summary>框里的数与被钳制后的实际值不一致时，把实际值写回框里。返回是否写过。</summary>
        private static bool EchoClamped(TextBox tb, double frac)
        {
            if (tb == null) return false;
            string text = Pct(frac);
            if (tb.Text == text) return false;
            if (!TryRead(tb, out var shown)) return false;
            double shownFrac = shown / 100.0;
            // 数值上已经一致（例如 400 与 400.0）就不用动，避免打断用户正在输入的写法
            if (Math.Abs(shownFrac - frac) < 1e-9) return false;
            tb.Text = text;
            return true;
        }

        // ==================== 事件 ====================

        private void Num_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                CommitNumbers();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                Refresh();     // 反悔：恢复成目标当前值
                Keyboard.ClearFocus();
                e.Handled = true;
            }
        }

        private void Num_LostFocus(object sender, RoutedEventArgs e)
        {
            // 失焦即提交：输完直接去拖画面也不会丢输入（与制作条上的缩放框同一习惯）
            if (!IsLoaded) return;
            CommitNumbers();
        }

        /// <summary>
        /// 填充方式（2026-10-25 异形屏）。只改合成器的绘制矩形 / 源裁剪，
        /// 不重开媒体 —— 抛 Border 让宿主重推几何即可，直播中切换画面不闪。
        /// </summary>
        private void ComboFit_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _target == null) return;
            if (!(_target is Layer lay)) return;
            if (ComboFit?.SelectedItem is ComboBoxItem it && it.Tag is string s && int.TryParse(s, out var fm))
            {
                if (lay.FitMode == fm) return;
                lay.FitMode = fm;
                if (FitHint != null) FitHint.Text = fm == 0 ? "留黑边" : "已铺满";
                Edited?.Invoke(GeoEdit.Border);
            }
        }

        /// <summary>
        /// 阴影（2026-10-25）：扩散粗细 + 浓淡。失焦即提交，与其它数字框同一习惯。
        /// 抛 <see cref="GeoEdit.Border"/> —— 宿主只重推几何给合成器，不重开媒体，
        /// 所以直播中改阴影画面不会闪。
        /// </summary>
        private void Shadow_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_loading || !IsLoaded || !(_target is Layer lay)) return;

            double sp = TryRead(BoxShadow, out var v1) ? v1 : lay.Shadow;
            double dp = TryRead(BoxShadowDeep, out var v2) ? v2 : lay.ShadowOpacity;
            double oldS = lay.Shadow;
            int oldD = lay.ShadowOpacity;

            lay.Shadow = sp;
            lay.ShadowOpacity = (int)Math.Round(dp);

            if (Math.Abs(lay.Shadow - oldS) < 1e-6 && lay.ShadowOpacity == oldD) return;
            Edited?.Invoke(GeoEdit.Border);
        }

        private void ChkLock_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading || _target == null) return;
            _target.AspectLock = ChkLock.IsChecked == true;
        }

        /// <summary>
        /// 不透明度滑杆（2026-10-24）。拖动时连续触发 —— 只改数值 + 抛事件，
        /// 宿主那边只是重推几个 double 给合成器（不重开媒体），所以拖起来是实时的。
        /// </summary>
        private void SldOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (OpacityVal != null)
                OpacityVal.Text = e.NewValue <= 0 ? "全透明" : e.NewValue.ToString("0") + "%";

            if (_loading || _target == null || !_target.SupportsOpacity) return;
            _target.OpacityFrac = e.NewValue / 100.0;
            Edited?.Invoke(GeoEdit.Opacity);
        }

        private void ColorSwatch_Click(object sender, MouseButtonEventArgs e)
        {
            if (_target == null) return;
            string cur = _target.BorderColor ?? "";
            int i = Array.FindIndex(Palette, p => string.Equals(p, cur, StringComparison.OrdinalIgnoreCase));
            _target.BorderColor = Palette[(i + 1 + Palette.Length) % Palette.Length];
            UpdateSwatch();
            Edited?.Invoke(GeoEdit.Border);
            e.Handled = true;
        }

        private void ComboRotate_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _target?.Source == null) return;
            int deg = RotationDegrees;
            if (_target.Source.Rotate == deg) return;
            _target.Source.Rotate = deg;
            Edited?.Invoke(GeoEdit.Rotation);
        }

        /// <summary>
        /// 去隔行开关（2026-10-24）。改的是**这一路输入**：预览框、输出图层、播放列表一起生效。
        /// 勾/取消都会重开这一路媒体（预处理链要重建），所以画面会闪一下 —— 这是预期的。
        /// </summary>
        private void ChkDeinterlace_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading || _target?.Source == null) return;
            bool on = ChkDeinterlace.IsChecked == true;
            if (_target.Source.Deinterlace == on) return;
            _target.Source.Deinterlace = on;
            if (DeinterlaceHint != null)
                DeinterlaceHint.Text = on ? "已启用（这一路走 yadif 逐行输出）"
                                          : "1080i 有横丝才勾；逐行源勾了会变糊";
            Edited?.Invoke(GeoEdit.Deinterlace);
        }

        private void BtnCrop_Click(object sender, RoutedEventArgs e)
        {
            CropBox.Visibility = CropBox.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;
        }

        private void BtnCropApply_Click(object sender, RoutedEventArgs e)
        {
            var src = _target?.Source;
            if (src == null) return;

            var (l, t, r, b) = CropPercents;

            // 四边各自 0–90；左右、上下**相加**也不能超过 90（否则裁成负数/空画面）。
            // 超了按比例缩回 90 —— 像滑块撞到边界一样静默纠正，并把纠正后的数写回框里，
            // 免得用户以为"输了 60 却只生效 45"。
            (l, r) = FitPair(l, r, 90);
            (t, b) = FitPair(t, b, 90);

            BoxCropL.Text = Pct(l / 100.0);
            BoxCropT.Text = Pct(t / 100.0);
            BoxCropR.Text = Pct(r / 100.0);
            BoxCropB.Text = Pct(b / 100.0);

            src.UsePctCrop = (l + t + r + b) > 0.01;
            src.CropPctLeft = l; src.CropPctTop = t;
            src.CropPctRight = r; src.CropPctBottom = b;
            Edited?.Invoke(GeoEdit.Crop);
        }

        /// <summary>一对（左/右、上/下）裁切量：各自夹到 0–90，且加起来不超过 maxSum。</summary>
        private static (double, double) FitPair(double a, double b, double maxSum)
        {
            a = Math.Clamp(a, 0, 90);
            b = Math.Clamp(b, 0, 90);
            double s = a + b;
            if (s <= maxSum) return (a, b);
            double k = maxSum / s;
            return (a * k, b * k);
        }

        private void BtnCropClear_Click(object sender, RoutedEventArgs e)
        {
            var src = _target?.Source;
            if (src == null) return;
            src.UsePctCrop = false;
            src.CropPctLeft = src.CropPctTop = src.CropPctRight = src.CropPctBottom = 0;
            src.CropTop = src.CropBottom = src.CropLeft = src.CropRight = 0;
            Refresh();
            Edited?.Invoke(GeoEdit.Crop);
        }

        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            if (_target == null) return;
            _target.X = 0; _target.Y = 0;
            _target.WidthFrac = 1; _target.HeightFrac = 1;
            Edited?.Invoke(GeoEdit.Reset);
        }

        private void BtnPopOut_Click(object sender, RoutedEventArgs e) => PopOutRequested?.Invoke();

        private void BtnCollapse_Click(object sender, RoutedEventArgs e)
        {
            SetExpanded(!IsExpanded);
            _userCollapsed = !IsExpanded;   // 手动收起 → 以后不再自动弹开
        }
    }
}
