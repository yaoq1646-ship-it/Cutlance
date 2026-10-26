using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace CutlanceSetup
{
    /// <summary>
    /// 安装界面（纯代码构建，不用 XAML —— net48 下少一层编译依赖，出问题也好查）。
    /// 三步：显示装到哪 → 一条进度条 → 装完变「立即运行」。
    /// 刻意不做「下一步/下一步」，测试机器上少点几下，也少一个出错的机会。
    /// </summary>
    internal sealed class InstallerWindow : Window
    {
        private static readonly Brush Bg = New("#0F1216");
        private static readonly Brush Panel = New("#171B21");
        private static readonly Brush Line = New("#2A303A");   // 不能叫 Border：会遮蔽 System.Windows.Controls.Border 类型
        private static readonly Brush Text = New("#E8EDF3");
        private static readonly Brush Muted = New("#8A96A6");
        private static readonly Brush Accent = New("#6EC7E8");
        private static readonly Brush Alarm = New("#FF5A5A");  // 「特别提醒」四个字用它

        // 状态行两句固定文案：勾选时靠**文本比对**认出「那句红字」，别用颜色判断
        private const string HintReady = "勾选下面的「我已阅读并同意」，再点右下角「开始安装」。";
        private const string HintNeedAgree = "请先勾选下面的「我已阅读并同意上述说明」，再点「开始安装」。";

        private string _dir;
        private readonly bool _noLaunch;
        private readonly ProgressBar _bar;
        private readonly TextBlock _status;
        private readonly Button _primary;
        private readonly CheckBox _agree;
        private TextBox _dirBox;
        private bool _done;

        public InstallerWindow(string dir, bool noLaunch)
        {
            _dir = dir;
            _noLaunch = noLaunch;

            Title = "Cutlance 安装程序";
            Width = 660;
            // 多了免责声明和同意勾选，但整体高度反而要收：说明行是 Star，窗口给多了
            // 就会在中间空一大块（600 高时实测空了 167 DIP）。520 是贴合内容 + 装完后
            // 状态文字变三行的余量。小屏笔电上还要夹进工作区，否则按钮会被顶到屏幕外。
            Height = 520;
            try
            {
                double maxH = System.Windows.SystemParameters.WorkArea.Height - 40;
                if (maxH > 470 && Height > maxH) Height = maxH;
            }
            catch { }
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Bg;
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
            UseLayoutRounding = true;

            var root = new Grid { Margin = new Thickness(26, 22, 26, 20) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 标题
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 位置卡
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 免责声明
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 进度
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 说明
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 同意勾选
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 按钮

            // ---- 标题 ----
            var head = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
            head.Children.Add(new TextBlock
            {
                Text = "Cutlance",
                FontSize = 26,
                FontWeight = FontWeights.SemiBold,
                Foreground = Text
            });
            head.Children.Add(new TextBlock
            {
                Text = "轻量级直播导播台 · 安装程序",
                FontSize = 12.5,
                Foreground = Muted,
                Margin = new Thickness(0, 4, 0, 0)
            });
            Grid.SetRow(head, 0);
            root.Children.Add(head);

            // ---- 安装位置卡 ----
            var card = new Border
            {
                Background = Panel,
                BorderBrush = Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(14, 10, 14, 12),
                Margin = new Thickness(0, 0, 0, 16)
            };
            var cardBody = new StackPanel();
            cardBody.Children.Add(new TextBlock
            {
                Text = "安装到（可以改，或点「浏览…」另选一个目录）",
                FontSize = 11.5,
                Foreground = Muted
            });

            // 2026-10-21 老板要求「安装目录要可选」：原来是只读 TextBlock，装哪儿改不了。
            // 改成可编辑输入框 + 浏览按钮；默认仍是 %LOCALAPPDATA%\Cutlance。
            var dirRow = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            dirRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            dirRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _dirBox = new TextBox
            {
                Text = _dir,
                FontSize = 13,
                Foreground = Text,
                Background = New("#0F1216"),
                BorderBrush = Line,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(7, 4, 7, 5),
                VerticalContentAlignment = VerticalAlignment.Center,
                CaretBrush = Text,
                ToolTip = "程序会装到这个目录。建议放在用户目录下 —— 那里不需要管理员权限。"
            };
            Grid.SetColumn(_dirBox, 0);
            dirRow.Children.Add(_dirBox);

            var browse = MakeButton("浏览…", false);
            browse.Padding = new Thickness(13, 5, 13, 6);
            browse.Margin = new Thickness(8, 0, 0, 0);
            browse.Click += (s, e) => BrowseDir();
            Grid.SetColumn(browse, 1);
            dirRow.Children.Add(browse);

            cardBody.Children.Add(dirRow);
            cardBody.Children.Add(new TextBlock
            {
                Text = "默认装在当前用户目录下：不需要管理员权限、不弹 UAC。装到 C:\\Program Files 等受保护目录时需要以管理员身份运行安装器。",
                FontSize = 11.5,
                Foreground = Muted,
                Margin = new Thickness(0, 7, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
            card.Child = cardBody;
            Grid.SetRow(card, 1);
            root.Children.Add(card);

            // ---- 免责声明（老板 2026-10-20：装之前必须看到，同意才能装）----
            // 「特别提醒」四个字用红字 + 加粗，其余保持正文色 —— 整段都红会变成"警报墙"，
            // 反而没人读。红字只用来把最关键那半句拎出来。
            var eulaCard = new Border
            {
                Background = Panel,
                BorderBrush = New("#4A2B2B"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(14, 10, 14, 12),
                Margin = new Thickness(0, 0, 0, 14)
            };
            var eulaBody = new StackPanel();
            eulaBody.Children.Add(new TextBlock
            {
                Text = "安装前请阅读",
                FontSize = 11.5,
                Foreground = Muted,
                Margin = new Thickness(0, 0, 0, 5)
            });

            var eula = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12.5,
                Foreground = New("#C7D0DA"),
                LineHeight = 21
            };
            eula.Inlines.Add(new Run("感谢大家的支持，个人测试环境有限，欢迎沟通交流bug问题，以便不断完善。"));
            eula.Inlines.Add(new Run("特别提醒：")
            {
                Foreground = Alarm,
                FontWeight = FontWeights.Bold
            });
            eula.Inlines.Add(new Run(
                "本软件免费分享，务必充分测试后使用，必要时做好备份。" +
                "开发者不对使用过程中的出现的任何问题负责。"));
            eulaBody.Children.Add(eula);

            eulaCard.Child = eulaBody;
            Grid.SetRow(eulaCard, 2);
            root.Children.Add(eulaCard);

            // ---- 进度条 ----
            _bar = new ProgressBar
            {
                Height = 6,
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Foreground = Accent,
                Background = New("#232A33"),
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(_bar, 3);
            root.Children.Add(_bar);

            _status = new TextBlock
            {
                Text = HintReady,
                FontSize = 12,
                Foreground = Muted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 0)
            };
            Grid.SetRow(_status, 4);
            root.Children.Add(_status);

            // ---- 按钮 ----
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };

            var cancel = MakeButton("关闭", false);
            cancel.Margin = new Thickness(0, 0, 8, 0);
            cancel.Click += (s, e) => Close();

            _primary = MakeButton("开始安装", true);
            _primary.MinWidth = 104;
            _primary.Click += Primary_Click;
            // 刻意不用 IsEnabled=false 表示「还没同意」：
            // WPF 默认按钮模板在禁用态会换成浅灰底黑字，在这套深色界面上比主按钮还显眼，
            // 反而像"可以点"。改成点击时拦截 —— 样式统一，而且点了能明确告诉你为什么没装。

            buttons.Children.Add(cancel);
            buttons.Children.Add(_primary);
            Grid.SetRow(buttons, 6);
            root.Children.Add(buttons);

            // ---- 同意勾选 ----
            // 不用 IsEnabled 联动按钮（禁用态会变浅色，见上面注释），只记录状态；
            // 真正的拦截在 Primary_Click 里。
            _agree = new CheckBox
            {
                Content = "我已阅读并同意上述说明（软件免费分享，请先充分测试并做好备份）",
                Foreground = Text,
                FontSize = 13,
                VerticalContentAlignment = VerticalAlignment.Center,
                IsChecked = false,
                Margin = new Thickness(0, 0, 0, 8),
                Cursor = System.Windows.Input.Cursors.Hand,
                // 深色自绘模板：系统默认模板在这套深色窗体上「未勾时看不见、勾上变黑勾」，见方法注释
                Template = BuildAgreeTemplate()
            };

            // 勾上就把「请先勾选…」那句红字撤掉（老板 2026-10-21：勾了还挂着红字，像没勾上）。
            // 只认**这一句文本**：安装失败、程序占用、目录写不进这些重要提示，不许被勾选动作冲掉。
            _agree.Checked += (s, e) =>
            {
                if (_done || _status.Text != HintNeedAgree) return;
                _status.Foreground = Muted;
                _status.Text = HintReady;
            };
            Grid.SetRow(_agree, 5);
            root.Children.Add(_agree);

            Content = root;

            if (Shortcuts.IsAppRunning())
            {
                _status.Text = "检测到 Cutlance 正在运行 —— 安装会覆盖程序文件，请先关掉它再点「开始安装」。";
                _status.Foreground = New("#E8B45A");
            }

            SourceInitialized += (s, e) => ApplyDarkTitleBar();
            Loaded += (s, e) => { _bar.Value = 0; };
        }

        private async void Primary_Click(object sender, RoutedEventArgs e)
        {
            if (_done)
            {
                // 装完了：启动程序并退出安装器
                Shortcuts.Launch(_dir);
                Close();
                return;
            }

            // 没同意就别装。这是老板 2026-10-20 明确要求的"同意后才能安装"。
            if (_agree.IsChecked != true)
            {
                _status.Foreground = Alarm;
                _status.Text = HintNeedAgree;
                _agree.Focus();
                return;
            }

            // 目录可选（2026-10-21 老板要求）：点安装时才把输入框里的路径当准 ——
            // 先体检，不合格当场说清楚，别等解压到一半才报错。
            string target = ResolveDir(out string dirErr);
            if (target == null)
            {
                _status.Foreground = Alarm;
                _status.Text = dirErr;
                _dirBox.Focus();
                _dirBox.SelectAll();
                return;
            }
            _dir = target;
            if (!string.Equals(_dirBox.Text, _dir, StringComparison.Ordinal)) _dirBox.Text = _dir;
            _dirBox.IsReadOnly = true;      // 装的过程中不许再改，避免"显示的"和"装的"不是一处

            if (Shortcuts.IsAppRunning())
            {
                MessageBox.Show(this,
                    "Cutlance 正在运行，程序文件被占用，装不进去。\n请先关闭 Cutlance，再点「开始安装」。",
                    "请先关闭 Cutlance", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _primary.IsEnabled = false;
            _primary.Content = "安装中…";
            _status.Foreground = Muted;
            _status.Text = "正在释放文件…";

            var cts = new CancellationTokenSource();
            var progress = new Progress<KeyValuePair<int, string>>(p =>
            {
                _bar.Value = p.Key;
                if (!string.IsNullOrEmpty(p.Value)) _status.Text = p.Value;
            });

            string error = null;
            try
            {
                await Task.Run(() => Payload.Extract(_dir, (pct, msg) =>
                {
                    ((IProgress<KeyValuePair<int, string>>)progress)
                        .Report(new KeyValuePair<int, string>(pct, msg));
                }, cts.Token));
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            if (error != null)
            {
                _status.Foreground = New("#FF6B6B");
                _status.Text = "安装失败：" + error + "\n（如果提示无权限，请换一个目录或联系技术支持）";
                _primary.IsEnabled = true;
                _primary.Content = "重试";
                return;
            }

            _bar.Value = 100;
            _status.Text = "正在创建快捷方式…";
            Shortcuts.Create(_dir);

            _done = true;
            _status.Foreground = New("#7ED6A5");
            _status.Text = "安装完成。程序已装到：" + _dir +
                           "\n运行中出现错误时，错误信息会自动保存在程序目录的 logs 文件夹里" +
                           "（菜单「帮助 → 打开错误日志目录」也能直接打开）。";
            _primary.IsEnabled = true;
            _primary.Content = _noLaunch ? "完成" : "立即运行";
            if (_noLaunch)
            {
                _done = false;
                _primary.Content = "完成";
                _primary.Click -= Primary_Click;
                _primary.Click += (s2, e2) => Close();
            }
        }

        /// <summary>选安装目录（2026-10-21 老板要求「安装目录要可选」）。</summary>
        private void BrowseDir()
        {
            try
            {
                using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
                {
                    dlg.Description = "选择 Cutlance 的安装位置（建议放在用户目录下，免管理员权限）";
                    dlg.ShowNewFolderButton = true;
                    string cur = (_dirBox.Text ?? "").Trim().Trim('"');
                    try { if (Directory.Exists(cur)) dlg.SelectedPath = cur; } catch { }

                    var owner = new Win32Owner(new System.Windows.Interop.WindowInteropHelper(this).Handle);
                    if (dlg.ShowDialog(owner) == System.Windows.Forms.DialogResult.OK)
                    {
                        _dirBox.Text = dlg.SelectedPath;
                        _status.Foreground = Muted;
                        _status.Text = "安装位置已改为：" + dlg.SelectedPath;
                    }
                }
            }
            catch (Exception ex)
            {
                _status.Foreground = Alarm;
                _status.Text = "打开目录选择框失败：" + ex.Message + "（可以直接在框里手动输入路径）";
            }
        }

        /// <summary>FolderBrowserDialog 要一个 IWin32Window 才能挂在安装窗上，否则会跑到屏幕中间自成一体。</summary>
        private sealed class Win32Owner : System.Windows.Forms.IWin32Window
        {
            public IntPtr Handle { get; private set; }
            public Win32Owner(IntPtr h) { Handle = h; }
        }

        /// <summary>把输入框里的路径取出来体检。返回 null = 不合格，err 说明原因。</summary>
        private string ResolveDir(out string err)
        {
            err = null;
            string d = (_dirBox.Text ?? "").Trim().Trim('"');
            if (d.Length == 0)
            {
                err = "请先填写安装目录，或点「浏览…」选一个。";
                return null;
            }

            try { d = Path.GetFullPath(d); }
            catch { err = "这个路径不合法：" + d; return null; }

            string root = Path.GetPathRoot(d) ?? "";
            if (root.Length > 0 &&
                string.Equals(d.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                err = "不能直接装到磁盘根目录（如 C:\\），请选一个子目录，例如 C:\\Cutlance。";
                return null;
            }
            if (File.Exists(d))
            {
                err = "这个路径已经是一个文件，不能当目录用：" + d;
                return null;
            }
            if (root.Length > 0 && !Directory.Exists(root))
            {
                err = "盘符不存在：" + root;
                return null;
            }
            if (d.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                err = "路径里有非法字符，请重新选。";
                return null;
            }
            if (d.Length > 200)
            {
                err = "路径太长（超过 200 字符），请换短一点的目录。";
                return null;
            }

            // 真去写一下 —— 很多"看着能装"的目录其实没权限，
            // 等解压到一半才失败，用户只会看到"安装失败"四个字。
            try
            {
                Directory.CreateDirectory(d);
                string probe = Path.Combine(d, ".cutlance-write-probe");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
            }
            catch (Exception ex)
            {
                err = "这个目录写不进去：" + d + "\n" + ex.Message +
                      "\n（如果选的是 C:\\Program Files 之类，请改用用户目录，或以管理员身份运行本安装器）";
                return null;
            }
            return d;
        }

        /// <summary>
        /// 「同意」勾选框的深色自绘模板。
        /// 系统默认 CheckBox 在这套深色窗体上是「深底 + 系统浅色方块」，未勾时边框几乎看不见，
        /// 勾上后又变成浅灰底 + 黑勾，跟旁边的青色主按钮抢视觉 —— 老板 2026-10-21 说「不精致」。
        /// 自绘成：15×15 圆角方框，未选 = 深底细灰边，选中 = 青底 + 白色对勾，鼠标移上去边框提亮。
        /// 布局用 DockPanel 而不是横向 StackPanel：StackPanel 给 Content 的可用宽度是无限大，
        /// 长文案不会自动换行、直接顶出窗口（本项目踩过的老坑）。
        /// </summary>
        private static ControlTemplate BuildAgreeTemplate()
        {
            var dock = new FrameworkElementFactory(typeof(DockPanel));
            dock.SetValue(DockPanel.LastChildFillProperty, true);

            // ---- 方框 ----
            var box = new FrameworkElementFactory(typeof(Border));
            box.Name = "box";
            box.SetValue(FrameworkElement.WidthProperty, 15.0);
            box.SetValue(FrameworkElement.HeightProperty, 15.0);
            box.SetValue(Border.CornerRadiusProperty, new CornerRadius(3.5));
            box.SetValue(Border.BorderThicknessProperty, new Thickness(1.4));
            box.SetValue(Border.BorderBrushProperty, New("#5C6878"));
            box.SetValue(Border.BackgroundProperty, New("#0D1117"));
            box.SetValue(DockPanel.DockProperty, Dock.Left);
            box.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            box.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 1, 9, 0));

            // ---- 对勾（默认隐藏，选中时由 Trigger 放出来）----
            // 注意：本文件 using System.IO，直接写 Path 会解析成 System.IO.Path → 必须全限定。
            var tick = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
            tick.Name = "tick";
            tick.SetValue(System.Windows.Shapes.Path.DataProperty,
                          Geometry.Parse("M 3.2,8.0 L 6.1,10.9 L 11.9,4.4"));
            tick.SetValue(System.Windows.Shapes.Path.StrokeProperty, New("#0B1014"));
            tick.SetValue(System.Windows.Shapes.Path.StrokeThicknessProperty, 2.0);
            tick.SetValue(System.Windows.Shapes.Path.StrokeStartLineCapProperty, PenLineCap.Round);
            tick.SetValue(System.Windows.Shapes.Path.StrokeEndLineCapProperty, PenLineCap.Round);
            tick.SetValue(System.Windows.Shapes.Path.StrokeLineJoinProperty, PenLineJoin.Round);
            tick.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            tick.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            tick.SetValue(UIElement.VisibilityProperty, Visibility.Hidden);
            box.AppendChild(tick);
            dock.AppendChild(box);

            // ---- 文案 ----
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.Name = "content";
            content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            content.SetValue(System.Windows.Controls.ContentPresenter.RecognizesAccessKeyProperty, false);
            dock.AppendChild(content);

            var tpl = new ControlTemplate(typeof(CheckBox)) { VisualTree = dock };

            // 勾上：青底 + 显示对勾（对勾用深色，压得住青色）
            var on = new Trigger
            {
                Property = System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
                Value = true
            };
            on.Setters.Add(new Setter(Border.BackgroundProperty, Accent) { TargetName = "box" });
            on.Setters.Add(new Setter(Border.BorderBrushProperty, Accent) { TargetName = "box" });
            on.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible) { TargetName = "tick" });
            tpl.Triggers.Add(on);

            // 鼠标移上去 / 键盘焦点：边框提亮，让人知道这块能点
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BorderBrushProperty, New("#9FB0C4")) { TargetName = "box" });
            tpl.Triggers.Add(hover);

            var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
            focus.Setters.Add(new Setter(Border.BorderBrushProperty, Accent) { TargetName = "box" });
            tpl.Triggers.Add(focus);

            return tpl;
        }

        private static Button MakeButton(string text, bool primary)
        {
            var b = new Button
            {
                Content = text,
                Padding = new Thickness(16, 7, 16, 8),
                FontSize = 13,
                FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = primary ? Accent : New("#232A33"),
                Foreground = primary ? New("#0B1014") : Text,
                BorderThickness = new Thickness(0)
            };
            b.BorderThickness = new Thickness(0);
            // 不用自绘模板：实测在 PrintWindow / 不同 DPI 下容易出现「按钮变白底黑字」，
            // 直接用系统默认模板 + 自己给底色，行为最稳。
            return b;
        }

        /// <summary>原生标题栏切成深色（Win10 1809+ / Win11）。失败就算了，不影响安装。</summary>
        private void ApplyDarkTitleBar()
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                int on = 1;
                // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE（19 是老版本号）
                if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
            }
            catch { }
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private static Brush New(string hex)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
