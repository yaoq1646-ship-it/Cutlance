using System;
using System.Windows;
using Cutlance.Services;

namespace Cutlance
{
    /// <summary>
    /// 纯文本帮助窗（老板 2026-10-20）。
    ///
    /// 为什么不继续用 `MessageBox.Show`：它会把窗口撑到内容的自然高度 ——
    /// 快捷键说明 70 多行，实测弹框做到 **3385 像素高**，而屏幕只有 1600，
    /// 下半截内容看不见也滚不动（等于帮助后半本书是废的）。
    ///
    /// 这个窗口只干三件事：
    ///   ① 把高度夹进屏幕可用区（1366×768@125% 只有 614 DIP 高，必须夹）；
    ///   ② 内容放只读 TextBox —— 能滚、能选中、能 Ctrl+C；
    ///   ③ 抓屏排除 + 置顶：压在播出框上时不会被抓进推流（不装 WorkFrame 的按钮带，
    ///      因为它不需要「保存」，那是给参数面板用的）。
    /// </summary>
    public partial class HelpWindow : Window
    {
        public HelpWindow(string title, string text, string? hint = null)
        {
            InitializeComponent();

            if (!string.IsNullOrWhiteSpace(title)) Title = title;
            Body.Text = text ?? "";
            // 顶部提示按用途换：操作手册不该说「全部键位都能改」，那会让人以为整本手册是键位表。
            if (!string.IsNullOrWhiteSpace(hint)) HintText.Text = hint;

            // 跟着主窗居中：不指定的话 WPF 按 CW_USEDEFAULT 丢在屏幕默认位置，
            // 主窗在离屏测试（-12000）时会变成「帮助窗单独弹到老板屏幕上」。
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            // 先置顶、再排除抓屏（顺序无所谓，但都要在窗口句柄生成后/前兜住）
            Topmost = true;
            WorkFrame.ExcludeOnly(this);

            FitToWorkArea();

            Loaded += (_, _) =>
            {
                // 离屏回归测试：主窗在 -12000 时 CenterOwner 会退化到主屏 (0,0)，把屏幕盖住。
                // 跟着主窗躲到屏外，保证「跑测试时电脑还能正常用」。
                if (Owner != null && Owner.Left < -1000)
                {
                    Left = Owner.Left + 40;
                    Top = Owner.Top + 40;
                }

                Body.CaretIndex = 0;
                Body.Focus();
            };
        }

        /// <summary>
        /// 把窗口夹进当前屏幕可用区：小屏笔电上不夹会把「关闭」按钮顶到屏幕外。
        ///
        /// ⚠ 位置只做「别出左上边」的兜底，**不能无条件拉回主屏工作区**：
        ///   离屏回归测试时主窗在 -12000，拉回去就等于测试时窗口真弹到老板屏幕上
        ///   （MEMORY 里那条「主窗离屏时先判是否在主屏再夹」说的就是这个）。
        /// </summary>
        private void FitToWorkArea()
        {
            try
            {
                var wa = SystemParameters.WorkArea;
                double maxH = wa.Height - 60;
                double maxW = wa.Width - 60;
                if (maxH > 260 && Height > maxH) Height = maxH;
                if (maxW > 400 && Width > maxW) Width = maxW;

                // 只有主窗就在主屏上时，才把位置也夹进工作区
                bool ownerOnScreen = Owner != null && Owner.Left > -1000 && Owner.Top > -1000;
                if (!ownerOnScreen) return;
                if (Left < wa.Left) Left = wa.Left + 8;
                if (Top < wa.Top) Top = wa.Top + 8;
            }
            catch { }
        }

        /// <summary>
        /// 把正文滚到包含 key 的那一行（供截图自检用，正常使用不会调用）。
        /// 为什么需要：手册全文一百多行，只截顶部等于没验 —— 改动的章节趴在下面看不见。
        /// </summary>
        public bool ScrollToText(string key, int contextLines = 3)
        {
            if (string.IsNullOrEmpty(key)) return false;
            try
            {
                int i = Body.Text.IndexOf(key, StringComparison.Ordinal);
                if (i < 0) return false;
                int line = Body.GetLineIndexFromCharacterIndex(i);
                if (line < 0) return false;
                // 用「关键字相对视口的 Y 坐标」反算滚动位置。
                // 为什么不用 ScrollToLine：它只保证目标行**可见**，目标行已在视口边缘时几乎不滚，
                // 截图永远截在章节标题那一行。这里直接把它推到视口顶部往下 contextLines 行处。
                var rect = Body.GetRectFromCharacterIndex(i);
                if (!rect.IsEmpty && rect.Height > 0)
                {
                    double want = Math.Max(0, contextLines) * rect.Height;
                    Body.ScrollToVerticalOffset(Math.Max(0, Body.VerticalOffset + rect.Top - want));
                    return true;
                }

                Body.CaretIndex = i;
                Body.ScrollToLine(line);
                return true;
            }
            catch { return false; }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
