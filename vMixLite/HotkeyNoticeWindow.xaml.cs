using System.Windows;
using Cutlance.Services;

namespace Cutlance
{
    /// <summary>
    /// 进入「导播键盘 / 快捷键」设置前的**必读确认框**。
    ///
    /// 明确告知：需要厂商 SDK / 专用驱动的外设键盘，本软件收不到它的按键。
    /// 流程上是一道闸门 —— 只有单击「确定」才会进入快捷键设置；
    /// 单击「关闭」或按 Esc（含直接关窗），都视为放弃（返回 <c>false</c>）。
    /// </summary>
    public partial class HotkeyNoticeWindow : Window
    {
        public HotkeyNoticeWindow()
        {
            InitializeComponent();
            WindowFit.FitToWorkArea(this);   // 小屏自动收敛

            // ⚠ 必须置顶：这是 ShowDialog 模态框，一旦被别的置顶窗口（工作框现在全是 Topmost）
            //   盖住，主窗和它都点不动 —— 老板会卡在这一步，正好对应
            //   「要加上关闭键，方便进行下一步动作」那句反馈。
            Topmost = true;

            // 本框停留期间挂起全局快捷键：用户若顺手在外设上试键，不会误触发导播动作。
            // 关闭即恢复（此时若接着打开设置窗，由 HotkeyWindow 自己接管捕获模式）。
            HotkeyMap.CaptureMode = true;
            Closed += (_, _) => HotkeyMap.CaptureMode = false;
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e) => DialogResult = true;

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
