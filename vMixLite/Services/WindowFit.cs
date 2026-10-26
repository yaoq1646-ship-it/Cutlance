using System.Windows;

namespace Cutlance.Services
{
    /// <summary>
    /// 窗口尺寸自适应：把按设计稿写死的高/宽收敛到「当前屏幕可用区」之内。
    ///
    /// 为什么需要：
    ///   几个配置窗（输入信号 900 / 网络流 820 / 输出设置 790 / 画面效果 740 DIP）
    ///   是按高分辨率屏定的尺寸。换成 1080p@150% 这类设置时，**逻辑可用高只有约 670 DIP**
    ///   （1080 物理 ÷ 1.5 - 任务栏），窗口打开就顶到屏幕外：底部「添加为输入 / 关闭」
    ///   一排按钮被切掉，而标题栏已经跑到屏幕上方，没法把它拖回来。
    ///
    /// 设计取舍（重要）：
    ///   只纠正**打开时的初始尺寸**，不设 MaxHeight/MaxWidth。
    ///   原因：WPF 的最大化尺寸会被 MaxHeight 压住 —— 一旦限制，用户点最大化时
    ///   窗口四边会留出黑边（本机 2560×1600@150% 下就会白留 16 DIP），得不偿失。
    ///   缩小后内容靠窗口自身的 ScrollViewer 承担滚动，保证任何屏幕都够得着。
    /// </summary>
    public static class WindowFit
    {
        /// <summary>四周余量（DIP）：别让窗口紧贴任务栏。</summary>
        private const double Margin = 16;

        /// <summary>在窗口构造函数里、InitializeComponent() 之后调用一次即可。</summary>
        public static void FitToWorkArea(Window? w)
        {
            if (w == null || w.WindowState != WindowState.Normal) return;

            try
            {
                // SystemParameters.WorkArea 已经是 DIP（已按主屏 DPI 换算），可直接与 Width/Height 比较
                var wa = SystemParameters.WorkArea;
                double maxH = wa.Height - Margin;
                double maxW = wa.Width - Margin;

                // 下限保护：远程桌面 / 异常 DPI 下别把窗口压成一条缝
                if (maxH > 240 && !double.IsNaN(w.Height) && w.Height > maxH) w.Height = maxH;
                if (maxW > 360 && !double.IsNaN(w.Width) && w.Width > maxW) w.Width = maxW;
            }
            catch
            {
                // 纯观感问题：查不到就按设计尺寸显示，不影响功能
            }
        }
    }
}
