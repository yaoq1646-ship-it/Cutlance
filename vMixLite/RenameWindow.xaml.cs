using System.Windows;
using System.Windows.Input;
using Cutlance.Services;

namespace Cutlance
{
    /// <summary>
    /// 「改名」小对话框（老板 2026-10-20）。
    ///
    /// 只负责收一个名字，不做任何业务判断 —— 改哪个输入、改完怎么刷新，全在 MainWindow 里。
    /// 三条细节：
    ///   · 打开即全选旧名字：多半是要整个替换（"GATHER CAM2" 改成"主讲机位"），全选能省两次按键；
    ///   · 只做「抓屏排除」不装工作框按钮带：它压在播出框上时不能被抓进推流，
    ///     但也不该多出一排「保存 / 关闭」（那是给参数面板用的）；
    ///   · 空名/纯空格直接挡回：列表里出现一个空白项，比名字丑得多。
    /// </summary>
    public partial class RenameWindow : Window
    {
        /// <summary>用户确定后的新名字（取消时为空串）。</summary>
        public string ResultName { get; private set; } = "";

        public RenameWindow(string currentName, string subtitle = "")
        {
            InitializeComponent();

            NameBox.Text = currentName ?? "";
            if (!string.IsNullOrWhiteSpace(subtitle))
                TipText.Text = "给「" + subtitle + "」起个自己认得的名字";

            WorkFrame.ExcludeOnly(this);

            Loaded += (_, _) =>
            {
                NameBox.Focus();
                NameBox.SelectAll();
            };
        }

        private void NameBox_KeyDown(object sender, KeyEventArgs e)
        {
            // 回车走「确定」、Esc 走「取消」：TextBox 会吃掉这两个键的冒泡，所以在这里就地处理。
            if (e.Key == Key.Enter) { Accept(); e.Handled = true; }
            else if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e) => Accept();
        private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void Accept()
        {
            var name = (NameBox.Text ?? "").Trim();
            if (name.Length == 0)
            {
                ErrText.Text = "名字不能是空的";
                NameBox.Focus();
                return;
            }
            if (name.Length > 60)
            {
                ErrText.Text = "名字太长（最多 60 个字）";
                NameBox.Focus();
                return;
            }
            ResultName = name;
            DialogResult = true;
        }
    }
}
