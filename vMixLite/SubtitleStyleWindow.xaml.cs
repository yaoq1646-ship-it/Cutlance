using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Cutlance.Services;

namespace Cutlance
{
    /// <summary>
    /// 字幕条样式窗口（老板 2026-10-19 21:42：大小 / 位置可调 + 文字颜色可换）。
    ///
    /// 交互口径：
    ///   · 所有滑块／颜色框都是**实时**改预览，窗口里那条示例字幕当场就变；
    ///   · 点「应用」才真正重新生成所有字幕条的 PNG（行多时要几秒，主窗那边给状态栏提示）；
    ///   · 点「取消」一个像素都不改；「恢复默认」把界面回滚成出厂参数（仍要看「应用」才生效）。
    ///
    /// ⚠ XAML 解析期就会触发 ValueChanged / TextChanged（赋值即触发），
    ///   那一刻后面的控件还没建出来 —— 所以 `_loading` 初值必须是 true，
    ///   所有处理器第一行判它。这是本项目第 13 条铁律，踩过一次。
    /// </summary>
    public partial class SubtitleStyleWindow : Window
    {
        private bool _loading = true;
        private bool _previewDirty = true;
        private readonly DispatcherTimer _previewTimer;

        private TextBox? _colorTarget;
        private Border? _swatchTarget;

        /// <summary>预览用的示例行。主窗会传「第一条字幕的真实内容」进来，看着更贴近实播。</summary>
        public string[] SampleCells { get; set; } = TableData.SampleRow();

        /// <summary>
        /// 整张表（老板 2026-10-19 22:15：「应该出现表格前 现在 后 表格里的文字，
        /// 防止上字幕出现错误」）。主窗导入过表格才有；为 null 时对照区整块隐藏。
        /// </summary>
        public TableData? Table { get; set; }

        /// <summary>**正在上屏**的那一条的行号（0 基）。注意它和「当前预览哪一条」是两件事。</summary>
        public int CurrentIndex { get; set; }

        /// <summary>当前预览的是第几条（0 基）。点「前一条 / 现在 / 后一条」只改这个。</summary>
        private int _previewRow;
        private int[] _peekIndexes = Array.Empty<int>();
        private bool _peekBuilt;

        /// <summary>
        /// 「应用」之后的有效样式。
        /// ⚠ 名字不能叫 `Style` —— 那会遮蔽 `FrameworkElement.Style`，
        ///   XAML 里 `Style="{StaticResource AppWindow}"` 就会和它打架（编译期 CS0108）。
        /// </summary>
        public SubtitleStyle ResultStyle { get; private set; } = new();

        /// <summary>
        /// 拖动过程中把当前样式实时推给主窗（老板 2026-10-19 22:57：
        /// 「移动字幕条时同步在输出框里显示移动后效果」）。
        ///
        /// 触发节奏 = 本窗预览的同一个 50ms 节流，不会更密。
        /// 主窗收到后用「当前正在上屏那一行」重渲染一张，直接换到输出框的画布上。
        /// ⚠ 这里只是**看效果**，点「应用」才真正重生成所有字幕条；点「取消」主窗会还原。
        /// </summary>
        public event Action<SubtitleStyle>? LiveStyleChanged;

        /// <summary>对字幕友好的 16 个预设色（白/灰系够字幕用，其余给强调）。</summary>
        private static readonly string[] PaletteColors =
        {
            "#FFFFFFFF", "#FFDAE2EC", "#FFB9C4D0", "#FF8A9AAB",
            "#FFFFD54F", "#FFFFB300", "#FFFF8A65", "#FFEF5350",
            "#FFF06292", "#FFBA68C8", "#FF64B5F6", "#FF4DD0E1",
            "#FF81C784", "#FFC5E1A5", "#FF10141A", "#FF2B3644",
        };

        /// <summary>同一个调试口（仅 Debug 版写 %TEMP%\cutlance-live.log）。</summary>
        private static void LiveLog(string msg)
        {
#if DEBUG
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "cutlance-live.log"),
                    $"{DateTime.Now:HH:mm:ss.fff}   [style] {msg}\r\n");
            }
            catch { }
#endif
        }

        public SubtitleStyleWindow(SubtitleStyle? style = null)
        {
            InitializeComponent();
            FitToWorkArea();

            // 最高权限（老板 2026-10-21 第 1 条）：置顶 + 不透明 + 抓屏排除 + Esc 关闭。
            // 字幕条编辑器以前漏装 —— 导播时开着播出框，这个窗口会被压在下面看不见，
            // 更糟的是它若恰好盖在播出框上，还会被一起抓进推流/录制。
            WorkFrame.Pin(this);

            ResultStyle = (style ?? new SubtitleStyle()).Clone();
            ApplyToControls(ResultStyle);

            // 拖动滑块时事件很密，1920×1080 的离屏渲染一次十几毫秒 —— 用 50ms 节流合并，
            // 否则拖条会明显发涩。
            _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _previewTimer.Tick += (_, _) =>
            {
                if (!_previewDirty) return;
                _previewDirty = false;
                RefreshPreview();

                // 顺手把同一份样式推给输出框 —— 窗里预览和节目画面永远同步
                try
                {
                    LiveLog($"push: handlers={LiveStyleChanged?.GetInvocationList().Length ?? 0}");
                    LiveStyleChanged?.Invoke(ReadStyle());
                }
                catch (Exception ex) { LiveLog("push FAIL " + ex.Message); /* 主窗那边出问题不能把调样式的人卡住 */ }
            };

            Loaded += (_, _) =>
            {
                PlaceOnLeftOfOwner();     // 老板 2026-10-19 23:24：贴左侧，且不压住输出框
                _loading = false;
                BuildPalette();
                BuildRowPeek();
                RefreshPreview();
                _previewTimer.Start();
            };
            Closed += (_, _) => _previewTimer.Stop();
        }

        // ================= 控件 <-> 样式 =================

        /// <summary>
        /// 贴在主窗左侧显示，而且**绝不压住输出（节目）监视器**。
        ///
        /// 老板 2026-10-19 23:24：「字幕条样式出现位置应该在左侧，不覆盖输出框」
        /// —— 原来用 CenterOwner 正好盖在屏幕中间，把右边的输出画面挡掉一半；
        ///   而这个窗存在的意义就是「一边拖一边看输出框里的效果」，挡住就完全失去意义。
        ///
        /// 做法：
        ///   ① 左上角对齐主窗内侧（跟着主窗走，主窗挪到哪就跟到哪）；
        ///   ② 拿「节目框相对主窗左缘的偏移」当宽度硬上限，超了就收窄自身
        ///      （内容都在 ScrollViewer 里，窄一点照样滚得到每一行，不会点不到按钮）；
        ///   ③ 主窗真的在屏幕上时，再把自身夹进屏幕可用区（1366×768@125% 也不越界）。
        ///</summary>
        private void PlaceOnLeftOfOwner()
        {
            try
            {
                var wa = SystemParameters.WorkArea;
                double left = wa.Left + 8;
                double top = wa.Top + 8;
                double pgmOffset = 0;      // 节目框相对主窗左缘的偏移（离屏也能算，不受绝对坐标影响）
                double pgmLeft = 0;

                if (Owner is Window ow && ow.IsLoaded && ow.WindowState != WindowState.Minimized)
                {
                    left = ow.Left + 8;
                    top = ow.Top + 8;

                    // 输出监视器左缘 = 可用宽度的硬上限（留 18 DIP：8 自身留白 + 10 不贴边）
                    if (ow is MainWindow mw)
                    {
                        pgmLeft = mw.ProgramCellLeftDip;
                        if (pgmLeft != 0)
                        {
                            pgmOffset = pgmLeft - ow.Left;
                            if (pgmOffset > 100)     // 合理值才用，防止布局未完成时算出垃圾
                            {
                                double limit = pgmOffset - 18;
                                if (limit >= MinWidth && limit < Width) Width = limit;
                            }
                        }
                    }

                    // 只有主窗确实在屏幕上时才夹屏幕可用区；
                    // 离屏（--offscreen 把主窗搬到 -12000）或主窗在别的显示器时不要硬拽回主屏
                    bool onPrimary = ow.Left > wa.Left - 50 && ow.Left < wa.Right - 200
                                  && ow.Top > wa.Top - 50 && ow.Top < wa.Bottom - 200;
                    if (onPrimary)
                    {
                        left = Math.Max(wa.Left + 8, left);
                        top = Math.Max(wa.Top + 8, top);
                        if (left + Width > wa.Right - 8) left = Math.Max(wa.Left + 8, wa.Right - 8 - Width);
                        double maxH = wa.Bottom - 8 - top;
                        if (maxH >= MinHeight && Height > maxH) Height = maxH;
                    }
                }

                Left = left;
                Top = top;

#if DEBUG
                // 调试口：定位结果写盘（离屏验证没法直接量窗口坐标，靠这行取真话）
                try
                {
                    File.AppendAllText(Path.Combine(Path.GetTempPath(), "cutlance-live.log"),
                        $"{DateTime.Now:HH:mm:ss.fff}   [place] left={Left:0} top={Top:0} " +
                        $"w={Width:0} h={Height:0} pgmOffset={pgmOffset:0} " +
                        $"right={Left + Width:0} pgmLeft={pgmLeft:0}\r\n");
                }
                catch { }
#endif
            }
            catch
            {
                // 定位失败就让它留在系统默认位置 —— 千万不要因为摆位置抛异常导致窗口打不开
            }
        }

        /// <summary>
        /// 把窗口夹进当前屏幕可用区。
        /// 小屏笔电（1366×768 @125% 只有 614 DIP 高）放不下默认 792 高，
        /// 不夹的话「应用」按钮会掉到屏幕外面，老板点不到。
        /// 内容在 ScrollViewer 里，夹小了照样能滚到每一行。
        /// </summary>
        private void FitToWorkArea()
        {
            try
            {
                var wa = SystemParameters.WorkArea;
                if (wa.Height > 200 && Height > wa.Height - 24) Height = wa.Height - 24;
                if (wa.Width > 300 && Width > wa.Width - 24) Width = wa.Width - 24;
            }
            catch { }
        }

        private void ApplyToControls(SubtitleStyle s)
        {
            SldMainSize.Value = Clamp(s.MainFontSize, 30, 140);
            SldSubSize.Value = Clamp(s.SubFontSize, 16, 100);
            SldBarWidth.Value = Clamp(s.BarWidth, 500, 1920);
            SldBottom.Value = Clamp(s.MarginBottom, 0, 600);
            SldOffsetX.Value = Clamp(s.OffsetX, -600, 600);
            SldAlpha.Value = Clamp(s.BackAlpha, 0, 255);

            TxtMainColor.Text = s.MainColor;
            TxtSubColor.Text = s.SubColor;
            TxtExtraColor.Text = s.ExtraColor;
            TxtAccentColor.Text = s.AccentColor;
            TxtBackColor.Text = s.BackColor;

            UpdateValueLabels();
            UpdateSwatches();
        }

        private SubtitleStyle ReadStyle() => new()
        {
            MainFontSize = SldMainSize.Value,
            SubFontSize = SldSubSize.Value,
            // 第三行不给单独滑块：跟着副字幕走 0.75 比例，少一个控件少一份迷惑
            ExtraFontSize = Math.Max(14, Math.Round(SldSubSize.Value * 0.75)),
            BarWidth = SldBarWidth.Value,
            MarginBottom = SldBottom.Value,
            OffsetX = SldOffsetX.Value,
            MainColor = TxtMainColor.Text.Trim(),
            SubColor = TxtSubColor.Text.Trim(),
            ExtraColor = TxtExtraColor.Text.Trim(),
            AccentColor = TxtAccentColor.Text.Trim(),
            BackColor = TxtBackColor.Text.Trim(),
            BackAlpha = (int)Math.Round(SldAlpha.Value),
            ShowAccent = true,
            BoldMain = true,
        };

        private static double Clamp(double v, double lo, double hi)
            => double.IsNaN(v) ? lo : Math.Clamp(v, lo, hi);

        private void UpdateValueLabels()
        {
            if (ValMainSize != null) ValMainSize.Text = ((int)Math.Round(SldMainSize.Value)).ToString();
            if (ValSubSize != null) ValSubSize.Text = ((int)Math.Round(SldSubSize.Value)).ToString();
            if (ValBarWidth != null) ValBarWidth.Text = ((int)Math.Round(SldBarWidth.Value)).ToString();
            if (ValBottom != null) ValBottom.Text = ((int)Math.Round(SldBottom.Value)).ToString();
            if (ValOffsetX != null) ValOffsetX.Text = ((int)Math.Round(SldOffsetX.Value)).ToString();
            if (ValAlpha != null) ValAlpha.Text = ((int)Math.Round(SldAlpha.Value)).ToString();
        }

        private void UpdateSwatches()
        {
            SetSwatch(SwMain, TxtMainColor.Text);
            SetSwatch(SwSub, TxtSubColor.Text);
            SetSwatch(SwExtra, TxtExtraColor.Text);
            SetSwatch(SwAccent, TxtAccentColor.Text);
            SetSwatch(SwBack, TxtBackColor.Text);
        }

        private static void SetSwatch(Border? b, string? hex)
        {
            if (b == null || string.IsNullOrWhiteSpace(hex)) return;
            try
            {
                var o = ColorConverter.ConvertFromString(hex.Trim());
                if (o is Color c) b.Background = new SolidColorBrush(c);
            }
            catch
            {
                // 手输中途（例如只打了 "#FF"）解析不了 —— 保持原色，别弹错
            }
        }

        // ================= 事件 =================

        private void AnyValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_loading) return;
            UpdateValueLabels();
            _previewDirty = true;
        }

        private void ColorTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            UpdateSwatches();
            _previewDirty = true;
        }

        private void RefreshPreview()
        {
            if (_loading) return;
            try
            {
                // withBackdrop = true：底下画一层模拟节目画面的渐变，
                // 不然透明区域在窗口里看不见，就看不出字幕条到底压在什么位置。
                PreviewImage.Source = TableSubtitleService.RenderPreview(PreviewCells, ReadStyle(), true);
            }
            catch { /* 预览失败不影响参数本身，别打断用户调 */ }
        }

        /// <summary>当前要预览的那一行的单元格（有表就用表里那行，没表就用示例行）。</summary>
        private string[] PreviewCells
        {
            get
            {
                if (Table != null && _previewRow >= 0 && _previewRow < Table.Rows.Count)
                    return Table.Rows[_previewRow];
                return SampleCells;
            }
        }

        // ================= 表格内容对照（前 / 现在 / 后）=================

        /// <summary>
        /// 把「上一条 / 上屏中 / 下一条」的字摆出来，方便上屏前核对。
        /// ⚠ 三条按钮的序号锚定在**正在上屏那条**上，不跟着预览走 ——
        ///   否则点了「前一条」之后「后一条」会变成再往后一条，越点越偏。
        /// </summary>
        private void BuildRowPeek()
        {
            if (RowPeekPanel == null) return;
            if (Table == null || Table.Rows.Count == 0)
            {
                RowPeekPanel.Visibility = Visibility.Collapsed;
                _peekBuilt = false;
                return;
            }

            RowPeekPanel.Visibility = Visibility.Visible;
            int n = Table.Rows.Count;
            int cur = Math.Clamp(CurrentIndex, 0, n - 1);
            if (!_peekBuilt) { _previewRow = cur; _peekBuilt = true; }

            _peekIndexes = new[] { cur - 1, cur, cur + 1 };

            FillRowButton(BtnRowPrev, TxtPrev, "前一条", _peekIndexes[0], n, "（已是第一条）");
            FillRowButton(BtnRowNow, TxtNow, "现在上屏", _peekIndexes[1], n, "（空）");
            FillRowButton(BtnRowNext, TxtNext, "后一条", _peekIndexes[2], n, "（已是最后一条）");

            HighlightPeek();
        }

        private void FillRowButton(Button? btn, TextBlock? txt, string tag, int idx, int total, string emptyHint)
        {
            if (btn == null || txt == null) return;
            bool ok = Table != null && idx >= 0 && idx < total;
            btn.IsEnabled = ok || tag == "现在上屏";   // 「现在」永远可点（就算越界也只是回原位）

            txt.Inlines.Clear();
            string tagText = ok ? $"{tag} · 第 {idx + 1} / {total} 条" : tag;
            txt.Inlines.Add(new Run(tagText)
            {
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = Res("Brush.Text.Muted", Color.FromRgb(0x8A, 0x9A, 0xAB)),
            });
            txt.Inlines.Add(new LineBreak());
            txt.Inlines.Add(new Run(ok ? RowText(idx) : emptyHint)
            {
                FontSize = 12.5,
                Foreground = Res("Brush.Text", Colors.White),
            });
        }

        /// <summary>一行单元格 → 一句话（只取非空单元格，用 · 隔开）。</summary>
        private string RowText(int idx)
        {
            if (Table == null || idx < 0 || idx >= Table.Rows.Count) return "—";
            var parts = Table.Rows[idx].Where(c => !string.IsNullOrWhiteSpace(c))
                                        .Select(c => c.Trim())
                                        .ToArray();
            return parts.Length == 0 ? "（空行）" : string.Join("  ·  ", parts);
        }

        /// <summary>高亮「当前预览的是哪一条」：用通道强调色描边 + 淡色底。</summary>
        private void HighlightPeek()
        {
            var accent = Res("Brush.Accent", Color.FromRgb(0x6E, 0xC7, 0xE8));
            var normal = Res("Brush.Border", Color.FromRgb(0x3A, 0x45, 0x58));
            var selBg = Res("Brush.Bg.Raised", Color.FromRgb(0x2B, 0x36, 0x44));

            var btns = new[] { BtnRowPrev, BtnRowNow, BtnRowNext };
            for (int k = 0; k < btns.Length; k++)
            {
                var b = btns[k];
                if (b == null) continue;
                bool sel = k < _peekIndexes.Length && _peekIndexes[k] == _previewRow && _peekIndexes[k] >= 0;
                b.BorderBrush = sel ? accent : normal;
                b.BorderThickness = new Thickness(sel ? 1.6 : 1);
                b.Background = sel ? selBg : Brushes.Transparent;
            }

            int cur = Table == null ? 0 : Math.Clamp(CurrentIndex, 0, Math.Max(0, Table.Rows.Count - 1));
            if (Table == null || Table.Rows.Count == 0 || TxtRowPeek == null) return;

            TxtRowPeek.Text = _previewRow == cur
                ? $"正在预览：第 {cur + 1} / {Table.Rows.Count} 条（就是现在上屏的那条）"
                : $"正在预览：第 {_previewRow + 1} / {Table.Rows.Count} 条 —— 上屏的仍是第 {cur + 1} 条，点「现在上屏」看回它";
        }

        private static Brush Res(string key, Color fallback)
        {
            try
            {
                if (Application.Current?.TryFindResource(key) is Brush b) return b;
            }
            catch { }
            var sb = new SolidColorBrush(fallback);
            sb.Freeze();
            return sb;
        }

        private void BtnRowPrev_Click(object sender, RoutedEventArgs e) => Peek(0);
        private void BtnRowNow_Click(object sender, RoutedEventArgs e) => Peek(1);
        private void BtnRowNext_Click(object sender, RoutedEventArgs e) => Peek(2);

        private void Peek(int slot)
        {
            if (Table == null || slot >= _peekIndexes.Length) return;
            int idx = _peekIndexes[slot];
            if (idx < 0 || idx >= Table.Rows.Count) return;
            _previewRow = idx;
            HighlightPeek();
            RefreshPreview();
        }

        // ================= 色板 =================

        private void BuildPalette()
        {
            PalettePanel.Children.Clear();
            foreach (var hex in PaletteColors)
            {
                Brush bg;
                try { bg = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
                catch { continue; }
                bg.Freeze();

                var btn = new Button
                {
                    Width = 24,
                    Height = 24,
                    Margin = new Thickness(1),
                    Background = bg,
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
                    BorderThickness = new Thickness(1),
                    Tag = hex,
                    ToolTip = hex,
                };
                var captured = hex;
                btn.Click += (_, _) =>
                {
                    if (_colorTarget != null) _colorTarget.Text = captured;
                    if (_swatchTarget != null) SetSwatch(_swatchTarget, captured);
                    PalettePopup.IsOpen = false;
                    _previewDirty = true;
                };
                PalettePanel.Children.Add(btn);
            }
        }

        private void PickMainColor(object sender, RoutedEventArgs e) => OpenPalette(TxtMainColor, SwMain, sender);
        private void PickSubColor(object sender, RoutedEventArgs e) => OpenPalette(TxtSubColor, SwSub, sender);
        private void PickExtraColor(object sender, RoutedEventArgs e) => OpenPalette(TxtExtraColor, SwExtra, sender);
        private void PickAccentColor(object sender, RoutedEventArgs e) => OpenPalette(TxtAccentColor, SwAccent, sender);
        private void PickBackColor(object sender, RoutedEventArgs e) => OpenPalette(TxtBackColor, SwBack, sender);

        private void OpenPalette(TextBox target, Border swatch, object anchor)
        {
            _colorTarget = target;
            _swatchTarget = swatch;
            if (anchor is UIElement el)
            {
                PalettePopup.PlacementTarget = el;
                PalettePopup.Placement = PlacementMode.Bottom;
            }
            PalettePopup.IsOpen = true;
        }

        // ================= 按钮 =================

        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            ApplyToControls(new SubtitleStyle());
            RefreshPreview();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            ResultStyle = ReadStyle();
            DialogResult = true;
        }
    }
}
