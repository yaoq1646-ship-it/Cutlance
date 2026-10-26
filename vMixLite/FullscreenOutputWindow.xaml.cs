using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cutlance.Models;
using Cutlance.Services;

namespace Cutlance
{
    /// <summary>
    /// HDMI 全屏镜像输出窗口（模块7）。
    ///
    /// ── 2026-10-20：改为「合成器直供」──────────────────────────────────────
    ///   以前用 screen:// 重新抓「播出框」那块屏幕 —— 主窗被盖住/最小化，副屏就黑。
    ///   现在直接显示合成器的成品帧（与推流、录制同一份像素），跟主窗状态彻底无关。
    ///
    /// ── 2026-10-24：搬到独立 STA 线程（老板要求拆线程）──────────────────────
    ///   以前画面刷新走主 UI 线程：主界面一卡，副屏就停一下。
    ///   现在这个窗口由 <see cref="Views.FullscreenOutputHost"/> 在**自己的线程**上创建、
    ///   自己持有 WriteableBitmap，帧由合成器 sink 拷过来（每帧多拷一份 ≈8MB）。
    ///   所以：**这个类的构造函数必须在那条专用线程上调用**，不要去 new 在主线程上试。
    ///
    /// 右键「刷新画面」保留（卡住/花屏/热插拔后重新贴图 + 重新定位）。
    /// </summary>
    public partial class FullscreenOutputWindow : Window
    {
        /// <summary>目标显示器：刷新时用它把窗口重新摆正（热插拔/改分辨率后常会错位）。</summary>
        private MonitorInfo? _monitor;

        /// <summary>本窗口自己的帧缓冲（在这条线程上创建，只在这条线程上写）。</summary>
        private WriteableBitmap? _frame;

        public FullscreenOutputWindow(MonitorInfo? monitor)
        {
            InitializeComponent();

            _monitor = monitor;

            // 在窗口句柄就绪与首帧加载时各铺一次，确保精确铺满目标显示器（避免默认位置闪一下）
            SourceInitialized += (_, _) => PositionOnMonitor(_monitor);
            Loaded += (_, _) => PositionOnMonitor(_monitor);
        }

        /// <summary>兼容旧签名（老调用方传 ImageSource）：现在由宿主推帧，忽略 source。</summary>
        public FullscreenOutputWindow(MonitorInfo? monitor, ImageSource? source) : this(monitor) { }

        /// <summary>
        /// 自检用：右键菜单挂了几项。
        /// 不能靠"真的右键点一下副屏"来验（会移动鼠标、打扰正在用的电脑），
        /// 所以这里直接读 XAML 挂上去的那个 ContextMenu。
        /// </summary>
        internal int RefreshMenuItemCount
            => (Content as FrameworkElement)?.ContextMenu?.Items.Count ?? 0;

        /// <summary>
        /// 把一帧贴到窗口上（**必须在窗口自己的线程调用**）。
        /// 尺寸变了就重建 WriteableBitmap —— 换分辨率/横竖屏切换时不会错位或拉伸。
        /// </summary>
        public void ShowFrame(IntPtr buf, int stride, int w, int h)
        {
            if (buf == IntPtr.Zero || w <= 0 || h <= 0 || stride <= 0) return;

            if (_frame == null || _frame.PixelWidth != w || _frame.PixelHeight != h)
            {
                _frame = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
                Host.Source = _frame;
            }

            // ⚠ AddDirtyRect 必须在 Lock 里面调：放外面会抛「在图像未锁定时无法调用此方法」，
            //   实测每个 beginframe 刷一条错误日志、画面一帧都没上去（2026-10-24）。
            _frame.Lock();
            try
            {
                _frame.WritePixels(new Int32Rect(0, 0, w, h), buf, stride * h, stride);
                _frame.AddDirtyRect(new Int32Rect(0, 0, w, h));
            }
            finally { _frame.Unlock(); }
        }

        /// <summary>
        /// 右键「刷新画面」（老板 2026-10-21 第 3 条）：副屏卡住 / 花屏 / 从最小化恢复后停旧帧 /
        /// 热插拔后错位，都可以靠这一下救回来。
        /// 两步：① Source 摘掉再挂回去 → 逼 WPF 重新上传纹理重画一遍；② 重新摆到目标显示器。
        /// 不做"重建窗口"，那样会闪黑一下，导播中更不能接受。
        /// </summary>
        private void MenuRefresh_Click(object sender, RoutedEventArgs e) => RefreshNow();

        internal void RefreshNow()
        {
            try
            {
                var src = Host.Source;
                Host.Source = null;
                Host.InvalidateMeasure();
                Host.UpdateLayout();
                Host.Source = src;
                Host.InvalidateVisual();

                PositionOnMonitor(_monitor);
                Services.ErrorLog.Info("[刷新] 全屏输出窗重新贴图 + 重新定位");
            }
            catch (Exception ex)
            {
                try { Services.ErrorLog.Error("[刷新] 全屏输出窗刷新失败", ex); } catch { }
            }
        }

        /// <summary>重新摆到指定显示器（或原目标显示器）。</summary>
        internal void RepositionTo(MonitorInfo? monitor)
        {
            if (monitor != null) _monitor = monitor;
            PositionOnMonitor(_monitor);
        }

        private void PositionOnMonitor(MonitorInfo? monitor)
        {
            if (monitor == null) return;
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero) DisplayService.MoveWindowToMonitor(hwnd, monitor);
            }
            catch { }
        }

        /// <summary>
        /// 旧接口（抓屏版每次播出框变化都要重开媒体）。
        /// 现在画面来自合成器，窗口位置与画面内容无关，这里保留只为不改调用方。
        /// </summary>
        public void UpdateCapture(double left, double top, double width, double height)
        {
            // 合成器直供：什么都不用做。留着空实现，防止以后有人误以为「必须重开媒体」。
        }
    }
}
