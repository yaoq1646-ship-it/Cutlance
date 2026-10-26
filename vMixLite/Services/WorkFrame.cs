using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Cutlance.Services
{
    /// <summary>
    /// 「工作框」统一外观与行为（老板 2026-10-18 的第 ④⑤ 条）。
    ///
    /// 老板原话：
    ///   ④「当一个新的工作框打开时 它一定是当前最高权限，被遮挡的后面界面不能出现」
    ///   ⑤「每次新开工作框要有保存和关闭的按钮以及快捷方式」
    ///
    /// 落成四条硬规则，任何工作框在构造函数末尾调一次 <see cref="Attach"/> 就全有了：
    ///   ① **Topmost**：打开即置顶，永远压在播出框/主窗之上（"最高权限"）；
    ///   ② **不透明**：窗口背景必须实心 —— 半透明会让后面的播出画面"透出来"，
    ///      在导播软件里等于把节目画面糊进设置面板（本次不看 AllowTransparency，
    ///      直接把 Opacity 钉成 1，任何人以后加透明效果都会被这里纠正）；
    ///   ③ **抓屏排除**（WDA_EXCLUDEFROMCAPTURE）：节目输出是「抓播出框」的，
    ///      工作框压在上面就会被一起抓进推流/录制 —— 2026-10-18 竖屏回归实测到过
    ///      （竖屏输出框居中，混音台正好压在它上面）。排除后窗口照常显示，
    ///      但任何抓屏 API 都拍不到它；
    ///   ④ **保存 / 关闭 + 快捷键**：窗口底部固定一条按钮带（保存 Ctrl+S、关闭 Esc）。
    ///      窗口自己已经有「关闭」的，只在左侧补一个「保存」，不重复摆两个关闭。
    ///
    /// ⚠ 这里不碰「导播中不许弹模态框」那条铁律：工作框本身就是用户主动打开的，
    ///   它只在打开期间置顶，关掉就结束，不影响正常播出。
    /// </summary>
    public static class WorkFrame
    {
        /// <summary>
        /// 「保存」的真身：由 MainWindow 在启动时挂上（= 存当前工程）。
        /// 各工作框的参数大多**改动即生效**（滑杆、音量、裁切都是即时作用到播放器），
        /// 所以这里的保存 = 把当前工程落盘，关掉程序/重开工程不会丢设置。
        /// </summary>
        public static Action? SaveHook;

        /// <summary>给一个工作框装上全部统一行为。在 InitializeComponent() 之后调用。</summary>
        public static void Attach(Window? w, Action? save = null)
        {
            if (w == null) return;

            ApplyTopmostRules(w);

            // ④ 快捷键：Esc 关闭 / Ctrl+S 保存
            // ⚠ Esc 必须走 KeyBinding，不能用 PreviewKeyDown（2026-10-18 实测：
            //   焦点在 ComboBox/滚动区里时 PreviewKeyDown 收不到，Esc 按下去没反应；
            //   Ctrl+S 一直好使，是因为它本来就是 KeyBinding —— 统一成同一套机制）。
            w.InputBindings.Add(new KeyBinding(new RelayCommand(() => { try { w.Close(); } catch { } }),
                                               Key.Escape, ModifierKeys.None));
            w.InputBindings.Add(new KeyBinding(new RelayCommand(() => DoSave(w, save)), Key.S, ModifierKeys.Control));

            // ⑤ 底部按钮带
            BuildBar(w, save);
        }

        /// <summary>
        /// 「只要最高权限，不要按钮带」的轻量版 —— 老板 2026-10-21 第 1 条
        /// 「最上层窗口拥有最大权限，其他层要遮挡住」的补齐。
        ///
        /// 2026-10-18 立的规矩只覆盖了调 <see cref="Attach"/> 的窗口，
        /// 实测有三个窗口一直漏在外面（PPT 导入方式、字幕条样式、切换台）：
        /// 它们既没置顶、也没排除抓屏 —— 播出框是全屏置顶的，这些窗口一开就被压在下面看不见，
        /// 更糟的是如果它恰好盖在播出框上，还会被一起抓进推流/录制。
        ///
        /// 这里只做三件事（不碰布局，所以不会挤压任何窗口的现有版式）：
        ///   ① 置顶  ② 不透明  ③ 抓屏排除   + Esc 关闭（自带「取消」的窗口按 Esc 也符合直觉）
        /// </summary>
        public static void Pin(Window? w)
        {
            if (w == null) return;
            ApplyTopmostRules(w);
            w.InputBindings.Add(new KeyBinding(new RelayCommand(() => { try { w.Close(); } catch { } }),
                                               Key.Escape, ModifierKeys.None));
        }

        /// <summary>置顶 + 不透明 + 抓屏排除（Attach 与 Pin 共用，避免两套规则走偏）。</summary>
        private static void ApplyTopmostRules(Window w)
        {
            // ① 最高权限（置顶）
            w.Topmost = true;

            // ② 不许半透明：被遮挡的后面界面一律不能"透"出来
            w.Opacity = 1;

            // ③ 抓屏排除（Win10 2004+；老系统退回 WDA_MONITOR，抓屏里是黑块而不是界面）
            if (new WindowInteropHelper(w).Handle != IntPtr.Zero) ExcludeFromCapture(w);
            else w.SourceInitialized += (_, _) => ExcludeFromCapture(w);
        }

        /// <summary>每个窗口左下角那行「已保存 HH:mm:ss」提示（窗口关掉就从表里去掉）。</summary>
        private static readonly System.Collections.Generic.Dictionary<Window, TextBlock> Hints = new();

        private static void DoSave(Window w, Action? save)
        {
            try
            {
                if (save != null) save();
                else SaveHook?.Invoke();
                ShowHint(w);
            }
            catch (Exception ex)
            {
                try { MessageBox.Show(w, "保存失败：" + ex.Message, "Cutlance", MessageBoxButton.OK, MessageBoxImage.Warning); }
                catch { }
            }
        }

        /// <summary>所有打开着的工作框都闪一下「已保存 HH:mm:ss」（保存成功后由主窗统一广播）。</summary>
        public static void NotifyAllSaved()
        {
            var list = new System.Collections.Generic.List<Window>(Hints.Keys);
            foreach (var w in list) ShowHint(w);
        }

        private static void ShowHint(Window w)
        {
            if (!Hints.TryGetValue(w, out var hint)) return;
            hint.Text = "已保存 " + DateTime.Now.ToString("HH:mm:ss");
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            t.Tick += (_, _) => { t.Stop(); hint.Text = ""; };
            t.Start();
        }

        /// <summary>
        /// 在窗口内容下面加一条按钮带（DockPanel 底部），不覆盖原有布局：
        /// 左边「保存」，右边「关闭」（窗口自己已有「关闭」时就不重复加）。
        /// </summary>
        private static void BuildBar(Window w, Action? save)
        {
            if (w.Content is not FrameworkElement root) return;
            if (root is DockPanel dp && dp.Tag as string == "WorkFrameBar") return;   // 已经装过

            var hint0 = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                FontSize = 11,
                Foreground = Res<Brush>(w, "Brush.Ok") ?? Brushes.LightGreen,
                Text = ""
            };
            var btnSave0 = MakeButton(w, "保存 (Ctrl+S)", () => DoSave(w, save), accent: true);

            // 首选：窗口自己已经有一行按钮（「重启输出 / 刷新设备列表 / 关闭」这种），
            // 就把「保存」插到「关闭」左边，跟原生按钮排在一起，不加多余的横条。
            if (TryInsertIntoExistingRow(root, hint0, btnSave0))
            {
                Hints[w] = hint0;
                w.Closed += (_, _) => Hints.Remove(w);
                return;
            }

            bool hasClose = HasButtonText(root, "关闭");

            var row = new Grid();
            var left = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
            var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            row.Children.Add(left);
            row.Children.Add(right);

            var hint = hint0;
            var btnSave = btnSave0;
            left.Children.Add(hint);
            left.Children.Add(btnSave);

            if (!hasClose)
                right.Children.Add(MakeButton(w, "关闭 (Esc)", () => { try { w.Close(); } catch { } }, accent: false));

            var bar = new Border
            {
                Child = row,
                Padding = new Thickness(12, 8, 12, 8),
                Background = Res<Brush>(w, "Brush.Bg.StatusBar") ?? new SolidColorBrush(Color.FromRgb(0x16, 0x1B, 0x22)),
                BorderBrush = Res<Brush>(w, "Brush.Border") ?? new SolidColorBrush(Color.FromRgb(0x2A, 0x33, 0x40)),
                BorderThickness = new Thickness(0, 1, 0, 0)
            };

            var outer = new DockPanel { Tag = "WorkFrameBar", LastChildFill = true };
            DockPanel.SetDock(bar, Dock.Bottom);
            outer.Children.Add(bar);

            // ⚠ 必须先把 root 从窗口上摘下来再塞进 DockPanel ——
            //   直接 Add 会抛「指定的元素已经是另一个元素的子元素」（2026-10-18 实测崩在
            //   AudioMixerWindow 构造函数里）。摘下来 → 加进去 → 整体再挂回 Content 即可。
            try { w.Content = null; }
            catch { }
            outer.Children.Add(root);
            w.Content = outer;

            Hints[w] = hint;
            w.Closed += (_, _) => Hints.Remove(w);
        }

        /// <summary>
        /// 找窗口里内容含「关闭」的那个按钮，若它在 StackPanel 里，就把「保存」和提示插到它左边。
        /// 只认 StackPanel：Grid 里插子元素会打乱行列定义（后台布局的窗口基本都用 StackPanel 收按钮）。
        /// </summary>
        private static bool TryInsertIntoExistingRow(DependencyObject root, TextBlock hint, Button save)
        {
            var close = FindButton(root, "关闭");
            if (close?.Parent is not StackPanel panel) return false;
            int idx = panel.Children.IndexOf(close);
            if (idx < 0) return false;
            panel.Children.Insert(idx, save);
            panel.Children.Insert(idx, hint);
            return true;
        }

        private static Button? FindButton(DependencyObject root, string text)
        {
            if (root is Button b && b.Content is string s && s.Replace(" ", "").Contains(text)) return b;
            // ⚠ 走**逻辑树**而不是可视树：Attach 是在构造函数里调用的，这时窗口还没布局，
            //   可视树是空的（只有 ContentPresenter 一个壳），XAML 里写好的按钮一个都找不到。
            foreach (var child in LogicalTreeHelper.GetChildren(root))
            {
                if (child is not DependencyObject d) continue;
                var r = FindButton(d, text);
                if (r != null) return r;
            }
            return null;
        }

        private static Button MakeButton(Window w, string text, Action onClick, bool accent)
        {
            var b = new Button
            {
                Content = text,
                MinWidth = 96,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                FontSize = 12,
                Cursor = Cursors.Hand,
                ToolTip = accent ? "把当前设置保存进工程文件（Ctrl+S）" : "关闭本窗口（Esc）"
            };
            if (Res<Brush>(w, accent ? "Brush.Accent.Dim" : "Brush.Bg.Surface") is Brush bg) b.Background = bg;
            if (Res<Brush>(w, "Brush.Text.Primary") is Brush fg) b.Foreground = fg;
            if (Res<Brush>(w, "Brush.Border") is Brush bd) b.BorderBrush = bd;
            b.Click += (_, _) => onClick();
            return b;
        }

        private static T? Res<T>(Window w, string key) where T : class
        {
            try { return w.TryFindResource(key) as T ?? Application.Current?.TryFindResource(key) as T; }
            catch { return null; }
        }

        /// <summary>窗口里有没有内容含「关闭」的按钮（避免摆两个关闭）。</summary>
        private static bool HasButtonText(DependencyObject root, string text) => FindButton(root, text) != null;

        // ==================== 抓屏排除 ====================

        private const uint WDA_NONE = 0;
        private const uint WDA_MONITOR = 1;
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

        [DllImport("user32.dll")]
        private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

        /// <summary>
        /// 只做「抓屏排除」，不装按钮带 / 不置顶。
        /// 给那些不该被当成完整工作框、但压在播出框上会被抓进推流的小对话框用（如「改名」）。
        /// </summary>
        public static void ExcludeOnly(Window? w)
        {
            if (w == null) return;
            if (new WindowInteropHelper(w).Handle != IntPtr.Zero) ExcludeFromCapture(w);
            else w.SourceInitialized += (_, _) => ExcludeFromCapture(w);
        }

        private static void ExcludeFromCapture(Window w)
        {
            // 自动化截图要能看见界面 → 允许用环境变量临时关掉（只影响本机测试）
            if (Environment.GetEnvironmentVariable("CUTLANCE_NO_WDA") == "1") return;
            try
            {
                var h = new WindowInteropHelper(w).Handle;
                if (h == IntPtr.Zero) return;
                // Win10 2004+ 才有 0x11；失败再退回 0x1（老系统：抓屏里是一块黑，也不会露出界面）
                if (!SetWindowDisplayAffinity(h, WDA_EXCLUDEFROMCAPTURE))
                    SetWindowDisplayAffinity(h, WDA_MONITOR);
            }
            catch { /* 不支持就算了：不影响窗口本身使用 */ }
        }

        /// <summary>极简命令包装（避免为两行代码再引一个 RelayCommand 依赖）。</summary>
        private sealed class RelayCommand : ICommand
        {
            private readonly Action _run;
            public RelayCommand(Action run) => _run = run;
            public event EventHandler? CanExecuteChanged { add { } remove { } }
            public bool CanExecute(object? parameter) => true;
            public void Execute(object? parameter) => _run();
        }
    }
}
