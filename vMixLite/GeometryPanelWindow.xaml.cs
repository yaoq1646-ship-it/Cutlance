using Cutlance.Services;
using System;
using System.Windows;
using Cutlance.Views;

namespace Cutlance
{
    /// <summary>
    /// 精调面板的「浮出」窗口（老板 2026-10-18：「可拖出成浮动大窗」）。
    ///
    /// 三条硬约束（都是为了导播安全）：
    ///   ① **非模态** —— 模态窗口会卡住主窗口，导播中等于事故；
    ///   ② **不置顶** —— Topmost 会盖住播出画面；
    ///   ③ **关窗不销毁面板状态** —— 只是把面板还回右栏（由主窗口负责，见 MainWindow.FloatGeometryPanel）。
    ///
    /// 窗口位置默认贴着主窗口右缘（"停靠右栏"），之后用户拖到哪就记住哪（同一会话内）。
    /// </summary>
    public partial class GeometryPanelWindow : Window
    {
        public GeometryPanelWindow()
        {
            InitializeComponent();
            WorkFrame.Attach(this);   // 统一：置顶 + 抓屏排除 + 底部「保存/关闭」+ Esc/Ctrl+S
            Panel.ShowPopOut = false;     // 已经在浮动窗口里，不需要再"浮出"
            Panel.ShowCollapse = false;   // 浮动窗口固定展开
            Panel.SetExpanded(true);
        }

        /// <summary>内嵌的精调面板（主窗口把同一个 Target/事件接到它身上）。</summary>
        public GeometryPanel Embedded => Panel;

        /// <summary>用户关窗时通知主窗口把面板搬回右栏。</summary>
        public event Action? ClosedByUser;

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            ClosedByUser?.Invoke();
        }
    }
}
