using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cutlance.Models;
using Cutlance.Services;

namespace Cutlance
{
    /// <summary>
    /// 画面效果窗口（模块8）：调色 / 美颜（美白·磨皮）/ 美体（瘦身·拉长近似）。
    /// 按老板确认的方案：这些功能在「英伟达显卡（NVENC）工作状态」下启用；
    /// 参数由 FFmpeg 滤镜链落地，作用于播出框整幅画面，随推流与录制一起输出。
    /// 内置「抓帧对比预览」：用同一套滤镜抓一帧播出框画面，左侧原图、右侧效果图。
    /// </summary>
    public partial class VideoFxWindow : Window
    {
        private readonly StreamService _svc = StreamService.Instance;
        private readonly VideoFx _fx;
        private readonly BodyProportion _body = BodyProfileStore.Load();   // 模块9：美学比例测算
        private bool _ready;
        private bool _busy;
        private bool _autoShot;      // 「打开即自动抓一次」只跑一次（抓帧会 Hide/Show 本窗）
        private bool _portrait;      // 当前输出是不是竖屏（决定腰臀线用哪套默认）

        public VideoFxWindow()
        {
            _fx = _svc.Fx;
            InitializeComponent();
            WorkFrame.Attach(this);   // 统一：置顶 + 抓屏排除 + 底部「保存/关闭」+ Esc/Ctrl+S

            // 740 DIP 在小逻辑屏上会超出 → 收敛到可用区（左右两列各自可滚）
            WindowFit.FitToWorkArea(this);

            _ready = false;

            // 老板 2026-10-22：竖屏单独一套腰臀线默认值。
            //   横屏 46/62（原出厂值，已过 7/7 回归）；竖屏 52/66（实测得出，见 p17 对比图）。
            //   ⚠ 只在「没手动调过」时套用 —— 用户拖过的值一律不覆盖。
            //   回归对照时可设 CUTLANCE_NO_FX_AUTO=1 关掉自动套用。
            _portrait = _svc.OutputHeight > _svc.OutputWidth;
            if (Environment.GetEnvironmentVariable("CUTLANCE_NO_FX_AUTO") != "1")
                _fx.ApplyOrientationDefaults(_portrait);

            // 「↺」单项重置要把腰臀线还原成**当前方向**的出厂值，否则竖屏一点还原就跳回 46/62
            var (dw, dh) = VideoFx.LineDefaults(_portrait);
            FxDefaults["waisty"] = dw;
            FxDefaults["hipy"] = dh;

            LoadUiFromFx();
            LoadBodyUi();
            _ready = true;
            RefreshLinePresetUi();
            AddResetButtons();       // 每个调节项右侧补一个「↺」单项重置（老板 2026-10-18）
            // 保险丝：模板迟一点实例化也能补上（AddResetButtons 幂等，不会重复加）
            Loaded += (_, _) => AddResetButtons();
            RefreshBodyReadout();

            // 单项重置自检（2026-10-22）：离屏模式下 UIA 拿不到控件，只能让窗口自己点一遍
            if (Environment.GetEnvironmentVariable("CUTLANCE_FXRESET_TEST") == "1")
                Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(RunFxResetSelfTest),
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            bool nvenc = _svc.Encoder.Equals("nvenc", StringComparison.OrdinalIgnoreCase);
            FxPanel.IsEnabled = nvenc;
            StateHint.Foreground = nvenc
                ? Brushes.LightGreen
                : Brushes.OrangeRed;
            StateHint.Text = nvenc
                ? "编码器：NVIDIA NVENC（显卡）· 画面效果已启用。参数改动在重新开始输出后生效。"
                : "⚠ 当前编码器不是 NVIDIA NVENC，画面效果已禁用。请在「输出设置 / 设置 → 推流·录制编码器」里切换到 NVIDIA NVENC 后重开本窗口。";

            // 打开即自动抓一次对比图：老板反馈「画面效果窗口看不到原图 / 效果图」。
            // 根因之一是按钮被参数列表挤出可视区（现已固定到底栏），再自动抓一次，打开就能看到图。
            // 抓不到（播出框不可见 / 没关联主窗）只写提示文字，不弹框、不阻塞。
            // ⚠ 抓帧会 Hide()/Show() 本窗，只自动抓**一次**，避免每次重显示都抓。
            Loaded += async (_, _) =>
            {
                if (_autoShot) return;
                _autoShot = true;
                await System.Threading.Tasks.Task.Delay(260);   // 等本窗完成布局与定位，再让开镜头抓帧
                if (IsVisible) await CaptureCompareAsync();
            };
        }

        // ================= 界面 <-> 参数 =================

        private void LoadUiFromFx()
        {
            SdBright.Value = _fx.Brightness * 100;
            SdContrast.Value = _fx.Contrast * 100;
            SdSat.Value = _fx.Saturation * 100;
            SdGamma.Value = _fx.Gamma * 100;
            SdTemp.Value = _fx.Temperature * 100;
            SdWhiten.Value = _fx.Whiten;
            SdSmooth.Value = _fx.Smooth;
            SdSlim.Value = _fx.Slim;
            SdStretch.Value = _fx.Stretch;
            // 分段局部形变
            SdWaistY.Value = _fx.WaistY;
            SdHipY.Value = _fx.HipY;
            SdCenterX.Value = _fx.WarpCenterX;
            SdWarpW.Value = _fx.WarpWidth;
            SdSlimSeg.Value = _fx.SlimSeg;
            SdLegSeg.Value = _fx.LegSeg;
            CbShape.SelectedIndex = _fx.SegmentedBody ? 0 : 1;
            RefreshValueLabels();
        }

        /// <summary>
        /// 刷新滑块右侧数值（现在全是可直接输入的文本框）。
        /// ⚠ XAML 加载时滑块的初始 Value 也会触发 ValueChanged，
        /// 而靠后的标签框要到文档解析到它时才创建（此刻字段为 null），
        /// 所以这里必须先判空返回，否则会抛 NullReferenceException 让窗口打不开。
        /// 正在输入（有键盘焦点）的框不覆盖，避免打断老板打字。
        /// </summary>
        private void RefreshValueLabels()
        {
            if (TxBright == null || TxContrast == null || TxSat == null || TxGamma == null ||
                TxTemp == null || TxWhiten == null || TxSmooth == null || TxSlim == null ||
                TxStretch == null || TxWaistY == null || TxHipY == null || TxCenterX == null ||
                TxWarpW == null || TxSlimSeg == null || TxLegSeg == null) return;

            SetVal(TxBright, SdBright);
            SetVal(TxContrast, SdContrast);
            SetVal(TxSat, SdSat);
            SetVal(TxGamma, SdGamma);
            SetVal(TxTemp, SdTemp);
            SetVal(TxWhiten, SdWhiten);
            SetVal(TxSmooth, SdSmooth);
            SetVal(TxSlim, SdSlim);
            SetVal(TxStretch, SdStretch);
            // 分段局部形变（腰线 / 臀线 / 作用中心 / 作用宽度 / 腰段收缩 / 腿段拉伸）
            SetVal(TxWaistY, SdWaistY);
            SetVal(TxHipY, SdHipY);
            SetVal(TxCenterX, SdCenterX);
            SetVal(TxWarpW, SdWarpW);
            SetVal(TxSlimSeg, SdSlimSeg);
            SetVal(TxLegSeg, SdLegSeg);
        }

        /// <summary>把一个滑块的值写进它右侧的数值框（带 % 的按 % 显示）；正在输入的框跳过。</summary>
        private static void SetVal(TextBox box, Slider sl)
        {
            if (box == null || sl == null) return;
            if (box.IsKeyboardFocusWithin) return;
            string s = FormatVal(sl);
            if (box.Text != s) box.Text = s;
        }

        /// <summary>数值框显示文本：分段/宽高类带 %，其余为纯数字（色温带正负号）。</summary>
        private static string FormatVal(Slider sl)
        {
            bool pct = sl.Tag as string is "waisty" or "hipy" or "centerx" or "warpw";
            int v = (int)Math.Round(sl.Value);
            return pct ? v + "%" : v.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>数值框 ↔ 滑块：Tag 一一对应。</summary>
        private Slider? SliderByTag(string? tag) => tag switch
        {
            "bright" => SdBright,
            "contrast" => SdContrast,
            "sat" => SdSat,
            "gamma" => SdGamma,
            "temp" => SdTemp,
            "whiten" => SdWhiten,
            "smooth" => SdSmooth,
            "slim" => SdSlim,
            "stretch" => SdStretch,
            "waisty" => SdWaistY,
            "hipy" => SdHipY,
            "centerx" => SdCenterX,
            "warpw" => SdWarpW,
            "slimseg" => SdSlimSeg,
            "legseg" => SdLegSeg,
            _ => null
        };

        private void ValBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender is TextBox tb) tb.SelectAll();   // 点进来即全选，直接输入覆盖
        }

        private void ValBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox tb) return;
            if (e.Key == Key.Enter)
            {
                CommitValBox(tb);
                Keyboard.ClearFocus();      // 提交后收起焦点，画面恢复常态
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                RefreshValueLabels();       // 放弃修改，回显原值
                Keyboard.ClearFocus();
                e.Handled = true;
            }
        }

        private void ValBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender is TextBox tb) CommitValBox(tb);
        }

        /// <summary>
        /// 把数值框里的输入应用到对应滑块：支持带 % 或纯数字，超范围自动夹到边界，
        /// 提交后强制回显归一值（滑块没变化时也要刷新，避免显示与真实值不一致）。
        /// </summary>
        private void CommitValBox(TextBox tb)
        {
            try
            {
                if (!IsLoaded) return;
                var sl = SliderByTag(tb.Tag?.ToString());
                if (sl == null) { RefreshValueLabels(); return; }

                var raw = (tb.Text ?? string.Empty).Trim().TrimEnd('%', '％').Trim();
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    sl.Value = Math.Clamp(v, sl.Minimum, sl.Maximum);   // 触发 Slider_Changed → 写回参数
                    tb.Text = FormatVal(sl);                            // 强制回显（含夹取后的边界值）
                }
                RefreshValueLabels();
            }
            catch { }
        }

        private void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            RefreshValueLabels();
            if (!_ready || sender is not Slider sl) return;
            switch (sl.Tag?.ToString())
            {
                case "bright": _fx.Brightness = sl.Value / 100.0; break;
                case "contrast": _fx.Contrast = sl.Value / 100.0; break;
                case "sat": _fx.Saturation = sl.Value / 100.0; break;
                case "gamma": _fx.Gamma = sl.Value / 100.0; break;
                case "temp": _fx.Temperature = sl.Value / 100.0; break;
                case "whiten": _fx.Whiten = (int)sl.Value; break;
                case "smooth": _fx.Smooth = (int)sl.Value; break;
                case "slim": _fx.Slim = (int)sl.Value; break;
                case "stretch": _fx.Stretch = (int)sl.Value; break;
                // —— 分段局部形变 ——
                case "waisty": _fx.WaistY = sl.Value; break;
                case "hipy": _fx.HipY = sl.Value; break;
                case "centerx": _fx.WarpCenterX = sl.Value; break;
                case "warpw": _fx.WarpWidth = sl.Value; break;
                case "slimseg": _fx.SlimSeg = (int)sl.Value; break;
                case "legseg": _fx.LegSeg = (int)sl.Value; break;
            }
        }

        /// <summary>把「当前方向 + 该方向的出厂腰臀线」写到预设条上（老板 2026-10-22）。</summary>
        private void RefreshLinePresetUi()
        {
            if (TbLinePreset == null || BtnLinePreset == null) return;
            var (w, h) = VideoFx.LineDefaults(_portrait);
            BtnLinePreset.Content = _portrait ? "套用竖屏默认" : "套用横屏默认";
            TbLinePreset.Text = _portrait
                ? $"腰臀线预设：竖屏 {w:0} / {h:0}（腰线压腰最细处、臀线在胯最宽处）"
                : $"腰臀线预设：横屏 {w:0} / {h:0}（人物全身占满画面时用）";
            try { TbLinePreset.Foreground = (System.Windows.Media.Brush)FindResource("Brush.Text.Secondary"); }
            catch { }
        }

        /// <summary>
        /// 一键套用当前输出方向的腰臀线默认值。
        /// 存在的意义：这两个数按画面高度百分比算，用户不可能自己推算 —— 给个按钮比看说明快。
        /// </summary>
        private void BtnLinePreset_Click(object sender, RoutedEventArgs e)
        {
            _fx.ApplyOrientationDefaults(_portrait, force: true);
            var (w, h) = VideoFx.LineDefaults(_portrait);

            // 写回滑块（_ready 此时为 true，Slider_Changed 会把值同步进 _fx，与手动拖动同一路径）
            if (SdWaistY != null) SdWaistY.Value = w;
            if (SdHipY != null) SdHipY.Value = h;
            RefreshValueLabels();

            TbLinePreset.Text = $"已套用{(_portrait ? "竖屏" : "横屏")}默认：腰线 {w:0}% / 臀线 {h:0}%";
            try { TbLinePreset.Foreground = (System.Windows.Media.Brush)FindResource("Brush.Accent"); }
            catch { }
        }

        /// <summary>形变方式切换：0 = 分段局部形变（推荐），1 = 整幅近似形变。</summary>
        private void ShapeMode_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (SegPanel == null || GlobalPanel == null || CbShape == null) return;
            bool seg = CbShape.SelectedIndex <= 0;
            SegPanel.Visibility = seg ? Visibility.Visible : Visibility.Collapsed;
            GlobalPanel.Visibility = seg ? Visibility.Collapsed : Visibility.Visible;
            if (!_ready) return;
            _fx.SegmentedBody = seg;
            RefreshBodyReadout();
        }

        // ================= 模块9：美学比例测算（人体数据 → 美体建议值）=================
        // 三条标准：黄金比例（下身/身高 ≈ 0.618）、腰臀比（≈ 0.70）、
        //           腿身比（马氏指数 74.4~94.4，约等于身高 54%~57%）。
        // 改动任意输入框，下方建议值实时重算；点「应用建议值」直接写入瘦身/拉长滑块。

        private void LoadBodyUi()
        {
            BxHeight.Text = Fmt(_body.Height);
            BxNavel.Text = Fmt(_body.NavelHeight);
            BxSit.Text = Fmt(_body.SittingHeight);
            BxWaist.Text = Fmt(_body.Waist);
            BxHip.Text = Fmt(_body.Hip);
            CbSex.SelectedIndex = _body.IsFemale ? 0 : 1;
            CbLegStd.SelectedIndex = _body.UseManouvrierForLeg ? 1 : 0;
        }

        private void BodyInput_Changed(object sender, TextChangedEventArgs e)
        {
            if (!_ready) return;
            ReadBodyFromUi();
            RefreshBodyReadout();
        }

        private void BodySex_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready) return;
            ReadBodyFromUi();
            RefreshBodyReadout();
        }

        private void BodyLegStd_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready) return;
            ReadBodyFromUi();
            RefreshBodyReadout();
        }

        /// <summary>把界面输入回读到测算模型并持久化（空/非法值退回默认，避免除零）。</summary>
        private void ReadBodyFromUi()
        {
            _body.Height = ParseNum(BxHeight.Text, 170);
            _body.NavelHeight = ParseNum(BxNavel.Text, 103);
            _body.SittingHeight = ParseNum(BxSit.Text, 90);
            _body.Waist = ParseNum(BxWaist.Text, 72);
            _body.Hip = ParseNum(BxHip.Text, 95);
            _body.IsFemale = CbSex.SelectedIndex <= 0;
            _body.UseManouvrierForLeg = CbLegStd.SelectedIndex == 1;
            BodyProfileStore.Save(_body);
        }

        /// <summary>刷新三项指标读数与建议值（输入变化时实时调用）。</summary>
        private void RefreshBodyReadout()
        {
            var b = _body;
            var warn = new SolidColorBrush(Color.FromRgb(0xFF, 0xB7, 0x4D));   // 未达标：橙
            var good = new SolidColorBrush(Color.FromRgb(0x81, 0xC7, 0x84));   // 达标：绿
            var dim = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));

            TxGolden.Foreground = b.GoldenState == 0 ? good : warn;
            TxGolden.Text = $"① 黄金比例（以肚脐为界）\n" +
                            $"     下身/身高 = {b.LowerRatio:0.000}（理想 0.618）· 上/下身 = {b.UpperLowerRatio:0.000}\n" +
                            $"     {b.GoldenStateText}";

            TxWhr.Foreground = b.WhrState == 0 ? good : (b.WhrState > 0 ? warn : dim);
            TxWhr.Text = $"② 腰臀比\n" +
                         $"     腰/臀 = {b.Whr:0.000}（理想 {(b.IsFemale ? "0.70（女性）" : "0.90（男性）")}）\n" +
                         $"     {b.WhrStateText}";

            TxLeg.Foreground = b.LegState == 0 ? good : warn;
            TxLeg.Text = $"③ 腿身比\n" +
                         $"     腿长/身高 = {b.LegRatio * 100:0.0}%（理想 54%~57%）· {b.LegStateText}\n" +
                         $"     马氏躯干腿长指数 = {b.Manouvrier:0.0}（理想 74.4~94.4 · {b.ManouvrierGrade}）";

            TxManouvrier.Foreground = dim;
            TxManouvrier.Text = $"     · 按「腿长占身高」口径 → 建议拉长 {b.SuggestStretchByLegRatioOnly}\n" +
                                $"     · 按「马氏指数」口径 → 建议拉长 {b.SuggestStretchByManouvrierOnly}\n" +
                                $"     当前口径：{(b.UseManouvrierForLeg ? "马氏指数 74.4~94.4" : "腿长占身高 54%~57%")}" +
                                $" → 采用 {b.SuggestStretchByLeg}（两口径标准不同，结果差异大）";

            if (!b.IsValid)
            {
                SuggestHint.Text = "⚠ 数据不合理：肚脐高 / 坐高 必须小于身高，且各项均 > 0。";
                ApplySuggestBtn.Content = "应用建议值到美体滑块";
                return;
            }

            ApplySuggestBtn.Content = b.HasSuggestion
                ? $"应用建议值（瘦身 {b.SuggestSlim} · 拉长 {b.SuggestStretch}）"
                : "应用建议值到美体滑块";

            SuggestHint.Text = b.SuggestSummary + "\n" +
                $"分项：腿身比 → 拉长 {b.SuggestStretchByLeg}；黄金比例 → 拉长 {b.SuggestStretchByGolden}" +
                $"（综合 {b.SuggestStretch} = 腿身比×0.6 + 黄金比例×0.4）；腰臀比 → 瘦身 {b.SuggestSlim}。\n" +
                (_fx.SegmentedBody
                    ? "注：分段局部形变 = 腰段单独收窄 + 腿段单独拉长，头部胸部保持原样，可实际改变腰臀比 / 腿身比的观感（过渡带羽化，无接缝）。应用后请把「腰线 / 臀线」对准人物实际位置。"
                    : "注：整幅近似形变会把整个画面一起缩放/桶形，只是视觉改善强度，不能只改某一部位。");
        }

        private void ApplySuggestBtn_Click(object sender, RoutedEventArgs e)
        {
            var b = _body;
            if (!b.IsValid)
            {
                SuggestHint.Text = "⚠ 输入数据不合理，未应用（肚脐高 / 坐高须小于身高）。";
                return;
            }

            int slim = b.SuggestSlim, stretch = b.SuggestStretch;

            _ready = false;                    // 避免逐个触发写回，统一在本方法末尾落值
            SdSlim.Value = slim;
            SdStretch.Value = stretch;
            SdSlimSeg.Value = slim;            // 分段模式：腰臀比偏差 → 腰段收缩
            SdLegSeg.Value = stretch;          // 分段模式：腿身比/黄金比例偏差 → 腿段拉伸
            _ready = true;
            _fx.Slim = slim;
            _fx.Stretch = stretch;
            _fx.SlimSeg = slim;
            _fx.LegSeg = stretch;
            RefreshValueLabels();

            bool nvenc = _svc.Encoder.Equals("nvenc", StringComparison.OrdinalIgnoreCase);
            var applied = _fx.SegmentedBody
                ? $"已应用 → 腰段收缩 {slim} · 腿段拉伸 {stretch}（分段局部形变）"
                : $"已应用 → 瘦身 {slim} · 拉长 {stretch}（整幅近似）";
            SuggestHint.Text = applied + (_fx.SegmentedBody
                ? "。请把美体区的「腰线 / 臀线」拖到画面里人物实际位置（腰最细处 / 胯最宽处），并按需微调「作用中心」。"
                : "。") +
                (nvenc
                    ? "可点「抓帧对比预览」看效果；参数改动在重新开始输出后生效。"
                    : "⚠ 当前编码器非 NVIDIA NVENC，画面效果不生效，请先切到 NVENC。");
        }

        /// <summary>整数优先、允许小数：解析失败或 ≤0 时退回默认值。</summary>
        private static double ParseNum(string? s, double fallback)
        {
            if (double.TryParse(s?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0)
                return v;
            return fallback;
        }

        private static string Fmt(double v) =>
            Math.Abs(v - Math.Round(v)) < 0.01
                ? ((int)Math.Round(v)).ToString()
                : v.ToString("0.#", CultureInfo.InvariantCulture);

        /// <summary>
        /// 载入工程后重新读取人体测算数据并刷新界面（由主窗口调用；窗口未打开时无需处理）。
        /// </summary>
        public void ReloadBodyFromStore()
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(ReloadBodyFromStore); return; }

            var b = BodyProfileStore.Load();
            _body.Height = b.Height;
            _body.NavelHeight = b.NavelHeight;
            _body.SittingHeight = b.SittingHeight;
            _body.Waist = b.Waist;
            _body.Hip = b.Hip;
            _body.IsFemale = b.IsFemale;
            _body.UseManouvrierForLeg = b.UseManouvrierForLeg;

            _ready = false;
            LoadUiFromFx();      // 工程载入后同步画效（含分段形变）参数
            LoadBodyUi();
            _ready = true;
            RefreshBodyReadout();
        }

        private void ResetBtn_Click(object sender, RoutedEventArgs e)
        {
            _fx.Reset();
            _fx.ApplyOrientationDefaults(_portrait, force: true);   // 竖屏别被重置回横屏值
            _ready = false;
            LoadUiFromFx();
            _ready = true;
            RefreshLinePresetUi();
        }

        // ================= 单项重置（老板 2026-10-18 第 1 条）=================
        // 每个调节项右侧一个「↺」按钮，只把这一项恢复默认，不碰其它参数。
        // 默认值必须与 VideoFx 的属性初值一致（改了模型默认值要同步改这里）。

        private static readonly Dictionary<string, double> FxDefaults = new()
        {
            ["bright"] = 0,       // 亮度
            ["contrast"] = 100,   // 对比度 %
            ["sat"] = 100,        // 饱和度 %
            ["gamma"] = 100,      // 伽马 %
            ["temp"] = 0,         // 色温
            ["whiten"] = 0,       // 美白
            ["smooth"] = 0,       // 磨皮
            ["slim"] = 0,         // 瘦身（整幅）
            ["stretch"] = 0,      // 拉长（整幅）
            ["waisty"] = 46,      // 腰线
            ["hipy"] = 62,        // 臀线
            ["slimseg"] = 0,      // 腰段收缩
            ["legseg"] = 0,       // 腿段拉伸
            ["centerx"] = 50,     // 作用中心
            ["warpw"] = 70,       // 作用宽度
        };

        /// <summary>
        /// 给每个调节项右侧补一个「↺」单项重置按钮。
        /// 为什么在代码里动态加而不是写十几遍 XAML：滑块多，XAML 复制粘贴漏一个就少一个；
        /// 挂在「每个 Slider 的父 DockPanel」上，以后新增滑块自动带上。
        /// </summary>
        private void AddResetButtons()
        {
            // ⚠ 必须先 ToList() 再遍历：Panel.Children 就是 VisualCollection，
            //   边遍历边往里 Add 会抛「因为集合已更改，枚举器无效」（2026-10-19 实测崩溃）。
            foreach (var sl in EnumerateSliders(FxPanel).ToList())
            {
                if (sl.Tag is not string tag || !FxDefaults.ContainsKey(tag)) continue;
                if (sl.Parent is not DockPanel dp) continue;
                if (dp.Children.OfType<Button>().Any(b => b.Tag as string == tag)) continue;

                var btn = new Button
                {
                    Content = "↺",
                    Tag = tag,
                    Width = 22,
                    Height = 18,
                    MinWidth = 0,
                    Padding = new Thickness(0),
                    Margin = new Thickness(4, 0, 0, 0),
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    ToolTip = "恢复该项默认值",
                    Style = (Style)FindResource("FxBtn"),
                };
                DockPanel.SetDock(btn, Dock.Right);
                btn.Click += ResetOne_Click;
                dp.Children.Add(btn);
            }
        }

        private static IEnumerable<Slider> EnumerateSliders(DependencyObject root)
        {
            // ⚠ 必须走**逻辑树**，不能用 VisualTreeHelper：
            //   构造期（InitializeComponent 之后、还没 ApplyTemplate/Measure）视觉树里
            //   没有 GroupBox → ContentPresenter 展开出来的内容，里面那些 DockPanel/Slider
            //   都还没接上视觉树 —— 实测遍历到 0 个滑块，一个「↺」都加不上（2026-10-19 验证发现）。
            //   逻辑树在 XAML 解析完成后就是完整的，构造期就能拿到全部 15 个滑块。
            foreach (var child in LogicalTreeHelper.GetChildren(root))
            {
                if (child is not DependencyObject d) continue;
                if (d is Slider s) yield return s;
                foreach (var inner in EnumerateSliders(d)) yield return inner;
            }
        }

        private void ResetOne_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b) return;
            var tag = b.Tag?.ToString();
            if (tag == null || !FxDefaults.TryGetValue(tag, out var def)) return;
            var sl = SliderByTag(tag);
            if (sl == null) return;
            sl.Value = Math.Clamp(def, sl.Minimum, sl.Maximum);   // 触发 Slider_Changed → 写回 _fx
            RefreshValueLabels();
        }

        /// <summary>
        /// 单项重置自检：对每个滑杆「先推离默认值 → 点它旁边的 ↺ → 必须回到默认值」。
        /// 判据看三样：按钮数量是否等于滑杆数、点完的值是否等于 FxDefaults、_fx 模型是否同步。
        /// 为什么让窗口自己点：`--offscreen` 下窗口在屏幕外，外部 UIA 一个控件都拿不到。
        /// </summary>
        private void RunFxResetSelfTest()
        {
            void Log(string m) { try { ErrorLog.Info("[单项重置] " + m); } catch { } }

            try
            {
                int sliders = 0, buttons = 0, pass = 0;
                var bad = new List<string>();

                foreach (var sl in EnumerateSliders(FxPanel).ToList())
                {
                    if (sl.Tag is not string tag || !FxDefaults.TryGetValue(tag, out var def)) continue;
                    sliders++;

                    var dp = sl.Parent as DockPanel;
                    var btn = dp?.Children.OfType<Button>().FirstOrDefault(b => b.Tag as string == tag);
                    if (btn == null) { bad.Add(tag + "(缺按钮)"); continue; }
                    buttons++;

                    // ① 先推到明显不是默认值的位置（顺带验证 Slider_Changed 会把值写进模型）
                    double away = def + (sl.Maximum - def >= 8 ? 6 : 3);
                    away = Math.Clamp(away, sl.Minimum, sl.Maximum);
                    sl.Value = away;
                    bool moved = Math.Abs(sl.Value - away) < 0.001;

                    // ② 走真实按钮处理器（跟用户点 ↺ 同一条路径）
                    btn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                    // ③ 必须精确回到默认
                    bool back = Math.Abs(sl.Value - def) < 0.001;
                    if (moved && back) pass++;
                    else bad.Add($"{tag}(推={away} 回={sl.Value:0.##} 应={def})");
                }

                Log($"滑杆 {sliders} 个 / 按钮 {buttons} 个 / 通过 {pass} / 失败 {bad.Count}"
                    + (bad.Count > 0 ? " → " + string.Join(", ", bad) : ""));
                bool ok = sliders == 15 && buttons == sliders && pass == sliders && bad.Count == 0;
                Log("结果: " + (ok ? "PASS" : "FAIL") + "（期望 15 个滑杆各带一个 ↺）");
            }
            catch (Exception ex)
            {
                Log("自检异常：" + ex.Message);
            }
            finally
            {
                try { Close(); } catch { }
            }
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

        // ================= 抓帧对比预览 =================

        private void PreviewBtn_Click(object sender, RoutedEventArgs e) => _ = CaptureCompareAsync();

        /// <summary>
        /// 抓帧对比：抓主窗「播出框」当前画面一帧，分别按「零效果」与「当前参数」处理成两张图。
        ///
        /// ⚠ 抓帧期间必须把自己**从屏幕上挪开**：本窗口居中打开时正好压住播出框，
        ///   gdigrab 抓的是屏幕区域，不挪开就抓到本窗口自己 —— 表现就是"看不到原图/效果图"。
        ///
        /// ★ 实测教训（2026-10-17，用美女图复现）：**`Opacity = 0` 不管用！**
        ///   屏幕位图（gdigrab/BitBlt）遇到「全透明分层窗口」不是"透过去"，而是**画成纯黑**——
        ///   抓到的就是"播出框位置上盖了一块黑布"，只有露在窗口外的那一条边是真画面。
        ///   必须用 `Hide()` 真正把窗口从屏幕上撤掉，再把主窗提到前台，
        ///   才能保证播出框那块屏幕区域上空无一物。
        /// </summary>
        private async System.Threading.Tasks.Task CaptureCompareAsync()
        {
            if (_busy) return;
            var mw = Owner as MainWindow;
            if (mw == null) { PreviewHint.Text = "无法取得播出框位置（窗口未正确关联）。"; return; }

            _busy = true;
            PreviewBtn.IsEnabled = false;
            PreviewHint.Text = "正在抓取播出框画面并套用滤镜…本窗口会隐去约 2 秒，抓完自动回来。";
            try
            {
                var r = mw.ProgramScreenRectPhysical();
                if (r.Width < 2 || r.Height < 2)
                {
                    PreviewHint.Text = "播出框当前不可见（宽高为 0），请让主窗口的「输出」监视器保持可见后重试。";
                    return;
                }
                int w = _svc.OutputWidth, h = _svc.OutputHeight;
                var dir = Path.GetTempPath();
                var orig = Path.Combine(dir, "cutlance_fx_orig.png");
                var fx = Path.Combine(dir, "cutlance_fx_effect.png");

                // —— 让开镜头：撤掉本窗 + 把主窗顶到最前（否则播出框会被别的窗口盖住，抓到的是别人的画面）
                Hide();
                try { (Owner as Window)?.Activate(); } catch { }
                await System.Threading.Tasks.Task.Delay(420);   // 等 DWM 合成一帧，确保屏幕已是"无我"状态
                try
                {
                    string err = "";
                    var fxs = _svc.Fx;
                    // 原图：用同一区域但"零效果"抓一帧
                    bool ok1 = await System.Threading.Tasks.Task.Run(() =>
                        FfmpegOutput.CaptureStill(r.X, r.Y, r.Width, r.Height, w, h, new VideoFx(), orig, out err));
                    string err2 = "";
                    bool ok2 = await System.Threading.Tasks.Task.Run(() =>
                        FfmpegOutput.CaptureStill(r.X, r.Y, r.Width, r.Height, w, h, fxs, fx, out err2));

                    if (!ok1) { PreviewHint.Text = "原图抓取失败：" + err; return; }
                    ImgOrig.Source = LoadPng(orig);
                    if (!ok2) { PreviewHint.Text = "效果图抓取失败：" + err2; return; }
                    ImgFx.Source = LoadPng(fx);

                    PreviewHint.Text = $"已抓取 {w}×{h} 效果对比图 · {DateTime.Now:HH:mm:ss} · " +
                                       (_fx.Any ? "当前参数：" + _fx.Summary : "当前无效果（参数全默认）")
                                       + "（抓帧时本窗口会先隐去，抓到的是主窗「播出框」的真实画面）";
                }
                finally
                {
                    try { Show(); Activate(); } catch { }   // 抓完立刻回来
                }
            }
            catch (Exception ex)
            {
                PreviewHint.Text = "预览失败：" + ex.Message;
            }
            finally
            {
                _busy = false;
                PreviewBtn.IsEnabled = true;
            }
        }

        private static BitmapImage? LoadPng(string path)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;   // 立刻读入，避免占用文件
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }
    }
}
