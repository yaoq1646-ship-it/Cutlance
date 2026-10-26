using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Cutlance.Views
{
    /// <summary>
    /// 极简「输入一行文字」对话框（2026-10-22 为输出布局改名/新建而加）。
    ///
    /// 为什么不引 Microsoft.VisualBasic 的 InputBox：那是 WinForms 原生窗，
    /// 不进 WPF 的视觉风格，在高 DPI 下还会糊。
    /// 这里用代码搭一个 420 宽的小窗，跟随主窗居中，Enter = 确定、Esc = 取消。
    /// </summary>
    public static class SimpleInputBox
    {
        /// <summary>弹出输入框；取消或空则返回 null。</summary>
        public static string? Show(Window? owner, string title, string label, string initial = "")
        {
            var win = new Window
            {
                Title = title,
                Width = 420,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = owner != null
                    ? WindowStartupLocation.CenterOwner
                    : WindowStartupLocation.CenterScreen,
                ShowInTaskbar = false,
                Owner = owner,
                WindowStyle = WindowStyle.ToolWindow
            };

            // 尽量跟着主题走；资源找不到就退回系统色（不能因为缺一个画刷就弹不出来）
            if (owner?.TryFindResource("Brush.Bg.Panel") is Brush bg) win.Background = bg;
            var fg = owner?.TryFindResource("Brush.Text") as Brush ?? Brushes.Black;

            var root = new StackPanel { Margin = new Thickness(18, 16, 18, 14) };

            root.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = fg,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 8),
                TextWrapping = TextWrapping.Wrap
            });

            var box = new TextBox
            {
                Text = initial ?? "",
                Height = 28,
                FontSize = 13,
                Padding = new Thickness(6, 3, 6, 3),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            root.Children.Add(box);

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };
            var ok = new Button { Content = "确定", Width = 78, Height = 28, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = "取消", Width = 78, Height = 28, IsCancel = true };
            if (owner?.TryFindResource("VmButton") is Style bs) { ok.Style = bs; cancel.Style = bs; }
            ok.Click += (_, _) => { win.DialogResult = true; };
            row.Children.Add(ok);
            row.Children.Add(cancel);
            root.Children.Add(row);

            win.Content = root;
            win.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
            // Enter 直接提交（TextBox 里按 Enter 默认不触发默认按钮，这里补一下）
            box.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { win.DialogResult = true; e.Handled = true; }
            };

            bool? r = win.ShowDialog();
            if (r != true) return null;
            var text = box.Text?.Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
    }
}
