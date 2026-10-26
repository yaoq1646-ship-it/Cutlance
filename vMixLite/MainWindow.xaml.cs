using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;
using Microsoft.Win32;
using Cutlance.Services.Audio;
using Cutlance.Models;
using Cutlance.Services;

namespace Cutlance
{
    public partial class MainWindow : Window
    {
        public ObservableCollection<InputSource> Inputs { get; } = new();
        public ObservableCollection<OverlayItem> Overlays { get; } = new();

        /// <summary>
        /// 节目（输出）监视器左边缘在**屏幕**上的 X 坐标（DIP）。
        /// 老板 2026-10-19 23:24：「字幕条样式出现位置应该在左侧，不覆盖输出框」
        /// —— 样式窗自己贴左放，但要靠这个值算出「再往右就压住输出画面了」的宽度上限。
        /// 输出框还没布局好（IsLoaded=false）时返回 0，调用方按「不裁剪」处理。
        /// </summary>
        public double ProgramCellLeftDip
        {
            get
            {
                try
                {
                    if (ProgramCell == null || !ProgramCell.IsLoaded) return 0;
                    var p = ProgramCell.PointToScreen(new Point(0, 0));
                    var dpi = VisualTreeHelper.GetDpi(ProgramCell);
                    return p.X / dpi.DpiScaleX;
                }
                catch { return 0; }
            }
        }
        public ObservableCollection<Layer> Layers { get; } = new();   // 模块5：输出图层
        public ObservableCollection<PlayListItem> PlayItems { get; } = new();   // 模块A：播放列表项

        private readonly VideoEngine _engine = VideoEngine.Instance;
        private InputSource? _previewSource;          // 当前 PREVIEW 指向的输入
        private TransitionType _transition = TransitionType.Cut;
        private double _duration = 1.0;
        private OverlayItem? _selOverlay;             // 当前选中的叠加项
        private Layer? _selLayer;                     // 当前选中的图层
        private bool _isBlack = false;                // FTB（淡黑）状态
        private string? _projectPath;                 // 当前工程文件路径（*.cutlance），null = 未保存过

        // —— 模块A：播放列表（多素材自动顺播）——
        // 画面呈现走「主画面图层」（见 ShowMainLayer）：切项时只换它的 Source 重新 Play，
        // 不每项都新建/销毁 VideoView（那会闪一下且吃资源）。
        private readonly PlayListService _playSvc = new();

        // 主画面图层：播放列表的当前项、倒计时「切换到指定输入」共用这一个全屏图层。
        private Layer? _mainLayer;

        // 状态栏「重要提示」保护窗：倒计时到点 / 播放列表切项这类提示写入后的一小段时间内，
        // 不允许 LibVLC 预览的 Opening/Playing 回调把它顶掉。
        // ⚠ 这是必须的：那些回调是异步的（Play() 之后几十毫秒才回来），
        //    而无条件写 StatusText 会把刚弹出的提示覆盖成「○ PREVIEW：xxx」，用户根本看不到结果。
        private DateTime _statusHoldUntil = DateTime.MinValue;
        private bool StatusHeld => DateTime.Now < _statusHoldUntil;

        /// <summary>写状态栏。hold=true 表示这是"结果类"提示，短时间内不可被预览回调覆盖。</summary>
        private void SetStatus(string text, bool hold = false)
        {
            StatusText.Text = text;
            if (hold) _statusHoldUntil = DateTime.Now.AddSeconds(2.5);
        }

        // ===== 右键刷新画面（老板 2026-10-21 第 3 条）=====

        private void MenuRefreshPreview_Click(object sender, RoutedEventArgs e)
            => RefreshPicture(PvwImage, "预览");

        private void MenuRefreshProgram_Click(object sender, RoutedEventArgs e)
            => RefreshPicture(PgmImage, "输出");

        /// <summary>
        /// 把 Image 的 Source 摘下来再挂回去，逼 WPF 丢掉旧的可视层缓存、按当前
        /// WriteableBitmap 重新上传纹理再画一遍。不是"截屏再贴"，画面内容仍来自合成器。
        ///
        /// 能治：显卡驱动复位（TDR）后的花屏/花块、切换显示器或改 DPI 后不重绘、
        ///       窗口从最小化恢复后停在旧帧、双屏热插拔后副屏画面卡死。
        /// 治不了：合成器自己的线程停了 —— 那种情况这里会顺手报一句，让人知道该去查哪，
        ///         而不是让人误以为点了刷新还是坏的。
        /// </summary>
        private void RefreshPicture(Image img, string name)
        {
            try
            {
                bool running = _monitor != null && _monitor.Running;

                var src = img.Source;
                img.Source = null;
                img.InvalidateMeasure();
                img.UpdateLayout();
                img.Source = src;
                img.InvalidateVisual();

                SetStatus(running
                    ? $"{name}画面已刷新。"
                    : $"{name}画面已刷新 —— 但合成器没在运行，画面不会动（看「视图 → 切换台」里的状态）。",
                    hold: true);
                ErrorLog.Info($"[刷新] {name}画面重新贴图（合成器={(running ? "运行中" : "已停止")}）");
            }
            catch (Exception ex)
            {
                SetStatus("刷新失败：" + ex.Message, hold: true);
                ErrorLog.Error("[刷新] " + name + "画面刷新失败", ex);
            }
        }

        // ===== 播出保护（抓屏语义下的补救，见 Services/BroadcastGuard.cs） =====
        private string _guardBaseTitle = "Cutlance";
        private bool _guardRestoring;

        /// <summary>
        /// 播出框被别的窗口盖住了。状态栏本身可能也在遮挡物下面，所以
        /// 同时改窗口标题 + 闪任务栏按钮（只闪，不抢前台 —— 抢焦点会打断老板正在做的事）。
        /// </summary>
        private void OnBroadcastBlocked(string who)
        {
            SetStatus("⚠ 播出画面被「" + who + "」遮挡 —— 观众现在看到的就是它", hold: true);
            try
            {
                // 标题可能已被工程名改过，这里只在没带标记时重新取样
                if (!Title.StartsWith("⚠", StringComparison.Ordinal)) _guardBaseTitle = Title;
                Title = "⚠ 画面被遮挡 · " + _guardBaseTitle;
                FlashTaskbar(5);
            }
            catch { }
        }

        private void OnBroadcastRecovered()
        {
            SetStatus("播出画面已恢复可见", hold: true);
            try
            {
                if (Title.StartsWith("⚠", StringComparison.Ordinal)) Title = _guardBaseTitle;
                FlashTaskbar(0);   // 0 = 停止闪烁
            }
            catch { }
        }

        /// <summary>输出期间拦掉最小化：一次误触就会让整场直播变成桌面。</summary>
        private void GuardAgainstMinimize()
        {
            if (_guardRestoring) return;
            if (WindowState != WindowState.Minimized) return;

            var g = BroadcastGuard.Instance;
            if (!g.IsLive || !g.ProtectMinimize) return;

            _guardRestoring = true;
            try
            {
                WindowState = WindowState.Normal;
                // 只有合成器起不来、退回抓屏兜底时才会走到这里（合成器在跑时 IsLive 恒 false）。
                SetStatus("播出中不能最小化：当前是抓屏兜底模式，最小化会让推流画面变成桌面", hold: true);
                FlashTaskbar(3);
            }
            catch { }
            finally { _guardRestoring = false; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FLASHWINFO
        {
            public uint cbSize;
            public IntPtr hwnd;
            public uint dwFlags;
            public uint uCount;
            public uint dwTimeout;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

        /// <summary>闪任务栏按钮，count=0 表示停止。不激活窗口、不抢焦点。</summary>
        private void FlashTaskbar(uint count)
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                var fi = new FLASHWINFO
                {
                    hwnd = hwnd,
                    dwFlags = count == 0 ? 0u /* FLASHW_STOP */ : 3u /* FLASHW_ALL */,
                    uCount = count,
                    dwTimeout = 0
                };
                fi.cbSize = (uint)Marshal.SizeOf(fi);
                FlashWindowEx(ref fi);
            }
            catch { }
        }

        // —— 模块B：倒计时 / 触发器 ——
        // 配置窗口与主窗口共享同一个 CountdownService 单例；到点动作在这里执行。
        private readonly CountdownService _countdown = CountdownService.Instance;

        // —— 倒计时牌（2026-10-24 老板要求）：右栏图层区上面那个，给观众看的 ——
        // 与模块B 分开：那个归零就停并触发动作，这个归零后继续走负数直到手动停。
        private readonly CountdownClock _cdClock = new();
        private bool _cdOverlayOn;                 // 「叠加」开关：数字是否进输出画面
        private double _cdFontPx = 120;            // 叠加字号（1080p 基准）

        // 叠加数字的位置（归一化中心点，0.5/0.5 = 画面正中）。老板可以直接在输出框里拖着走（10-24）。
        private double _cdX = 0.5, _cdY = 0.5;

        /// <summary>
        /// 截图输出目录（自检钩子 / 文档配图用）。
        /// 可用环境变量 CUTLANCE_SHOT_DIR 指定；不指定就放系统临时目录，避免在源码里写死任何本机路径。
        /// </summary>
        private static string ShotDir()
        {
            var d = Environment.GetEnvironmentVariable("CUTLANCE_SHOT_DIR");
            if (string.IsNullOrWhiteSpace(d)) d = Path.Combine(Path.GetTempPath(), "cutlance-shots");
            return d;
        }
        private bool _cdDragging;
        private Point _cdDragStart;                // 按下时的画布坐标（DIP）
        private double _cdDragNx0, _cdDragNy0;     // 按下时的归一化位置

        // 字体/颜色（2026-10-24 老板「做字体/颜色自定义」）：与 8 路叠加层同一套交互
        private readonly Views.CountdownStyle _cdStyle = new();
        private bool _cdStyleOpen;                 // 「样式」两行是否展开（默认收起，省右栏高度）

        // —— Win32 低级鼠标钩子（WH_MOUSE_LL）：绕开 LibVLC VideoView 底层 Win32 子窗口(HwndHost 空气空间)吞事件，
        //    直接接收全屏鼠标输入，预览框与输出图层里的视频/文字一律可拖可缩放 ——
        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        // ── 真实鼠标输入（只给自检用：验证"系统 → 钩子"完整路径，2026-10-26）──
        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT pt);
        [DllImport("user32.dll")]
        private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_MOVE = 0x0001;
        private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT pt);
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);
        // 客户区左上角 → 屏幕物理像素（确定性换算，不依赖 WPF PointToScreen 的单位约定）
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        // 倒计时「弹框提醒」用：调 Win32 的 MessageBoxW 而不是 WPF 的 MessageBox.Show。
        // 原因：只有 Win32 版能挂 MB_TOPMOST —— 提醒框必须压在其它程序之上，否则导播在别的窗口里干活就看不见提醒了。
        // （MB_SETFOREGROUND 让它抢到前台，未点掉前不会缩到后面去。）
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        // MessageBoxW 的标志位（winuser.h）
        private const uint MB_OK = 0x0, MB_YESNO = 0x4;
        private const uint MB_ICONWARNING = 0x30, MB_DEFBUTTON2 = 0x100;
        private const uint MB_TOPMOST = 0x40000, MB_SETFOREGROUND = 0x10000;
        private const int IDYES = 6;

        // 退出兜底：万一第三方库释放路径死锁，用内核级结束进程（不会残留窗口/声音/占用）
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")]
        private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        private const int VK_LBUTTON = 0x01;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private static LowLevelMouseProc? _mouseProc;
        private static IntPtr _mouseHook = IntPtr.Zero;

        /// <summary>拖动中 MOVE 计数（诊断限流用，每 10 条记一次）。</summary>
        private int _cdMoveSeen;

        /// <summary>钩子累计收到多少个事件（诊断：0 说明系统根本没派发给我们，钩子被摘了或没装上）。</summary>
        private long _hookSeen;

        /// <summary>钩子回调里只入队，不写盘 —— 低级钩子回调里做磁盘 IO 有被系统摘钩的风险。</summary>
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _hookDiag = new();

        /// <summary>把钩子攒下的诊断批量落盘（500ms 一次，在 UI 线程跑，不在回调里）。</summary>
        private void FlushHookDiag()
        {
            if (_hookDiag.IsEmpty) return;
            while (_hookDiag.TryDequeue(out var line))
                try { ErrorLog.Info(line); } catch { }
        }
        private IntPtr _hwnd = IntPtr.Zero;   // 主窗口句柄（用于判定前台窗口与 DPI 换算）

        // —— Win32 低级键盘钩子（WH_KEYBOARD_LL）：外设导播键盘 / 快捷键的「全局生效」——
        //    为什么用钩子而不是 WPF 的 KeyDown：导播键盘常常是在主窗**没有焦点**时按的
        //    （人在别的窗口里操作、或把导播台摆在副屏），WPF 事件根本收不到。
        //    委托签名复用鼠标钩子的 LowLevelMouseProc —— 两者都是 (int, WPARAM, LPARAM) → LRESULT，
        //    同一个 P/Invoke SetWindowsHookEx 重载即可，不必再加一份声明。
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
        private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private static LowLevelMouseProc? _keyProc;
        private static IntPtr _keyHook = IntPtr.Zero;

        // 修饰键状态由钩子自己记账 —— 与鼠标钩子里的教训同源：低级钩子上下文中
        // GetAsyncKeyState 的返回值不可信，只有"自己收到的事件流"才是准的。
        private bool _modCtrl, _modShift, _modAlt;

        // 被本程序吃掉的按键：它对应的 KEYUP 也要一起吃掉，否则系统会留下"按下了却没抬起"的错状态
        private readonly bool[] _swallowedVk = new bool[256];

        // 拖动/缩放图层用临时状态（鼠标统一由全局 Win32 消息钩子接管，绕过 VideoView 底层 Win32 子窗口吞事件）
        private Layer? _dragLayer;
        private bool _resizing;

        // 左键是否按下（由钩子的事件流自己记账，绝不用 GetAsyncKeyState —— 见 MouseHookProc 里的说明）
        private bool _leftDown;

        /// <summary>清除一切拖拽/缩放状态（松开左键、失焦、切走前台时都调用，避免“粘住乱飞”）。</summary>
        /// <summary>
        /// 清除「输出画布内图层」的拖拽/缩放状态。
        ///
        /// ⚠ 绝对不要在这里动 _pvDrag / _pvResize ！
        /// 那两个是「预览框浮层（PreviewOpPopup）」自己的状态，由 PreviewOpRoot 的鼠标事件
        /// （MouseLeftButtonDown / MouseMove / LostMouseCapture）全权管理。
        /// 低级鼠标钩子在每个 WM_MOUSEMOVE 之前都会调用本方法当安全网，如果顺手把 _pvDrag 清零，
        /// 预览框拖动会在按下的第一个移动事件就被掐断 —— 症状正是"按住左键拖不动画面"。
        /// </summary>
        /// <summary>
        /// 倒计时拖动诊断（2026-10-26）。老板报「叠加后拖不动」，而且上一轮我猜错了一次 ——
        /// 所以这次要一次按键就能看出卡在哪一环，不再靠猜：
        ///   前台是不是本进程 / 点有没有落在画布内 / 有没有被误判成"点在控件上" /
        ///   命中判定结果 / 数字到底可不可见 / 它此刻的像素矩形。
        /// 只在「叠加开着」时记录，频率可控（正常点画面不会刷日志）。
        /// </summary>
        private void LogCdDragProbe(Point clientDip, string stage)
        {
            try
            {
                IntPtr fg = GetForegroundWindow();
                uint fgPid = 0;
                try { GetWindowThreadProcessId(fg, out fgPid); } catch { }

                bool hasRect = TryElementRect(LayersCanvas, out var cr);
                bool inCanvas = hasRect && cr.Contains(clientDip);
                bool overUi = IsOverInteractiveUi(clientDip);
                bool hitCd = HitCountdownAt(clientDip);
                var r = _monitor?.CountdownRect ?? (0, 0, 0, 0);

                _hookDiag.Enqueue($"[倒计时拖动诊断/{stage}] client=({clientDip.X:0},{clientDip.Y:0}) " +
                              $"前台pid={fgPid}/本进程={Environment.ProcessId} 同主窗={fg == _hwnd} " +
                              $"画布内={inCanvas} 画布=({(hasRect ? cr.X : -1):0},{(hasRect ? cr.Y : -1):0}," +
                              $"{(hasRect ? cr.Width : 0):0},{(hasRect ? cr.Height : 0):0}) " +
                              $"交互控件={overUi} 命中倒计时={hitCd} 叠加开={_cdOverlayOn} " +
                              $"数字可见={_monitor?.CountdownVisible == true} " +
                              $"矩形=({r.X},{r.Y}) {r.W}x{r.H} 钩子装={_mouseHook != IntPtr.Zero} " +
                              $"钩子累计收到={System.Threading.Interlocked.Read(ref _hookSeen)}");
            }
            catch (Exception ex)
            {
                _hookDiag.Enqueue("[倒计时拖动诊断] 失败：" + ex.Message);
            }
        }

        private void ClearDragState()
        {
            _dragLayer = null; _resizing = false;
            _cdDragging = false;
        }

        // ===== 退出清理（问题5）=====
        // 病根：ShutdownMode 默认是「最后一个窗口关闭才退出」。老板常把「输出设置 / 音频混音台 /
        // 画面效果」开着，此时关主窗口 → WPF 认为还有窗口 → 进程不退出 → 预览/图层的 LibVLC
        // 播放器一直在往声卡出声（“退出后系统里还有软件声音”），exe 也被占用（没法重新编译/覆盖）。
        // 治法：① 把 ShutdownMode 设成「关主窗口即退出」；② 关窗口时把音频/播放器/推流全部停干净。
        private bool _shuttingDown;

        // ── A 方案（2026-10-20 老板拍板）：中间监视器由合成器出图 ──
        // 一个输入只解一次，预览和节目共用；画面在内存里真实存在，与窗口状态无关。
        private Views.MonitorBridge? _monitor;
        private DispatcherTimer? _monitorSync;

        // ── 待生效的输出分辨率（0 = 没有待生效项）──
        // 合成器的画面缓冲/预览位图/离屏画布都在构造时按尺寸分配，运行时改不了。
        // 所以「换成竖屏」只能记在这里，等重启程序（App.OnStartup 先读工程尺寸再建窗）才真生效。
        // StreamService.OutputWidth/Height 始终保持**实际**尺寸，绝不假装改过 —— 见 MenuRes_Click。
        private int _pendingResW, _pendingResH;

        /// <summary>用户点了浮动条上的「稍后」→ 收起提示条，但待生效选择仍留着（菜单里还能再点）。</summary>
        private bool _resBarDismissed;

        /// <summary>合成器实际出图尺寸（没起合成器时退回设定值）。</summary>
        public int ActiveOutputWidth => _monitor?.Width ?? StreamService.Instance.OutputWidth;
        public int ActiveOutputHeight => _monitor?.Height ?? StreamService.Instance.OutputHeight;

        /// <summary>已记下但还没重启生效的分辨率；没有则返回 false。</summary>
        public bool GetPendingRes(out int w, out int h)
        {
            w = _pendingResW; h = _pendingResH;
            return _pendingResW > 0 && _pendingResH > 0;
        }

        /// <summary>
        /// 关闭程序的统一收尾。
        ///
        /// ── 为什么这么写（血泪教训，全部实测过）──────────────────────────────
        /// 本程序给 MediaPlayer 装了 LibVLC 的「音频接管回调」（amem），一旦播放过媒体，
        /// 关闭路径里只要触碰 LibVLC —— MediaPlayer.Stop()/Dispose()、AudioBus 关声卡、
        /// HDMI 播放器释放 —— 都有概率触发 native 死锁。更致命的是：这个死锁会把整个 CLR
        /// 拖住，**所有托管线程一起被挂起**。实测表现：专门用来兜底的看门狗线程打出第一行
        /// 日志后，连 500ms 的循环点都再没出现过 —— 任何"进程内兜底"方案全部失效
        /// （那些死锁的进程最后靠 Windows 自己回收，要 10 秒以上才消失，期间窗口关了、
        /// 声音还在、exe 被占用）。
        ///
        /// ── 所以策略改成 ────────────────────────────────────────────────
        ///  只做"进程外仍然可见"的必要收尾，然后直接硬终止本进程：
        ///    1. 杀掉 ffmpeg 子进程 —— 它是独立进程，父进程死了它不会跟着死，必须主动收尾；
        ///    2. 卸载全局鼠标钩子 —— 系统级资源；
        ///    3. 关掉顶层浮层窗口（纯 WPF 对象，安全）。
        ///  其余内存、句柄、LibVLC 的 native 线程一律交还操作系统回收 —— 瞬间完成、
        ///  绝不会出现"关了还有声音"或"exe 被占用"。
        /// </summary>
        private void ShutdownAll()
        {
            if (_shuttingDown) return;
            _shuttingDown = true;

            // ① 兜底看门狗：独立线程，1.2 秒后无条件硬杀（万一下面哪一步意外卡住）
            var killer = new System.Threading.Thread(() =>
            {
                try { System.Threading.Thread.Sleep(1200); } catch { }
                try { TerminateProcess(GetCurrentProcess(), 0); } catch { }
                try { Environment.Exit(0); } catch { }
            })
            { IsBackground = true, Name = "cutlance-hard-exit" };
            killer.Start();

            // ② 杀掉 ffmpeg 子进程。
            //    ⚠ 这是整个关闭流程里唯一"必须做"的事：ffmpeg 是独立进程，我们自我终止后
            //    它会变成孤儿继续推流/录制。HardStop 内部有 600ms 超时，绝不会无限等。
            //    绝不能调 StreamService.Stop() —— 它会连带 AudioBus.StopPipe()，
            //    实测那条路径也可能卡住（停 TcpListener 的线程 Join）。
            try { FfmpegOutput.Instance.HardStop(); } catch { }
            //    模块C 的「输入预处理」ffmpeg 同理 —— 它也是独立进程，不杀掉会变成孤儿继续编码。
            try { InputPreprocessService.Instance.HardStopAll(); } catch { }
            //    模块F：NDI 输出（合成器直供；兜底时是抓屏进程）+ 一个 NDI 发送会话。
            //    不收掉的后果很直观：程序关了，局域网里的 NDI 源还在广播，
            //    接收端会看到一个「幽灵源」一直挂着（ffmpeg 也变成孤儿）。
            try { NdiOutputService.Instance.Stop(); } catch { }
            try { NdiService.Instance.Shutdown(); } catch { }

            // ③ 停合成器线程 + 卸载全局鼠标 / 键盘钩子（系统级资源）
            try { _monitorSync?.Stop(); } catch { }
            try { _cdClock.Shutdown(); } catch { }   // 倒计时牌：停表，别留一个 100ms 定时器在退出路径上
            try { _monitor?.Stop(); } catch { }   // 只停线程，不 Dispose 非托管缓冲（下一步就硬终止）
            RemoveMouseHook();
            RemoveKeyHook();

            // ④ 立刻硬终止本进程 —— 不关声卡、不停播放器、不释放 LibVLC，
            //    这些资源交给操作系统回收，比"优雅释放"快且绝不会卡死。
            try { TerminateProcess(GetCurrentProcess(), 0); } catch { }
            try { Environment.Exit(0); } catch { }
        }

        private Point _dragStart;
        private double _dragOriginX, _dragOriginY;
        private double _resizeLeft, _resizeTop;   // 缩放时固定的左上角（画布像素）

        // 预览框内的拖动/缩放状态（作用于「当前选中的那一路」，见 _selPreviewSlot）
        private bool _pvDrag, _pvResize;
        private Point _pvStart;
        private double _pvOX, _pvOY, _pvRL, _pvRT;

        // —— 多路预览组（老板 2026-10-17 第③条）——
        // 预览框里可以同时摆多路画面（每路独立 MediaPlayer + VideoView，互不干扰），
        // 各自拖位置 / 缩大小，再「全部上输出」一次性推到输出框。
        // ⚠ 位置/缩放都存在 PreviewSlot 里（不再有 _pvX/_pvY/_pvScale 这组「全局单例」字段）——
        //   以前那些字段只能描述一路，多路时会互相踩。
        private readonly List<PreviewSlot> _previewSlots = new();
        private PreviewSlot? _selPreviewSlot;         // 当前操作的那一路（拖动/滚轮作用对象）
        private const int MaxPreviewSlots = 9;        // 与分屏网格上限一致（3×3）

        // —— 精调面板（老板 2026-10-18）——
        // 面板的 Target 只有两种可能：当前预览那一路，或当前选中的输出图层。
        // 点预览框 / 点图层列表都会把「目标类型」切过去，面板随之重建。
        private bool _geoPanelTargetIsPreview = true;
        private GeometryPanelWindow? _geoFloatWindow;

        // —— 预览放大（老板 2026-10-20）——
        // 放大 = 把输出那一列收成 0、预览占满中区；还原 = 恢复两边各一半。
        // 为什么记「原本输出是显示还是隐藏」：老板可能本来就隐藏了输出，
        // 还原时要回到他本来的状态，不能一律打开。
        private bool _previewZoomed;
        private bool _pgmVisibleBeforeZoom = true;

        // 单画面放大（2026-10-25）：把预览列收起、输出占满监视区。
        // 与「放大预览」互斥 —— 两者都在改 PvwCol/GoCol/PgmCol 三列宽度，同时开必然打架。
        private bool _programZoomed;
        private bool _pvwVisibleBeforeZoom = true;

        // —— 导播键盘 / 快捷键设置窗（非模态单例：主窗和 F9 都能开，重复开就激活已有那个）——
        private HotkeyWindow? _hotkeyWindow;

        // —— 模块7：HDMI 全屏镜像输出 ——
        private Views.FullscreenOutputHost? _hdmiWindow;
        private DispatcherTimer? _hdmiRepositionTimer;

        public MainWindow()
        {
            InitializeComponent();
            LoadSystemFonts();       // 字体下拉列出系统已安装的全部字体（老板 2026-10-18 第 4 条）
            LoadCountdownFonts();    // 倒计时牌的字体/颜色（老板 2026-10-24）

            // 预览画面区的右键菜单（老板 2026-10-23）：
            // 预览框上盖着一层操作浮层 PreviewOpPopup（PreviewOpRoot，Background=#01000000），
            // 它专门用来命中拖动/缩放，但顺带把**右键**也全吃掉了 —— 结果是菜单只能在标题栏上弹，
            // 点画面右键毫无反应，「刷新画面」等于用不到。
            // 这里把同一份 ContextMenu 也挂到浮层上：两个入口、一套菜单，不复制逻辑。
            PreviewOpRoot.ContextMenu = PreviewCell.ContextMenu;

            InputTiles.ItemsSource = Inputs;
            OverlayList.ItemsSource = Overlays;
            LayerList.ItemsSource = Layers;
            PlayListBox.ItemsSource = PlayItems;

            // 各「工作框」（混音台/画面效果/设置类）底部的「保存」按钮都走这里：
            // 它们的参数本来就改动即生效，这一步只把当前状态落盘到工程文件。
            Cutlance.Services.WorkFrame.SaveHook = () => SaveProject(false);

            // 模块C：输入预处理层 —— 把它的错误接到状态栏（文案已写成可直接显示的一句话），
            // 并在后台预热一次性探测（本机有没有可用的 NVENC、摄像头设备名），
            // 免得用户第一次点播放时卡在探测上。
            InputPreprocessService.Instance.Error += msg =>
                Dispatcher.Invoke(() => { try { SetStatus(msg, hold: true); } catch { } });
            // 模块E B-3：断流重连的过程提示（"第 N 次重连…"/"信号已恢复"）同样上状态栏
            InputPreprocessService.Instance.Notify += msg =>
                Dispatcher.Invoke(() => { try { SetStatus(msg, hold: true); } catch { } });
            InputPreprocessService.Instance.WarmUp();

            // 模块F：NDI 的 Start 已异步化（首次 CreateSender 要 12~60 秒，同步调会卡死 UI），
            // 成败不再走同步返回值 —— 必须把这两个事件接到状态栏，否则失败会被静默吞掉，
            // 用户以为在广播、接收端什么都没有。
            NdiOutputService.Instance.Error += msg =>
                Dispatcher.Invoke(() => { try { SetStatus("⚠ NDI 输出：" + msg, hold: true); } catch { } });
            NdiOutputService.Instance.Notify += msg =>
                Dispatcher.Invoke(() => { try { SetStatus(msg); } catch { } });

            // 模块E B-3：采集设备热插拔 —— 系统消息要等窗口句柄就绪后才能挂
            Loaded += (_, _) => InstallDeviceChangeHook();
            // 分辨率浮动条贴着「视图」菜单项定位 → 窗口尺寸一变就要重算（右边界要夹）
            SizeChanged += (_, _) =>
            {
                if (ResRestartBar != null && ResRestartBar.Visibility == Visibility.Visible) PositionResBar();
            };
            DeviceWatcher.Changed += info =>
                Dispatcher.Invoke(() => { try { OnCaptureDevicesChanged(info); } catch { } });

            // 播出保护：**抓屏时代的补救手段**（禁止最小化 + 被遮挡报警）。
            // 合成器直供之后，输出根本不看屏幕 —— 实测主窗最小化照样 420 帧零丢，
            // 所以合成器在跑时这套监控必须关掉，否则只会拿「你被别的窗口盖住了」这种
            // 已经不成立的理由去闪任务栏、拦最小化，纯属误报。
            // 只有合成器起不来、退回 gdigrab 抓屏时，它才重新有意义。
            BroadcastGuard.Instance.Attach(GetOutputScreenRect,
                () => _monitor == null &&
                      (StreamService.Instance.IsRunning || NdiOutputService.Instance.IsRunning));
            BroadcastGuard.Instance.Blocked += who => Dispatcher.Invoke(() => { try { OnBroadcastBlocked(who); } catch { } });
            BroadcastGuard.Instance.Recovered += () => Dispatcher.Invoke(() => { try { OnBroadcastRecovered(); } catch { } });
            StateChanged += (_, _) => GuardAgainstMinimize();

            // 全局异常兜底统一在 App.OnStartup 里注册（那里会先落盘 %TEMP%\cutlance-crash.log 再弹框）。
            // ⚠ 这里不要再注册一份：同一个异常会被两个处理器各弹一次框，用户看到的是两个报错窗口。

            // 预建 8 个固定叠加通道（对标 vMix 29 的 8 路叠加），初始为空
            for (int i = 1; i <= 8; i++)
            {
                var ch = new OverlayItem { ChannelIndex = i };
                ch.PropertyChanged += (_, _) => { SyncOverlays(); RefreshOverlayButtons(); };
                Overlays.Add(ch);
            }
            RefreshOverlayButtons();

            // 预览框：多路组合后每一路都有自己的专属播放器（见 AddPreviewSlot），
            // 这个共享的 PreviewPlayer 只作为「兜底」保留（老代码路径与错误提示仍指向它）。
            // 问题4：只有「播出框」往声卡出声 —— 预览框一律静音，
            // 避免同一路素材在预览框和输出框里各出一遍声（也避免误以为输出没声）。
            try { _engine.PreviewPlayer.Mute = true; } catch { }
            PreviewCanvas.SizeChanged += (_, _) => { RenderPreview(); SyncPreviewPopup(); };
            // 安装系统级低级鼠标钩子（WH_MOUSE_LL）：只负责「输出画布内的图层」拖动/缩放。
            // 预览框已改由 PreviewOpPopup（独立顶层窗口 + 标准 WPF 鼠标事件）接管 ——
            // 钩子在部分环境下会失效（表现为"按住左键拖不动"），不再把预览的成败押在它身上。
            InstallMouseHook();
            // 安装系统级低级键盘钩子（WH_KEYBOARD_LL）：外设导播键盘 / 自定义快捷键的「全局生效」。
            // 映射表（HotkeyMap）在这里第一次读盘，之后触发路径上只查内存字典。
            InstallKeyHook();
            // 失活时把两套拖拽状态都清掉（图层拖拽 + 预览框浮层拖拽），
            // 避免"松手在窗外"之后状态粘住、画面继续跟手
            Deactivated += (_, _) =>
            {
                ClearDragState(); _pvDrag = _pvResize = false;
                // ⚠ 失活时必须连「预览浮层」一起收起 —— 它是带 WS_EX_TOPMOST 的独立顶层窗口，
                //    而系统「打开文件…」对话框是普通窗口，只要浮层还开着就会盖在对话框上，
                //    把落在那一块的点击全部吃掉（表现为"对话框里点不中文件 / 点不动按钮"）。
                //    实测：浮层矩形 (347,474)-(1178,939) 正压在对话框文件列表左侧，
                //    该区域内 WindowFromPoint 返回的是浮层窗口而非对话框。详见 SyncPreviewPopup。
                HidePreviewOpPopup();
            };
            // 重新拿回焦点（关掉对话框、切回来）后再把浮层贴回去；
            // 是否真的打开由 SyncPreviewPopup 里的 IsActive 判断决定。
            Activated += (_, _) => SyncPreviewPopup();

            // 两个操作浮层：贴在预览框/输出画布上的独立顶层窗口
            _popupReanchor = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _popupReanchor.Tick += (_, _) => { _popupReanchor.Stop(); ReanchorPopups(); };
            StateChanged += (_, _) => { SyncPreviewPopup(); SyncProgramOverlayPopup(); };
            Loaded += (_, _) => ReanchorPopups();   // 首帧布局完成后才能拿到画布尺寸，届时再贴合浮层
            RenderPreview();
            SyncPreviewPopup();
            SyncProgramOverlayPopup();

            // ══════════════════ A 方案：中间监视器改由合成器出图 ══════════════════
            // 两个 Image 的 Source 就是合成器的成品帧 —— 屏幕上看到的和推出去的是同一份像素，
            // 因此「窗口被盖住 / 最小化 / 界面卡住」都不再影响输出。
            try
            {
                // 成品帧尺寸跟「输出分辨率」一致：竖屏工程就拿 1080×1920 的画布合成，
                // 免得 1920×1080 的画布被拉伸成竖屏。
                // ⚠ 这里的尺寸在建窗那一刻就定了；工程里的分辨率由 App.OnStartup 提前读进来
                //   （PresetOutputSizeFromProject），运行时改只能记 pending 等重启。
                _monitor = new Views.MonitorBridge(
                    StreamService.Instance.OutputWidth, StreamService.Instance.OutputHeight);
                PgmImage.Source = _monitor.PgmBitmap;
                PvwImage.Source = _monitor.PvwBitmap;
                _monitor.Start();
                try
                {
                    Services.ErrorLog.Info($"[监视器] 合成器 {_monitor.Width}×{_monitor.Height}，" +
                                           $"预览 {_monitor.PvwW}×{_monitor.PvwH}");
                }
                catch { }

                // 500ms 同步一轮工程状态（叠加内容、图层增删、预览槽增删）。
                // 为什么用轮询而不是订阅 PropertyChanged：拖图层时每帧都在改几何，
                // 订阅会收到成百上千次通知；轮询反而更稳、更省（同切换台的取舍）。
                _monitorSync = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _monitorSync.Tick += (_, _) => { SyncMonitorState(); FlushHookDiag(); };
                _monitorSync.Start();
                SyncMonitorState();
            }
            catch (Exception ex)
            {
                SetStatus("⚠ 合成器启动失败，已回落抓屏链路：" + ex.Message, hold: true);
                try { ErrorLog.Info("[监视器] 启动失败：" + ex); } catch { }
                _monitor = null;
            }

            OverlayCanvas.SizeChanged += (_, _) => SyncOverlays();

            // 倒计时牌（2026-10-24）：10Hz 刷新只改右栏文字 + 必要时重渲染叠加位图
            // （内容/字号没变时 SetCountdown 是空操作，不会每秒重画 10 次）
            _cdClock.Changed += UpdateCountdownUi;
            UpdateCountdownUi();
            // 画布尺寸变化时重新定位所有图层
            LayersCanvas.SizeChanged += (_, _) =>
            {
                foreach (var l in Layers) RenderLayer(l);
                SyncProgramOverlayPopup();
            };
            // 预览框尺寸变化时同样重摆（取景框的窗口区域是按画布尺寸算的，重摆才会跟着更新）
            PreviewCanvas.SizeChanged += (_, _) => RenderPreview();

            // 模块7：预览框/播出框按「输出分辨率」约束宽高比（1920x1080 横屏 / 1080x1920 竖屏）
            PreviewCell.SizeChanged += (_, _) => FitMonitors();
            ProgramCell.SizeChanged += (_, _) => FitMonitors();
            StreamService.Instance.OutputGeometryChanged += () => Dispatcher.Invoke(FitMonitors);
            FitMonitors();

            // 主窗口移动/缩放时，若 HDMI 输出开启，去抖更新其捕获区域
            LocationChanged += (_, _) => { ScheduleHdmiReposition(); SchedulePopupReanchor(); };
            SizeChanged += (_, _) => { ScheduleHdmiReposition(); SchedulePopupReanchor(); };

            var pv = _engine.PreviewPlayer;
            pv.Opening += (_, _) => Dispatcher.Invoke(() =>
            {
                if (!StatusHeld) StatusText.Text = "预览 正在打开媒体…";
            });
            pv.Playing += (_, _) => Dispatcher.Invoke(() =>
            {
                if (!StatusHeld && _previewSource != null)
                    StatusText.Text = "○ PREVIEW：" + _previewSource.Name;
            });
            pv.EncounteredError += (_, _) => Dispatcher.Invoke(() => ShowMediaError(pv.Media?.Mrl ?? "?"));

            // 状态栏时钟（仿 vMix 右下角时间）
            var clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            clock.Tick += (_, _) => ClockText.Text = DateTime.Now.ToString("HH:mm:ss");
            clock.Start();

            // 模块4：推流/录制状态联动（指示灯 + 计时）
            StreamService.Instance.StatusChanged += () => Dispatcher.Invoke(UpdateStreamStatus);
            // ⚠ 顺序不能反：自动保存必须在 ShutdownAll 之前 —— 后者最后会 TerminateProcess 硬终止进程，
            //    一旦先执行，工程就再也写不进去了（这条路径上没有任何异步补救的机会）。
            this.Closing += (_, _) =>
            {
                // 程序退出时把还在放映的 PPT 一起收掉，别留一堆 PowerPoint 进程在后台
                foreach (var s in _pptSessions.Values.ToList())
                {
                    try { PptShowService.Close(s); } catch { }
                }
                _pptSessions.Clear();
                AutoSaveOnExit();
                ShutdownAll();
            };
            _streamTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _streamTimer.Tick += (_, _) => Dispatcher.Invoke(UpdateStreamStatus);
            _streamTimer.Start();

            // 模块E B-2：采集输入信号状态灯（常显）。2 秒一轮，只读进程状态，成本可忽略。
            _camStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _camStatusTimer.Tick += (_, _) => RefreshCaptureStatus();
            _camStatusTimer.Start();
            RefreshCaptureStatus();

            // 留白区刷黑（老板 2026-10-17：16:9 信号进竖屏画框时留白是浅灰白块，应是黑边）。
            // 看守轮询 700ms 一轮，自动覆盖主窗预览框 / 输出图层 / HDMI 全屏窗。
            VideoBackdrop.Start();

            // 系统监控（CPU / 内存 / 显存）—— 老板 2026-10-17 要求。
            // 2 秒一轮，全是只读的系统调用，成本可忽略；数值与告警都落在状态栏右下角。
            _sysTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _sysTimer.Tick += (_, _) => RefreshSystemMonitor();
            _sysTimer.Start();
            RefreshSystemMonitor();

            FitToDesktop();          // 启动即铺满桌面工作区（不遮任务栏）
            SizeChanged += (_, _) => ApplyCompactLayout();   // 小屏自适应：窄 / 矮时进入紧凑模式
            RefreshRecentMenu();     // 文件 → 最近工程
            InitGeometryPanel();     // 精调面板接线（组合预设那排已于 2026-10-20 改为布局文件）
            InitProgramLayouts();    // P3：输出布局预设下拉（读 %APPDATA%\Cutlance\program-layouts.json）
            UpdateTitle();
            UpdateStreamStatus();
            InitPlayList();          // 模块A：播放列表事件挂接与初始状态
            InitCountdown();         // 模块B：倒计时状态栏显示与到点动作挂接

            StartUiSelfTest();       // 自检钩子（只在设了 CUTLANCE_UITEST 时动作，正常使用零影响）
            StartZOrderTest();       // 图层切换错乱复现（CUTLANCE_ZORDER_TEST=1）
            StartLayoutTest();       // 输出布局预设自检（CUTLANCE_LAYOUT_TEST=1）
            StartFtbTest();          // FTB 淡黑自检（CUTLANCE_FTB_TEST=1）
            StartFadeTest();         // 转场三态 + 每格音量自检（CUTLANCE_FADE_TEST=1）
            StartMixLayoutTest();    // 布局带混音角色档/压缩档自检（CUTLANCE_MIX_TEST=1）
            StartProgAudioTest();    // 画面声音进混音台自检（CUTLANCE_PROG_AUDIO_TEST=1）
            StartMixerShot();        // 混音台界面截图（CUTLANCE_MIXER_SHOT=1，文档配图）
            StartSlotsShot();        // 拍文档配图用（CUTLANCE_SLOTS_SHOT=1）
            StartPickTest();         // 点左侧输入的分流自检（CUTLANCE_PICK_TEST=1）
            StartCountdownTest();    // 倒计时牌自检（CUTLANCE_CD_TEST=1）
            StartFitTest();          // 异形屏填充方式自检（CUTLANCE_FIT_TEST=1）
            StartZoomTest();         // 单画面放大自检（CUTLANCE_ZOOM_TEST=1）
            StartCdHitTest();        // 倒计时真实命中链路（CUTLANCE_CDHIT_TEST=1）
            StartCdDragE2E();        // 倒计时拖动端到端（CUTLANCE_CDDRAG_TEST=1）
            StartCdRealDragTest();   // 真实鼠标事件拖动（CUTLANCE_CDREAL_TEST=1）
            StartPptSelfTest();      // PPT 抓放映窗端到端自检（CUTLANCE_PPT_TEST=1）
            StartProjectRoundTripTest();  // 工程「存→重启→还原」往返自检（CUTLANCE_PROJ_RT=save|load）
            StartOutputConfigShot(); // 「输出设置」窗屏外截图（CUTLANCE_SHOT_OUTCFG=1）
            StartMenuShot();         // 「视图 → 输出分辨率」子菜单屏外截图（CUTLANCE_SHOT_MENU=1）
            StartOutputSelfTest();   // C 方案输出自检（CUTLANCE_OUTTEST=1）
            StartNdiSelfTest();      // NDI 直供闭环自检（CUTLANCE_NDITEST=1）
            StartHdmiSelfTest();     // HDMI 全屏窗渲染色块自检（CUTLANCE_HDMITEST=1）
            StartPinAudit();         // 工作框「最高权限」自检（CUTLANCE_PIN_AUDIT=1）
            StartRefreshSelfTest();  // 右键刷新画面自检（CUTLANCE_REFRESH_TEST=1）
            StartFxResetSelfTest();  // 画面效果「逐项 ↺ 重置」自检（CUTLANCE_FXRESET_TEST=1）
            StartHelpShot();         // 操作手册指定章节截图（CUTLANCE_HELP_SHOT=1）
        }

        // ==================== 逐项重置自检（老板 2026-10-18 第 1 条）====================

        /// <summary>
        /// 自检 `CUTLANCE_FXRESET_TEST=1`：打开「画面效果」窗，由窗口自己把 15 个调节项
        /// 逐个「推离默认 → 点 ↺ → 必须回默认」走一遍（离屏下 UIA 拿不到控件）。
        /// `CUTLANCE_FXRESET_EXIT=n`：n 秒后自己关程序（默认 12）。
        /// </summary>
        // ==================== 操作手册截图自检（CUTLANCE_HELP_SHOT=1）====================
        /// <summary>
        /// 打开操作手册窗，滚动到指定章节后离屏截图，存进「验证截图」目录再退出。
        ///
        /// 为什么必须看图：手册正文是一整段纯文本，排版问题（长行顶出窗口、
        /// 中文宽度算错、滚动条吃掉右边界）编译期一律看不出来。
        /// 为什么用 RenderTargetBitmap 而不是 PrintWindow：后者抓屏幕外 WPF 窗永远纯白。
        /// </summary>
        private void StartHelpShot()
        {
            if (Environment.GetEnvironmentVariable("CUTLANCE_HELP_SHOT") != "1") return;

            Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(800);
                HelpWindow? w = null;
                try
                {
                    w = new HelpWindow("操作手册 · Cutlance", BuildManualText(),
                                       "手册全文都能选中复制（Ctrl+C）· 上下滚动查看全部章节")
                    { Owner = this };
                    w.Show();
                    await Task.Delay(900);   // 等 TextBox / ScrollViewer 排完，否则截到空白

                    string at = Environment.GetEnvironmentVariable("CUTLANCE_HELP_SHOT_AT") ?? "";
                    int ctx = 3;
                    int.TryParse(Environment.GetEnvironmentVariable("CUTLANCE_HELP_SHOT_CTX"), out ctx);
                    string shotName = Environment.GetEnvironmentVariable("CUTLANCE_HELP_SHOT_NAME") ?? "p19_操作手册_美体新章节.png";
                    if (!string.IsNullOrWhiteSpace(at) && w.ScrollToText(at, ctx))
                    {
                        try { ErrorLog.Info("[帮助截图] 已定位到：" + at); } catch { }
                        await Task.Delay(500);
                    }

                    var dpi = VisualTreeHelper.GetDpi(w);
                    var rtb = new RenderTargetBitmap(
                        Math.Max(1, (int)Math.Ceiling(w.ActualWidth * dpi.DpiScaleX)),
                        Math.Max(1, (int)Math.Ceiling(w.ActualHeight * dpi.DpiScaleY)),
                        96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
                    rtb.Render(w);

                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(rtb));
                    var dir = ShotDir();
                    Directory.CreateDirectory(dir);
                    var file = Path.Combine(dir, shotName);
                    using (var fs = File.Create(file)) enc.Save(fs);
                    try { ErrorLog.Info("[帮助截图] 已保存 " + file); } catch { }
                }
                catch (Exception ex)
                {
                    try { ErrorLog.Info("[帮助截图] 失败：" + ex.Message); } catch { }
                }
                finally
                {
                    try { w?.Close(); } catch { }
                    try { Application.Current.Shutdown(); } catch { }
                }
            });
        }

        private void StartFxResetSelfTest()
        {
            if (Environment.GetEnvironmentVariable("CUTLANCE_FXRESET_TEST") != "1") return;

            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                try { new VideoFxWindow { Owner = this }.Show(); }
                catch (Exception ex)
                {
                    try { ErrorLog.Info("[单项重置] 打开窗口失败：" + ex.Message); } catch { }
                    return;
                }

                double exitSec = 12;
                double.TryParse(Environment.GetEnvironmentVariable("CUTLANCE_FXRESET_EXIT"), out exitSec);
                if (exitSec <= 0) exitSec = 12;
                var tx = new DispatcherTimer { Interval = TimeSpan.FromSeconds(exitSec) };
                tx.Tick += (_, _) => { tx.Stop(); Close(); };
                tx.Start();
            };
            t.Start();
        }

        // ==================== 右键刷新画面自检（老板 2026-10-21 第 3 条）====================
        /// <summary>
        /// 自检 `CUTLANCE_REFRESH_TEST=1`：查两头，中间那一下（鼠标右键）不模拟。
        ///   ① 菜单真的挂上了吗 —— 读 PreviewCell / ProgramCell / 全屏输出窗的 ContextMenu 项数；
        ///   ② 处理器真的能跑吗 —— 直接调两个 Click 处理器（跟用户点菜单走同一条路径），看日志回执。
        /// 为什么不用 UIA 真去右键点屏幕：会移动鼠标、可能点到老板别的窗口，
        /// 而且 `--offscreen` 下窗口在屏幕外，坐标是负的，根本点不到。
        /// </summary>
        private void StartRefreshSelfTest()
        {
            if (Environment.GetEnvironmentVariable("CUTLANCE_REFRESH_TEST") != "1") return;

            Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(1200);

                string MenuInfo(System.Windows.Controls.ContextMenu? m)
                    => m == null ? "未挂" : m.Items.Count + " 项[" + string.Join(" / ",
                        m.Items.OfType<System.Windows.Controls.MenuItem>()
                                  .Select(i => i.Header?.ToString() ?? "?")) + "]";

                ErrorLog.Info($"[自检] 右键菜单 预览框：{MenuInfo(PreviewCell.ContextMenu)}");
                ErrorLog.Info($"[自检] 右键菜单 输出框：{MenuInfo(ProgramCell.ContextMenu)}");

                try
                {
                    // 不显示，只建一个窗口看 XAML 里的右键菜单有没有挂上（Show 会占一块屏）
                    var fs = new FullscreenOutputWindow(null!, PgmImage.Source);
                    ErrorLog.Info($"[自检] 右键菜单 全屏输出窗：{fs.RefreshMenuItemCount} 项");
                    fs.Close();
                }
                catch (Exception ex)
                {
                    ErrorLog.Info("[自检] 全屏输出窗菜单检查失败：" + ex.Message);
                }

                // 走真实处理器
                MenuRefreshPreview_Click(this, new RoutedEventArgs());
                MenuRefreshProgram_Click(this, new RoutedEventArgs());

                if (Environment.GetEnvironmentVariable("CUTLANCE_REFRESH_EXIT") == "1")
                {
                    await Task.Delay(600);
                    try { Application.Current.Shutdown(); } catch { }
                }
            });
        }

        // ==================== 工作框最高权限自检（老板 2026-10-21 第 1 条）====================

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
        private const uint WDA_MONITOR = 0x01;

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out uint affinity);

        /// <summary>
        /// 把三个**曾经漏装**工作框规则的窗口按真实构造函数各开一个，然后查四件事：
        ///   ① WPF 的 Topmost 属性   ② Win32 扩展样式里的 WS_EX_TOPMOST
        ///   ③ 不透明度是否 1        ④ 抓屏排除（GetWindowDisplayAffinity）
        ///
        /// ① 和 ② 必须分开查：WPF 的 `Topmost` 只是「意图」，真正让它压在别的窗口上面的是
        /// ExStyle 那一位；重设 Owner、改 WindowState 之后两者会不一致（以前踩过）。
        /// ④ 尤其重要 —— 播出框是全屏置顶的，工作框盖在它上面时若不排除抓屏，
        /// 那半截界面会被一起推流/录进节目里。
        ///
        /// 只在 `CUTLANCE_PIN_AUDIT=1` 时动作，正常使用零影响；
        /// 加 `CUTLANCE_PIN_AUDIT_EXIT=1` 连主窗一起退出（脚本用）。
        /// </summary>
        private void StartPinAudit()
        {
            if (Environment.GetEnvironmentVariable("CUTLANCE_PIN_AUDIT") != "1") return;

            Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(900);   // 等主窗布局稳定，窗口才拿得到句柄

                var wins = new List<(string Name, Window W)>();
                void Open(string name, Func<Window> make)
                {
                    try
                    {
                        var w = make();
                        w.Show();
                        wins.Add((name, w));
                    }
                    catch (Exception ex)
                    {
                        ErrorLog.Info($"[自检] 开窗失败 {name}：{ex.Message}");
                    }
                }

                Open("PPT导入方式", () => new PptImportWindow { Owner = this });
                Open("字幕条样式", () => new SubtitleStyleWindow(_subtitle?.Style ?? _subtitleStyle) { Owner = this });
                try
                {
                    OpenSynthWindow();
                    if (_synthWindow != null) wins.Add(("切换台", _synthWindow));
                }
                catch (Exception ex) { ErrorLog.Info("[自检] 切换台开窗失败：" + ex.Message); }

                await Task.Delay(1400);   // 等 SourceInitialized → 抓屏排除设置完成

                int pass = 0, fail = 0;
                foreach (var (name, w) in wins)
                {
                    try
                    {
                        var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                        var ex2 = hwnd == IntPtr.Zero ? IntPtr.Zero : GetWindowLongPtr(hwnd, GWL_EXSTYLE);
                        bool exTop = ((long)ex2 & WS_EX_TOPMOST) != 0;
                        uint aff = 0;
                        bool hasAff = hwnd != IntPtr.Zero && GetWindowDisplayAffinity(hwnd, out aff);
                        bool excluded = hasAff && (aff == WDA_EXCLUDEFROMCAPTURE || aff == WDA_MONITOR);

                        bool ok = w.Topmost && exTop && Math.Abs(w.Opacity - 1) < 0.001 && excluded;
                        if (ok) pass++; else fail++;
                        ErrorLog.Info($"[自检] 工作框权限 {name}：Topmost={w.Topmost} ExStyle置顶={exTop} "
                                    + $"不透明={w.Opacity:0.##} 抓屏排除={excluded}(aff=0x{aff:X}) "
                                    + $"→ {(ok ? "PASS" : "FAIL")}");
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        ErrorLog.Info($"[自检] 工作框权限 {name} 查询失败：{ex.Message}");
                    }
                }

                ErrorLog.Info($"[自检] 工作框权限汇总：{pass} 通过 / {fail} 失败");

                foreach (var (_, w) in wins)
                {
                    try { w.Close(); } catch { }
                }

                if (Environment.GetEnvironmentVariable("CUTLANCE_PIN_AUDIT_EXIT") == "1")
                {
                    await Task.Delay(400);
                    try { Application.Current.Shutdown(); } catch { }
                }
            });
        }

        /// <summary>
        /// 「视图 → 输出分辨率」子菜单屏外截图钩子（CUTLANCE_SHOT_MENU=1）。
        ///
        /// 为什么不用 PrintWindow：菜单是独立 Popup 窗口，PrintWindow 只拿到白底
        /// （WPF Popup 的内容并不画在那个 hwnd 上，实测 4 个窗口全白）。
        /// 改成取 MenuItem 模板里的 PART_Popup，对它的 Child 做 RenderTargetBitmap —— 
        /// 这是纯 WPF 离屏渲染，窗口在屏幕外也照样有像素。
        /// 展开前先选一个和当前不同的尺寸，好让「保存文件立即重启」那一项显形
        /// （没有待生效项时它是收起的）。
        /// </summary>
        private void StartMenuShot()
        {
            var mode = Environment.GetEnvironmentVariable("CUTLANCE_SHOT_MENU");
            if (string.IsNullOrWhiteSpace(mode)) return;

            void Log(string m) { try { ErrorLog.Info("[菜单截图] " + m); } catch { } }
            var dir = ShotDir();
            try { Directory.CreateDirectory(dir); } catch { }

            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            t.Tick += (_, _) =>
            {
                t.Stop();

                // mode=setup：只展开「设置」菜单，截给老板确认输出设置那行文字里已经没有「分辨率」
                if (mode.Equals("setup", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        MiSetupMenu.IsSubmenuOpen = true;
                        Log("设置菜单已展开");
                    }
                    catch (Exception ex) { Log("展开设置菜单失败：" + ex.Message); }

                    var ts = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                    ts.Tick += (_, _) =>
                    {
                        ts.Stop();
                        SnapPopup(MiSetupMenu, Path.Combine(dir, "p7_menu_setup.png"), Log);
                        var te = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                        te.Tick += (_, _) => { te.Stop(); Log("结束，退出"); Close(); };
                        te.Start();
                    };
                    ts.Start();
                    return;
                }

                // mode=help：只展开「帮助」菜单，确认「打赏支持作者（敬请期待）」那一项在不在
                if (mode.Equals("help", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        MiHelpMenu.IsSubmenuOpen = true;
                        Log("帮助菜单已展开");
                    }
                    catch (Exception ex) { Log("展开帮助菜单失败：" + ex.Message); }

                    var th = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                    th.Tick += (_, _) =>
                    {
                        th.Stop();
                        SnapPopup(MiHelpMenu, Path.Combine(dir, "p9_menu_help.png"), Log);
                        var te2 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                        te2.Tick += (_, _) => { te2.Stop(); Log("结束，退出"); Close(); };
                        te2.Start();
                    };
                    th.Start();
                    return;
                }

                try
                {
                    SetOutputResolution(1080, 1920);   // 造出「待生效」状态
                    SyncMenuState();
                    MiViewMenu.IsSubmenuOpen = true;
                    MiResMenu.IsSubmenuOpen = true;
                    Log($"菜单已展开：重启项={MiResRestart?.Visibility}「{MiResRestart?.Header}」");
                }
                catch (Exception ex) { Log("展开失败：" + ex.Message); }

                // 布局要跑完才有 ActualWidth，隔一会儿再截
                var t1 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                t1.Tick += (_, _) =>
                {
                    t1.Stop();
                    SnapPopup(MiViewMenu, Path.Combine(dir, "p7_menu_view.png"), Log);
                    SnapPopup(MiResMenu, Path.Combine(dir, "p7_menu_res.png"), Log);
                };
                t1.Start();

                var t2 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(28) };
                t2.Tick += (_, _) => { t2.Stop(); Log("结束，退出"); Close(); };
                t2.Start();
            };
            t.Start();
        }

        /// <summary>把 MenuItem 的弹出层离屏渲染成 PNG（Popup.Child 才是真正画菜单内容的那块视觉）。</summary>
        private void SnapPopup(MenuItem mi, string path, Action<string> log)
        {
            try
            {
                mi.ApplyTemplate();
                if (mi.Template.FindName("PART_Popup", mi) is not System.Windows.Controls.Primitives.Popup pop)
                {
                    log($"{mi.Header}：找不到 PART_Popup");
                    return;
                }
                if (pop.Child is not FrameworkElement fe)
                {
                    log($"{mi.Header}：Popup.Child 为空");
                    return;
                }

                int w = (int)Math.Ceiling(fe.ActualWidth);
                int h = (int)Math.Ceiling(fe.ActualHeight);
                if (w <= 0 || h <= 0) { log($"{mi.Header}：尺寸无效 {w}x{h}"); return; }

                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(fe);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using var fs = File.Create(path);
                enc.Save(fs);
                log($"{Path.GetFileName(path)} 已保存 {w}x{h}");
            }
            catch (Exception ex) { log($"{mi.Header}：截图失败 {ex.Message}"); }
        }

        /// <summary>
        /// 「输出设置」窗屏外截图钩子（CUTLANCE_SHOT_OUTCFG=1）。
        ///
        /// 为什么需要：`--offscreen` 时主窗在屏幕外，UIA 树里一个控件都取不到，
        /// 没法用自动化确认排版。这个钩子把窗口也开到屏幕外、只写一句 hwnd 进日志，
        /// 外部脚本拿 hwnd 做 PrintWindow 截图 —— 全程不碰老板的屏幕。
        /// </summary>
        private void StartOutputConfigShot()
        {
            if (Environment.GetEnvironmentVariable("CUTLANCE_SHOT_OUTCFG") != "1") return;

            void Log(string m) { try { ErrorLog.Info("[截图自检] " + m); } catch { } }

            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                try
                {
                    var w = new OutputWindow { Owner = this, ShowActivated = false };
                    w.WindowStartupLocation = WindowStartupLocation.Manual;
                    w.Left = -12000; w.Top = -12000;
                    w.Show();
                    w.Left = -12000; w.Top = -12000;
                    var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                    Log($"输出设置窗 hwnd={hwnd} size={w.ActualWidth:0}x{w.ActualHeight:0}");
                    // 版式探针要等布局跑完再读（Show() 之后立刻读 ActualHeight 是 0）
                    var tProbe = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
                    tProbe.Tick += (_, _) =>
                    {
                        tProbe.Stop();
                        try { Log("[版式] " + w.DescribeLayout()); } catch { }
                    };
                    tProbe.Start();

                    // 可选：自动选一个分辨率（CUTLANCE_SHOT_RESV=1 → 竖屏；=H → 横屏）。
                    // 分辨率入口已从输出设置窗口搬到「视图 → 输出分辨率」子菜单，
                    // 这里直接走同一套逻辑（SetOutputResolution + SyncMenuState），
                    // 顺便把菜单的勾选 / 当前画面 / 重启项状态打进日志。
                    var pick = Environment.GetEnvironmentVariable("CUTLANCE_SHOT_RESV");
                    bool wantV = pick == "1";
                    bool wantH = string.Equals(pick, "H", StringComparison.OrdinalIgnoreCase);
                    if (wantV || wantH)
                    {
                        int rw = wantV ? 1080 : 1920;
                        int rh = wantV ? 1920 : 1080;
                        string rname = wantV ? "竖屏" : "横屏";
                        var t3 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                        t3.Tick += (_, _) =>
                        {
                            t3.Stop();
                            try
                            {
                                bool ok = SetOutputResolution(rw, rh);
                                SyncMenuState();
                                bool hasP = GetPendingRes(out var pw, out var ph);
                                // 浮动条位置：要能机器判定「贴着视图菜单、不是贴着屏幕底」
                                // （老板 2026-10-20 23:12 要求挪到菜单附近）。
                                double vx = -1, vw = 0, barL = -1, barT = -1;
                                try
                                {
                                    vx = MiViewMenu.TranslatePoint(new Point(0, 0), RootGrid).X;
                                    vw = MiViewMenu.ActualWidth;
                                    barL = ResRestartBar.Margin.Left;
                                    barT = ResRestartBar.Margin.Top;
                                }
                                catch { }
                                Log($"已选{rname}（菜单）→ ok={ok}，待生效={hasP} {pw}x{ph}，" +
                                    $"重启项={MiResRestart?.Visibility} 标题={MiResRestart?.Header}，" +
                                    $"当前画面行={MiResNow?.Header}，" +
                                    $"勾选: 横={MiResH?.IsChecked} 竖={MiResV?.IsChecked}，" +
                                    $"浮动条={ResRestartBar?.Visibility} 文案={ResRestartText?.Text}，" +
                                    $"视图菜单X={vx:0}(宽{vw:0}) 浮动条左={barL:0} 上={barT:0}");

                                // 浮动条动作（CUTLANCE_SHOT_RESBAR=click|later）：
                                // click = 点「保存文件立即重启」；later = 点「稍后」。
                                var barAct = Environment.GetEnvironmentVariable("CUTLANCE_SHOT_RESBAR");
                                if (!string.IsNullOrWhiteSpace(barAct))
                                {
                                    var t5 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                                    t5.Tick += (_, _) =>
                                    {
                                        t5.Stop();
                                        try
                                        {
                                            if (barAct == "click")
                                            {
                                                Log("点浮动条：保存文件立即重启");
                                                ResRestartBar_Click(BtnResRestart, new RoutedEventArgs());
                                            }
                                            else if (barAct == "later")
                                            {
                                                ResRestartLater_Click(BtnResLater, new RoutedEventArgs());
                                                bool stillP = GetPendingRes(out var qw, out var qh);
                                                Log($"点浮动条：稍后 → 浮动条={ResRestartBar?.Visibility}，" +
                                                    $"待生效={stillP} {qw}x{qh}，菜单重启项={MiResRestart?.Visibility}");
                                            }
                                        }
                                        catch (Exception ex) { Log("浮动条动作失败：" + ex.Message); }
                                    };
                                    t5.Start();
                                }

                                // 再点「保存文件立即重启」（CUTLANCE_SHOT_RESTART=1）：
                                // 验证整条路径 —— 存盘 → 新实例读工程 → 合成器真按所选尺寸建
                                if (Environment.GetEnvironmentVariable("CUTLANCE_SHOT_RESTART") == "1")
                                {
                                    var t4 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                                    t4.Tick += (_, _) =>
                                    {
                                        t4.Stop();
                                        try
                                        {
                                            Log("点菜单项：" + MiResRestart?.Header);
                                            MenuResRestart_Click(MiResRestart, new RoutedEventArgs());
                                        }
                                        catch (Exception ex) { Log("点重启失败：" + ex.Message); }
                                    };
                                    t4.Start();
                                }
                            }
                            catch (Exception ex) { Log("选分辨率失败：" + ex.Message); }
                        };
                        t3.Start();
                    }
                }
                catch (Exception ex) { Log("打开失败：" + ex.Message); }

                var t2 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(40) };
                t2.Tick += (_, _) => { t2.Stop(); Log("截图自检结束，退出"); Close(); };
                t2.Start();
            };
            t.Start();
        }

        /// <summary>
        /// HDMI 全屏窗自检（CUTLANCE_HDMITEST=1）：不开真双屏，直接把全屏窗也摆到屏幕外，
        /// 由外部脚本 PrintWindow 截图判断「Image 有没有真的显示合成器画面」。
        ///
        /// 为什么不能走正常的 StartHdmiOutput：它要求至少两块屏，本机只有一块；
        /// 而为了让窗口铺满真实显示器，正常路径会把整个桌面盖住 —— 绝不能在老板用电脑时这么干。
        /// </summary>
        private void StartHdmiSelfTest()
        {
            if (Environment.GetEnvironmentVariable("CUTLANCE_HDMITEST") != "1") return;

            void Log(string m) { try { ErrorLog.Info("[HDMI自检] " + m); } catch { } }

            var t1 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            t1.Tick += (_, _) =>
            {
                t1.Stop();
                // 自检素材：CUTLANCE_HDMI_DIR\src 里的纯色图（外部脚本会提前放好）。
                if (Inputs.Count == 0)
                {
                    string hdir = Environment.GetEnvironmentVariable("CUTLANCE_HDMI_DIR") ?? "";
                    if (string.IsNullOrWhiteSpace(hdir)) hdir = Path.Combine(Path.GetTempPath(), "cutlance-hdmi");
                    string hsrc = Path.Combine(hdir, "src");
                    if (Directory.Exists(hsrc))
                    {
                        foreach (var png in Directory.GetFiles(hsrc, "*.png")
                                                    .OrderBy(x => x, StringComparer.Ordinal).Take(1))
                            AddInput(new InputSource
                            {
                                Type = SourceType.File,
                                Name = Path.GetFileNameWithoutExtension(png),
                                Location = png
                            });
                    }
                }
                if (Inputs.Count == 0) { Log("没有输入"); Close(); return; }
                SetPreview(Inputs[0]);
                TakeToLayer(TransitionType.Cut);
                Log("已硬切（图层 " + Layers.Count + "）");

                var t2 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                t2.Tick += async (_, _) =>
                {
                    t2.Stop();
                    try
                    {
                        var mon = DisplayService.GetMonitors().FirstOrDefault();
                        if (mon == null) { Log("没有显示器信息"); Close(); return; }

                        var w = new Views.FullscreenOutputHost(mon, _monitor,
                                                               _monitor?.OutW ?? 1920, _monitor?.OutH ?? 1080,
                                                               offscreen: true);
                        _hdmiWindow = w;
                        Log($"全屏窗已打开（独立线程 + 屏外）：Handle=0x{w.Handle.ToInt64():X} 开启={w.IsOpen}" +
                            $" 右键菜单={w.RefreshMenuItemCount} 项");
                        // 等几帧再报"贴上去多少帧"（证明合成器真的把成品帧推过去了）
                        await Task.Delay(1200);
                        Log($"全屏窗已贴帧 {w.FramesDrawn} 帧");
                    }
                    catch (Exception ex) { Log("开窗失败：" + ex.Message); }

                    var t3 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(18) };
                    t3.Tick += (_, _) =>
                    {
                        t3.Stop();
                        Log("自检结束，关窗退出");
                        try { _hdmiWindow?.Close(); } catch { }
                        _hdmiWindow = null;
                        Close();
                    };
                    t3.Start();
                };
                t2.Start();
            };
            t1.Start();
        }

        /// <summary>
        /// NDI 直供闭环自检（CUTLANCE_NDITEST=1）。
        ///
        /// 只验「发出去」是不够的 —— 发送器建起来了、帧也交了，但接收端可能什么都看不到。
        /// 所以这里做**自环**：自己发 → 自己发现 → 自己收 → 报出收到的画面尺寸。
        /// 三件事都成立才算 NDI 链路真的通：
        ///   ① 发送器在跑且帧数在涨；② 发现器能列到自己的源；③ 接收机能连上并拿到 W/H。
        /// </summary>
        private void StartNdiSelfTest()
        {
            if (Environment.GetEnvironmentVariable("CUTLANCE_NDITEST") != "1") return;

            void Log(string m) { try { ErrorLog.Info("[NDI自检] " + m); } catch { } }
            var svc = StreamService.Instance;

            var t1 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            t1.Tick += (_, _) =>
            {
                t1.Stop();
                if (Inputs.Count == 0) { Log("没有输入"); Close(); return; }
                SetPreview(Inputs[0]);
                TakeToLayer(TransitionType.Cut);
                Log("已硬切（图层 " + Layers.Count + "）");

                var t2 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                t2.Tick += (_, _) =>
                {
                    t2.Stop();
                    // 只开 NDI：不推流不录制
                    svc.EnableStream = false;
                    svc.EnableRecord = false;
                    svc.EnableNdi = true;
                    svc.NdiSourceName = "CutlanceSelfTest";
                    svc.NdiFps = 30;

                    // ⚠ 必须把合成器挂上：不挂就退回 gdigrab，而自检里没传屏幕区域 → 直接报「区域无效」
                    svc.ProgramFrames = _monitor;
                    svc.Error += m => Log("⚠ " + m);

                    // NDI 自检也跟真实帧率走（NDI 那档由 NdiFps 覆盖，这里是兜底值）
                    svc.Start(0, 0, 0, 0, _monitor?.Fps ?? svc.Fps);

                    var ndi = NdiOutputService.Instance;
                    var swWait = Stopwatch.StartNew();

                    // ⚠ Start 已异步化（首次 CreateSender 可能 12~60 秒）→ 不能立刻读 IsRunning，
                    //   否则自检会把「还在启动」误判成「启动失败」。250ms 轮询等它真起来；
                    //   90 秒还起不来就按失败记一笔，别让自检"看起来成功"。
                    var tWait = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                    tWait.Tick += (_, _) =>
                    {
                        if (ndi.IsStarting && swWait.Elapsed < TimeSpan.FromSeconds(90)) return;
                        tWait.Stop();
                        Log($"NDI 启动结果：running={ndi.IsRunning} 等待={swWait.ElapsedMilliseconds}ms "
                            + $"源名={ndi.SourceName} 尺寸={ndi.Width}x{ndi.Height} 错误={ndi.LastError}");
                        StartCountdown();
                    };
                    tWait.Start();

                    void StartCountdown()
                    {
                    var t3 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                    t3.Tick += (_, _) =>
                    {
                        t3.Stop();
                        Log("5 秒后：已发 " + ndi.FramesSent + " 帧 / 连接数 " + ndi.Connections);

                        // 自环：发现自己 → 连自己 → 读回尺寸
                        try
                        {
                            var list = NdiService.Instance.Scan(4000);
                            Log("发现 " + list.Count + " 个源：" + string.Join(" | ",
                                list.Select(s => s.Name)));

                            var me = list.FirstOrDefault(s =>
                                s.Name.IndexOf("CutlanceSelfTest", StringComparison.OrdinalIgnoreCase) >= 0);
                            if (me == null) { Log("⚠ 没发现自己的源（发现器可能没走通）"); }
                            else
                            {
                                using var rx = NdiService.Instance.OpenReceiver(me.Name, out var err);
                                if (rx == null) Log("⚠ 接收失败：" + err);
                                else
                                {
                                    Log($"接收成功：{rx.Width}x{rx.Height} @ {rx.Fps}fps");
                                    // 等几秒看接收端有没有真的收到帧
                                    var t4 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                                    t4.Tick += (_, _) =>
                                    {
                                        t4.Stop();
                                        Log("收尾：发送 " + ndi.FramesSent + " 帧 / 接收端连接 "
                                            + ndi.Connections + " / 接收端尺寸 " + rx.Width + "x" + rx.Height);
                                        Finish();
                                    };
                                    t4.Start();
                                    return;
                                }
                            }
                        }
                        catch (Exception ex) { Log("自环异常：" + ex.Message); }

                        Finish();
                    };
                    t3.Start();

                    void Finish()
                    {
                        try { NdiService.Instance.ReleaseFinder(); } catch { }
                        svc.Stop();
                        var t9 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                        t9.Tick += (_, _) => { t9.Stop(); Log("NDI 已停止"); Close(); };
                        t9.Start();
                    }
                    }
                };
                t2.Start();
            };
            t1.Start();
        }

        /// <summary>
        /// C 方案输出自检钩子（CUTLANCE_OUTTEST=1）：把「起合成 → 切节目 → 开始录制 → 到点停」
        /// 这条链路自己走一遍，全程写日志。
        ///
        /// 为什么必须编进程序：要验证的命题是「输出不再依赖屏幕」——
        /// 而 --offscreen 下窗口在屏幕外，外部脚本既点不到按钮、也抓不到画面，
        /// 只能靠程序自己跑一遍，再用 ffprobe 看落盘文件。
        ///
        /// 环境变量：
        ///   CUTLANCE_OUTTEST=1     启用
        ///   CUTLANCE_REC_DIR=...   录制目录（默认 %TEMP%\cutlance_outtest）
        ///   CUTLANCE_SEG_SEC=6     分段段长（秒；0 = 不分段）
        ///   CUTLANCE_REC_SEC=24    录多少秒后停（默认 20）
        ///   CUTLANCE_OUT_MIN=1     录制途中把主窗最小化（验证被遮挡/最小化不影响输出）
        /// </summary>
        private void StartOutputSelfTest()
        {
            if (Environment.GetEnvironmentVariable("CUTLANCE_OUTTEST") != "1") return;

            void Log(string m) { try { ErrorLog.Info("[输出自检] " + m); } catch { } }

            var svc = StreamService.Instance;
            double recSec = 20, segSec = 0;
            double.TryParse(Environment.GetEnvironmentVariable("CUTLANCE_REC_SEC"), out recSec);
            double.TryParse(Environment.GetEnvironmentVariable("CUTLANCE_SEG_SEC"), out segSec);
            if (recSec <= 0) recSec = 20;
            bool minimize = Environment.GetEnvironmentVariable("CUTLANCE_OUT_MIN") == "1";

            var dir = Environment.GetEnvironmentVariable("CUTLANCE_REC_DIR");
            if (string.IsNullOrWhiteSpace(dir))
                dir = Path.Combine(Path.GetTempPath(), "cutlance_outtest");
            try
            {
                Directory.CreateDirectory(dir);
                foreach (var f in Directory.GetFiles(dir, "record_*")) { try { File.Delete(f); } catch { } }
            }
            catch { }

            // ① 8s：第 1 路进预览；② +2s：硬切到节目；③ +2s：起录制
            var t1 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            t1.Tick += (_, _) =>
            {
                t1.Stop();
                if (Inputs.Count == 0) { Log("没有输入，无法自检"); Close(); return; }
                SetPreview(Inputs[0]);
                Log("预览 ← " + Inputs[0].Name);

                var t2 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                t2.Tick += (_, _) =>
                {
                    t2.Stop();
                    TakeToLayer(TransitionType.Cut);
                    Log("已硬切（图层 " + Layers.Count + "）");

                    var t3 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                    t3.Tick += (_, _) =>
                    {
                        t3.Stop();

                        // 可选：塞一条字幕叠加上去，验证「叠加层确实进输出文件」
                        // （合成器模式下叠加和画面是同一帧合出来的，抽帧应该能看到字）
                        if (Environment.GetEnvironmentVariable("CUTLANCE_OUT_OVL") == "1" && Overlays.Count > 0)
                        {
                            try
                            {
                                Overlays[0].Text = "CUTLANCE 合成输出 " + DateTime.Now.ToString("HH:mm:ss");
                                Overlays[0].HasContent = true;
                                Overlays[0].Visible = true;
                                SyncOverlays();
                                Log("已开启字幕叠加通道 1");
                            }
                            catch (Exception ex) { Log("叠加设置失败：" + ex.Message); }
                        }

                        // 默认只录不推（推不出去会污染判据）；
                        // CUTLANCE_RTMP=1 时才开推流 —— 配本机 `ffmpeg -listen 1` 当 RTMP 服务器，
                        // 这样「推流」这条链路第一次有了可复现的验收手段（2026-10-22 加）。
                        bool wantRtmp = Environment.GetEnvironmentVariable("CUTLANCE_RTMP") == "1";
                        svc.EnableStream = wantRtmp;
                        svc.EnableRecord = true;
                        svc.EnableNdi = false;
                        svc.RecordFolder = dir;
                        svc.RecordSegmentSeconds = segSec;

                        // CUTLANCE_RTMP_URL=rtmp://127.0.0.1:1935/live/test 覆盖推流地址
                        var rtmpUrl = Environment.GetEnvironmentVariable("CUTLANCE_RTMP_URL");
                        if (wantRtmp && !string.IsNullOrWhiteSpace(rtmpUrl)) svc.RtmpUrl = rtmpUrl;

                        // CUTLANCE_ENCODER=software|nvenc|qsv 覆盖编码器（软/硬解 CPU 对比用）
                        var encOv = Environment.GetEnvironmentVariable("CUTLANCE_ENCODER");
                        if (!string.IsNullOrWhiteSpace(encOv)) svc.Encoder = encOv;

                        StartStreaming();

                        Log("开始" + (wantRtmp ? "推流+录制" : "录制") + "：合成器=" + (_monitor != null
                                ? _monitor.Width + "x" + _monitor.Height
                                : "无（抓屏兜底）")
                            + " 编码器=" + svc.Encoder
                            + " 实际编码器=" + FfmpegOutput.Instance.ActiveEncoder
                            + " 码率=" + svc.BitrateKbps + "k 帧率=" + (_monitor?.Fps ?? svc.Fps)
                            + (wantRtmp ? " 推流=" + svc.RtmpUrl : "")
                            + " 段长=" + segSec + "s 目录=" + dir);

                        if (minimize)
                        {
                            var tm = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                            tm.Tick += (_, _) => { tm.Stop(); WindowState = WindowState.Minimized; Log("主窗已最小化"); };
                            tm.Start();
                        }

                        // 每 2 秒记一次喂帧数：客观判据（≈ 帧率 × 秒数）
                        var tk = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                        tk.Tick += (_, _) =>
                        {
                            if (!svc.IsRunning) { tk.Stop(); return; }
                            var fo = FfmpegOutput.Instance;
                            Log("运行中：喂帧 " + fo.FramesFed + " 丢弃 " + fo.FramesDropped
                                + " 合成帧 " + (_monitor?.Frames ?? 0)
                                + " 合成耗时 " + (_monitor?.LastComposeMs ?? 0).ToString("0.00") + "ms");
                        };
                        tk.Start();

                        var ts = new DispatcherTimer { Interval = TimeSpan.FromSeconds(recSec) };
                        ts.Tick += (_, _) =>
                        {
                            ts.Stop(); tk.Stop();
                            svc.Stop();
                            Log("已停止录制（录制 " + recSec + "s）");

                            // ffmpeg 收尾要写容器索引，等 4 秒再统计文件
                            var tq = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                            tq.Tick += (_, _) =>
                            {
                                tq.Stop();
                                var fo = FfmpegOutput.Instance;
                                Log("收尾：喂帧 " + fo.FramesFed + " 丢弃 " + fo.FramesDropped);
                                try
                                {
                                    var files = Directory.GetFiles(dir, "record_*");
                                    Array.Sort(files);
                                    Log("段文件 " + files.Length + " 个：" + string.Join(", ",
                                        files.Select(f => Path.GetFileName(f) + "("
                                            + (new FileInfo(f).Length / 1048576.0).ToString("0.00") + "MB)")));
                                }
                                catch { }
                                Close();
                            };
                            tq.Start();
                        };
                        ts.Start();
                    };
                    t3.Start();
                };
                t2.Start();
            };
            t1.Start();
        }

        /// <summary>
        /// 界面自检钩子。存在的唯一原因：<c>--offscreen</c> 时窗口在屏幕外，
        /// UIA 树里一个控件都拿不到（实测 0 个按钮），没法用外部脚本点「输入」「硬切」。
        /// 所以把动作编进程序，靠环境变量触发，验证脚本只负责截图比对。
        ///
        /// 用法：CUTLANCE_UITEST=1  → 8s 后把第 1 路塞进预览，再 3s 硬切；
        ///       CUTLANCE_UITEST_EXIT=n → n 秒后自己关。
        /// </summary>
        private void StartUiSelfTest()
        {
            if (Environment.GetEnvironmentVariable("CUTLANCE_UITEST") != "1") return;

            void Log(string m) { try { ErrorLog.Info("[自检] " + m); } catch { } }

            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                try
                {
                    if (Inputs.Count == 0) { Log("没有输入，无法自检"); return; }
                    SetPreview(Inputs[0]);
                    Log($"已把第 1 路「{Inputs[0].Name}」送进预览");

                    var t2 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                    t2.Tick += (_, _) =>
                    {
                        t2.Stop();
                        try
                        {
                            TakeToLayer(TransitionType.Cut);
                            Log($"已硬切（图层 {Layers.Count} 个，预览槽 {_previewSlots.Count} 个）");

                            // 再等 3 秒：模拟"拖动图层/缩放"落到工程上的效果。
                            // 拖拽本身走 Win32 鼠标钩子（没改逻辑），这里验的是
                            // 「几何改了 → 合成器下一帧就跟上」这段。
                            var t3 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                            t3.Tick += (_, _) =>
                            {
                                t3.Stop();
                                var top = Layers.Count > 0 ? Layers[Layers.Count - 1] : null;
                                if (top == null) { Log("没有图层可移动"); return; }
                                top.X = 0.34; top.Y = 0.22; top.ScaleX = 0.55; top.ScaleY = 0.55;
                                ClampLayerPos(top);
                                RenderLayer(top);
                                Log($"已把图层 #{top.Id} 移到 ({top.X:0.00},{top.Y:0.00}) 缩放 {top.ScaleX:0.00}");
                            };
                            t3.Start();
                        }
                        catch (Exception ex) { Log("硬切失败：" + ex.Message); }
                    };
                    t2.Start();
                }
                catch (Exception ex) { Log("自检失败：" + ex.Message); }
            };
            t.Start();

            var exitSec = 0.0;
            double.TryParse(Environment.GetEnvironmentVariable("CUTLANCE_UITEST_EXIT"), out exitSec);
            if (exitSec > 0)
            {
                var te = new DispatcherTimer { Interval = TimeSpan.FromSeconds(exitSec) };
                te.Tick += (_, _) => { te.Stop(); Log("自检到点，关闭"); Close(); };
                te.Start();
            }
        }

        // ============ 图层切换错乱复现（CUTLANCE_ZORDER_TEST=1）============
        //
        // 老板 2026-10-22 报：「在全部上输出后，再进行切换其他画面，输出框画面都会出错」。
        // 调研过 vMix / OBS：两家都是「一组画面 = 一个整体单元，切换 = 整组替换」
        // （vMix 的 Layer 是某个 Input 内部的事；OBS 的 Scene 是一份源的有序列表）。
        // 本程序输出框是一个**只增不减的全局图层栈**：「全部上输出」push N 层，
        // CUT 只隐藏其它**满框**图层（见 TakeToLayer）→ 分屏小窗留在画面上。
        //
        // 这个钩子只做一件事：把「错」的形态抓成图，不做任何修复、不改业务逻辑。
        // 素材用纯色带编号的 PNG（脚本生成），抓帧里一眼看出哪一路在什么位置。
        //   环境变量：CUTLANCE_ZORDER_TEST=1 开启
        //             CUTLANCE_ZORDER_DIR=<目录>  内含 src\*.png 素材，抓帧写到 shot\
        //             CUTLANCE_ZORDER_EXIT=1      跑完自己关（测试脚本必设，别留进程）
        private async void StartZOrderTest()
        {
            if (Environment.GetEnvironmentVariable("CUTLANCE_ZORDER_TEST") != "1") return;

            string dir = Environment.GetEnvironmentVariable("CUTLANCE_ZORDER_DIR") ?? "";
            if (string.IsNullOrWhiteSpace(dir)) dir = Path.Combine(Path.GetTempPath(), "cutlance-zorder");
            try { Directory.CreateDirectory(dir); Directory.CreateDirectory(Path.Combine(dir, "shot")); } catch { }

            var log = new List<string>();
            void Log(string m)
            {
                log.Add($"{DateTime.Now:HH:mm:ss.fff}\t{m}");
                try { ErrorLog.Info("[Z序复现] " + m); } catch { }
            }
            void Flush()
            {
                try
                {
                    System.IO.File.WriteAllLines(Path.Combine(dir, "zorder.log"), log,
                        new System.Text.UTF8Encoding(false));
                }
                catch { }
            }

            // 输出图层栈快照 —— 判断「错」到底错在图层集合，还是错在合成/显示
            string LayerSnap()
                => string.Join(" | ", Layers.Select((l, i) =>
                       $"#{i + 1}(id{l.Id},{l.DisplayName},vis={(l.Visible ? 1 : 0)},"
                       + $"S={l.ScaleX:0.00}x{l.ScaleY:0.00},Z={l.Z})"));

            // 从合成器 front buffer 抓一帧（与推流/录制同一份像素），存 PNG
            bool Grab(string tag)
            {
                try
                {
                    if (_monitor == null) { Log($"  ⚠ {tag}：没有合成器，抓不到成品帧"); return false; }
                    int w = _monitor.OutW, h = _monitor.OutH;
                    var buf = new byte[w * h * 4];
                    bool ok = false;
                    _monitor.WithProgram((p, stride) =>
                    {
                        for (int y = 0; y < h; y++)
                            System.Runtime.InteropServices.Marshal.Copy(
                                new IntPtr(p.ToInt64() + (long)y * stride), buf, y * w * 4, w * 4);
                        ok = true;
                    });
                    if (!ok) { Log($"  ⚠ {tag}：front buffer 没读到"); return false; }
                    for (int i = 3; i < buf.Length; i += 4) buf[i] = 255;   // Bgra32 alpha 补满（否则 PNG 全透明）
                    var bs = System.Windows.Media.Imaging.BitmapSource.Create(
                        w, h, 96, 96, PixelFormats.Bgra32, null, buf, w * 4);
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bs));
                    string outPath = Path.Combine(dir, "shot", tag + ".png");
                    using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write)) enc.Save(fs);
                    Log($"  抓帧 {tag}.png（{w}×{h}，合成器累计 {_monitor.Frames} 帧）");
                    return true;
                }
                catch (Exception ex) { Log($"  ⚠ 抓帧失败（{tag}）：{ex.Message}"); return false; }
            }

            // 抓「主界面本身」（含输出框、预览框、右侧图层列表）——
            // 合成帧干净但操作员屏幕上看花了，问题就在 WPF 这一层，这张图能分开两者。
            bool GrabUi(string tag)
            {
                try
                {
                    UpdateLayout();
                    int w = (int)Math.Round(ActualWidth), h = (int)Math.Round(ActualHeight);
                    if (w <= 0 || h <= 0) { Log($"  ⚠ 主界面尺寸为 0（{tag}）"); return false; }
                    var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(this);
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                    string outPath = Path.Combine(dir, "shot", tag + ".png");
                    using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write)) enc.Save(fs);
                    Log($"  抓界面 {tag}.png（{w}×{h}）");
                    return true;
                }
                catch (Exception ex) { Log($"  ⚠ 抓界面失败（{tag}）：{ex.Message}"); return false; }
            }

            try
            {
                await Task.Delay(1200);   // 等首帧布局：预览画布要有真实尺寸，否则几何算不出来
                // ① 6 路纯色图片输入（名字 R1..R6，与素材文件名一致）
                var srcDir = Path.Combine(dir, "src");
                var pics = Directory.Exists(srcDir)
                    ? Directory.GetFiles(srcDir, "*.png").OrderBy(x => x, StringComparer.Ordinal).ToList()
                    : new List<string>();
                Log($"素材 {pics.Count} 张：{string.Join(", ", pics.Select(Path.GetFileName))}");
                if (pics.Count < 6) { Log("⚠ 素材不足 6 张，复现中止"); Flush(); return; }

                foreach (var p in pics.Take(6))
                    AddInput(new InputSource
                    {
                        Type = SourceType.File,
                        Name = Path.GetFileNameWithoutExtension(p),
                        Location = p
                    });
                Log($"输入数 = {Inputs.Count}");

                // ② 4 路多选进预览（自动 2×2）
                LoadInputsIntoPreview(Inputs.Take(4).ToList());
                await Task.Delay(2000);
                Log($"[A] 4 路进预览：slots={_previewSlots.Count}，几何="
                    + string.Join(",", _previewSlots.Select(s => $"{s.X:0.00}/{s.Y:0.00}@{s.ScaleX:0.00}")));
                Grab("A_4路进预览");

                // ③ 全部上输出
                BtnPreviewToProgram_Click(this, new RoutedEventArgs());
                await Task.Delay(1600);
                Log($"[B] 全部上输出：Layers={Layers.Count} :: {LayerSnap()}");
                Log($"[B] 状态栏：{StatusText.Text}");
                Grab("B_全部上输出");
                GrabUi("B_界面_全部上输出");

                // ④ 切第 5 路（CUT）—— 老板报的「再切换其他画面」
                SetPreview(Inputs[4]);
                await Task.Delay(900);
                TakeToLayer(TransitionType.Cut);
                await Task.Delay(1600);
                Log($"[C] 切第5路：Layers={Layers.Count} :: {LayerSnap()}");
                Log($"[C] 状态栏：{StatusText.Text}");
                Grab("C_切第5路");
                GrabUi("C_界面_切第5路");

                // ⑤ 再切第 6 路（CUT）—— 看是不是越切越乱
                SetPreview(Inputs[5]);
                await Task.Delay(900);
                TakeToLayer(TransitionType.Cut);
                await Task.Delay(1600);
                Log($"[D] 切第6路：Layers={Layers.Count} :: {LayerSnap()}");
                Log($"[D] 状态栏：{StatusText.Text}");
                Grab("D_切第6路");
                GrabUi("D_界面_切第6路");

                // ⑥ 换一组画面再「全部上输出」—— 老板最可能的操作：4 宫格看完了，
                //    想换成 2 路，于是重新多选进预览、再点一次「全部上输出」。
                //    旧 4 格（Scale=1，非满框）不会被 CUT 那条规则清掉，于是和新格子共存：
                //    新格子盖住的地方看不见，没盖住的地方旧画面就露出来。
                LoadInputsIntoPreview(new List<InputSource> { Inputs[4], Inputs[5] });
                await Task.Delay(1200);
                BtnPreviewToProgram_Click(this, new RoutedEventArgs());
                await Task.Delay(1600);
                Log($"[G] 换一组 2 路再上输出：Layers={Layers.Count} :: {LayerSnap()}");
                Log($"[G] 状态栏：{StatusText.Text}");
                Grab("G_换一组2路上输出");
                GrabUi("G_界面_换一组2路上输出");

                // ⑦ 对照组：清空输出框后推**同样这两路** —— 证明布局本身是均衡的，
                //    失衡完全来自上一组图层没下线。
                BtnClearLayers_Click(this, new RoutedEventArgs());
                await Task.Delay(900);
                BtnPreviewToProgram_Click(this, new RoutedEventArgs());
                await Task.Delay(1600);
                Log($"[H] 对照组·清空后 2 路上输出：Layers={Layers.Count} :: {LayerSnap()}");
                Grab("H_对照组_清空后2路");
                GrabUi("H_界面_对照组_清空后2路");

                // ⑧ 单格切：H 是干净的两路（左 R5 右 R6）。
                //    选中第 1 格 → 点左侧第 1 路（走真实 Tile_Click，含双击去抖逻辑）。
                //    预期：只有第 1 格变 R1，第 2 格还是 R6。
                if (Layers.Count >= 2)
                {
                    SelectLayer(Layers[0]);
                    await Task.Delay(400);
                    var fakeTile = new Button { DataContext = Inputs[0] };
                    Tile_Click(fakeTile, new RoutedEventArgs());      // 第一下
                    Tile_Click(fakeTile, new RoutedEventArgs());      // 第二下（模拟双击，应被去抖吃掉）
                    await Task.Delay(1400);
                    Log($"[I] 单格切·第1格换 R1：Layers={Layers.Count} :: {LayerSnap()}");
                    Log($"[I] 状态栏：{StatusText.Text}");
                    Grab("I_单格切_第1格换R1");
                    GrabUi("I_界面_单格切第1格");
                }

                // ⑨ 层数收敛：连续整组切 5 轮，Layers.Count 不能再涨（老实现每轮 +4，迟早顶满上限）
                int beforeCount = Layers.Count;
                for (int k = 0; k < 5; k++)
                {
                    LoadInputsIntoPreview(Inputs.Take(4).ToList());
                    await Task.Delay(350);
                    BtnPreviewToProgram_Click(this, new RoutedEventArgs());
                    await Task.Delay(350);
                }
                Log($"[J] 连切 5 轮：Layers={Layers.Count}（起始 {beforeCount}，上限 {_engine.MaxLayers}）");
                Grab("J_连切5轮");
                GrabUi("J_界面_连切5轮");

                // ⑦ 对照组：清空全部图层后，单路 Cut 连切两路（预期画面干净）
                //    这一组用来证明「错」来自图层只增不减，而不是切画面这个动作本身。
                BtnClearLayers_Click(this, new RoutedEventArgs());
                await Task.Delay(900);
                SetPreview(Inputs[0]);
                await Task.Delay(700);
                TakeToLayer(TransitionType.Cut);
                await Task.Delay(1400);
                Log($"[E] 对照组·单路切换1：Layers={Layers.Count} :: {LayerSnap()}");
                Grab("E_对照组_单路切换1");

                SetPreview(Inputs[1]);
                await Task.Delay(700);
                TakeToLayer(TransitionType.Cut);
                await Task.Delay(1400);
                Log($"[F] 对照组·单路切换2：Layers={Layers.Count} :: {LayerSnap()}");
                Grab("F_对照组_单路切换2");

                Log("复现流程结束");
            }
            catch (Exception ex)
            {
                Log("EXCEPTION\t" + ex);
            }
            finally
            {
                Flush();
                if (Environment.GetEnvironmentVariable("CUTLANCE_ZORDER_EXIT") == "1")
                {
                    try { await Task.Delay(400); } catch { }
                    try { Close(); } catch { }
                }
            }
        }

        // ============ 输出布局预设自检（CUTLANCE_LAYOUT_TEST=1）============
        //
        // P3 的验收：存两套布局 → 来回切 → 每一帧都只能是「这一套」，不能有上一套的残留。
        //   L_A 切四宫格   ：R1..R4 四色各 ≈25%
        //   L_B 切双人     ：只有 R5/R6，上下黑边 ≈51%（旧四宫格不许露出来）
        //   L_C 切回四宫格 ：四色又回来（来回切不衰减、不叠加）
        //   L_D 撤回       ：回到双人（撤回的是「切四宫格之前」那一套）
        //   L_E 只选不切   ：与 L_D 逐像素一致（证明「选中不生效」，滚轮误改选中不会动画面）
        // 环境变量：CUTLANCE_LAYOUT_TEST=1 / CUTLANCE_LAYOUT_DIR / CUTLANCE_LAYOUT_EXIT=1
        private async void StartLayoutTest()
        {
            if (Environment.GetEnvironmentVariable("CUTLANCE_LAYOUT_TEST") != "1") return;

            string dir = Environment.GetEnvironmentVariable("CUTLANCE_LAYOUT_DIR") ?? "";
            if (string.IsNullOrWhiteSpace(dir)) dir = Path.Combine(Path.GetTempPath(), "cutlance-layout");
            try { Directory.CreateDirectory(dir); Directory.CreateDirectory(Path.Combine(dir, "shot")); } catch { }

            var log = new List<string>();
            void Log(string m)
            {
                log.Add($"{DateTime.Now:HH:mm:ss.fff}\t{m}");
                try { ErrorLog.Info("[布局自检] " + m); } catch { }
            }
            void Flush()
            {
                try
                {
                    System.IO.File.WriteAllLines(Path.Combine(dir, "layout.log"), log,
                        new System.Text.UTF8Encoding(false));
                }
                catch { }
            }

            string LayerSnap()
                => string.Join(" | ", Layers.Select((l, i) =>
                       $"#{i + 1}({l.DisplayName},S={l.ScaleX:0.00}x{l.ScaleY:0.00},"
                       + $"XY={l.X:0.000}/{l.Y:0.000},Z={l.Z},G={l.GroupId},"
                       + $"brd={l.BorderThickness:0.0})"));

            byte[]? Grab(string tag)
            {
                try
                {
                    if (_monitor == null) { Log($"  ⚠ {tag}：没有合成器"); return null; }
                    int w = _monitor.OutW, h = _monitor.OutH;
                    var buf = new byte[w * h * 4];
                    bool ok = false;
                    _monitor.WithProgram((p, stride) =>
                    {
                        for (int y = 0; y < h; y++)
                            System.Runtime.InteropServices.Marshal.Copy(
                                new IntPtr(p.ToInt64() + (long)y * stride), buf, y * w * 4, w * 4);
                        ok = true;
                    });
                    if (!ok) { Log($"  ⚠ {tag}：front buffer 没读到"); return null; }
                    for (int i = 3; i < buf.Length; i += 4) buf[i] = 255;
                    var bs = System.Windows.Media.Imaging.BitmapSource.Create(
                        w, h, 96, 96, PixelFormats.Bgra32, null, buf, w * 4);
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bs));
                    using (var fs = new FileStream(Path.Combine(dir, "shot", tag + ".png"),
                                                   FileMode.Create, FileAccess.Write))
                        enc.Save(fs);
                    Log($"  抓帧 {tag}.png（{w}×{h}）");
                    return buf;
                }
                catch (Exception ex) { Log($"  ⚠ 抓帧失败（{tag}）：{ex.Message}"); return null; }
            }

            const string NAME4 = "测试-四宫格";
            const string NAME2 = "测试-双人";

            try
            {
                await Task.Delay(1200);

                var srcDir = Path.Combine(dir, "src");
                var pics = Directory.Exists(srcDir)
                    ? Directory.GetFiles(srcDir, "*.png").OrderBy(x => x, StringComparer.Ordinal).ToList()
                    : new List<string>();
                Log($"素材 {pics.Count} 张");
                if (pics.Count < 6) { Log("⚠ 素材不足 6 张，自检中止"); Flush(); return; }

                foreach (var p in pics.Take(6))
                    AddInput(new InputSource
                    {
                        Type = SourceType.File,
                        Name = Path.GetFileNameWithoutExtension(p),
                        Location = p
                    });
                Log($"输入数 = {Inputs.Count}");

                // 清掉上次跑留下的同名布局，保证可重复运行
                ProgramLayoutStore.Delete(NAME4, out _);
                ProgramLayoutStore.Delete(NAME2, out _);

                // ① 摆四宫格 → 存
                LoadInputsIntoPreview(Inputs.Take(4).ToList());
                await Task.Delay(1200);
                BtnPreviewToProgram_Click(this, new RoutedEventArgs());
                await Task.Delay(1200);
                var p4 = ProgramLayoutStore.Capture(NAME4, Layers, DateTime.Now);
                ProgramLayoutStore.Upsert(p4, out string e4);
                Log($"[存] {NAME4}：{p4.Cells.Count} 格 err={e4}  :: {LayerSnap()}");

                // ② 摆双人 → 存
                LoadInputsIntoPreview(new List<InputSource> { Inputs[4], Inputs[5] });
                await Task.Delay(1200);
                BtnPreviewToProgram_Click(this, new RoutedEventArgs());
                await Task.Delay(1200);
                var p2 = ProgramLayoutStore.Capture(NAME2, Layers, DateTime.Now);
                ProgramLayoutStore.Upsert(p2, out string e2);
                Log($"[存] {NAME2}：{p2.Cells.Count} 格 err={e2}  :: {LayerSnap()}");

                InitProgramLayouts();
                Log($"[下拉] 共 {LayoutCombo.Items.Count} 项；落盘文件存在={File.Exists(ProgramLayoutStore.FilePath)}");

                // ③ 切四宫格
                ApplyProgramLayout(p4);
                await Task.Delay(1600);
                Log($"[A] 切四宫格：Layers={Layers.Count} :: {LayerSnap()}");
                Log($"[A] 状态栏：{StatusText.Text}");
                Grab("L_A_切四宫格");

                // ④ 切双人 —— 旧四宫格必须整组下线
                ApplyProgramLayout(p2);
                await Task.Delay(1600);
                Log($"[B] 切双人：Layers={Layers.Count} :: {LayerSnap()}");
                Log($"[B] 状态栏：{StatusText.Text}");
                Grab("L_B_切双人");

                // ⑤ 切回四宫格（来回切）
                ApplyProgramLayout(p4);
                await Task.Delay(1600);
                Log($"[C] 切回四宫格：Layers={Layers.Count} :: {LayerSnap()}");
                Grab("L_C_切回四宫格");

                // ⑥ 撤回：应回到「切四宫格之前」= 双人
                BtnLayoutUndo_Click(this, new RoutedEventArgs());
                await Task.Delay(1600);
                Log($"[D] 撤回：Layers={Layers.Count} :: {LayerSnap()}");
                Log($"[D] 状态栏：{StatusText.Text}");
                var dBuf = Grab("L_D_撤回");

                // ⑦ 只选不切：改 SelectedIndex，画面必须一动不动
                int before = Layers.Count;
                string beforeSnap = LayerSnap();
                LayoutCombo.SelectedIndex = (LayoutCombo.SelectedIndex + 1) % Math.Max(1, LayoutCombo.Items.Count);
                await Task.Delay(1200);
                Log($"[E] 只改选中项（{LayoutCombo.SelectedIndex}）：Layers={before}→{Layers.Count}，" +
                    $"图层集合{(beforeSnap == LayerSnap() ? "未变" : "变了 ⚠")}");
                var eBuf = Grab("L_E_只选不切");

                // 逐像素比对 D / E（脚本也会比，这里先自己判一次，日志里能直接看到结论）
                if (dBuf != null && eBuf != null)
                {
                    int diff = 0;
                    for (int i = 0; i < dbufLen(dBuf, eBuf); i++) if (dBuf[i] != eBuf[i]) diff++;
                    Log($"[E] D 与 E 逐字节不同数 = {diff}（{dBuf.Length} 字节）→ "
                        + (diff == 0 ? "画面完全一致 ✓" : "画面变了 ✗"));
                }

                // ⑧ 快捷键路径：Ctrl+Alt+N 走的是 ApplyLayoutByNumber（真正的"一键切"）
                int idx4 = _programPresets.FindIndex(x => x.Name == NAME4) + 1;
                Log($"[G] 快捷键切第 {idx4} 套（{NAME4}）");
                ApplyLayoutByNumber(idx4);
                await Task.Delay(1600);
                Log($"[G] Layers={Layers.Count} :: {LayerSnap()}");
                Log($"[G] 状态栏：{StatusText.Text}");
                Grab("L_G_快捷键切第1套");

                // ⑨ 缺素材：临时把一个输入摘掉，切布局时应留空但不崩、不报异常
                //    （不走右键删除那条路：那里有确认框，自检里不能弹模态框）
                var victim = Inputs.LastOrDefault();
                if (victim != null)
                {
                    Log($"[F] 移除输入「{victim.Name}」后切双人布局");
                    foreach (var l in Layers.Where(x => x.Source == victim).ToList()) RemoveLayer(l);
                    foreach (var it in PlayItems.Where(x => x.Source == victim).ToList()) PlayItems.Remove(it);
                    Inputs.Remove(victim);
                    RenumberInputs();
                    await Task.Delay(600);
                    ApplyProgramLayout(p2);
                    await Task.Delay(1400);
                    Log($"[F] Layers={Layers.Count} :: {LayerSnap()}");
                    Log($"[F] 状态栏：{StatusText.Text}");
                    Grab("L_F_缺一路素材");
                }

                // ⑩ 转场真生效（2026-10-22）：给四宫格挂 AlphaFade 0.8s，切过去时抓中途帧。
                //    判据：中途帧既不等于旧画面、也不等于最终画面（说明在做渐变，不是硬切）。
                var p4f = ProgramLayoutStore.LoadAll().FirstOrDefault(x => x.Name == NAME4);
                if (p4f != null)
                {
                    // 先造「硬切对照组」：同一套布局、转场 = Cut
                    p4f.Transition = "Cut";
                    ProgramLayoutStore.Upsert(p4f, out _);
                    InitProgramLayouts();
                    ApplyProgramLayout(p2);
                    await Task.Delay(1300);
                    ApplyProgramLayout(p4f);
                    await Task.Delay(120);
                    var cutMid = Grab("L_H0_硬切120ms");
                    await Task.Delay(1200);
                    var cutEnd = Grab("L_H1_硬切完成");

                    // 再挂 AlphaFade
                    p4f.Transition = "AlphaFade";
                    p4f.TransitionSeconds = 0.8;
                    ProgramLayoutStore.Upsert(p4f, out _);
                    InitProgramLayouts();
                    ApplyProgramLayout(p2);
                    await Task.Delay(1300);
                    var fadeOld = Grab("L_H2_转场前_双人");
                    Log($"[H] 转场前合成耗时 {_monitor?.LastComposeMs ?? 0:0.00}ms");
                    ApplyProgramLayout(p4f);
                    await Task.Delay(100);
                    var fadeA = Grab("L_H3_转场中A");
                    await Task.Delay(200);
                    var fadeB = Grab("L_H4_转场中B");
                    Log($"[H] 转场中合成耗时 {_monitor?.LastComposeMs ?? 0:0.00}ms");
                    await Task.Delay(1500);
                    var fadeEnd = Grab("L_H5_转场完成");
                    Log($"[H] 转场后合成耗时 {_monitor?.LastComposeMs ?? 0:0.00}ms");

                    // 用平均绝对差（MAD）而不是"不同字节个数"：
                    // 渐变过程中几乎每个字节都在变，"变了的字节数"会早早饱和到接近全帧，
                    // 看不出推进幅度；MAD 才是随透明度线性下降的，能证明是在渐变而不是跳变。
                    static double Mad(byte[]? a, byte[]? b)
                    {
                        if (a == null || b == null) return -1;
                        int n = Math.Min(a.Length, b.Length);
                        long sum = 0; int cnt = 0;
                        for (int i = 0; i < n; i += 16) { sum += Math.Abs(a[i] - b[i]); cnt++; }
                        return cnt == 0 ? -1 : (double)sum / cnt;
                    }
                    Log($"[H] 硬切：120ms帧 vs 完成帧 MAD = {Mad(cutMid, cutEnd):0.00}（应 <0.5）");
                    Log($"[H] 转场：A vs 完成 MAD = {Mad(fadeA, fadeEnd):0.00}；B vs 完成 MAD = {Mad(fadeB, fadeEnd):0.00}");
                    Log($"[H] 转场：A vs 旧 MAD = {Mad(fadeA, fadeOld):0.00}；完成 vs 旧 MAD = {Mad(fadeEnd, fadeOld):0.00}");
                    Log($"[H] 转场：A vs B MAD = {Mad(fadeA, fadeB):0.00}（>0 说明中途帧在动）");
                    Log($"[H] 状态栏：{StatusText.Text}");

                    // 手动 CUT / AUTO 走的是 TakeToLayer，转场也必须真生效（不能只有布局切换才有效）
                    _transition = TransitionType.AlphaFade;
                    _duration = 0.8;
                    ApplyProgramLayout(p2);
                    await Task.Delay(1300);
                    _previewSource = Inputs[0];
                    TakeToLayer(TransitionType.AlphaFade);
                    await Task.Delay(100);
                    var manA = Grab("L_M_手动转场中");
                    await Task.Delay(1500);
                    var manB = Grab("L_M_手动转场完成");
                    Log($"[M] 手动转场：中途 vs 完成 MAD = {Mad(manA, manB):0.00}（>1 说明在渐变）");
                    Log($"[M] 状态栏：{StatusText.Text}");
                    _transition = TransitionType.Cut;      // 复位，别影响后面的用例
                }

                // ⑪ 音频按布局批量套用（2026-10-22）
                //    四宫格里让前两格都出声 → 存一套；切走再切回，必须还是两格出声。
                //    再存一套「全静音」→ 切过去必须是真静音（旧实现会强行让第一格出声）。
                const string NAMEA = "测试-双音源";
                const string NAMEB = "测试-静音";
                ProgramLayoutStore.Delete(NAMEA, out _);
                ProgramLayoutStore.Delete(NAMEB, out _);

                ApplyProgramLayout(p4);
                await Task.Delay(1300);
                for (int i = 0; i < Layers.Count; i++) Layers[i].IsAudioSource = i < 2;
                ApplyAudioRouting();
                var pa = ProgramLayoutStore.Capture(NAMEA, Layers, DateTime.Now, TransitionType.Cut, 1.0);
                ProgramLayoutStore.Upsert(pa, out string ea);
                Log($"[I] 存「{NAMEA}」：格={pa.Cells.Count} 标音源={pa.Cells.Count(c => c.IsAudioSource)} err={ea}");

                foreach (var l in Layers) l.IsAudioSource = false;
                ApplyAudioRouting();
                var pb = ProgramLayoutStore.Capture(NAMEB, Layers, DateTime.Now, TransitionType.Cut, 1.0);
                ProgramLayoutStore.Upsert(pb, out string eb);
                Log($"[I] 存「{NAMEB}」：格={pb.Cells.Count} 标音源={pb.Cells.Count(c => c.IsAudioSource)} err={eb}");

                InitProgramLayouts();
                ApplyProgramLayout(p2);                       // 先切走（双人自带音源状态）
                await Task.Delay(1300);
                ApplyProgramLayout(pa);                       // 切回双音源
                await Task.Delay(1500);
                int onA = Layers.Count(l => l.IsAudioSource);
                Log($"[J] 切到双音源：Layers={Layers.Count} 出声={onA} :: {LayerSnap()}");
                Log($"[J] 状态栏：{StatusText.Text}");
                Grab("L_J_双音源");

                ApplyProgramLayout(p2);
                await Task.Delay(1300);
                ApplyProgramLayout(pb);                       // 切到全静音
                await Task.Delay(1500);
                int onB = Layers.Count(l => l.IsAudioSource);
                Log($"[K] 切到全静音：Layers={Layers.Count} 出声={onB}（应为 0）");
                Log($"[K] 状态栏：{StatusText.Text}");
                Grab("L_K_全静音");

                // ⑫ 导出 / 导入（2026-10-22）：走 Store 直调，不弹文件对话框
                string expDir = Path.Combine(dir, "export");
                try { Directory.CreateDirectory(expDir); } catch { }
                string expPath = Path.Combine(expDir, NAME4 + ProgramLayoutStore.ExportExtension);
                bool expOk = ProgramLayoutStore.ExportFile(p4f, expPath, out string expErr);
                long expLen = expOk && File.Exists(expPath) ? new FileInfo(expPath).Length : -1;
                Log($"[L] 导出：ok={expOk} bytes={expLen} err={expErr} → {expPath}");

                string expJson = expLen > 0 ? File.ReadAllText(expPath) : "";
                Log($"[L] 导出文件含 App={(expJson.Contains("\"App\": \"Cutlance\"") || expJson.Contains("\"App\":\"Cutlance\""))}" +
                    $" Transition={(expJson.Contains("AlphaFade"))} Cells={(expJson.Contains("\"Cells\""))}");

                var back = ProgramLayoutStore.ImportFile(expPath, out string impErr);
                Log($"[L] 导入：{(back == null ? "失败 " + impErr : $"格={back.Cells.Count} 转场={back.Transition} {back.TransitionSeconds:0.0}s 名={back.Name}")}");

                // 坏文件必须被挡住（别的程序的布局文件）
                string badPath = Path.Combine(expDir, "bad.clayout");
                try { File.WriteAllText(badPath, "{\"App\":\"OtherApp\",\"Name\":\"x\",\"Cells\":[{\"SourceName\":\"a\"}]}"); } catch { }
                var bad = ProgramLayoutStore.ImportFile(badPath, out string badErr);
                Log($"[L] 坏文件：{(bad == null ? "已挡住 ✓ 原因=" + badErr : "没挡住 ✗")}");

                Log("布局自检结束");
            }
            catch (Exception ex)
            {
                Log("EXCEPTION\t" + ex);
            }
            finally
            {
                Flush();
                if (Environment.GetEnvironmentVariable("CUTLANCE_LAYOUT_EXIT") == "1")
                {
                    try { await Task.Delay(400); } catch { }
                    try { Close(); } catch { }
                }
            }
        }

        private static int dbufLen(byte[] a, byte[] b) => Math.Min(a.Length, b.Length);

        /// <summary>
        /// FTB 淡黑自检（CUTLANCE_FTB_TEST=1）。
        ///
        /// 验的是**成品帧**，不是界面：抓的是合成器 front buffer ——
        /// 推流 / 录制 / NDI / 全屏输出吃的都是这一帧，它黑了才算真黑。
        /// 老实现只盖界面浮层，这套断言一条都过不去。
        /// </summary>
        private async void StartFtbTest()
        {
            if (Environment.GetEnvironmentVariable("CUTLANCE_FTB_TEST") != "1") return;

            string dir = Environment.GetEnvironmentVariable("CUTLANCE_FTB_DIR") ?? "";
            if (string.IsNullOrWhiteSpace(dir)) dir = Path.Combine(Path.GetTempPath(), "cutlance-ftb");
            try { Directory.CreateDirectory(dir); Directory.CreateDirectory(Path.Combine(dir, "shot")); } catch { }

            var log = new List<string>();
            void Log(string m)
            {
                log.Add($"{DateTime.Now:HH:mm:ss.fff}\t{m}");
                try { ErrorLog.Info("[FTB自检] " + m); } catch { }
            }
            void Flush()
            {
                try
                {
                    System.IO.File.WriteAllLines(Path.Combine(dir, "ftb.log"), log,
                        new System.Text.UTF8Encoding(false));
                }
                catch { }
            }

            byte[]? Grab(string tag)
            {
                try
                {
                    if (_monitor == null) { Log($"  ⚠ {tag}：没有合成器"); return null; }
                    int w = _monitor.OutW, h = _monitor.OutH;
                    var buf = new byte[w * h * 4];
                    bool ok = false;
                    _monitor.WithProgram((p, stride) =>
                    {
                        for (int y = 0; y < h; y++)
                            System.Runtime.InteropServices.Marshal.Copy(
                                new IntPtr(p.ToInt64() + (long)y * stride), buf, y * w * 4, w * 4);
                        ok = true;
                    });
                    if (!ok) { Log($"  ⚠ {tag}：front buffer 没读到"); return null; }
                    for (int i = 3; i < buf.Length; i += 4) buf[i] = 255;
                    var bs = System.Windows.Media.Imaging.BitmapSource.Create(
                        w, h, 96, 96, PixelFormats.Bgra32, null, buf, w * 4);
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bs));
                    using (var fs = new FileStream(Path.Combine(dir, "shot", tag + ".png"),
                                                   FileMode.Create, FileAccess.Write))
                        enc.Save(fs);
                    Log($"  抓帧 {tag}.png  均值={Mean(buf):0.00}  FtbAlpha={_monitor.FtbAlpha}");
                    return buf;
                }
                catch (Exception ex) { Log($"  ⚠ 抓帧失败（{tag}）：{ex.Message}"); return null; }
            }

            // 画面亮度均值（0=全黑，255=全白）：FTB 该把它压到接近 0
            static double Mean(byte[]? b)
            {
                if (b == null) return -1;
                long sum = 0; int n = 0;
                for (int i = 0; i + 2 < b.Length; i += 64)   // 每 16 像素采样一次
                {
                    sum += b[i] + b[i + 1] + b[i + 2];
                    n += 3;
                }
                return n == 0 ? -1 : (double)sum / n;
            }

            static double Mad(byte[]? a, byte[]? b)
            {
                if (a == null || b == null) return -1;
                int n = Math.Min(a.Length, b.Length);
                long sum = 0; int cnt = 0;
                for (int i = 0; i < n; i += 64) { sum += Math.Abs(a[i] - b[i]); cnt++; }
                return cnt == 0 ? -1 : (double)sum / cnt;
            }

            try
            {
                await Task.Delay(1200);

                var srcDir = Path.Combine(dir, "src");
                var pics = Directory.Exists(srcDir)
                    ? Directory.GetFiles(srcDir, "*.png").OrderBy(x => x, StringComparer.Ordinal).ToList()
                    : new List<string>();
                Log($"素材 {pics.Count} 张");
                if (pics.Count < 4) { Log("⚠ 素材不足 4 张，自检中止"); Flush(); return; }

                foreach (var p in pics.Take(4))
                    AddInput(new InputSource
                    {
                        Type = SourceType.File,
                        Name = Path.GetFileNameWithoutExtension(p),
                        Location = p
                    });
                Log($"输入数 = {Inputs.Count}");

                // 摆四宫格作为被测画面（有画面才看得出「压黑」）
                LoadInputsIntoPreview(Inputs.Take(4).ToList());
                await Task.Delay(1200);
                BtnPreviewToProgram_Click(this, new RoutedEventArgs());
                await Task.Delay(1400);
                Log($"[N] 四宫格就位：Layers={Layers.Count}");

                var baseBuf = Grab("N0_基准未淡黑");
                Log($"[N] 基准 FtbAlpha={_monitor?.FtbAlpha ?? -1}（应为 0）");

                // ① 按 FTB：0.4s 淡到黑
                Log("[N] 按 FTB（淡到黑 0.4s）");
                BtnFtb_Click(this, new RoutedEventArgs());
                await Task.Delay(150);
                var midIn = Grab("N1_淡黑中");
                Log($"[N] 淡黑中 FtbAlpha={_monitor?.FtbAlpha ?? -1}（应在 40~220 之间）");
                await Task.Delay(700);
                var black = Grab("N2_全黑");
                Log($"[N] 全黑 FtbAlpha={_monitor?.FtbAlpha ?? -1}（应为 255）");
                Log($"[N] 全黑帧 vs 基准帧 MAD = {Mad(black, baseBuf):0.00}（应 > 30）");
                Log($"[N] 状态栏：{StatusText.Text}");

                // ② 黑屏中切一路画面：总闸优先，还得是黑的（播出安全）
                _previewSource = Inputs[0];
                TakeToLayer(TransitionType.Cut);
                await Task.Delay(900);
                var blackAfterCut = Grab("N3_黑屏中换画面");
                Log($"[N] 黑屏中切画面后：Layers={Layers.Count} FtbAlpha={_monitor?.FtbAlpha ?? -1} " +
                    $"均值={Mean(blackAfterCut):0.00}（仍应≈0）");

                // ③ 再按 FTB：0.4s 恢复
                Log("[N] 再按 FTB（恢复 0.4s）");
                BtnFtb_Click(this, new RoutedEventArgs());
                await Task.Delay(150);
                var midOut = Grab("N4_恢复中");
                Log($"[N] 恢复中 FtbAlpha={_monitor?.FtbAlpha ?? -1}（应在 40~220 之间）");
                await Task.Delay(800);
                var back = Grab("N5_恢复出画");
                Log($"[N] 恢复后 FtbAlpha={_monitor?.FtbAlpha ?? -1}（应为 0）均值={Mean(back):0.00}（应>20）");
                Log($"[N] 恢复帧 vs 全黑帧 MAD = {Mad(back, blackAfterCut):0.00}（应 > 30）");
                Log($"[N] 状态栏：{StatusText.Text}");

                // ④ 合成耗时：淡黑期间每帧要整屏扫一遍，不能吃掉帧预算
                Log($"[N] 合成耗时 当前={_monitor?.LastComposeMs ?? 0:0.00}ms（预算 33ms@30fps）");

                // ⑤ 端到端（可选）：录一段真文件，中间按 FTB —— 证明「推出去的」也黑。
                //    抓成品帧已经逻辑等价（四条输出线都吃这一帧），但总闸级功能值得再钉一颗钉子。
                if (Environment.GetEnvironmentVariable("CUTLANCE_FTB_RECORD") == "1")
                {
                    var svc = StreamService.Instance;
                    string recDir = Path.Combine(dir, "rec");
                    try { Directory.CreateDirectory(recDir); } catch { }
                    svc.EnableStream = false;          // 只录不推，别在自检里真去连 RTMP
                    svc.EnableRecord = true;
                    svc.RecordFolder = recDir;
                    svc.Container = "mkv";
                    svc.RecordSegmentSeconds = 0;      // 不分段，一个文件好量黑段
                    Log($"[R] 开始录制 → {recDir}");
                    StartStreaming();
                    await Task.Delay(1800);            // 前段：正常画面
                    Log("[R] 录制中按 FTB（应黑 1.5s）");
                    BtnFtb_Click(this, new RoutedEventArgs());
                    await Task.Delay(1500);
                    BtnFtb_Click(this, new RoutedEventArgs());   // 恢复
                    await Task.Delay(1400);            // 后段：正常画面
                    StreamService.Instance.Stop();
                    await Task.Delay(1200);
                    var files = Directory.Exists(recDir)
                        ? Directory.GetFiles(recDir).Where(f => !f.EndsWith(".json")).ToList()
                        : new List<string>();
                    foreach (var f in files)
                        Log($"[R] 录像 {Path.GetFileName(f)} {new FileInfo(f).Length} 字节");
                    Log($"[R] 录像文件数 = {files.Count}（应为 1）");
                }

                Log("FTB 自检结束");
            }
            catch (Exception ex)
            {
                Log("EXCEPTION\t" + ex);
            }
            finally
            {
                Flush();
                if (Environment.GetEnvironmentVariable("CUTLANCE_FTB_EXIT") == "1")
                {
                    try { await Task.Delay(400); } catch { }
                    try { Close(); } catch { }
                }
            }
        }

        /// <summary>
        /// <summary>
        /// 混音台界面截图专用（CUTLANCE_MIXER_SHOT=1）：把窗口开起来、设成「中压」后就**停住不退出**，
        /// 等外部 PrintWindow 抓图。只用于拍文档配图，正常使用零影响。
        /// </summary>
        private async void StartMixerShot()
        {
            if (Environment.GetEnvironmentVariable("CUTLANCE_MIXER_SHOT") != "1") return;
            try
            {
                await Task.Delay(1500);
                // 只把压缩档设成「中压」，设备交给程序自己自动预选（截图才真实：
                // 别用假设备名，否则界面显示「未使用」而提示又说"启用 N 路"，自相矛盾）
                var ch = StreamService.Instance.AudioChannels;
                if (!string.IsNullOrWhiteSpace(ch[1].DeviceName))
                {
                    ch[1].Role = AudioRole.Secondary;
                    ch[1].Volume = 90;
                }
                var cmp = StreamService.Instance.Compressor;
                cmp.Level = CompressorLevel.Medium;
                cmp.ApplyPresetValues();

                // 摆 3 路一起推到输出，让「画面声音」那一段有内容可看（2026-10-23）。
                // 截图脚本没造输入时这段是空的，配图就看不出新功能长什么样。
                try
                {
                    string sdir = Environment.GetEnvironmentVariable("CUTLANCE_FADE_DIR") ?? "";
                    if (string.IsNullOrWhiteSpace(sdir)) sdir = Path.Combine(Path.GetTempPath(), "cutlance-fade");
                    var ssrc = Path.Combine(sdir, "src");
                    var pics = Directory.Exists(ssrc)
                        ? Directory.GetFiles(ssrc, "*.png").OrderBy(x => x, StringComparer.Ordinal).Take(3).ToList()
                        : new List<string>();
                    if (pics.Count >= 3 && Inputs.Count == 0)
                    {
                        foreach (var p in pics)
                            AddInput(new InputSource
                            {
                                Type = SourceType.File,
                                Name = Path.GetFileNameWithoutExtension(p),
                                Location = p
                            });
                        LoadInputsIntoPreview(Inputs.Take(3).ToList());
                        await Task.Delay(900);
                        BtnPreviewToProgram_Click(this, new RoutedEventArgs());
                        await Task.Delay(1500);
                        ErrorLog.Info($"[混音台截图] 已摆 {Layers.Count} 路输出，画面声音段应有内容");
                    }
                }
                catch (Exception ex) { ErrorLog.Info("[混音台截图] 摆输出失败：" + ex.Message); }

                // 诊断：截图时把"模型里的设备名 / 下拉实际选中项"一起记下来。
                // 曾出现"八路都显示未使用、底部却说启用 2 路"的自相矛盾，靠这行日志定位。
                try
                {
                    var lines = new List<string>();
                    for (int i = 0; i < ch.Count; i++)
                        lines.Add($"#{i + 1} dev=[{ch[i].DeviceName}] inuse={ch[i].InUse}");
                    ErrorLog.Info("[混音台截图] " + string.Join(" | ", lines));
                }
                catch { }

                Window? exist = null;
                foreach (Window w0 in Application.Current.Windows)
                    if (w0 is AudioMixerWindow) { exist = w0; break; }
                AudioMixerWindow? shown = exist as AudioMixerWindow;
                if (exist == null)
                {
                    // 与「输出设置」截图同一套安全姿势：不激活 + 扔到屏幕外，绝不闪在老板眼前
                    var w = new AudioMixerWindow(this) { Owner = this, ShowActivated = false };
                    w.WindowStartupLocation = WindowStartupLocation.Manual;
                    w.Left = -12000; w.Top = -12000;
                    w.Show();
                    w.Left = -12000; w.Top = -12000;
                    shown = w;
                }
                else ((AudioMixerWindow)exist).SyncFromModel();

                // 程序内截屏（2026-10-23）：外部 PrintWindow 抓屏外的 WPF 窗口会得到一片白
                // （DWM 重定向不往那个 DC 里画）。这里直接用 RenderTargetBitmap 渲染窗口自身，
                // 稳定且和屏幕上看的一模一样 —— 混音台改版（画面声音置顶）就靠它验收。
                string rtShot = Environment.GetEnvironmentVariable("CUTLANCE_MIXER_RT_SHOT") ?? "";
                if (!string.IsNullOrWhiteSpace(rtShot) && shown != null)
                {
                    await Task.Delay(900);      // 等布局与动态行排完
                    var src2 = PresentationSource.FromVisual(shown);
                    double dpi = src2?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                    int pw = (int)Math.Ceiling(shown.ActualWidth * dpi);
                    int ph = (int)Math.Ceiling(shown.ActualHeight * dpi);
                    if (pw > 10 && ph > 10)
                    {
                        var rtb = new RenderTargetBitmap(pw, ph, 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
                        rtb.Render(shown);
                        var enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(rtb));
                        using var fs = File.Create(rtShot);
                        enc.Save(fs);
                        ErrorLog.Info($"[混音台截图] 已渲染保存 {rtShot}（{pw}×{ph}）");
                    }
                }
            }
            catch (Exception ex)
            {
                try { ErrorLog.Info("[混音台截图] " + ex); } catch { }
            }
        }

        /// <summary>
        /// 布局带混音/压缩的自检（CUTLANCE_MIX_TEST=1，2026-10-23）。
        ///
        /// 验三件事：
        ///   ① 角色档 / 音量 / 静音 真的被存进布局，切回来原样还原（按**设备名**认领）；
        ///   ② 压缩档（含阈值/压缩比/增益补偿）跟着布局走，切回来不是"打开时的默认值"；
        ///   ③ **老布局**（没有 Mixer / Compressor 段）切过来**不许动混音台** —— 不能因为升级就把别人的声音关掉。
        /// 结果写 mix.log，由 vmtest/verify_mixlayout.py 判定。
        /// </summary>
        /// <summary>
        /// 画面声音进混音台的自检（CUTLANCE_PROG_AUDIO_TEST=1，2026-10-23）。
        ///
        /// 老板的原话：「预览框多选文件不只有一个声音时，切到输出，混音台里现有声音应该都进调音台，
        /// 方便按主次调，最后混合输出给推流和录制。」
        /// 所以这里逐条验：多路都出声 → 混音台列得出来 → 在混音台改能落到合成器 → 布局能存能还原。
        /// </summary>
        private async void StartProgAudioTest()
        {
            if (Environment.GetEnvironmentVariable("CUTLANCE_PROG_AUDIO_TEST") != "1") return;

            string dir = Environment.GetEnvironmentVariable("CUTLANCE_PROG_DIR") ?? "";
            if (string.IsNullOrWhiteSpace(dir)) dir = Path.Combine(Path.GetTempPath(), "cutlance-progaudio");
            try { Directory.CreateDirectory(dir); } catch { }

            var log = new List<string>();
            void Log(string m)
            {
                log.Add($"{DateTime.Now:HH:mm:ss.fff}\t{m}");
                try { ErrorLog.Info("[画面声音] " + m); } catch { }
            }
            void Flush()
            {
                try
                {
                    System.IO.File.WriteAllLines(Path.Combine(dir, "prog.log"), log,
                        new System.Text.UTF8Encoding(false));
                }
                catch { }
            }
            void Check(bool ok, string name, string detail)
            {
                string line = (ok ? "PASS" : "FAIL") + "\t" + name + "\t" + detail;
                log.Add($"{DateTime.Now:HH:mm:ss.fff}\t{line}");
                try { ErrorLog.Info("[画面声音] " + line); } catch { }
            }

            AudioMixerWindow? mixer = null;
            string lastName = "__progA_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            string lastName2 = "__progB_" + Guid.NewGuid().ToString("N").Substring(0, 6);

            try
            {
                await Task.Delay(1500);

                var srcDir = Path.Combine(dir, "src");
                var pics = Directory.Exists(srcDir)
                    ? Directory.GetFiles(srcDir, "*.png").OrderBy(x => x, StringComparer.Ordinal).Take(3).ToList()
                    : new List<string>();
                Log($"素材 {pics.Count} 张");
                if (pics.Count < 3) { Check(false, "⓪ 素材准备", "不足 3 张，中止"); Flush(); return; }

                foreach (var p in pics)
                    AddInput(new InputSource
                    {
                        Type = SourceType.File,
                        Name = Path.GetFileNameWithoutExtension(p),
                        Location = p
                    });

                // ── ① 三路一起推上输出（老板的真实路径：预览多选 → 推到输出）──
                LoadInputsIntoPreview(Inputs.Take(3).ToList());
                await Task.Delay(900);
                BtnPreviewToProgram_Click(this, new RoutedEventArgs());
                await Task.Delay(1400);
                Log($"输出层数={Layers.Count}");

                Check(Layers.Count >= 3, "① 三路一起推上输出", $"输出 {Layers.Count} 格");
                Check(Layers.All(l => l.IsAudioSource),
                      "② 每一路都出声（不再只留第一格）",
                      string.Join(",", Layers.Select(l => l.IsAudioSource ? "开" : "关")));
                Check(Layers.Count > 0 && Layers[0].AudioRole == AudioRole.Main
                      && Layers.Skip(1).All(l => l.AudioRole == AudioRole.Secondary),
                      "③ 默认第一格主音、其余次音",
                      string.Join(",", Layers.Select(l => l.RoleLabel)));

                // ── ② 合成器侧的轨道电平（真正决定推流/录制里听到什么）──
                var t0 = _monitor?.TrackFor(Layers[0].Source);
                var t1 = _monitor?.TrackFor(Layers[1].Source);
                Check(t0 != null && !t0.Mute && Math.Abs(t0.Volume - 1f) < 0.02f,
                      "④ 主音轨道：未静音、音量 1.00",
                      t0 == null ? "无轨道" : $"Mute={t0.Mute} Vol={t0.Volume:0.000}");
                Check(t1 != null && !t1.Mute && Math.Abs(t1.Volume - 0.398f) < 0.02f,
                      "⑤ 次音轨道：音量 ≈0.398（-8dB）",
                      t1 == null ? "无轨道" : $"Mute={t1.Mute} Vol={t1.Volume:0.000}");

                // ── ③ 混音台列出画面声音（不 Show，离屏跑，别打扰老板）──
                mixer = new AudioMixerWindow(this);
                await Task.Delay(900);
                Check(mixer.TestRowCount == Layers.Count,
                      "⑥ 混音台「画面声音」把输出里每一路都列出来了",
                      $"混音台 {mixer.TestRowCount} 行 / 输出 {Layers.Count} 格");
                Log("混音台首行：" + mixer.TestRowText(0));

                // ── ④ 在混音台里改角色 → 图层与合成器都要跟着变 ──
                mixer.TestSetRole(0, AudioRole.Background);
                await Task.Delay(200);
                var t0b = _monitor?.TrackFor(Layers[0].Source);
                Check(Layers[0].AudioRole == AudioRole.Background
                      && t0b != null && Math.Abs(t0b.Volume - 0.1995f) < 0.02f,
                      "⑦ 混音台改成「背景」→ 图层变 + 轨道降到 ≈0.199",
                      $"图层={Layers[0].RoleLabel} 轨道={(t0b?.Volume ?? -1f):0.000}");

                // ── ⑤ 拖音量 → 两边同步 ──
                mixer.TestSetVolume(0, 80);
                await Task.Delay(200);
                var t0c = _monitor?.TrackFor(Layers[0].Source);
                Check(Layers[0].Volume == 80
                      && t0c != null && Math.Abs(t0c.Volume - 0.8f * 0.1995f) < 0.02f,
                      "⑧ 混音台拖到 80% → 图层=80、轨道 = 0.8×0.199",
                      $"图层={Layers[0].Volume}% 轨道={(t0c?.Volume ?? -1f):0.000}");

                // ── ⑥ 静音某一路 ──
                mixer.TestSetMuted(1, true);
                await Task.Delay(200);
                var t1b = _monitor?.TrackFor(Layers[1].Source);
                Check(!Layers[1].IsAudioSource && t1b != null && t1b.Mute,
                      "⑨ 混音台勾「静音」→ 那一路真不出声",
                      $"图层音源={Layers[1].IsAudioSource} 轨道Mute={t1b?.Mute}");

                // ── ⑦ 反向：改右栏图层滑杆 → 混音台显示跟着走（同一份设置）──
                Layers[2].Volume = 55;
                AudioMixerWindow.NotifyProgramChanged();
                await Task.Delay(400);
                Check(mixer.TestRowText(2).Contains("音量=55"),
                      "⑩ 改右栏滑杆 → 混音台那一行同步显示 55",
                      mixer.TestRowText(2));

                // ── ⑧ 布局存角色档：存 → 改乱 → 切回来必须原样 ──
                Layers[0].Volume = 80;
                Layers[0].AudioRole = AudioRole.Background;
                ProgramLayoutStore.Delete(lastName, out _);
                var preset = ProgramLayoutStore.Capture(lastName, Layers, DateTime.Now, TransitionType.Cut, 0.5);
                bool saved = ProgramLayoutStore.Upsert(preset, out string err);
                Check(saved, "⑪ 存布局（含角色档）", saved ? $"格数 {preset.Cells.Count}" : err);

                for (int i = 0; i < Layers.Count; i++)
                {
                    Layers[i].Volume = 100;
                    Layers[i].AudioRole = AudioRole.Main;
                }
                ApplyAudioRouting();
                await Task.Delay(300);

                var pA = ProgramLayoutStore.LoadAll().FirstOrDefault(x => x.Name == lastName);
                if (pA != null) ApplyProgramLayout(pA, recordUndo: false);
                await Task.Delay(1200);
                Check(Layers.Count > 0 && Layers[0].AudioRole == AudioRole.Background && Layers[0].Volume == 80,
                      "⑫ 切回布局 → 角色档与音量都还原",
                      Layers.Count == 0 ? "无图层" : $"{Layers[0].RoleLabel}/{Layers[0].Volume}%");

                // ── ⑨ 老布局（没有 AudioRole 字段）→ 必须当主音，不许额外衰减 ──
                var oldP = ProgramLayoutStore.Capture(lastName2, Layers, DateTime.Now, TransitionType.Cut, 0.5);
                foreach (var c in oldP.Cells) c.AudioRole = "";      // 模拟升级前存的文件
                for (int i = 0; i < Layers.Count; i++) Layers[i].AudioRole = AudioRole.Background;
                ApplyProgramLayout(oldP, recordUndo: false);
                await Task.Delay(1000);
                Check(Layers.All(l => l.AudioRole == AudioRole.Main),
                      "⑬ 老布局没角色档 → 一律按主音（音量不变）",
                      string.Join(",", Layers.Select(l => l.RoleLabel)));

                ProgramLayoutStore.Delete(lastName, out _);
                ProgramLayoutStore.Delete(lastName2, out _);
            }
            catch (Exception ex)
            {
                Log("[异常] " + ex);
                Check(false, "自检异常", ex.Message);
            }
            finally
            {
                try { mixer?.Close(); } catch { }
                Flush();
                if (Environment.GetEnvironmentVariable("CUTLANCE_PROG_EXIT") == "1")
                {
                    try { Application.Current.Shutdown(); } catch { }
                }
            }
        }

        private async void StartMixLayoutTest()
        {
            if (Environment.GetEnvironmentVariable("CUTLANCE_MIX_TEST") != "1") return;

            string dir = Environment.GetEnvironmentVariable("CUTLANCE_MIX_DIR") ?? "";
            if (string.IsNullOrWhiteSpace(dir)) dir = Path.Combine(Path.GetTempPath(), "cutlance-mix");
            try { Directory.CreateDirectory(dir); } catch { }

            var log = new List<string>();
            void Log(string m)
            {
                log.Add($"{DateTime.Now:HH:mm:ss.fff}\t{m}");
                try { ErrorLog.Info("[混音自检] " + m); } catch { }
            }
            void Flush()
            {
                try
                {
                    System.IO.File.WriteAllLines(Path.Combine(dir, "mix.log"), log,
                        new System.Text.UTF8Encoding(false));
                }
                catch { }
            }

            var svc = StreamService.Instance;
            var ch = svc.AudioChannels;
            var cmp = svc.Compressor;

            // 原状态（结束时还原，别把老板的混音台改坏）
            var origDev = ch.Select(c => c.DeviceName).ToList();
            var origRole = ch.Select(c => c.Role).ToList();
            var origVol = ch.Select(c => c.Volume).ToList();
            var origMut = ch.Select(c => c.Muted).ToList();
            var origComp = cmp.Clone();

            string Snap() => string.Join(" | ", ch.Select(c =>
                $"{c.Index}:{(string.IsNullOrWhiteSpace(c.DeviceName) ? "-" : c.DeviceName)}/{c.RoleLabel}/{c.Volume}%/{(c.Muted ? "静" : "开")}"));

            try
            {
                await Task.Delay(1200);

                // 素材：CUTLANCE_MIX_DIR/src 里的纯色图，一张一路（离屏跑，不碰老板的工程）
                string srcDir = Path.Combine(dir, "src");
                if (Inputs.Count == 0 && Directory.Exists(srcDir))
                {
                    foreach (var png in Directory.GetFiles(srcDir, "*.png")
                                                .OrderBy(x => x, StringComparer.Ordinal).Take(4))
                        AddInput(new InputSource
                        {
                            Type = SourceType.File,
                            Name = Path.GetFileNameWithoutExtension(png),
                            Location = png
                        });
                    await Task.Delay(900);
                }

                LoadInputsIntoPreview(Inputs.Take(4).ToList());
                await Task.Delay(900);
                BtnPreviewToProgram_Click(this, new RoutedEventArgs());
                await Task.Delay(1100);
                Log($"[准备] 图层数={Layers.Count} 可见={Layers.Count(l => l.Visible)}");
                Log($"[准备] 初始混音 {Snap()}");

                // 端到端（可选）：真录一段，证明压缩链**真的进了 ffmpeg 命令行**，
                // 而不是只存在模型里。CUTLANCE_MIX_RECORD=1 时跑。
                if (Environment.GetEnvironmentVariable("CUTLANCE_MIX_RECORD") == "1")
                {
                    var rs = StreamService.Instance;
                    string recDir = Path.Combine(dir, "rec");
                    try { Directory.CreateDirectory(recDir); } catch { }
                    rs.EnableStream = false;           // 只录不推，别在自检里真连 RTMP
                    rs.EnableRecord = true;
                    rs.RecordFolder = recDir;
                    rs.Container = "mkv";
                    rs.RecordSegmentSeconds = 0;
                    // 总得有音源，否则滤波链里根本没有音频段（on 也看不到 acompressor）
                    if (Layers.Count > 0) { Layers[0].IsAudioSource = true; ApplyAudioRouting(); }
                    cmp.Level = CompressorLevel.Medium;
                    cmp.ApplyPresetValues();
                    Log($"[R] 压缩档={cmp.LevelText} 开始录制 → {recDir}");
                    StartStreaming();
                    await Task.Delay(4000);
                    StreamService.Instance.Stop();
                    await Task.Delay(1200);
                    var files = Directory.Exists(recDir)
                        ? Directory.GetFiles(recDir).Where(f => !f.EndsWith(".json")).ToList()
                        : new List<string>();
                    foreach (var f in files)
                        Log($"[R] 录像 {Path.GetFileName(f)} {new FileInfo(f).Length} 字节");
                    Log($"[R] 录像文件数 = {files.Count}（应为 1）");
                }

                // ① 造一套"两路麦 + 中压"的场面（设备名用假名：验的是按名字认领的逻辑，
                //    不依赖本机到底插了哪支麦；输出没启动，所以不会真去开设备）
                ch[0].DeviceName = "T-MIC-A";
                ch[0].Role = AudioRole.Background;
                ch[0].Volume = 60;
                ch[0].Muted = false;
                ch[1].DeviceName = "T-MIC-B";
                ch[1].Role = AudioRole.Secondary;
                ch[1].Volume = 120;
                ch[1].Muted = false;
                ch[0].Compress = CompressorLevel.Medium;   // 逐路压缩（2026-10-24）
                ch[1].Compress = CompressorLevel.Off;
                cmp.Level = CompressorLevel.Medium;
                cmp.ApplyPresetValues();
                Log($"[设置] {Snap()}");
                Log($"[设置] 压缩={cmp.Summary}");
                Log($"[设置] 滤镜串={cmp.FilterChain}");

                // ①b 顺手把倒计时牌摆成一套非默认样式（2026-10-24：布局也要带它）
                _cdFontPx = 165;
                _cdStyle.FontFamilyName = "微软雅黑";
                _cdStyle.TextColorHex = "#FFFFEB3B";
                _cdStyle.BackColorHex = "#00000000";
                _cdX = 0.22; _cdY = 0.78;
                _cdOverlayOn = true;
                Log($"[存前] 倒计时 字号={_cdFontPx:0} 字体={_cdStyle.FontFamilyName} " +
                    $"字色={_cdStyle.TextColorHex} 底板={_cdStyle.BackColorHex} 位置=({_cdX:0.##},{_cdY:0.##}) 叠加={_cdOverlayOn}");

                var p = ProgramLayoutStore.Capture("__mix_test__", Layers, DateTime.Now,
                                                   _transition, _duration, ch, cmp, BuildClockPreset());
                Log($"[存] 混音={p.Mixer?.Count ?? -1} 路 / 压缩={p.Compressor?.Level} " +
                    $"阈值={p.Compressor?.ThresholdDb} 比={p.Compressor?.Ratio} 补偿={p.Compressor?.MakeupDb} " +
                    $"upsert={ProgramLayoutStore.Upsert(p, out string e1)} {e1}");

                // ② 改乱：角色换主音、音量回 100、第二路静音、压缩换强压；倒计时牌也换一套样式
                ch[0].Role = AudioRole.Main;
                ch[0].Volume = 100;
                ch[1].Muted = true;
                cmp.Level = CompressorLevel.Strong;
                cmp.ApplyPresetValues();
                _cdFontPx = 90;
                _cdStyle.FontFamilyName = "宋体";
                _cdStyle.TextColorHex = "#FF00E5FF";
                _cdX = 0.5; _cdY = 0.5;
                Log($"[改乱] {Snap()} / 压缩={cmp.Summary}");
                Log($"[改乱] 倒计时 字号={_cdFontPx:0} 字体={_cdStyle.FontFamilyName} " +
                    $"字色={_cdStyle.TextColorHex} 位置=({_cdX:0.##},{_cdY:0.##}) 叠加={_cdOverlayOn}");
                ch[0].Compress = CompressorLevel.Strong;   // 逐路压缩也一起改乱
                ch[1].Compress = CompressorLevel.Light;
                Log($"[逐路] 存 布局里 通道1={p.Mixer?.FirstOrDefault(m => m.DeviceName == "T-MIC-A")?.Compress} " +
                    $"通道2={p.Mixer?.FirstOrDefault(m => m.DeviceName == "T-MIC-B")?.Compress}");
                Log($"[逐路] 改乱后 通道1={ch[0].CompressLabel} 通道2={ch[1].CompressLabel}");

                ApplyProgramLayout(p);
                await Task.Delay(1200);
                Log($"[切回] {Snap()}");
                Log($"[切回] 压缩={cmp.Summary} 阈值={cmp.ThresholdDb} 比={cmp.Ratio} 补偿={cmp.MakeupDb}");
                Log($"[倒计时存] 字号={p.Clock?.FontSize:0} 字体={p.Clock?.FontFamily} " +
                    $"字色={p.Clock?.TextColor} 位置=({p.Clock?.X:0.##},{p.Clock?.Y:0.##}) 叠加={p.Clock?.OverlayOn}");
                Log($"[倒计时切回] 字号={_cdFontPx:0} 字体={_cdStyle.FontFamilyName} " +
                    $"字色={_cdStyle.TextColorHex} 位置=({_cdX:0.##},{_cdY:0.##}) 叠加={_cdOverlayOn}");
                Log($"[逐路] 切回后 通道1={ch[0].CompressLabel} 通道2={ch[1].CompressLabel}");

                // 逐路压缩的滤镜串内容（2026-10-24）：与总线压缩同一套预设、点号小数、末尾限幅。
                // 端到端录音这条测试用的是「假设备名」，不可能真开出采集卡，所以这里验到
                // 「串进滤镜图的那串参数是对的」，真机播放时 [音频图] 日志会把它打出来。
                var probe = new AudioChannel { Index = 1, Compress = CompressorLevel.Medium };
                Log($"[逐路滤镜] {probe.CompressFilter}");

                // ②c 图层透明度（2026-10-24）：从**成品帧**验 —— 压到 0 那一块应该变黑
                var l0 = Layers.FirstOrDefault(l => l.Visible && l.Source != null);
                if (l0 != null && _monitor != null)
                {
                    double SampleAt(double fx, double fy)
                    {
                        double sum = 0; long n = 0;
                        _monitor.WithProgram((ptr, stride) =>
                        {
                            int bw = 120, bh = 60;
                            int x0 = Math.Clamp((int)(fx * _monitor.OutW) - bw / 2, 0, Math.Max(0, _monitor.OutW - bw));
                            int y0 = Math.Clamp((int)(fy * _monitor.OutH) - bh / 2, 0, Math.Max(0, _monitor.OutH - bh));
                            var row = new byte[stride];
                            for (int y = y0; y < y0 + bh; y++)
                            {
                                Marshal.Copy(IntPtr.Add(ptr, y * stride), row, 0, stride);
                                for (int x = x0; x < x0 + bw; x++)
                                {
                                    sum += row[x * 4] + row[x * 4 + 1] + row[x * 4 + 2];
                                    n++;
                                }
                            }
                        });
                        return n == 0 ? 0 : sum / n;
                    }

                    // 取这一格的正中（归一化：左上角 + 半个可见尺寸）
                    double cx = l0.X + 0.25 * l0.ViewScaleX;
                    double cy = l0.Y + 0.25 * l0.ViewScaleY;

                    l0.Opacity = 100; RenderLayer(l0); await Task.Delay(400);
                    double lit = SampleAt(cx, cy);
                    l0.Opacity = 50; RenderLayer(l0); await Task.Delay(400);
                    double half = SampleAt(cx, cy);
                    l0.Opacity = 0; RenderLayer(l0); await Task.Delay(400);
                    double dark = SampleAt(cx, cy);
                    Log($"[透明] 100%={lit:0} 50%={half:0} 0%={dark:0}");

                    l0.Opacity = 100; RenderLayer(l0);
                    await Task.Delay(300);

                    // ②d 描边 / 阴影（2026-10-25 老板「图层边框/阴影」）：
                    //     落点必须是成品帧 —— 界面上的 WPF Border 推流和录制里根本看不见。
                    //     判据同样取自成品帧：内侧应该变金、框外应该变暗。
                    (double R, double G, double B) RgbAtPx(int px, int py, int bw = 16, int bh = 10)
                    {
                        double sr = 0, sg = 0, sb = 0; long n = 0;
                        _monitor.WithProgram((ptr, stride) =>
                        {
                            int x0 = Math.Clamp(px - bw / 2, 0, Math.Max(0, _monitor.OutW - bw));
                            int y0 = Math.Clamp(py - bh / 2, 0, Math.Max(0, _monitor.OutH - bh));
                            var row = new byte[stride];
                            for (int y = y0; y < y0 + bh; y++)
                            {
                                Marshal.Copy(IntPtr.Add(ptr, y * stride), row, 0, stride);
                                for (int x = x0; x < x0 + bw; x++)
                                {
                                    sb += row[x * 4]; sg += row[x * 4 + 1]; sr += row[x * 4 + 2];
                                    n++;
                                }
                            }
                        });
                        return n == 0 ? (0, 0, 0) : (sr / n, sg / n, sb / n);
                    }

                    // 先缩成画中画 —— 铺满全屏时框外就是画布外，阴影没地方画
                    double ox0 = l0.X, oy0 = l0.Y, osx0 = l0.ScaleX, osy0 = l0.ScaleY;
                    double obt0 = l0.BorderThickness, obr0 = l0.BorderRadius;
                    string obc0 = l0.BorderColor;
                    double osh0 = l0.Shadow; int oshd0 = l0.ShadowOpacity;

                    // 先把底色改成中灰：纯黑底上叠黑色阴影等于没叠（量不出差别，判据会假失败）
                    uint oldBg = _monitor.ProgramBackground;
                    _monitor.ProgramBackground = 0xFF808080u;

                    l0.X = 0.15; l0.Y = 0.15; l0.ScaleX = 1.0; l0.ScaleY = 1.0;
                    l0.BorderThickness = 0; l0.BorderRadius = 0; l0.Shadow = 0;
                    RenderLayer(l0);
                    await Task.Delay(500);

                    bool gotRect = _monitor.TryProgramLayerRect(0, out int rrX, out int rrY, out int rrW, out int rrH);
                    Log($"[框] 实际画面矩形 = ({rrX},{rrY}) {rrW}×{rrH}");

                    var in0 = RgbAtPx(rrX + 14, rrY + rrH / 2);
                    var out0 = RgbAtPx(rrX - 22, rrY + rrH / 2);

                    l0.BorderThickness = 26;
                    l0.BorderRadius = 18;
                    l0.BorderColor = "#FFFFD24A";     // 金
                    l0.Shadow = 46;
                    l0.ShadowOpacity = 100;
                    RenderLayer(l0);
                    await Task.Delay(600);

                    var in1 = RgbAtPx(rrX + 14, rrY + rrH / 2);
                    var out1 = RgbAtPx(rrX - 22, rrY + rrH / 2);
                    Log($"[框] 内侧 RGB ({in0.R:0},{in0.G:0},{in0.B:0}) → ({in1.R:0},{in1.G:0},{in1.B:0})");
                    Log($"[框] 外侧 RGB ({out0.R:0},{out0.G:0},{out0.B:0}) → ({out1.R:0},{out1.G:0},{out1.B:0})");

                    // 判据由 vmtest/verify_mixlayout.py 解析这一行（自检只负责取数，别在这儿自己判）
                    Log($"[描边] rect={gotRect} x={rrX} y={rrY} w={rrW} h={rrH} " +
                        $"in={in0.R:0}/{in0.G:0}/{in0.B:0}->{in1.R:0}/{in1.G:0}/{in1.B:0} " +
                        $"out={out0.R:0}/{out0.G:0}/{out0.B:0}->{out1.R:0}/{out1.G:0}/{out1.B:0}");

                    // 还原（底色也要还，否则后面截出来一片灰）
                    _monitor.ProgramBackground = oldBg;
                    l0.BorderThickness = obt0; l0.BorderRadius = obr0; l0.BorderColor = obc0;
                    l0.Shadow = osh0; l0.ShadowOpacity = oshd0;
                    l0.X = ox0; l0.Y = oy0; l0.ScaleX = osx0; l0.ScaleY = osy0;
                    RenderLayer(l0);
                    await Task.Delay(400);
                }

                // ③ 老布局（无混音段）不许动混音台
                var oldP = ProgramLayoutStore.Capture("__mix_old__", Layers, DateTime.Now, _transition, _duration);
                oldP.Mixer = null;
                oldP.Compressor = null;
                string beforeOld = Snap();
                string beforeOldComp = cmp.Summary;
                ApplyProgramLayout(oldP);
                await Task.Delay(900);
                Log($"[老布局] 切前={beforeOld} / 压缩={beforeOldComp}");
                Log($"[老布局] 切后={Snap()} / 压缩={cmp.Summary}");
            }
            catch (Exception ex)
            {
                Log("[异常] " + ex);
            }
            finally
            {
                try
                {
                    for (int i = 0; i < ch.Count && i < origDev.Count; i++)
                    {
                        ch[i].DeviceName = origDev[i];
                        ch[i].Role = origRole[i];
                        ch[i].Volume = origVol[i];
                        ch[i].Muted = origMut[i];
                        ch[i].Compress = CompressorLevel.Off;   // 逐路压缩测完归零，别留在老板的混音台上
                    }
                    cmp.Level = origComp.Level;
                    cmp.ThresholdDb = origComp.ThresholdDb;
                    cmp.Ratio = origComp.Ratio;
                    cmp.MakeupDb = origComp.MakeupDb;
                    ProgramLayoutStore.Delete("__mix_test__", out _);
                    ProgramLayoutStore.Delete("__mix_old__", out _);
                    Log($"[还原] {Snap()} / 压缩={cmp.Summary}");
                }
                catch (Exception ex) { Log("[还原失败] " + ex.Message); }
                Flush();
                if (Environment.GetEnvironmentVariable("CUTLANCE_MIX_EXIT") == "1")
                {
                    try { await Task.Delay(300); } catch { }
                    try { Close(); } catch { }
                }
            }
        }

        /// 转场三态 + 每格音量自检（CUTLANCE_FADE_TEST=1，2026-10-22）。
        ///
        /// 验的是**成品帧**，不是界面（C 方案下对元素做动画看不出来，这里只看合成器输出）：
        ///   · Cut       切换后应"一帧到位"，没有中间态
        ///   · Fade      必须是"淡到黑再淡出"—— 中点要接近全黑
        ///   · AlphaFade 必须是交叉溶解 —— 中点**不能**黑（这是两者唯一的区别）
        ///   · 每格音量  存进布局、切回来要一样；图层音量要真的落到音频总线的轨道上
        /// </summary>
        private async void StartFadeTest()
        {
            if (Environment.GetEnvironmentVariable("CUTLANCE_FADE_TEST") != "1") return;

            string dir = Environment.GetEnvironmentVariable("CUTLANCE_FADE_DIR") ?? "";
            if (string.IsNullOrWhiteSpace(dir)) dir = Path.Combine(Path.GetTempPath(), "cutlance-fade");
            try { Directory.CreateDirectory(dir); Directory.CreateDirectory(Path.Combine(dir, "shot")); } catch { }

            var log = new List<string>();
            void Log(string m)
            {
                log.Add($"{DateTime.Now:HH:mm:ss.fff}\t{m}");
                try { ErrorLog.Info("[转场自检] " + m); } catch { }
            }
            void Flush()
            {
                try
                {
                    System.IO.File.WriteAllLines(Path.Combine(dir, "fade.log"), log,
                        new System.Text.UTF8Encoding(false));
                }
                catch { }
            }

            byte[]? Grab(string tag)
            {
                try
                {
                    if (_monitor == null) { Log($"  ⚠ {tag}：没有合成器"); return null; }
                    int w = _monitor.OutW, h = _monitor.OutH;
                    var buf = new byte[w * h * 4];
                    bool ok = false;
                    _monitor.WithProgram((p, stride) =>
                    {
                        for (int y = 0; y < h; y++)
                            System.Runtime.InteropServices.Marshal.Copy(
                                new IntPtr(p.ToInt64() + (long)y * stride), buf, y * w * 4, w * 4);
                        ok = true;
                    });
                    if (!ok) { Log($"  ⚠ {tag}：front buffer 没读到"); return null; }
                    for (int i = 3; i < buf.Length; i += 4) buf[i] = 255;
                    var bs = System.Windows.Media.Imaging.BitmapSource.Create(
                        w, h, 96, 96, PixelFormats.Bgra32, null, buf, w * 4);
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bs));
                    using (var fs = new FileStream(Path.Combine(dir, "shot", tag + ".png"),
                                                   FileMode.Create, FileAccess.Write))
                        enc.Save(fs);
                    Log($"  抓帧 {tag}.png  均值={Mean(buf):0.00}  FadeAlpha={_monitor.FadeAlphaNow} Ftb={_monitor.FtbAlpha}");
                    return buf;
                }
                catch (Exception ex) { Log($"  ⚠ 抓帧失败（{tag}）：{ex.Message}"); return null; }
            }

            static double Mean(byte[]? b)
            {
                if (b == null) return -1;
                long sum = 0; int n = 0;
                for (int i = 0; i + 2 < b.Length; i += 64)
                {
                    sum += b[i] + b[i + 1] + b[i + 2];
                    n += 3;
                }
                return n == 0 ? -1 : (double)sum / n;
            }

            static double Mad(byte[]? a, byte[]? b)
            {
                if (a == null || b == null) return -1;
                int n = Math.Min(a.Length, b.Length);
                long sum = 0; int cnt = 0;
                for (int i = 0; i < n; i += 64) { sum += Math.Abs(a[i] - b[i]); cnt++; }
                return cnt == 0 ? -1 : (double)sum / cnt;
            }

            // 摆四宫格（"旧画面"）：4 块纯色，判亮度最干净
            async Task BuildGrid()
            {
                LoadInputsIntoPreview(Inputs.Take(4).ToList());
                await Task.Delay(900);
                BtnPreviewToProgram_Click(this, new RoutedEventArgs());
                await Task.Delay(1100);
            }

            try
            {
                await Task.Delay(1200);

                var srcDir = Path.Combine(dir, "src");
                var pics = Directory.Exists(srcDir)
                    ? Directory.GetFiles(srcDir, "*.png").OrderBy(x => x, StringComparer.Ordinal).ToList()
                    : new List<string>();
                Log($"素材 {pics.Count} 张");
                if (pics.Count < 4) { Log("⚠ 素材不足 4 张，自检中止"); Flush(); return; }

                foreach (var p in pics.Take(4))
                    AddInput(new InputSource
                    {
                        Type = SourceType.File,
                        Name = Path.GetFileNameWithoutExtension(p),
                        Location = p
                    });
                Log($"输入数 = {Inputs.Count}");

                // ── ① 对照组：Cut 必须一帧到位 ──
                await BuildGrid();
                var gridBuf = Grab("A0_四宫格基准");
                Log($"[A] 四宫格 Layers={Layers.Count} 亮度={Mean(gridBuf):0.00}");

                _transition = TransitionType.Cut;
                SetPreview(Inputs[0]);
                await Task.Delay(200);
                BtnTake_Click(this, new RoutedEventArgs());
                await Task.Delay(120);
                var cutMid = Grab("A1_Cut后120ms");
                await Task.Delay(900);
                var cutEnd = Grab("A2_Cut完成");
                Log($"[A] Cut：120ms帧 vs 完成帧 MAD={Mad(cutMid, cutEnd):0.00}（应≈0）");

                // ── ② Fade：中点必须黑 ──
                await BuildGrid();
                var f0 = Grab("B0_Fade前四宫格");
                _transition = TransitionType.Fade;
                _duration = 1.0;
                SetPreview(Inputs[1]);
                await Task.Delay(200);
                BtnTake_Click(this, new RoutedEventArgs());
                await Task.Delay(380);
                var fA = Grab("B1_Fade前半");
                await Task.Delay(140);                       // ≈520ms，正中
                var fMid = Grab("B2_Fade中点应全黑");
                await Task.Delay(200);
                var fB = Grab("B3_Fade后半");
                await Task.Delay(900);
                var fEnd = Grab("B4_Fade完成");
                Log($"[B] Fade 亮度：基准={Mean(f0):0.00} → 前半={Mean(fA):0.00} → 中点={Mean(fMid):0.00} → 后半={Mean(fB):0.00} → 完成={Mean(fEnd):0.00}");
                Log($"[B] Fade：完成帧 vs 旧帧 MAD={Mad(fEnd, f0):0.00}（应明显>0，说明真换了画面）");

                // ── ③ AlphaFade：中点**不能**黑 ──
                await BuildGrid();
                var g0 = Grab("C0_AlphaFade前四宫格");
                _transition = TransitionType.AlphaFade;
                _duration = 1.0;
                SetPreview(Inputs[2]);
                await Task.Delay(200);
                BtnTake_Click(this, new RoutedEventArgs());
                await Task.Delay(480);                       // ≈500ms，正中
                var gMid = Grab("C1_AlphaFade中点不能黑");
                await Task.Delay(900);
                var gEnd = Grab("C2_AlphaFade完成");
                Log($"[C] AlphaFade 亮度：基准={Mean(g0):0.00} → 中点={Mean(gMid):0.00} → 完成={Mean(gEnd):0.00}");
                Log($"[C] AlphaFade 中点 vs Fade 中点 亮度差={Mean(gMid) - Mean(fMid):0.00}（应明显>0）");
                _transition = TransitionType.Cut;

                // ── ④ 每格音量：设值 → 查总线轨道 → 存布局 → 改回 → 切回来 ──
                await BuildGrid();
                for (int i = 0; i < Layers.Count; i++)
                {
                    Layers[i].IsAudioSource = i < 2;         // 前两格出声
                    // 本项专验「音量百分比 → 总线电平」这条链，把角色档统一压回主音（0dB），
                    // 否则推输出时给的「其余次音 −8dB」会把 130% 乘成 0.52，量出来的数对不上本意。
                    // 角色档自己的验证在 CUTLANCE_PROG_AUDIO_TEST 那套里做。
                    Layers[i].AudioRole = AudioRole.Main;
                }
                if (Layers.Count >= 2)
                {
                    Layers[0].Volume = 50;
                    Layers[1].Volume = 130;
                }
                ApplyAudioRouting();
                await Task.Delay(300);
                Log($"[V] 设值后图层：{string.Join(" ", Layers.Select(l => $"#{l.Id}(vol={l.Volume},audio={l.IsAudioSource})"))}");
                Log($"[V] 总线轨道：\n{Services.Audio.AudioBus.Instance.DumpTracks()}");

                var vp = ProgramLayoutStore.Capture("__fade_test_vol__", Layers, DateTime.Now,
                                                    TransitionType.Cut, 1.0);
                string vName = vp.Name;
                var saveErr = ProgramLayoutStore.Upsert(vp, out string saveMsg);
                Log($"[V] 存布局 ok={saveErr} msg={saveMsg} 格数={vp.Cells.Count} " +
                    $"音量=[{string.Join(",", vp.Cells.Select(c => c.Volume))}] " +
                    $"出声=[{string.Join(",", vp.Cells.Select(c => c.IsAudioSource ? "1" : "0"))}]");

                foreach (var l in Layers) l.Volume = 100;    // 故意改回去，看切布局能不能还原
                ApplyAudioRouting();
                await Task.Delay(200);

                var back = ProgramLayoutStore.LoadAll().FirstOrDefault(p => p.Name == vName);
                if (back != null)
                {
                    ApplyProgramLayout(back);
                    await Task.Delay(1200);
                    Log($"[V] 切回后图层：{string.Join(" ", Layers.Select(l => $"#{l.Id}(vol={l.Volume},audio={l.IsAudioSource})"))}");
                    Log($"[V] 切回后总线：\n{Services.Audio.AudioBus.Instance.DumpTracks()}");
                }
                else Log("[V] ⚠ 读不回刚存的布局");

                // 收尾：把测试布局删掉，别留在老板的下拉里
                try { ProgramLayoutStore.Delete(vName, out _); } catch { }
                Grab("D0_收尾");

                Log("转场与音量自检结束");
            }
            catch (Exception ex)
            {
                Log("EXCEPTION\t" + ex);
            }
            finally
            {
                Flush();
                if (Environment.GetEnvironmentVariable("CUTLANCE_FADE_EXIT") == "1")
                {
                    try { await Task.Delay(400); } catch { }
                    try { Close(); } catch { }
                }
            }
        }


        /// <summary>
        /// 「点左侧输入 = 换格 还是 全画面预览」分流自检（CUTLANCE_PICK_TEST=1，2026-10-23）。
        ///
        /// 老板报障：多选一起进预览后，再点左侧文件，预览画面没有换成新文件。
        /// 根因：Tile_Click 判"换格"只看 _selLayer、不看焦点在哪个区 —— 点过一次输出框之后
        ///       _selLayer 一直留着，于是点文件被当成"换输出框那一格"，预览纹丝不动。
        /// 判据：
        ///   ① 多选 3 路进预览 → 预览 3 路；
        ///   ② 普通点第 4 个输入 → 预览变 1 路，且就是第 4 路（日常动作，老板要的就是这个）；
        ///   ③ 选中输出框第 1 格 → 再点第 2 个输入 → 预览仍是第 4 路（不跟变），
        ///      但第 1 格换成第 2 路（2026-10-22 拍板的单格切还在）；
        ///   ④ 预览浮层挂了右键菜单 —— 不然多路预览时右键弹不出「刷新画面」。
        /// </summary>
        private async void StartPickTest()
        {
            if (Environment.GetEnvironmentVariable("CUTLANCE_PICK_TEST") != "1") return;

            string dir = Environment.GetEnvironmentVariable("CUTLANCE_FADE_DIR") ?? "";
            if (string.IsNullOrWhiteSpace(dir)) dir = Path.Combine(Path.GetTempPath(), "cutlance-fade");
            try { Directory.CreateDirectory(dir); } catch { }

            var log = new List<string>();
            void Log(string m)
            {
                log.Add($"{DateTime.Now:HH:mm:ss.fff}\t{m}");
                try { ErrorLog.Info("[点选分流自检] " + m); } catch { }
            }
            void Check(bool ok, string name, string detail) => Log($"{(ok ? "PASS" : "FAIL")}\t{name}\t{detail}");
            void Flush()
            {
                try
                {
                    System.IO.File.WriteAllLines(Path.Combine(dir, "pick.log"), log,
                        new System.Text.UTF8Encoding(false));
                }
                catch { }
            }

            // 模拟"普通单击左侧输入项"：Tile_Click 只认 sender 的 DataContext
            void ClickTile(InputSource s) => Tile_Click(new Button { DataContext = s }, new RoutedEventArgs());

            try
            {
                await Task.Delay(1400);

                var srcDir = Path.Combine(dir, "src");
                var pics = Directory.Exists(srcDir)
                    ? Directory.GetFiles(srcDir, "*.png").OrderBy(x => x, StringComparer.Ordinal).ToList()
                    : new List<string>();
                Log($"素材 {pics.Count} 张；键盘修饰键={Keyboard.Modifiers}");
                if (pics.Count < 4) { Log("⚠ 素材不足 4 张，自检中止"); Flush(); return; }
                if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
                {
                    Log("⚠ 当前真的按着 Ctrl —— 点选会被当成多选勾选，自检跳过（请在没人碰键盘时重跑）");
                    Flush(); return;
                }

                foreach (var p in pics.Take(4))
                    AddInput(new InputSource
                    {
                        Type = SourceType.File,
                        Name = Path.GetFileNameWithoutExtension(p),
                        Location = p
                    });
                Log($"输入数 = {Inputs.Count}");

                // ── ① 多选 3 路进预览 ──
                LoadInputsIntoPreview(Inputs.Take(3).ToList());
                await Task.Delay(900);
                Check(_previewSlots.Count == 3, "① 多选 3 路进预览 → 预览 3 路",
                      $"预览路数={_previewSlots.Count}");

                // ── ② 普通点第 4 个输入：应该整体换成这一路（老板要的日常动作）──
                var want = Inputs[3];
                ClickTile(want);
                await Task.Delay(900);
                Check(_previewSlots.Count == 1, "② 点新文件 → 预览变成单路全画面",
                      $"预览路数={_previewSlots.Count}（应为 1）");
                Check(ReferenceEquals(_previewSource, want), "② 预览换成的是刚点的那一路",
                      $"预览源=[{_previewSource?.Name}] 期望=[{want.Name}]");

                // ── ③ 播出框选中着格子时点文件：预览照样换，播出一个字节都不许动 ──
                //    这是老板 2026-10-23 定死的流程：点文件第一步只进预览，上播出必须点「切换」。
                SetPreview(Inputs[0]);
                await Task.Delay(500);
                BtnCut_Click(this, new RoutedEventArgs());
                await Task.Delay(900);
                Log($"输出层数={Layers.Count}，预览路数={_previewSlots.Count}");
                if (Layers.Count > 0)
                {
                    var target = Layers[0];
                    var original = target.Source;
                    SelectLayer(target);                    // 播出框选中着这一格（最容易误伤的状态）
                    var previewTo = Inputs[2];
                    ClickTile(previewTo);
                    await Task.Delay(800);
                    Check(_previewSlots.Count == 1 && ReferenceEquals(_previewSource, previewTo),
                          "③ 播出框选中着格子时点文件 → 预览照样换成新文件",
                          $"预览 {_previewSlots.Count} 路，源=[{_previewSource?.Name}] 期望=[{previewTo.Name}]");
                    Check(ReferenceEquals(target.Source, original),
                          "③ 点文件没动播出画面（播出只能靠「切换」改）",
                          $"第 1 格=[{target.Source?.Name}] 原值=[{original?.Name}]");

                    // ── ④ 显式换格：右键菜单「换入播出框选中的那一格」 ──
                    var swapTo = Inputs[1];
                    var previewSrcBefore = _previewSource;
                    int previewCntBefore = _previewSlots.Count;
                    TileSwapToSelectedLayer_Click(new MenuItem { DataContext = swapTo }, new RoutedEventArgs());
                    await Task.Delay(800);
                    Check(ReferenceEquals(target.Source, swapTo),
                          "④ 右键「换入播出框选中的那一格」→ 那一格真换了",
                          $"第 1 格=[{target.Source?.Name}] 期望=[{swapTo.Name}]");
                    Check(_previewSlots.Count == previewCntBefore && ReferenceEquals(_previewSource, previewSrcBefore),
                          "④ 换格不动预览（预览归预览）",
                          $"预览 {previewCntBefore}→{_previewSlots.Count} 路，源=[{_previewSource?.Name}]");
                }
                else Check(false, "③ 输出层为空", "没推出图层，跳过播出保护判定");

                // ── ⑤ 预览浮层的右键菜单 ──
                var cellMenu = PreviewCell.ContextMenu;
                var opMenu = PreviewOpRoot.ContextMenu;
                Check(opMenu != null && ReferenceEquals(opMenu, cellMenu),
                      "⑤ 预览浮层挂了与预览框同一份右键菜单",
                      $"浮层={(opMenu == null ? "未挂" : opMenu.Items.Count + " 项")} 预览框={(cellMenu == null ? "未挂" : cellMenu.Items.Count + " 项")}");

                Log("点选分流自检结束");
            }
            catch (Exception ex)
            {
                Log("EXCEPTION\t" + ex);
            }
            finally
            {
                Flush();
                if (Environment.GetEnvironmentVariable("CUTLANCE_PICK_EXIT") == "1")
                {
                    try { await Task.Delay(400); } catch { }
                    try { Close(); } catch { }
                }
            }
        }

        /// <summary>
        /// 倒计时牌自检（CUTLANCE_CD_TEST=1，2026-10-24）。
        ///
        /// 验的是老板提的三件事实：
        ///   ① 设定时长（默认一小时，可自由输入）—— 解析器 + 大字显示都要对；
        ///   ② 开始后一直走，过了 0 **继续走负数**，直到点停止（不自动停、不触发任何动作）；
        ///   ③ 「叠加」真的进了成品帧（有像素、字号变大变多、关掉就消失），
        ///      而且工程里存了设定值/字号/叠加开关。
        /// </summary>
        /// <summary>
        /// 倒计时拖动**端到端**自检（CUTLANCE_CDDRAG_TEST=1，2026-10-26）。
        ///
        /// 为什么必须做到这一步：前两轮我在模型层全过、命中链全过，老板照样拖不动。
        /// 这次直接在程序内**调用真正的钩子回调**（MouseHookProc），伪造按下/移动/松开三条消息，
        /// 走完整条路：坐标换算 → 归属判定（WindowFromPoint）→ 控件过滤 → 命中 → 设拖动态 → 位移。
        ///
        /// ⚠ 为了 WindowFromPoint 能拿到本窗口，会把窗口临时摆到屏幕左上角约 1.5 秒
        ///   （仍然 ShowActivated=false，不抢焦点、不动鼠标光标，测完立刻还原位置）。
        /// </summary>
        /// <summary>
        /// 真实鼠标事件版拖动自检（CUTLANCE_CDREAL_TEST=1，2026-10-26 老板「数字乱动但移动不了」）。
        /// 与 CDDRAG 的区别：CDDRAG 是**直接调钩子函数**，这条用 SetCursorPos + mouse_event 发**真实输入**，
        /// 走「系统 → 全局钩子 → 我们的回调」完整路径 —— 老板报的症状只在这条路径上出现。
        /// ⚠ 会移动真实光标约 3 秒；只在键鼠空闲时跑（外部守卫判断），跑完恢复光标与窗口位置。
        /// ⚠ 发事件前先确认「数字中心那一点属于本进程」，否则立即放弃 —— 绝不把点击送给别的程序。
        /// </summary>
        private async void StartCdRealDragTest()
        {
            if (Environment.GetEnvironmentVariable("CUTLANCE_CDREAL_TEST") != "1") return;

            string dir = Environment.GetEnvironmentVariable("CUTLANCE_CDREAL_DIR") ?? "";
            if (string.IsNullOrWhiteSpace(dir)) dir = Path.Combine(Path.GetTempPath(), "cutlance-cdreal");
            try { Directory.CreateDirectory(dir); } catch { }

            var log = new List<string>();
            void L(string m) { lock (log) log.Add(m); }
            void Flush()
            {
                try
                {
                    System.IO.File.WriteAllLines(Path.Combine(dir, "cdreal.log"), log,
                        new System.Text.UTF8Encoding(false));
                }
                catch { }
            }

            double oldL = Left, oldT = Top;
            POINT oldCur = default;
            bool moved = false;
            try
            {
                await Task.Delay(2200);
                if (_monitor == null) { L("[CDREAL] 合成器没起来"); Flush(); Close(); return; }

                _cdOverlayOn = true;
                _cdX = 0.5; _cdY = 0.5;
                _cdFontPx = 120;
                UpdateCountdownUi();
                await Task.Delay(700);

                Left = 0; Top = 0;
                await Task.Delay(500);

                EnsureHwnd();
                try { GetCursorPos(out oldCur); } catch { }

                // 数字中心（帧像素）→ 画布 DIP → 屏幕物理
                var r0 = _monitor.CountdownRect;
                double cw = LayersCanvas.ActualWidth, ch = LayersCanvas.ActualHeight;
                var centerDip = new Point((r0.X + r0.W / 2.0) / _monitor.OutW * cw,
                                          (r0.Y + r0.H / 2.0) / _monitor.OutH * ch);
                var tr = LayersCanvas.TransformToAncestor(this);
                var centerClient = tr.Transform(centerDip);
                double scale = DevicePixelPerDip();
                var po = new POINT { X = (int)Math.Round(centerClient.X * scale),
                                     Y = (int)Math.Round(centerClient.Y * scale) };
                try { ClientToScreen(_hwnd, ref po); } catch { }

                IntPtr under = IntPtr.Zero; uint underPid = 0;
                try { under = WindowFromPoint(po); GetWindowThreadProcessId(under, out underPid); } catch { }
                L($"[CDREAL] 数字中心屏幕物理=({po.X},{po.Y}) 画布={cw:0}x{ch:0} scale={scale:0.##} " +
                  $"该点pid={underPid}/本进程={Environment.ProcessId} 矩形=({r0.X},{r0.Y}) {r0.W}x{r0.H}");

                if (underPid != (uint)Environment.ProcessId)
                {
                    L("[CDREAL] ⚠ 该点不属于本进程（有别的窗口盖着）→ 放弃发事件，绝不误点别人");
                    Flush();
                    return;
                }

                // 真实输入：移动到数字上 → 按下 → 20 步缓慢移动 → 抬起
                SetCursorPos(po.X, po.Y); moved = true;
                await Task.Delay(150);
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                await Task.Delay(100);
                L($"[CDREAL] 按下后 _cdDragging={_cdDragging} 位置=({_cdX:0.###},{_cdY:0.###})");

                // 高频模拟（2026-10-26）：100 步 × 3 DIP、每步只等 3ms —— 相当于每秒 300+ 条 MOVE，
                // 这才是老板那种"快鼠标"的真实负载；之前 20 步/35ms（28Hz）太温柔，看不出 UI 被挤满。
                double step = 3 * scale;    // 每步 3 DIP
                for (int k = 1; k <= 100; k++)
                {
                    // ⚠ 用「绝对坐标」注入：相对移动会被系统"指针精确度"衰减
                    //   （实测发 18 像素/步，11 步后光标只走了 46 像素 → 位置当然只挪一点点，
                    //    差点又误判成产品 bug）。绝对坐标才能精确复现"拖 300 DIP"。
                    int sw = Math.Max(1, GetSystemMetrics(0)), sh2 = Math.Max(1, GetSystemMetrics(1));
                    int tx = Math.Min(sw - 1, po.X + (int)Math.Round(step * k));
                    int ty = Math.Min(sh2 - 1, po.Y + (int)Math.Round(step * k * 0.4));
                    mouse_event(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE,
                                (uint)((long)tx * 65535 / sw), (uint)((long)ty * 65535 / sh2), 0, UIntPtr.Zero);
                    await Task.Delay(3);
                    if (k % 10 == 0)
                    {
                        var rk = _monitor.CountdownRect;
                        L($"[CDREAL]  步{k:000} 光标=({po.X + step * k:0},{po.Y + step * k * 0.4:0}) " +
                          $"拖动中={_cdDragging} 位置=({_cdX:0.###},{_cdY:0.###}) 矩形=({rk.X},{rk.Y})");
                    }
                }

                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                await Task.Delay(120);
                var rf = _monitor.CountdownRect;
                L($"[CDREAL] 松手后 _cdDragging={_cdDragging} 位置=({_cdX:0.###},{_cdY:0.###}) 矩形=({rf.X},{rf.Y})");
                L("[CDREAL] 结束");
            }
            catch (Exception ex)
            {
                L("[CDREAL] 异常 " + ex.Message);
            }
            finally
            {
                try { if (moved) { mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero); } } catch { }
                try { if (moved && (oldCur.X != 0 || oldCur.Y != 0)) SetCursorPos(oldCur.X, oldCur.Y); } catch { }
                try { Left = oldL; Top = oldT; } catch { }
                Flush();
                try { await Task.Delay(200); } catch { }
                Close();
            }
        }

        private async void StartCdDragE2E()
        {
            if (Environment.GetEnvironmentVariable("CUTLANCE_CDDRAG_TEST") != "1") return;

            string dir = Environment.GetEnvironmentVariable("CUTLANCE_CDDRAG_DIR") ?? "";
            if (string.IsNullOrWhiteSpace(dir)) dir = Path.Combine(Path.GetTempPath(), "cutlance-cddrag");
            try { Directory.CreateDirectory(dir); } catch { }

            var log = new List<string>();
            void L(string m) { lock (log) log.Add(m); }
            void Flush()
            {
                try
                {
                    System.IO.File.WriteAllLines(Path.Combine(dir, "cddrag.log"), log,
                        new System.Text.UTF8Encoding(false));
                }
                catch { }
            }

            double oldL = Left, oldT = Top;
            IntPtr lp = IntPtr.Zero;
            try
            {
                await Task.Delay(2200);
                if (_monitor == null) { L("[CDRAG] 合成器没起来"); Flush(); Close(); return; }

                _cdOverlayOn = true;
                _cdX = 0.5; _cdY = 0.5;
                _cdFontPx = 120;
                UpdateCountdownUi();
                await Task.Delay(800);

                // 摆到屏幕内，WindowFromPoint 才拿得到本窗口
                Left = 0; Top = 0;
                await Task.Delay(500);

                var tr = LayersCanvas.TransformToAncestor(this);
                var centerClient = tr.Transform(new Point(LayersCanvas.ActualWidth / 2, LayersCanvas.ActualHeight / 2));
                double scale = DevicePixelPerDip();

                // ⚠ 必须用与 ScreenPhysicalToClientDip **同一套** API 反算（ClientToScreen），
                //   不能用 WPF 的 PointToScreen：两者在窗口刚被挪动、或 DPI 感知不一致时会差几百像素，
                //   自检就会把"点"算到画布外面去（第一次跑就是这样，白跑一轮）。
                EnsureHwnd();
                var po = new POINT { X = (int)(centerClient.X * scale), Y = (int)(centerClient.Y * scale) };
                try { ClientToScreen(_hwnd, ref po); } catch { }
                var phys = new POINT { X = po.X, Y = po.Y };

                var hs = new MSLLHOOKSTRUCT { pt = phys };
                lp = Marshal.AllocHGlobal(Marshal.SizeOf<MSLLHOOKSTRUCT>());
                Marshal.StructureToPtr(hs, lp, false);

                IntPtr under = IntPtr.Zero;
                uint underPid = 0;
                try { under = WindowFromPoint(phys); GetWindowThreadProcessId(under, out underPid); } catch { }
                L($"[CDRAG] 画布中心屏幕物理=({phys.X},{phys.Y}) WindowFromPoint pid={underPid}/本进程={Environment.ProcessId}");

                // ① 按下
                MouseHookProc(0, (IntPtr)0x0201, lp);
                bool downOk = _cdDragging;
                L($"[CDRAG] 按下后 _cdDragging={downOk}（true 才算钩子接管了）");

                // ② 连续拖动：20 步 × 15 DIP，共 300 DIP（≈ 画布宽度的 52%）
                //    ⚠ 2026-10-26 改：以前只拖 53 DIP（9%），看不到"夹取上限/中途失效"，
                //      而老板真实拖动动辄几百 DIP —— 一步到位才暴露得出问题。
                double stepDip = 15.0;
                double stepPhys = stepDip * scale;
                var (nx0, ny0) = _monitor.CountdownPos;
                L($"[CDRAG] 拖动前位置=({nx0:0.###},{ny0:0.###}) 画布={LayersCanvas.ActualWidth:0}x{LayersCanvas.ActualHeight:0} scale={scale:0.##}");

                int stopAt = -1;
                for (int k = 1; k <= 20; k++)
                {
                    var hs2 = hs;
                    hs2.pt = new POINT
                    {
                        X = phys.X + (int)Math.Round(stepPhys * k),
                        Y = phys.Y + (int)Math.Round(stepPhys * k * 0.35)
                    };
                    Marshal.StructureToPtr(hs2, lp, false);
                    MouseHookProc(0, (IntPtr)0x0200, lp);

                    var (nxk, nyk) = _monitor.CountdownPos;
                    L($"[CDRAG]   步{k:00} 目标x={(phys.X + stepPhys * k):0} → 位置=({nxk:0.###},{nyk:0.###}) 拖动中={_cdDragging}");
                    if (stopAt < 0 && !_cdDragging) { stopAt = k; L($"[CDRAG] ⚠ 第 {k} 步拖动已被中断"); }
                    await Task.Delay(40);
                }

                var (nx, ny) = _monitor.CountdownPos;
                double wantNx = 0.5 + 300.0 / LayersCanvas.ActualWidth;
                L($"[CDRAG] 拖动后位置=({nx:0.###},{ny:0.###}) 理论≈({wantNx:0.###}) 中断步={stopAt}");
                L($"[CDRAG] 拖动中 _cdDragging={_cdDragging}（false = 中途被清掉了）");

                // ②b 拖到**窗口外面**（2026-10-26 老板「只能挪一点」的真凶在这里）
                //     真实操作里鼠标一定会划出画布、划出窗口；这时 WindowFromPoint 命中别的进程，
                //     上一版会把拖动态清掉 → 数字当场停住。这里把鼠标移到屏幕右下角（窗口之外），
                //     拖动必须还在继续。
                var hs3 = hs;
                hs3.pt = new POINT { X = 2500, Y = 1550 };   // 屏幕右下（窗口在 0,0 起，肯定在外面）
                Marshal.StructureToPtr(hs3, lp, false);
                MouseHookProc(0, (IntPtr)0x0200, lp);
                await Task.Delay(80);
                bool outOk = _cdDragging;
                var (nxo, nyo) = _monitor.CountdownPos;
                L($"[CDRAG] 拖到窗口外(2500,1550) → _cdDragging={outOk} 位置=({nxo:0.###},{nyo:0.###})（必须仍为 True）");

                // ②c 在窗口外松手 → 必须正常收尾（不能卡在拖动态里）
                MouseHookProc(0, (IntPtr)0x0202, lp);
                await Task.Delay(60);
                L($"[CDRAG] 窗口外松手 → _cdDragging={_cdDragging}（必须 False）");

                // ③ 松开
                MouseHookProc(0, (IntPtr)0x0202, lp);
                L($"[CDRAG] 松开后 _cdDragging={_cdDragging} 位置=({_cdX:0.###},{_cdY:0.###})");
                L("[CDRAG] 结束");
            }
            catch (Exception ex)
            {
                L("[CDRAG] 异常 " + ex);
            }
            finally
            {
                try { if (lp != IntPtr.Zero) Marshal.FreeHGlobal(lp); } catch { }
                try { Left = oldL; Top = oldT; } catch { }
                Flush();
                try { await Task.Delay(200); } catch { }
                Close();
            }
        }

        /// <summary>
        /// 倒计时「按住能拖」的**真实链路**自检（CUTLANCE_CDHIT_TEST=1，2026-10-25 补）。
        ///
        /// 为什么必须单独补这一条：
        ///   原来的 countdown 自检是「直接喂成品帧像素给 CountdownContains」+「直接调 DragCountdownTo」，
        ///   验的是模型层。而老板真拖时走的是另一条链：
        ///     屏幕物理像素 → 客户区 DIP → 画布局部 DIP → 成品帧像素 → CountdownContains
        ///   这条换算链一旦被布局改动带偏（边距、列宽、标题栏高度都会改画布位置），
        ///   自检照样 32/32 全过，老板却拖不动 —— 这就是「改完把有用的弄坏了」的那道缝。
        ///
        /// 钩子本身还要求「主窗口在前台」，离屏测不了；这里直接调 HitCountdownAt，绕开前台判定。
        /// </summary>
        private async void StartCdHitTest()
        {
            if (Environment.GetEnvironmentVariable("CUTLANCE_CDHIT_TEST") != "1") return;

            string dir = Environment.GetEnvironmentVariable("CUTLANCE_CDHIT_DIR") ?? "";
            if (string.IsNullOrWhiteSpace(dir)) dir = Path.Combine(Path.GetTempPath(), "cutlance-cdhit");
            try { Directory.CreateDirectory(dir); } catch { }

            var log = new List<string>();
            void L(string m) { lock (log) log.Add(m); }
            void Flush()
            {
                try
                {
                    System.IO.File.WriteAllLines(Path.Combine(dir, "cdhit.log"), log,
                        new System.Text.UTF8Encoding(false));
                }
                catch { }
            }

            try
            {
                await Task.Delay(2500);
                if (_monitor == null) { L("[CDHIT] 合成器没起来"); Flush(); Close(); return; }

                // 摆成「叠加开 + 正中 + 常用字号」
                _cdOverlayOn = true;
                _cdX = 0.5; _cdY = 0.5;
                _cdFontPx = 120;
                UpdateCountdownUi();
                await Task.Delay(900);

                var t = LayersCanvas.TransformToAncestor(this);
                double cw = LayersCanvas.ActualWidth, ch = LayersCanvas.ActualHeight;
                var center = t.Transform(new Point(cw / 2, ch / 2));
                var corner = t.Transform(new Point(2, 2));

                bool hitCenter = HitCountdownAt(center);
                bool hitCorner = HitCountdownAt(corner);
                var rect = _monitor.CountdownRect;
                L($"[CDHIT] 画布={cw:0}x{ch:0} dpi={DevicePixelPerDip():0.##}");
                L($"[CDHIT] 中心client=({center.X:0},{center.Y:0}) 角落client=({corner.X:0},{corner.Y:0})");
                L($"[CDHIT] 数字矩形=({rect.X},{rect.Y}) {rect.W}x{rect.H} 画布内可见={_monitor.CountdownVisible}");
                L($"[CDHIT] 命中中心={hitCenter} 命中角落={hitCorner}");

                // 关键：旧代码是「先判控件、再判倒计时」。如果画面中心被误判成"点在控件上"，
                // 按下去会直接放行，永远轮不到倒计时 —— 这正是"点了没反应"的典型形态。
                // 这里把命中链打出来，一眼能看出是哪个元素把它带偏的。
                var chain = new List<string>();
                try
                {
                    var vhit = VisualTreeHelper.HitTest(this, center);
                    var dv = vhit?.VisualHit;
                    for (int i = 0; i < 12 && dv != null; i++, dv = VisualTreeHelper.GetParent(dv))
                        chain.Add(dv.GetType().Name);
                }
                catch (Exception ex) { chain.Add("HitTest异常:" + ex.Message); }

                L($"[CDHIT] 中心命中链={string.Join(" > ", chain)}");
                L($"[CDHIT] IsOverInteractiveUi(中心)={IsOverInteractiveUi(center)}");
                L("[CDHIT] 结束");
            }
            catch (Exception ex)
            {
                L("[CDHIT] 异常 " + ex.Message);
            }
            finally
            {
                Flush();
                try { await Task.Delay(200); } catch { }
                Close();
            }
        }

        /// <summary>
        /// 单画面放大自检（CUTLANCE_ZOOM_TEST=1，2026-10-25）。
        ///
        /// 判据看的是**画布实际宽度**，不是列宽属性 —— 属性写成 0 但布局没重算，
        /// 画面一点没变大，这种"设了但没生效"本项目已经踩过好几次。
        /// </summary>
        private async void StartZoomTest()
        {
            if (Environment.GetEnvironmentVariable("CUTLANCE_ZOOM_TEST") != "1") return;

            string dir = Environment.GetEnvironmentVariable("CUTLANCE_ZOOM_DIR") ?? "";
            if (string.IsNullOrWhiteSpace(dir)) dir = Path.Combine(Path.GetTempPath(), "cutlance-zoom");
            try { Directory.CreateDirectory(dir); } catch { }

            var log = new List<string>();
            void L(string m) { lock (log) log.Add(m); }
            void Flush()
            {
                try
                {
                    System.IO.File.WriteAllLines(Path.Combine(dir, "zoom.log"), log,
                        new System.Text.UTF8Encoding(false));
                }
                catch { }
            }

            try
            {
                await Task.Delay(2500);      // 等窗口排版完成（不排完宽高是 0，判据全废）

                double w0 = LayersCanvas.ActualWidth, h0 = LayersCanvas.ActualHeight;
                double p0 = PreviewCell.ActualWidth;
                L($"[放大] 前 画布={w0:0}x{h0:0} 预览宽={p0:0}");

                ToggleProgramZoom();
                await Task.Delay(900);
                double w1 = LayersCanvas.ActualWidth, h1 = LayersCanvas.ActualHeight;
                L($"[放大] 后 画布={w1:0}x{h1:0} 预览宽={PreviewCell.ActualWidth:0} " +
                  $"输出可见={ProgramCell.Visibility} 预览列宽={PvwCol.Width.Value:0.##}");

                ToggleProgramZoom();
                await Task.Delay(900);
                double w2 = LayersCanvas.ActualWidth;
                L($"[放大] 还原 画布={w2:0} 预览宽={PreviewCell.ActualWidth:0} " +
                  $"预览列宽={PvwCol.Width.Value:0.##}");
                L("[放大] 结束");
            }
            catch (Exception ex)
            {
                L("[放大] 异常 " + ex.Message);
            }
            finally
            {
                Flush();
                try { await Task.Delay(200); } catch { }
                Close();
            }
        }

        /// <summary>
        /// 异形屏填充方式自检（CUTLANCE_FIT_TEST=1，2026-10-25）。
        ///
        /// 要证的三件事：
        ///   ① 合成器真按工程的 3840×1080 建（没有偷偷回落成 1920×1080）；
        ///   ② 「等比内接」在 32:9 画布上必然留两条大黑边（≈50%，16:9 源铺 32:9 格）；
        ///   ③ 「铺满裁切 / 拉伸铺满」把黑边吃掉（≈0%）。
        ///
        /// ⚠ 黑边占比必须从**成品帧**上量 —— 模型里 FitMode 是几，不代表画面真的变了
        ///   （这正是当初「描边只画在界面上、推流看不见」那类假成功的翻版）。
        /// </summary>
        private async void StartFitTest()
        {
            if (Environment.GetEnvironmentVariable("CUTLANCE_FIT_TEST") != "1") return;

            string dir = Environment.GetEnvironmentVariable("CUTLANCE_FIT_DIR") ?? "";
            if (string.IsNullOrWhiteSpace(dir)) dir = Path.Combine(Path.GetTempPath(), "cutlance-fit");
            try { Directory.CreateDirectory(dir); } catch { }

            var log = new List<string>();
            void L(string m) { lock (log) log.Add(m); }
            void Flush()
            {
                try
                {
                    System.IO.File.WriteAllLines(Path.Combine(dir, "fit.log"), log,
                        new System.Text.UTF8Encoding(false));
                }
                catch { }
            }

            try
            {
                // 等合成器出帧 + 输入解码出画面（最多 25 秒）
                for (int i = 0; i < 50; i++)
                {
                    await Task.Delay(500);
                    if (_monitor != null && Layers.Any(l => l.Source != null) && _monitor.Frames > 3) break;
                }

                if (_monitor == null) { L("[异形] 合成器没起来"); Flush(); Close(); return; }

                int cw = _monitor.Width, ch = _monitor.Height;
                L($"[异形] 画布={cw}x{ch} 帧数={_monitor.Frames}");

                var lay = Layers.FirstOrDefault(l => l.Source != null);
                if (lay == null) { L("[异形] 没有可用图层"); Flush(); Close(); return; }
                L($"[异形] 图层={lay.DisplayName} 位置=({lay.X:0.###},{lay.Y:0.###}) 缩放=({lay.ScaleX:0.###},{lay.ScaleY:0.###})");

                // 黑边占比：整帧按 6px 网格采样，亮度 < 60 记为黑
                double BlackRatio()
                {
                    long black = 0, total = 0;
                    _monitor.WithProgram((ptr, stride) =>
                    {
                        var row = new byte[stride];
                        for (int y = 0; y < _monitor.OutH; y += 6)
                        {
                            Marshal.Copy(IntPtr.Add(ptr, y * stride), row, 0, stride);
                            for (int x = 0; x < _monitor.OutW; x += 6)
                            {
                                int lum = row[x * 4] + row[x * 4 + 1] + row[x * 4 + 2];
                                if (lum < 60) black++;
                                total++;
                            }
                        }
                    });
                    return total == 0 ? 0 : (double)black / total;
                }

                foreach (var mode in new[] { 0, 1, 2 })
                {
                    lay.FitMode = mode;
                    RenderLayer(lay);
                    await Task.Delay(700);
                    double br = BlackRatio();
                    L($"[填充] mode={mode} 名称={lay.FitLabel} 黑边={br:P1}");
                }

                // 还原成出厂（等比内接），别把工程状态带歪
                lay.FitMode = 0;
                RenderLayer(lay);
                await Task.Delay(400);
                L("[填充] 已还原等比内接");
                L("[异形] 结束");
            }
            catch (Exception ex)
            {
                L("[异常] " + ex.Message);
            }
            finally
            {
                Flush();
                try { await Task.Delay(200); } catch { }
                Close();
            }
        }

        private async void StartCountdownTest()
        {
            if (Environment.GetEnvironmentVariable("CUTLANCE_CD_TEST") != "1") return;

            string dir = Environment.GetEnvironmentVariable("CUTLANCE_CD_DIR") ?? "";
            if (string.IsNullOrWhiteSpace(dir)) dir = Path.Combine(Path.GetTempPath(), "cutlance-cd");
            try { Directory.CreateDirectory(dir); } catch { }

            var log = new List<string>();
            void Log(string m)
            {
                log.Add($"{DateTime.Now:HH:mm:ss.fff}\t{m}");
                try { ErrorLog.Info("[倒计时自检] " + m); } catch { }
            }
            void Flush()
            {
                try
                {
                    System.IO.File.WriteAllLines(Path.Combine(dir, "cd.log"), log,
                        new System.Text.UTF8Encoding(false));
                }
                catch { }
            }
            void Check(bool ok, string name, string detail) => Log($"{(ok ? "PASS" : "FAIL")}\t{name}\t{detail}");

            try
            {
                await Task.Delay(1200);

                // ── ① 设定值的解析（老板「自由输入设定倒计时时间」）──
                Check(CountdownClock.TryParseDuration("1:00:00", out var h) && Math.Abs(h - 3600) < 0.01,
                      "① 输入 1:00:00 = 一小时", $"{h:0.##} 秒");
                Check(CountdownClock.TryParseDuration("30:00", out var m) && Math.Abs(m - 1800) < 0.01,
                      "① 输入 30:00 = 半小时", $"{m:0.##} 秒");
                Check(CountdownClock.TryParseDuration("90", out var mn) && Math.Abs(mn - 5400) < 0.01,
                      "① 输入 90（裸数字）= 90 分钟", $"{mn:0.##} 秒");
                Check(CountdownClock.TryParseDuration("45s", out var s45) && Math.Abs(s45 - 45) < 0.01,
                      "① 输入 45s = 45 秒", $"{s45:0.##} 秒");
                Check(!CountdownClock.TryParseDuration("abc", out _) && !CountdownClock.TryParseDuration("-5", out _),
                      "① 乱码/负数判非法（不会静默改成 0）", "abc / -5 → 拒绝");
                Check(CountdownClock.Format(3600) == "1:00:00", "① 一小时显示成 1:00:00", CountdownClock.Format(3600));
                Check(CountdownClock.Format(-0.4) == "-00:01" && CountdownClock.Format(-3.4) == "-00:04",
                      "① 超时显示进一（-0.4s 显示 -00:01，不再有 -00:00）",
                      $"-0.4s = {CountdownClock.Format(-0.4)} / -3.4s = {CountdownClock.Format(-3.4)}");

                // ── ② 一小时显示 + 设定 3 秒（后面用短时长验超时）──
                _cdClock.Set(3600);
                Check(_cdClock.Text == "1:00:00", "② 设定一小时 → 右栏大字 1:00:00", _cdClock.Text);

                _cdClock.Set(3);
                await Task.Delay(150);
                Check(_cdClock.Text == "00:03", "② 设定 3 秒 → 右栏大字跟着变", _cdClock.Text);

                // ── ③ 开始后读数在减 ──
                _cdClock.Start();
                await Task.Delay(1200);
                Check(_cdClock.IsRunning && _cdClock.Remaining < 2.4 && _cdClock.Remaining > 0.8,
                      "③ 点开始后读数确实在减少", $"{_cdClock.Remaining:0.00} 秒");

                // ── ④ 过了 0 继续走负数，且不自动停 ──
                await Task.Delay(2600);
                Check(_cdClock.IsRunning && _cdClock.IsNegative && _cdClock.Text.StartsWith("-"),
                      "④ 归零后继续走负数（不自动停、不带动作）", $"{_cdClock.Text} running={_cdClock.IsRunning}");

                // ── ⑤ 点停止 → 读数冻住 ──
                _cdClock.Stop();
                string frozen = _cdClock.Text;
                await Task.Delay(1200);
                Check(!_cdClock.IsRunning && _cdClock.Text == frozen,
                      "⑤ 点停止 → 停在当前读数（负数也停住）", frozen);

                // ── ⑥ 恢复 → 回到设定值 ──
                _cdClock.Restore();
                await Task.Delay(150);
                Check(_cdClock.Text == "00:03" && !_cdClock.IsRunning,
                      "⑥ 点恢复 → 回到设定的那个时间", _cdClock.Text);

                // ── ⑦ 叠加进输出画面（成品帧）──
                if (_monitor == null)
                {
                    Check(false, "⑦ 叠加进输出画面", "合成器没起来（_monitor == null），这条验不了");
                }
                else
                {
                    // 直接从**成品帧**取样（推流/录制/NDI/全屏吃的就是这一帧）。
                    // 只验「Layer.Visible = true」是不够的：层挂了不等于真混进去了。
                    double Sample()
                    {
                        double sum = 0;
                        _monitor.WithProgram((ptr, stride) =>
                        {
                            int x0 = Math.Max(0, _monitor.OutW / 2 - 150);
                            int y0 = Math.Max(0, _monitor.OutH / 2 - 40);
                            int w = Math.Min(300, _monitor.OutW - x0);
                            int h = Math.Min(80, _monitor.OutH - y0);
                            var row = new byte[stride];
                            for (int y = y0; y < y0 + h; y++)
                            {
                                Marshal.Copy(IntPtr.Add(ptr, y * stride), row, 0, stride);
                                for (int x = x0; x < x0 + w; x++) sum += row[x * 4];
                            }
                        });
                        return sum;
                    }
                    double zonePx = 300.0 * 80.0 * 255.0;

                    int basePx = 120;
                    double before = Sample();
                    _cdOverlayOn = true;
                    if (CdFontSlider != null) CdFontSlider.Value = basePx;
                    UpdateCountdownUi();
                    await Task.Delay(500);
                    Check(_monitor.CountdownVisible, "⑦ 点叠加 → 合成器里有这一层", $"Visible={_monitor.CountdownVisible}");
                    int pxSmall = _monitor.CountdownPixels;
                    Check(pxSmall > 2000, "⑦ 叠加层真的画出了字（不透明像素数）", $"{pxSmall} 像素");

                    double after = Sample();
                    double mad = Math.Abs(after - before) / zonePx;
                    Check(mad > 0.02, "⑦ 成品帧正中真的多了倒计时（推流/录制那一帧）",
                          $"中心区域平均差 {mad:P1}（阈值 2%）");

                    // ⑧ 字号放大 → 叠上去的字变大
                    _cdFontPx = 260;
                    if (CdFontSlider != null) CdFontSlider.Value = 260;
                    UpdateCountdownUi();
                    await Task.Delay(500);
                    int pxBig = _monitor.CountdownPixels;
                    Check(pxBig > pxSmall * 1.8, "⑧ 字号 120→260 后叠加字明显变大", $"{pxSmall} → {pxBig} 像素");

                    // ⑨ 关掉叠加 → 层不再出画、成品帧回到原样
                    _cdOverlayOn = false;
                    UpdateCountdownUi();
                    await Task.Delay(500);
                    Check(!_monitor.CountdownVisible, "⑨ 再点一次叠加 → 从输出画面消失", $"Visible={_monitor.CountdownVisible}");
                    double back = Sample();
                    double madBack = Math.Abs(back - before) / zonePx;
                    Check(madBack < 0.01, "⑨ 关掉后成品帧恢复原样（不是留了块残影）",
                          $"中心区域平均差 {madBack:P1}（阈值 1%）");

                    // ══ ⑩–⑬ 挪动位置（2026-10-24 老板「倒计时数字还要可以挪动位置」）══
                    _cdOverlayOn = true;
                    _cdFontPx = 120;
                    if (CdFontSlider != null) CdFontSlider.Value = 120;
                    UpdateCountdownUi();
                    await Task.Delay(300);

                    _monitor.SetCountdownPosition(0.2, 0.28);
                    _cdX = 0.2; _cdY = 0.28;
                    await Task.Delay(200);
                    var (rx, ry, rw, rh) = _monitor.CountdownRect;
                    double cxPx = rx + rw / 2.0, cyPx = ry + rh / 2.0;
                    double wantX = 0.2 * _monitor.OutW, wantY = 0.28 * _monitor.OutH;
                    Check(Math.Abs(cxPx - wantX) <= 3 && Math.Abs(cyPx - wantY) <= 3,
                          "⑩ 数字挪到 (20%, 28%) → 位置真变了",
                          $"中心 ({cxPx:0},{cyPx:0}) 期望 ({wantX:0},{wantY:0})");

                    Check(_monitor.CountdownContains((int)cxPx, (int)cyPx),
                          "⑩ 点在数字上 → 命中（按住能拖）",
                          $"点 ({cxPx:0},{cyPx:0}) 在 {rw}×{rh} 的块内");
                    Check(!_monitor.CountdownContains(4, 4),
                          "⑩ 点在画面角落 → 不命中（不会抢图层拖动）",
                          "左上角 (4,4) → false");

                    // 与鼠标钩子**同一段代码**：按下 (0,0) 后拖到 (+25%, +10%)
                    _cdDragStart = new Point(0, 0);
                    _cdDragNx0 = _cdX; _cdDragNy0 = _cdY;
                    DragCountdownTo(new Point(LayersCanvas.ActualWidth * 0.25,
                                              LayersCanvas.ActualHeight * 0.10));
                    var (nx1, ny1) = _monitor.CountdownPos;
                    Check(Math.Abs(nx1 - 0.45) < 0.015 && Math.Abs(ny1 - 0.38) < 0.015,
                          "⑪ 拖 25%/10% → 数字跟着走到 (45%, 38%)",
                          $"实际 ({nx1:0.###}, {ny1:0.###})");

                    // 拖到画面外 → 整块仍要留在画布内（不许拖丢）
                    DragCountdownTo(new Point(LayersCanvas.ActualWidth * 5, LayersCanvas.ActualHeight * 5));
                    var (rx2, ry2, rw2, rh2) = _monitor.CountdownRect;
                    Check(rx2 >= -1 && ry2 >= -1 && rx2 + rw2 <= _monitor.OutW + 1 && ry2 + rh2 <= _monitor.OutH + 1,
                          "⑫ 拖出画面 → 被夹在画布内（不会拖丢）",
                          $"矩形 ({rx2},{ry2}) {rw2}×{rh2} / 画布 {_monitor.OutW}×{_monitor.OutH}");

                    // 「居中」一键回正中
                    CdCenter_Click(this, new RoutedEventArgs());
                    await Task.Delay(200);
                    var (nx2, ny2) = _monitor.CountdownPos;
                    Check(Math.Abs(nx2 - 0.5) < 0.02 && Math.Abs(ny2 - 0.5) < 0.02,
                          "⑬ 点「居中」→ 回到画面正中", $"({nx2:0.###}, {ny2:0.###})");

                    // ══ ⑭–⑯ 字体 / 颜色自定义（2026-10-24 老板「做字体/颜色自定义」）══
                    int v0 = _monitor.CountdownRenderVersion;
                    string fontPick = GetSystemFonts().FirstOrDefault(n => n.Contains("宋"))
                                      ?? GetSystemFonts().FirstOrDefault(n =>
                                             !string.Equals(n, "Consolas", StringComparison.OrdinalIgnoreCase))
                                      ?? "Consolas";
                    _cdStyle.FontFamilyName = fontPick;
                    UpdateCountdownUi();
                    await Task.Delay(300);
                    Check(_monitor.CountdownRenderVersion > v0 && _monitor.CountdownFontFamily == fontPick,
                          "⑭ 换字体 → 真的重渲染，且用的就是选的那个字体",
                          $"{fontPick}（重渲染 {v0} → {_monitor.CountdownRenderVersion}）");

                    // 内容没变时不该反复重画（10Hz 刷新只在文字/样式变了才重渲染）
                    int vIdle = _monitor.CountdownRenderVersion;
                    await Task.Delay(1100);
                    Check(_monitor.CountdownRenderVersion == vIdle,
                          "⑭ 内容没变 → 不重复重渲染（1.1 秒内版本号不涨）",
                          $"{vIdle} → {_monitor.CountdownRenderVersion}");

                    // 字色：白 → 黄，位图里的文字真的变黄（不是只改了面板）
                    var rgbWhite = _monitor.CountdownTextRgb;
                    _cdStyle.TextColorHex = "#FFFFEB3B";
                    UpdateCountdownUi();
                    await Task.Delay(300);
                    var rgbYellow = _monitor.CountdownTextRgb;
                    Check(rgbYellow.B < 120 && rgbYellow.R > 200 && rgbYellow.G > 150
                          && rgbWhite.B > rgbYellow.B + 60,
                          "⑮ 字色改黄 → 位图里文字真的变黄",
                          $"白 ({rgbWhite.R},{rgbWhite.G},{rgbWhite.B}) → 黄 ({rgbYellow.R},{rgbYellow.G},{rgbYellow.B})");

                    // 底板：去掉底板 → 实心像素大幅减少（只剩字）
                    int pxWithBack = _monitor.CountdownPixels;
                    _cdStyle.BackColorHex = "#00000000";
                    UpdateCountdownUi();
                    await Task.Delay(300);
                    int pxNoBack = _monitor.CountdownPixels;
                    Check(pxNoBack < pxWithBack * 0.75,
                          "⑯ 关掉底板 → 只剩字（不透明像素明显变少）",
                          $"{pxWithBack} → {pxNoBack} 像素");

                    // 粗体 / 斜体开关也要生效（关掉粗体后块宽通常会变，这里只看是否重渲染）
                    int vBold = _monitor.CountdownRenderVersion;
                    _cdStyle.Bold = false;
                    _cdStyle.Italic = true;
                    UpdateCountdownUi();
                    await Task.Delay(300);
                    Check(_monitor.CountdownRenderVersion > vBold,
                          "⑯ 粗体/斜体开关 → 生效（重渲染）",
                          $"粗体关、斜体开（重渲染 {vBold} → {_monitor.CountdownRenderVersion}）");
                }

                // ── ⑰ 进工程文件（下次打开直接回到上次设置）──
                _cdOverlayOn = true;
                _cdFontPx = 150;
                _cdX = 0.34; _cdY = 0.71;      // 拖过的位置也要存
                _cdStyle.FontFamilyName = "微软雅黑";
                _cdStyle.Bold = false;
                _cdStyle.Italic = true;
                _cdStyle.TextColorHex = "#FFFFEB3B";
                _cdStyle.BackColorHex = "#A6000000";
                _cdClock.Set(600);
                var pf = CollectProject();
                Check(pf.Clock != null && Math.Abs(pf.Clock.SetSeconds - 600) < 0.01
                      && Math.Abs(pf.Clock.FontSize - 150) < 0.01 && pf.Clock.OverlayOn
                      && Math.Abs(pf.Clock.X - 0.34) < 0.001 && Math.Abs(pf.Clock.Y - 0.71) < 0.001,
                      "⑰ 工程里存下了设定值 / 字号 / 叠加开关 / 数字位置",
                      pf.Clock == null ? "Clock = null"
                          : $"{pf.Clock.SetSeconds:0}s / 字号 {pf.Clock.FontSize:0} / 叠加 {pf.Clock.OverlayOn}" +
                            $" / 位置 ({pf.Clock.X:0.##}, {pf.Clock.Y:0.##})");
                Check(pf.Clock != null && pf.Clock.FontFamily == "微软雅黑"
                      && pf.Clock.Bold == false && pf.Clock.Italic == true
                      && pf.Clock.TextColor == "#FFFFEB3B" && pf.Clock.BackColor == "#A6000000",
                      "⑰ 工程里也存下了字体 / 粗斜体 / 颜色 / 底板",
                      pf.Clock == null ? "Clock = null"
                          : $"{pf.Clock.FontFamily} / 粗 {pf.Clock.Bold} 斜 {pf.Clock.Italic}" +
                            $" / 字色 {pf.Clock.TextColor} / 底板 {pf.Clock.BackColor}");

                Log("倒计时牌自检结束");
            }
            catch (Exception ex)
            {
                Log("EXCEPTION\t" + ex);
            }
            finally
            {
                Flush();
                if (Environment.GetEnvironmentVariable("CUTLANCE_CD_EXIT") == "1")
                {
                    try { await Task.Delay(400); } catch { }
                    try { Close(); } catch { }
                }
            }
        }

        /// <summary>
        /// PPT「抓放映窗」端到端自检（CUTLANCE_PPT_TEST=1，2026-10-24）。
        ///
        /// 验证的是老板选的这条路真的能用，而不是只看代码：
        ///   ① 能启动全屏放映并**按类名 screenClass 找到放映窗口**（拿到屏幕矩形）；
        ///   ② 桌面区域捕获抓到的确实是放映画面（取样偏红 = 第一页）；
        ///   ③ 投递 PageDown 能翻页（取样变偏蓝 = 第二页）；
        ///   ④ 收工时 Esc 能退出放映。
        ///
        /// ⚠ 这条**会真全屏挡屏约 6–8 秒**（放映必须可见，抓的是屏幕像素）。
        ///   只在老板明确说"可以跑"时才执行；跑完自动退出放映，不会留 PowerPoint 窗口。
        /// </summary>
        private async void StartPptSelfTest()
        {
            if (Environment.GetEnvironmentVariable("CUTLANCE_PPT_TEST") != "1") return;

            string file = Environment.GetEnvironmentVariable("CUTLANCE_PPT_FILE") ?? "";
            string dir = Environment.GetEnvironmentVariable("CUTLANCE_PPT_DIR") ?? "";
            if (string.IsNullOrWhiteSpace(dir)) dir = Path.Combine(Path.GetTempPath(), "cutlance-ppt");
            try { Directory.CreateDirectory(dir); } catch { }

            var log = new List<string>();
            void Log(string m)
            {
                log.Add($"{DateTime.Now:HH:mm:ss.fff}\t{m}");
                try { ErrorLog.Info("[PPT自检] " + m); } catch { }
            }
            void Flush()
            {
                try
                {
                    System.IO.File.WriteAllLines(Path.Combine(dir, "ppt.log"), log,
                        new System.Text.UTF8Encoding(false));
                }
                catch { }
            }
            void Check(bool ok, string name, string detail) => Log($"{(ok ? "PASS" : "FAIL")}\t{name}\t{detail}");

            // 成品帧中心取样（BGRA → 平均 R/G/B）
            (double R, double G, double B) SampleCenter()
            {
                double sr = 0, sg = 0, sb = 0; long n = 0;
                _monitor?.WithProgram((ptr, stride) =>
                {
                    int bw = 240, bh = 120;
                    int x0 = Math.Max(0, _monitor.OutW / 2 - bw / 2);
                    int y0 = Math.Max(0, _monitor.OutH / 2 - bh / 2);
                    var row = new byte[stride];
                    for (int y = y0; y < y0 + bh; y++)
                    {
                        Marshal.Copy(IntPtr.Add(ptr, y * stride), row, 0, stride);
                        for (int x = x0; x < x0 + bw; x++)
                        {
                            sb += row[x * 4]; sg += row[x * 4 + 1]; sr += row[x * 4 + 2];
                            n++;
                        }
                    }
                });
                return n == 0 ? (0, 0, 0) : (sr / n, sg / n, sb / n);
            }

            InputSource? src = null;
            try
            {
                await Task.Delay(1200);

                if (!File.Exists(file)) { Check(false, "① 测试 PPT 文件存在", file); return; }

                Log("开始启动放映（接下来约 6–8 秒会全屏挡住屏幕）");
                await ImportPptAsCapture(file, "");
                await Task.Delay(600);

                src = Inputs.FirstOrDefault(i => string.Equals(i.OfficeSourcePath, file, StringComparison.OrdinalIgnoreCase));
                _pptSessions.TryGetValue(src!, out var sess);

                Check(src != null && sess != null && sess.Hwnd != IntPtr.Zero,
                      "① 找到放映窗口并建成桌面捕获输入",
                      sess == null ? "没有 session" : $"Hwnd=0x{sess.Hwnd.ToInt64():X}");

                if (sess != null)
                {
                    Check(sess.Width >= 1280 && sess.Height >= 720,
                          "② 放映窗尺寸合理（≥1280×720）",
                          $"{sess.Width}×{sess.Height} @ ({sess.Left},{sess.Top})");

                    // 推上输出，成品帧里才有放映画面
                    SetPreview(src!);
                    TakeToLayer(TransitionType.Cut);
                    await Task.Delay(1800);

                    var c1 = SampleCenter();
                    Check(c1.R > 110 && c1.R > c1.B + 50,
                          "③ 成品帧抓到的就是放映画面（第一页偏红）",
                          $"中心平均 RGB=({c1.R:0},{c1.G:0},{c1.B:0})");

                    sess.Next();                      // 投递 PageDown（不改焦点）
                    await Task.Delay(1500);
                    var c2 = SampleCenter();
                    Check(c2.B > 110 && c2.B > c2.R + 50,
                          "④ 投递 PageDown 能翻到第二页（偏蓝）",
                          $"中心平均 RGB=({c2.R:0},{c2.G:0},{c2.B:0})");
                }
            }
            catch (Exception ex)
            {
                Log("EXCEPTION\t" + ex);
            }
            finally
            {
                // 收工：只退我们自己开的那个放映（Close 内部会先 Esc、再判断进程归属）
                try { if (src != null) ClosePptSession(src); } catch { }
                Log("PPT 自检结束（放映已退出）");
                Flush();

                if (Environment.GetEnvironmentVariable("CUTLANCE_PPT_EXIT") == "1")
                {
                    try { await Task.Delay(500); } catch { }
                    try { Close(); } catch { }
                }
            }
        }

        /// <summary>
        /// 工程文件「存 → 重启 → 还原」往返自检（CUTLANCE_PROJ_RT=save|load，2026-10-23）。
        ///
        /// 老板的要求：不管开始怎么摆（多选一起进预览 / 每格主次音 / 音量 / 压缩档），
        /// 保存工程以后重新打开，必须**直接回到保存时那套设置**。
        /// 所以分两个进程跑（真·重开程序，不是同一个实例内存里自娱自乐）：
        ///   · save：摆一套"非默认"的版面 → 存工程 → 另存一份期望值 expect.json → 退出
        ///   · load：打开工程 → 逐项比对 + 全字段深度对比 → 报 PASS/FAIL
        /// 判据看 projrt.log；退出码由外部脚本判（EXIT=1 关窗）。
        /// </summary>
        private async void StartProjectRoundTripTest()
        {
            string stage = Environment.GetEnvironmentVariable("CUTLANCE_PROJ_RT") ?? "";
            if (stage != "save" && stage != "load") return;

            string dir = Environment.GetEnvironmentVariable("CUTLANCE_RT_DIR") ?? "";
            if (string.IsNullOrWhiteSpace(dir)) dir = Path.Combine(Path.GetTempPath(), "cutlance-projrt");
            try { Directory.CreateDirectory(dir); } catch { }
            string projPath = Path.Combine(dir, "rt.cutlance");
            string expectPath = Path.Combine(dir, "expect.json");

            var log = new List<string>();
            void LogLine(string m)
            {
                log.Add($"{DateTime.Now:HH:mm:ss.fff}\t{m}");
                try { ErrorLog.Info("[工程往返] " + m); } catch { }
            }
            void Check(bool ok, string name, string detail) =>
                LogLine($"{(ok ? "PASS" : "FAIL")}\t{name}\t{detail}");
            void Flush()
            {
                try
                {
                    // 两个阶段各写各的日志（2026-10-23）：同名文件会被后跑的 load 覆盖，
                    // save 阶段的判定（比如「重复推是否沿用主次」）就再也看不到了。
                    System.IO.File.WriteAllLines(Path.Combine(dir, $"projrt-{stage}.log"), log,
                        new System.Text.UTF8Encoding(false));
                }
                catch { }
            }

            var jsonOpt = new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            try
            {
                await Task.Delay(1600);
                LogLine($"阶段={stage} 目录={dir}");

                if (stage == "save")
                {
                    var srcDir = Path.Combine(dir, "src");
                    var pics = Directory.Exists(srcDir)
                        ? Directory.GetFiles(srcDir, "*.png").OrderBy(x => x, StringComparer.Ordinal).Take(4).ToList()
                        : new List<string>();
                    if (pics.Count < 4) { Check(false, "⓪ 素材准备", $"只有 {pics.Count} 张，需 4 张"); Flush(); return; }
                    foreach (var p in pics)
                        AddInput(new InputSource
                        {
                            Type = SourceType.File,
                            Name = Path.GetFileNameWithoutExtension(p),
                            Location = p
                        });
                    LogLine($"输入数={Inputs.Count}");

                    // ① 多选 4 路一起进预览
                    LoadInputsIntoPreview(Inputs.Take(4).ToList());
                    await Task.Delay(900);

                    // ② 故意改成「非自动分屏」的样子：第 2 路挪位 + 宽高分离，第 1 路加描边。
                    //    这样如果还原时是重新自动分屏一次，必然对不上 —— 只有真按记录还原才过。
                    if (_previewSlots.Count >= 2)
                    {
                        _previewSlots[1].X = 0.53;
                        _previewSlots[1].Y = 0.04;
                        _previewSlots[1].ScaleX = 0.43;
                        _previewSlots[1].ScaleY = 0.55;
                        _previewSlots[1].AspectLock = false;
                    }
                    _previewSlots[0].BorderThickness = 6;
                    _previewSlots[0].BorderRadius = 18;
                    _previewSlots[0].BorderColor = "#FFFF8800";
                    RenderPreview();
                    await Task.Delay(400);

                    // ③ 切到输出框（4 格）
                    BtnPreviewToProgram_Click(this, new RoutedEventArgs());
                    await Task.Delay(1500);

                    // ④ 主次音 + 每格音量（刻意做成 4 种不同组合）
                    var roles = new[] { AudioRole.Main, AudioRole.Secondary, AudioRole.Background, AudioRole.Secondary };
                    var vols = new[] { 100, 130, 70, 90 };
                    for (int i = 0; i < Layers.Count && i < roles.Length; i++)
                    {
                        Layers[i].IsAudioSource = true;
                        Layers[i].AudioRole = roles[i];
                        Layers[i].Volume = vols[i];
                    }
                    ApplyAudioRouting();
                    await Task.Delay(400);

                    // ④b 同一批素材**再推一次**（老板报的「重复推会重置主次」）→ 必须沿用上次
                    var before = Layers.Select(l => (Src: l.Source, l.AudioRole, l.Volume)).ToList();
                    BtnPreviewToProgram_Click(this, new RoutedEventArgs());
                    await Task.Delay(1500);
                    bool keep = before.Count == Layers.Count && before.All(x =>
                    {
                        var l2 = Layers.FirstOrDefault(y => ReferenceEquals(y.Source, x.Src));
                        return l2 != null && l2.AudioRole == x.AudioRole && l2.Volume == x.Volume;
                    });
                    Check(keep, "④b 重复推同一批素材 → 主次与音量沿用上次（不被打回默认）",
                          string.Join(",", Layers.Select(l => $"{l.DisplayName}:{l.RoleLabel}/{l.Volume}%")));

                    // ⑤ 输出总线压缩
                    var cmp = StreamService.Instance.Compressor;
                    cmp.Level = CompressorLevel.Medium;
                    cmp.ThresholdDb = -20;
                    cmp.Ratio = 3.5;
                    cmp.MakeupDb = 2.5;
                    LogLine($"压缩档={cmp.Level} th={cmp.ThresholdDb} ratio={cmp.Ratio} makeup={cmp.MakeupDb}");

                    // ⑤b 界面状态（老板「都存入」）：把右栏放到最大 + 选中第 2 格，然后存工程
                    SetLayersMaximized(true, announce: false);
                    if (Layers.Count > 1)
                    {
                        _selLayer = Layers[1];
                        LayerList.SelectedItem = Layers[1];
                    }
                    LogLine($"存前：折叠={_layersExpanded} 选中={( _selLayer == null ? -1 : Layers.IndexOf(_selLayer))}");

                    // ⑤b2 描边 / 阴影（2026-10-25 老板「图层边框/阴影」）：
                    //   给第 2 格设一套非默认值，重开后必须原样回来（老工程缺字段 → 无阴影）
                    if (Layers.Count > 1)
                    {
                        var lB = Layers[1];
                        lB.BorderThickness = 22;
                        lB.BorderRadius = 14;
                        lB.BorderColor = "#FFFFD24A";
                        lB.Shadow = 34;
                        lB.ShadowOpacity = 77;
                    }
                    LogLine("存前：描边阴影 " + string.Join(",", Layers.Select((l, i) =>
                        $"{i}:[{l.BorderThickness:0}/{l.BorderRadius:0}/{l.BorderColor}/{l.Shadow:0}px/{l.ShadowOpacity}%]")));

                    // ⑤c 倒计时牌（2026-10-24）：设定 10 分钟、字号 180、叠加开、数字拖到 (30%, 65%)
                    _cdClock.Set(600);
                    _cdFontPx = 180;
                    _cdOverlayOn = true;
                    _cdX = 0.30; _cdY = 0.65;
                    _cdStyle.FontFamilyName = "微软雅黑";
                    _cdStyle.Bold = false;
                    _cdStyle.Italic = true;
                    _cdStyle.TextColorHex = "#FFFFEB3B";
                    _cdStyle.BackColorHex = "#A6000000";
                    UpdateCountdownUi();
                    LogLine($"存前：倒计时 设定={_cdClock.SetText} 字号={_cdFontPx:0} 叠加={_cdOverlayOn} " +
                            $"位置=({_cdX:0.##},{_cdY:0.##}) 字体={_cdStyle.FontFamilyName} " +
                            $"粗{_cdStyle.Bold}/斜{_cdStyle.Italic} 字色={_cdStyle.TextColorHex} 底板={_cdStyle.BackColorHex}");

                    // ⑥ 存工程
                    var snap = CollectProject();
                    bool ok = ProjectService.Save(snap, projPath, out string err);
                    Check(ok, "① 保存工程", ok ? $"{projPath}（{new FileInfo(projPath).Length} 字节）" : err);
                    if (!ok) { Flush(); return; }
                    _projectPath = projPath;
                    UpdateTitle();

                    // 期望值 = 保存完那一刻再收集一遍（与写盘内容同源）
                    System.IO.File.WriteAllText(expectPath,
                        System.Text.Json.JsonSerializer.Serialize(CollectProject(), jsonOpt),
                        new System.Text.UTF8Encoding(false));
                    Check((snap.Preview?.Count ?? 0) == 4, "② 工程里存下了预览框 4 路",
                          $"Preview={snap.Preview?.Count ?? 0}");
                    Check(snap.Layers.Count == 4, "③ 工程里存下了输出 4 格", $"Layers={snap.Layers.Count}");
                    Check(File.Exists(expectPath), "④ 期望值已落盘", expectPath);
                    LogLine("保存阶段结束");
                }
                else
                {
                    // ── load 阶段：真·重开程序之后再打开工程 ──
                    Check(File.Exists(projPath), "⓪ 工程文件存在", projPath);
                    if (!File.Exists(projPath)) { Flush(); return; }

                    LoadProjectFrom(projPath);
                    await Task.Delay(2500);

                    // ① 预览框：路数 + 几何 + 描边
                    Check(_previewSlots.Count == 4, "① 重开 → 预览框回到 4 路",
                          $"预览路数={_previewSlots.Count}");
                    if (_previewSlots.Count >= 2)
                    {
                        var s1 = _previewSlots[1];
                        bool geo = Math.Abs(s1.X - 0.53) < 1e-6 && Math.Abs(s1.Y - 0.04) < 1e-6
                                   && Math.Abs(s1.ScaleX - 0.43) < 1e-6 && Math.Abs(s1.ScaleY - 0.55) < 1e-6
                                   && !s1.AspectLock;
                        Check(geo, "② 第 2 路的挪位与宽高分离（非自动分屏）原样还原",
                              $"X={s1.X:0.###} Y={s1.Y:0.###} SX={s1.ScaleX:0.###} SY={s1.ScaleY:0.###} 锁比例={s1.AspectLock}");
                    }
                    else Check(false, "② 第 2 路缺失", $"只有 {_previewSlots.Count} 路");
                    if (_previewSlots.Count >= 1)
                    {
                        var s0 = _previewSlots[0];
                        Check(Math.Abs(s0.BorderThickness - 6) < 1e-6 && Math.Abs(s0.BorderRadius - 18) < 1e-6
                              && s0.BorderColor.Equals("#FFFF8800", StringComparison.OrdinalIgnoreCase),
                              "③ 第 1 路描边（6pt / 圆角 18 / 橙色）原样还原",
                              $"{s0.BorderThickness}pt / {s0.BorderRadius} / {s0.BorderColor}");
                    }

                    // ② 输出框：格数 + 每格绑的输入 + 角色档 + 音量
                    Check(Layers.Count == 4, "④ 重开 → 输出框回到 4 格", $"输出格数={Layers.Count}");
                    var rolesWant = new[] { AudioRole.Main, AudioRole.Secondary, AudioRole.Background, AudioRole.Secondary };
                    var volsWant = new[] { 100, 130, 70, 90 };
                    bool roleOk = true, volOk = true, srcOk = true;
                    var roleGot = new List<string>();
                    for (int i = 0; i < Layers.Count && i < rolesWant.Length; i++)
                    {
                        if (Layers[i].AudioRole != rolesWant[i]) roleOk = false;
                        if (Layers[i].Volume != volsWant[i]) volOk = false;
                        if (_previewSlots.Count > i && !ReferenceEquals(Layers[i].Source, _previewSlots[i].Source)) srcOk = false;
                        roleGot.Add($"{Layers[i].AudioRole}/{Layers[i].Volume}%");
                    }
                    Check(srcOk, "⑤ 每一格绑的还是原来那一路画面", string.Join(",", Layers.Select(l => l.DisplayName)));
                    Check(roleOk, "⑥ 每格的主次角色档原样还原", string.Join(",", roleGot));
                    Check(volOk, "⑦ 每格的音量原样还原", string.Join(",", Layers.Select(l => l.Volume + "%")));

                    // ③ 角色档不只是显示对，输出轨道上要真生效
                    if (Layers.Count >= 2)
                    {
                        var t1 = _monitor?.TrackFor(Layers[1].Source);
                        double want = 1.30 * Layer.GainOf(AudioRole.Secondary);   // 130% × 次音 −8dB
                        Check(t1 != null && !t1.Mute && Math.Abs(t1.Volume - want) < 0.03,
                              "⑧ 还原后声音真按「130% × 次音」出（不是只改界面）",
                              t1 == null ? "无轨道" : $"轨道Vol={t1.Volume:0.000} 期望={want:0.000}");
                    }

                    // ④ 压缩档
                    var cm = StreamService.Instance.Compressor;
                    Check(cm.Level == CompressorLevel.Medium
                          && Math.Abs(cm.ThresholdDb + 20) < 1e-6
                          && Math.Abs(cm.Ratio - 3.5) < 1e-6
                          && Math.Abs(cm.MakeupDb - 2.5) < 1e-6,
                          "⑨ 输出总线压缩档原样还原",
                          $"{cm.Level} th={cm.ThresholdDb} ratio={cm.Ratio} makeup={cm.MakeupDb}");

                    // ⑤ 界面状态（老板「都存入」）
                    Check(_layersExpanded, "⑪ 右栏「图层区放到最大」状态还原",
                          $"_layersExpanded={_layersExpanded}（存前 true）");
                    Check(Layers.Count > 1 && ReferenceEquals(_selLayer, Layers[1]),
                          "⑫ 选中的还是第 2 格（换格/精调都指着它）",
                          _selLayer == null ? "没有选中" : $"第 {Layers.IndexOf(_selLayer) + 1} 格");

                    // ⑫b 倒计时牌：设定值 / 字号 / 叠加开关 / 数字位置（老板 10-24 加的「要能挪位置」）
                    Check(Math.Abs(_cdClock.SetSeconds - 600) < 0.01
                          && Math.Abs(_cdFontPx - 180) < 0.01
                          && _cdOverlayOn
                          && Math.Abs(_cdX - 0.30) < 0.003 && Math.Abs(_cdY - 0.65) < 0.003,
                          "⑫b 倒计时牌（设定 / 字号 / 叠加 / 位置）原样还原",
                          $"{_cdClock.SetText} / 字号 {_cdFontPx:0} / 叠加 {_cdOverlayOn} / 位置 ({_cdX:0.###}, {_cdY:0.###})");

                    // ⑫d 描边 / 阴影（2026-10-25）：第 2 格那套非默认值必须原样回来
                    var lBack = Layers.Count > 1 ? Layers[1] : null;
                    Check(lBack != null
                          && Math.Abs(lBack.BorderThickness - 22) < 1e-6
                          && Math.Abs(lBack.BorderRadius - 14) < 1e-6
                          && lBack.BorderColor == "#FFFFD24A"
                          && Math.Abs(lBack.Shadow - 34) < 1e-6
                          && lBack.ShadowOpacity == 77,
                          "⑫d 描边 / 阴影原样还原（粗细 / 圆角 / 颜色 / 扩散 / 浓淡）",
                          lBack == null ? "没有第 2 格"
                              : $"{lBack.BorderThickness:0}/{lBack.BorderRadius:0}/{lBack.BorderColor}/{lBack.Shadow:0}px/{lBack.ShadowOpacity}%");

                    // ⑫c 字体 / 颜色（老板 10-24 加的「字体/颜色自定义」）
                    Check(_cdStyle.FontFamilyName == "微软雅黑"
                          && _cdStyle.Bold == false && _cdStyle.Italic == true
                          && _cdStyle.TextColorHex == "#FFFFEB3B" && _cdStyle.BackColorHex == "#A6000000",
                          "⑫c 倒计时牌样式（字体 / 粗斜体 / 字色 / 底板）原样还原",
                          $"{_cdStyle.FontFamilyName} / 粗 {_cdStyle.Bold} 斜 {_cdStyle.Italic}" +
                          $" / 字色 {_cdStyle.TextColorHex} / 底板 {_cdStyle.BackColorHex}");

                    // ⑥ 全字段深度对比（除 SavedAt）：抓"漏存了某个字段"这类问题
                    if (File.Exists(expectPath))
                    {
                        var want = System.Text.Json.Nodes.JsonNode.Parse(
                            System.IO.File.ReadAllText(expectPath, new System.Text.UTF8Encoding(false)));
                        var got = System.Text.Json.Nodes.JsonNode.Parse(
                            System.Text.Json.JsonSerializer.Serialize(CollectProject(), jsonOpt));
                        var diffs = new List<string>();
                        JsonDiff(want, got, "$", diffs);
                        Check(diffs.Count == 0, "⑬ 全字段往返一致（SavedAt 除外）",
                              diffs.Count == 0 ? "逐字段无差异" : $"{diffs.Count} 处：" + string.Join(" | ", diffs.Take(6)));
                        foreach (var d in diffs.Take(20)) LogLine("  DIFF " + d);
                    }
                    else Check(false, "⑬ 期望值文件缺失", expectPath);

                    LogLine("还原阶段结束");
                }
            }
            catch (Exception ex)
            {
                LogLine("EXCEPTION\t" + ex);
            }
            finally
            {
                Flush();
                if (Environment.GetEnvironmentVariable("CUTLANCE_PROJ_RT_EXIT") == "1")
                {
                    try { await Task.Delay(400); } catch { }
                    try { Close(); } catch { }
                }
            }
        }

        /// <summary>两个 JSON 的深度比对（仅自检用）：把差异按路径列出来，SavedAt 不算。</summary>
        private static void JsonDiff(System.Text.Json.Nodes.JsonNode? a, System.Text.Json.Nodes.JsonNode? b,
                                     string path, List<string> diffs)
        {
            if (diffs.Count >= 40) return;
            if (a == null || b == null)
            {
                if (!(a == null && b == null))
                    diffs.Add($"{path}: {(a?.ToJsonString() ?? "null")} → {(b?.ToJsonString() ?? "null")}");
                return;
            }
            if (a is System.Text.Json.Nodes.JsonObject oa && b is System.Text.Json.Nodes.JsonObject ob)
            {
                foreach (var kv in oa)
                {
                    if (kv.Key == "SavedAt") continue;
                    ob.TryGetPropertyValue(kv.Key, out var vb);
                    JsonDiff(kv.Value, vb, path + "." + kv.Key, diffs);
                }
                foreach (var kv in ob)
                {
                    if (kv.Key == "SavedAt") continue;
                    if (!oa.ContainsKey(kv.Key)) diffs.Add($"{path}.{kv.Key}: 多出字段 {kv.Value?.ToJsonString()}");
                }
                return;
            }
            if (a is System.Text.Json.Nodes.JsonArray aa && b is System.Text.Json.Nodes.JsonArray ab)
            {
                if (aa.Count != ab.Count) { diffs.Add($"{path}: 数组长度 {aa.Count} → {ab.Count}"); return; }
                for (int i = 0; i < aa.Count; i++) JsonDiff(aa[i], ab[i], $"{path}[{i}]", diffs);
                return;
            }
            if (a.ToJsonString() != b.ToJsonString())
                diffs.Add($"{path}: {a.ToJsonString()} → {b.ToJsonString()}");
        }

        /// <summary>
        /// 界面截图专用（CUTLANCE_SLOTS_SHOT=1）：摆好 4 格、给出不同音量，然后**停住不退出**，
        /// 等外部打印窗口截图。只用于拍文档配图，正常使用零影响。
        /// </summary>
        private async void StartSlotsShot()
        {
            if (Environment.GetEnvironmentVariable("CUTLANCE_SLOTS_SHOT") != "1") return;
            try
            {
                await Task.Delay(1500);
                string dir = Environment.GetEnvironmentVariable("CUTLANCE_FADE_DIR") ?? "";
                if (string.IsNullOrWhiteSpace(dir)) dir = Path.Combine(Path.GetTempPath(), "cutlance-fade");
                var srcDir = Path.Combine(dir, "src");
                if (!Directory.Exists(srcDir)) return;

                foreach (var p in Directory.GetFiles(srcDir, "*.png")
                                            .OrderBy(x => x, StringComparer.Ordinal).Take(4))
                    AddInput(new InputSource
                    {
                        Type = SourceType.File,
                        Name = Path.GetFileNameWithoutExtension(p),
                        Location = p
                    });

                LoadInputsIntoPreview(Inputs.Take(4).ToList());
                await Task.Delay(900);
                BtnPreviewToProgram_Click(this, new RoutedEventArgs());
                await Task.Delay(900);

                int[] vols = { 100, 60, 130, 0 };
                for (int i = 0; i < Layers.Count && i < vols.Length; i++)
                {
                    Layers[i].IsAudioSource = i < 3;
                    Layers[i].Volume = vols[i];
                }
                ApplyAudioRouting();

                // CUTLANCE_SLOTS_TIDY=1：收起播放列表 + 收起叠加编辑区，拍「腾出空间后」的样子
                // （2026-10-25 布局整理，用于前后对比截图）
                if (Environment.GetEnvironmentVariable("CUTLANCE_SLOTS_TIDY") == "1")
                {
                    await Task.Delay(400);
                    if (PlaylistPanel.Height > 40) BtnPlayCollapse_Click(this, new RoutedEventArgs());
                    await Task.Delay(300);
                    if (OverlayEditorRow.Height.Value > 0.5) BtnOverlayEdit_Click(this, new RoutedEventArgs());
                    await Task.Delay(300);
                }

                // CUTLANCE_SLOTS_EXPAND=1：顺手点一下「收起上面」，验证图层区放大后 4 格能全露出来
                if (Environment.GetEnvironmentVariable("CUTLANCE_SLOTS_EXPAND") == "1")
                {
                    await Task.Delay(400);
                    BtnLayersExpand_Click(this, new RoutedEventArgs());
                }

                // 文档配图：CUTLANCE_SLOTS_CHROME=1 → 给第一格加金色描边 + 阴影，第二格只加阴影，
                // 一眼能看出「描边/阴影是画进成品帧的」，不是界面取景框。
                if (Environment.GetEnvironmentVariable("CUTLANCE_SLOTS_CHROME") == "1")
                {
                    for (int i = 0; i < Layers.Count; i++)
                    {
                        var lc = Layers[i];
                        if (i == 0)
                        {
                            lc.BorderThickness = 10;
                            lc.BorderRadius = 16;
                            lc.BorderColor = "#FFFFD24A";
                        }
                        lc.Shadow = 26;
                        lc.ShadowOpacity = 85;
                        RenderLayer(lc);
                    }
                    // 精调面板挂到第一格，截图里能同时看到「描边 / 圆角 / 阴影 / 浓淡」四行
                    if (Layers.Count > 0)
                    {
                        _geoPanelTargetIsPreview = false;
                        _selLayer = Layers[0];
                        LayerList.SelectedItem = Layers[0];
                        if (GeoPanel != null) { GeoPanel.SetExpanded(true); }
                        RefreshGeoPanel();
                    }
                    await Task.Delay(900);
                }

                // 文档配图：CUTLANCE_SLOTS_CD=1 时把倒计时牌摆成「设定 10 分钟、已开始、叠加开」，
                // 这样截图里右栏有读数、输出画面正中也有数字（同一个数值，证据是同一份状态）。
                if (Environment.GetEnvironmentVariable("CUTLANCE_SLOTS_CD") == "1")
                {
                    _cdClock.Set(600);
                    _cdClock.Start();
                    _cdOverlayOn = true;
                    if (CdFontSlider != null) CdFontSlider.Value = 110;
                    // 挪到左上偏一点 —— 截图里能看出「位置是能挪的」（默认在正中）
                    _cdX = 0.20; _cdY = 0.17;
                    // 顺便把样式区展开、换成黄色大字，截图里能看到字体/颜色这两行
                    _cdStyle.FontFamilyName = "微软雅黑";
                    _cdStyle.TextColorHex = "#FFFFEB3B";
                    _cdStyle.BackColorHex = "#A6000000";
                    SyncCountdownStyleUi();
                    if (CdStyleRows != null) CdStyleRows.Visibility = Visibility.Visible;
                    if (CdStyleBtn != null) CdStyleBtn.Content = "收起";
                    UpdateCountdownUi();
                    await Task.Delay(900);
                }

                // 程序内截主窗（2026-10-23）：PrintWindow 已抓不到屏外 WPF 窗的内容（一片白），
                // 直接渲染自己，脚本再裁右栏 —— 用来验「音量滑杆并入混音台」后的行样式。
                string rtMain = Environment.GetEnvironmentVariable("CUTLANCE_MAIN_RT_SHOT") ?? "";
                if (!string.IsNullOrWhiteSpace(rtMain))
                {
                    await Task.Delay(1000);
                    var dpiM = VisualTreeHelper.GetDpi(this);
                    int mw = (int)Math.Ceiling(ActualWidth * dpiM.DpiScaleX);
                    int mh = (int)Math.Ceiling(ActualHeight * dpiM.DpiScaleY);
                    if (mw > 10 && mh > 10)
                    {
                        var rtb2 = new RenderTargetBitmap(mw, mh, 96 * dpiM.DpiScaleX, 96 * dpiM.DpiScaleY,
                                                          PixelFormats.Pbgra32);
                        rtb2.Render(this);
                        var enc2 = new PngBitmapEncoder();
                        enc2.Frames.Add(BitmapFrame.Create(rtb2));
                        using var fs2 = File.Create(rtMain);
                        enc2.Save(fs2);
                        ErrorLog.Info($"[主窗截图] 已保存 {rtMain}（{mw}×{mh}）");
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// 打开界面即为「符合桌面的最大窗口」：取桌面工作区（已扣除任务栏）的尺寸与位置，
        /// 并限制最大尺寸，避免多显示器下被系统拉成跨屏巨窗。
        /// </summary>
        private void FitToDesktop()
        {
            try
            {
                var wa = SystemParameters.WorkArea;
                MaxWidth = wa.Width;
                MaxHeight = wa.Height;
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = wa.Left;
                Top = wa.Top;
                Width = wa.Width;
                Height = wa.Height;
            }
            catch { }
        }

        private bool? _compactW;   // 窄屏紧凑：收起横排里的非关键元素
        private bool? _compactH;   // 矮屏紧凑：收缩制作条、收起快捷入口行

        /// <summary>
        /// 紧凑模式（窄屏 / 矮屏自适应）。
        ///
        /// 为什么需要：主窗在小屏上（1080p@150% 可用 1280×693 DIP、1366×768@125% 仅 1093×582 DIP）时，
        /// 中间「制作条」三行都是固定宽度的横排，窄了会把右侧 CUT / AUTO / FTB 裁掉；
        /// 而制作条本身要占 120+ DIP 高度，矮屏下又把播放列表 / 图层列表挤得几乎没有空间。
        ///
        /// 做法（只收尺寸与低价值文案，不删功能）：
        ///   · 窄（&lt; 1240 DIP）：收起「时长」滑块组、OVERLAY 标签与两处说明文字 —— 它们占宽大、信息价值低，
        ///     让 CUT / AUTO / FTB 能在一行里完整放下（另有横向滚动兜底，见 TransRow）。
        ///   · 矮（&lt; 780 DIP）：制作条内边距与行距收紧，并收起「音频混音台 / 画面效果」快捷入口行
        ///     —— 这两个窗口仍可从「设置 → 输出设置」打开，功能不丢，省下的高度全给列表。
        /// </summary>
        private void ApplyCompactLayout()
        {
            try
            {
                bool narrow = ActualWidth > 0 && ActualWidth < 1240;
                bool shortH = ActualHeight > 0 && ActualHeight < 780;

                if (_compactW != narrow)
                {
                    _compactW = narrow;
                    var vis = narrow ? Visibility.Collapsed : Visibility.Visible;
                    DurationGroup.Visibility = vis;      // 「时长」标签 + 滑块 + 读数
                    AudioFxHint.Visibility = vis;        // 音频 / 特效状态文字
                    // OVERLAY 1-8 通道条已移到右侧固定 300 DIP 的栏里（老板 2026-10-17），
                    // 它的宽度与主窗宽度无关，所以不再随窄屏收起。
                }

                if (_compactH != shortH)
                {
                    _compactH = shortH;
                    // 与 XAML 里的 8,6 对齐（2026-10-25 布局整理把常态内边距从 10 收到 8,6；
                    // 这里原来写死 10，紧凑模式切回来会把内边距又放大一档，两处不一致）
                    ProdBar.Padding = shortH ? new Thickness(6, 4, 6, 4) : new Thickness(8, 6, 8, 6);
                    QuickEntryRow.Visibility = shortH ? Visibility.Collapsed : Visibility.Visible;
                    // 矮屏时把叠加层编辑器压矮。它是 Grid 的 Auto 行，不限高就会把上面的
                    // 通道列表（* 行）挤成 0 高 —— 表现就是「叠加层列表整个消失、只剩编辑器」。
                    // 100 DIP 大约能露出编辑器里两行半 + 内部滚动，够用。
                    OverlayEditor.MaxHeight = shortH ? 100 : 252;
                }
            }
            catch
            {
                // 纯布局调整：出问题按原样显示，绝不能影响直播
            }
        }

        private readonly DispatcherTimer _streamTimer = new();

        // —— 添加输入源（带 vMix 风格编号）——
        private void AddInput(InputSource src)
        {
            Inputs.Add(src);
            // 1 基编号。删除后会重排（见 RenumberInputs）—— 因为「切到输入 N」是可绑定动作，
            // 编号必须与列表次序一致，否则导播键盘按下去会切错路。
            src.Number = Inputs.Count;
        }

        // ================= 办公文档输入（老板 2026-10-19 第 1 条）=================
        //
        // 表格：按 Excel 自己的**打印分页**导出成 PNG 序列，然后当成普通图片输入播
        //       （VLC 原生支持 PNG 且已设 `:image-duration=-1` 常显，不需要新造轮子）。
        // 翻页：改 `Location` 指向下一页 + `ReplayInput` 重建媒体 —— 预览框和所有输出图层一起换页。
        //       为什么这么翻：图层 / 预览各自持有独立的 VLC 播放器，改源对象后必须逐处重建媒体才生效。

        private async void BtnAddExcel_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择表格（按 Excel 打印分页翻页）",
                Filter = "Excel 表格|*.xlsx;*.xlsm;*.xls|所有文件|*.*"
            };
            if (dlg.ShowDialog(this) != true) return;

            string cacheDir = Path.Combine(Path.GetTempPath(), "cutlance-office",
                                           Guid.NewGuid().ToString("N"));
            StatusText.Text = "⏳ 正在按打印分页渲染表格…（首次调用 Excel 约几秒）";
            try
            {
                var pages = await OfficePageService.ExportExcelPagesAsync(dlg.FileName, cacheDir);
                if (pages.Count == 0)
                {
                    StatusText.Text = "⚠ 这个表格没有可导出的内容（空表 / 打印区域为空）";
                    return;
                }

                var src = new InputSource
                {
                    Type = SourceType.File,
                    Location = pages[0],
                    Name = Path.GetFileName(dlg.FileName),
                    OfficePages = pages,
                    OfficePageIndex = 0,
                    OfficeSourcePath = dlg.FileName,
                };
                AddInput(src);
                SetPreview(src);
                StatusText.Text = $"✓ 已加入表格：{src.Name}（共 {pages.Count} 页，PageUp / PageDown 翻页）";
            }
            catch (Exception ex)
            {
                // 导播中绝不弹模态框 → 只写状态栏
                StatusText.Text = "⚠ 表格导出失败：" + ex.Message;
            }
        }

        /// <summary>
        /// PageUp / PageDown 的总入口。
        ///
        /// ⚠ 老板 2026-10-19 21:42 报「字幕条没法翻页换」的根因就在这里：
        ///   以前是先去 `Inputs` 里找「可翻页的文档输入」，找到就翻它，**字幕表排在最后兜底**。
        ///   可字幕表是叠加层、不是 InputSource —— 老板只要导入过一份 PPT 或表格，
        ///   `Inputs.FirstOrDefault(IsDocInput)` 就永远命中，字幕表一次都轮不到。
        ///   现在改成：**字幕表正在播（有内容且可见）就优先翻字幕表**，
        ///   要翻表格 / PPT 用 Ctrl+PageUp / Ctrl+PageDown（见 HotkeyMap 的 docinput.*）。
        /// </summary>
        private void FlipOfficePage(int delta)
        {
            if (_subtitle?.Channel is { HasContent: true, Visible: true })
            {
                FlipSubtitle(delta);
                return;
            }
            FlipDocInputPage(delta);
        }

        /// <summary>翻「表格 / PPT」这类真输入源（不看字幕表）。</summary>
        private void FlipDocInputPage(int delta)
        {
            var src = _previewSource;
            if (src == null || !IsDocInput(src))
                src = Inputs.FirstOrDefault(IsDocInput);
            if (src == null)
            {
                if (_subtitle != null) { FlipSubtitle(delta); return; }
                StatusText.Text = "⚠ 当前没有可翻页的文档（先用「+表格」「+字幕表」或「+PPT」加一份）";
                return;
            }

            // PPT：翻页 = 给放映窗口投递 PageDown / PageUp（不动前台）
            if (_pptSessions.TryGetValue(src, out var sess))
            {
                bool ok = delta > 0 ? sess.Next() : sess.Prev();
                // 放映窗口可能被拖到别的显示器 → 每次翻页顺手核对一次区域，变了就重建媒体
                if (PptShowService.TryLocateShowWindow(sess.Process.Id, sess, out _))
                {
                    if (sess.Left != src.CaptureLeft || sess.Top != src.CaptureTop
                        || sess.Width != src.CaptureWidth || sess.Height != src.CaptureHeight)
                    {
                        src.CaptureLeft = sess.Left;
                        src.CaptureTop = sess.Top;
                        src.CaptureWidth = sess.Width;
                        src.CaptureHeight = sess.Height;
                        ReplayInput(src);
                        StatusText.Text = $"📽 {src.Name}：放映窗口位置已变化，已重新对准（{sess.Width}×{sess.Height}）";
                        return;
                    }
                }
                StatusText.Text = ok
                    ? $"📽 {src.Name}：{(delta > 0 ? "下一页" : "上一页")}"
                    : $"⚠ {src.Name}：放映窗口已不在（可能被关掉了），请重新点「+PPT」";
                return;
            }

            // 表格：翻页 = 换 Location 指向下一页图片
            if (src.OfficePages.Count == 0)
            {
                StatusText.Text = "⚠ 这一路输入不是可翻页的文档";
                return;
            }

            int idx = Math.Clamp(src.OfficePageIndex + delta, 0, src.OfficePages.Count - 1);
            if (idx == src.OfficePageIndex)
            {
                StatusText.Text = delta > 0
                    ? $"📄 {src.Name}：已是最后一页（{src.OfficePages.Count}/{src.OfficePages.Count}）"
                    : $"📄 {src.Name}：已是第一页（1/{src.OfficePages.Count}）";
                return;
            }

            src.OfficePageIndex = idx;
            src.Location = src.OfficePages[idx];
            ReplayInput(src);          // 预览 + 每个输出图层逐处重建媒体，才会真的换页
            StatusText.Text = $"📄 {src.Name}：第 {idx + 1} / {src.OfficePages.Count} 页";
        }

        /// <summary>是不是「可翻页的文档输入」（表格有页图片，或 PPT 有放映会话）。</summary>
        private bool IsDocInput(InputSource src)
            => src.OfficePages.Count > 0 || _pptSessions.ContainsKey(src);

        // ================= 字幕表（老板 2026-10-19 20:38）=================
        //
        // 「通过读取表格中不同行列，可以实现一个表格中不同行列通过翻页直接实现上屏幕，
        //   一次把很多字幕都轻松实现」
        //
        // 口径：表头以下**每一行 = 一条字幕**；翻页 = 换下一条。
        // 上屏方式 = 一张 1920×1080 的**透明 PNG 字幕条**挂到某一路图片叠加通道上，
        //   → 压在节目画面上，节目本身不受影响（图片叠加本来就是按 PNG 透明通道合成）。
        // 不用「整页表格图」的理由：那是投屏看报表用的，播字幕要的是一条一条上屏。

        /// <summary>当前挂着的字幕表（同一时间只服务一张表；再导入一张就顶掉前一张）。</summary>
        private sealed class SubtitleSession
        {
            public string SourcePath = "";
            public TableData Data = new();
            public List<string> Images = new();
            public int Index;
            public OverlayItem? Channel;
            public SubtitleStyle Style = new();
        }

        private SubtitleSession? _subtitle;

        /// <summary>字幕条样式（记忆上一次用的参数；还没导表时也能先调好）。</summary>
        private SubtitleStyle _subtitleStyle = new();

        private async void BtnAddSubTable_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择字幕表格（表头下每一行 = 一条字幕）",
                Filter = "Excel 表格|*.xlsx;*.xlsm;*.xls|所有文件|*.*"
            };
            bool? ok = null;
            RunWithMouseHookDetached(() => ok = dlg.ShowDialog(this));
            if (ok != true) return;

            var ch = TakeEmptyChannel();
            if (ch == null)
            {
                StatusText.Text = "8 个叠加通道已满，请先清空一路再导入字幕表";
                return;
            }

            string cacheDir = Path.Combine(Path.GetTempPath(), "cutlance-subtitle", Guid.NewGuid().ToString("N"));
            StatusText.Text = "⏳ 正在读表格、逐行生成字幕条…（行多时要几秒）";
            try
            {
                var built = await TableSubtitleService.BuildAsync(dlg.FileName, cacheDir, _subtitleStyle);
                if (built.Images.Count == 0)
                {
                    StatusText.Text = "⚠ 这张表里没有数据行（第一行被当成表头了）";
                    return;
                }

                _subtitle = new SubtitleSession
                {
                    SourcePath = dlg.FileName,
                    Data = built.Data,
                    Images = built.Images,
                    Index = 0,
                    Channel = ch,
                    Style = _subtitleStyle.Clone(),
                };
                ApplySubtitleRow(0);
                ch.HasContent = true;
                ch.Visible = true;
                OverlayList.SelectedItem = ch;
                StatusText.Text = $"✓ 字幕表就绪：{Path.GetFileName(dlg.FileName)} 共 {built.Images.Count} 条"
                                  + $"（挂通道 {ch.ChannelIndex}）—— PageUp / PageDown 一条条翻上屏";
            }
            catch (Exception ex)
            {
                StatusText.Text = "⚠ 字幕表导入失败：" + ex.Message;
            }
        }

        /// <summary>把第 idx 条字幕挂到通道上（换图片 → 重绘叠加画布）。</summary>
        private void ApplySubtitleRow(int idx)
        {
            if (_subtitle?.Channel == null) return;
            var ch = _subtitle.Channel;
            _subtitle.Index = idx;

            // ⚠ 老板 2026-10-19 22:15：「通道编辑也不能动字幕条了」。
            //   字幕条的位置 / 大小现在**只归「字幕样式」管**（已经烧进 1920×1080 的 PNG 里），
            //   通道编辑里的位置/缩放锁定成 50 / 50 / 100 —— 整幅图铺满画布就是"原位"。
            //   这里每次翻页都写死，是为了防止任何别的路径（工程载入、满框隐藏逻辑、
            //   旧版本存下来的值）把字幕条挤歪；同时也让 UI 上那几个滑块显示真实值。
            ch.Type = OverlayType.Image;
            ch.ImagePath = _subtitle.Images[idx];
            // 换行 = 上一行那张实时图作废（它画的是别人的字），必须清掉
            ch.LiveImage = null;
            ch.ImageScale = 100;
            ch.XPct = 50;
            ch.YPct = 50;
            ch.HasContent = true;
            SyncOverlays();

            // 编辑区正停在这一路时，把滑块也拉回锁定值并保持禁用状态
            if (ReferenceEquals(_selOverlay, ch)) SyncOverlayEditor(ch);
        }

        // ================= 字幕条实时跟手（老板 2026-10-19 22:57）=================
        //
        // 目标：在「字幕样式」窗里拖「距底 / 左右偏移 / 条子宽 / 字号」时，
        //       **输出框里的字幕条当场跟着动**，不用等点「应用」。
        //
        // 做法：样式窗 50ms 节流后把整份样式推过来 → 这里用**当前这一行**重渲染 →
        //       直接把位图换个 Image.Source。全程不落盘、不重建叠加树。
        //   为什么不全量重生成：行多时一次要几秒，拖滑块根本没法跟。
        //   为什么不清空重画：SyncOverlays() 每 50ms 重建一次整棵叠加树，画面会闪。
        //   为什么敢直接出 1920×1080：实测一次渲染只要 2–3ms、换源 1ms，
        //       50ms 一次只占 UI 线程 6%，**全程都是清晰图**，不需要降分辨率再补图。

        /// <summary>实时跟手的调试记录（仅 Debug 版写 %TEMP%\cutlance-live.log，发布版为空操作）。</summary>
        private static void LiveLog(string msg)
        {
#if DEBUG
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "cutlance-live.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} {msg}\r\n");
            }
            catch { }
#endif
        }

        /// <summary>只把这一路字幕条的 Image.Source 换掉，位置重算 —— 不动其它任何通道。</summary>
        private void RefreshSubtitleLiveVisual(OverlayItem ch, BitmapSource bmp)
        {
            ch.LiveImage = bmp;
            if (OverlayCanvas == null) { LiveLog("refresh: canvas null"); return; }
            LiveLog($"refresh: children={OverlayCanvas.Children.Count} bmp={bmp.PixelWidth}x{bmp.PixelHeight}");

            foreach (UIElement el in OverlayCanvas.Children)
            {
                if (el is Image img && ReferenceEquals(img.Tag, ch))
                {
                    img.Source = bmp;
                    PositionOverlay(img, ch);
                    // 无障碍描述里带上实时图尺寸（无视觉影响）：既方便读屏，
                    // 也让「输出框到底换没换源」有个可查的证据。
                    try
                    {
                        System.Windows.Automation.AutomationProperties.SetHelpText(
                            img, $"live-preview {bmp.PixelWidth}x{bmp.PixelHeight}");
                    }
                    catch { }
                    return;
                }
            }
            // 这一路还没上屏（刚清空过、或通道隐藏了）→ 只能走整建，让它显出来
            LiveLog("refresh: no Image matched -> SyncOverlays");
            SyncOverlays();
        }

        /// <summary>样式窗推过来的新样式：当场重渲染「正在上屏的那一条」，换到输出框上。</summary>
        private void OnSubtitleStyleLiveChanged(SubtitleStyle style)
        {
            var s = _subtitle;
            if (s?.Channel == null || s.Data.Rows.Count == 0) return;
            int i = Math.Clamp(s.Index, 0, s.Data.Rows.Count - 1);
            try
            {
                // 透明底（withBackdrop: false）—— 输出框底下就是节目画面，不能带模拟底色
                var bmp = TableSubtitleService.RenderPreview(s.Data.Rows[i], style, false);
                LogBarPosition(bmp, style);
                RefreshSubtitleLiveVisual(s.Channel, bmp);
            }
            catch
            {
                // 实时预览失败绝不能影响播出（这是导播中，不弹框、不改状态）
            }
        }

        /// <summary>
        /// 调试用：采样中轴一列，量出字幕条在渲染图里的上下边界。
        /// 换源成功 ≠ 画面变了 —— 这行日志才能证明「距底 110 改成 400」条子真的上移了。
        /// </summary>
        private static void LogBarPosition(BitmapSource bmp, SubtitleStyle st)
        {
#if DEBUG
            try
            {
                int col = bmp.PixelWidth / 2;
                var line = new byte[bmp.PixelHeight * 4];
                bmp.CopyPixels(new Int32Rect(col, 0, 1, bmp.PixelHeight), line, 4, 0);
                int top = -1, bottom = -1;
                for (int y = 0; y < bmp.PixelHeight; y++)
                {
                    if (line[y * 4 + 3] > 40) { if (top < 0) top = y; bottom = y; }
                }
                LiveLog($"bar y={top}..{bottom} (图高{bmp.PixelHeight}) 距底={st.MarginBottom} 偏移={st.OffsetX} 条宽={st.BarWidth}");
            }
            catch { }
#endif
        }

        /// <summary>撤掉实时画面，让通道回到磁盘上的正式图。</summary>
        private void ClearSubtitleLiveVisual()
        {
            if (_subtitle?.Channel?.LiveImage == null) return;
            _subtitle.Channel.LiveImage = null;
            SyncOverlays();
        }

        private void FlipSubtitle(int delta)
        {
            var s = _subtitle!;
            int idx = Math.Clamp(s.Index + delta, 0, s.Images.Count - 1);
            if (idx == s.Index)
            {
                StatusText.Text = delta > 0
                    ? $"📋 字幕：已是最后一条（{s.Images.Count}/{s.Images.Count}）"
                    : $"📋 字幕：已是第一条（1/{s.Images.Count}）";
                return;
            }
            ApplySubtitleRow(idx);
            StatusText.Text = $"📋 字幕：第 {idx + 1} / {s.Images.Count} 条 —— {s.Data.SummaryOf(idx)}";
        }

        // —— 字幕表的三颗按钮（老板 2026-10-19 21:42）——
        //   ◀ / ▶ ：不依赖任何快捷键，鼠标点就一定翻得动，也绝不会误翻到 PPT / 表格。

        private void BtnSubPrev_Click(object sender, RoutedEventArgs e) => NudgeSubtitle(-1);
        private void BtnSubNext_Click(object sender, RoutedEventArgs e) => NudgeSubtitle(+1);

        private void NudgeSubtitle(int delta)
        {
            if (_subtitle == null || _subtitle.Images.Count == 0)
            {
                StatusText.Text = "⚠ 还没有导入字幕表（先点「+字幕表」选一张 xlsx）";
                return;
            }
            // 通道被「清空」过 → 先把它重新挂上去再翻，不然点了没反应
            if (_subtitle.Channel != null && !_subtitle.Channel.HasContent)
            {
                ApplySubtitleRow(Math.Clamp(_subtitle.Index, 0, _subtitle.Images.Count - 1));
                StatusText.Text = $"📋 字幕已重新上屏：第 {_subtitle.Index + 1} / {_subtitle.Images.Count} 条"
                                  + $" —— {_subtitle.Data.SummaryOf(_subtitle.Index)}";
                return;
            }
            FlipSubtitle(delta);
        }

        /// <summary>
        /// 字幕条样式（大小 / 位置 / 颜色）。
        ///
        /// 改完不是「就地把一张图重画一下」—— 整个表所有行的 PNG 都要重新生成
        /// （因为每行的换行位置会随字号变），所以走异步 + 状态栏提示。
        /// 新图写到**新目录**：WPF 的 BitmapImage 按路径缓存，同路径覆盖不刷新（老坑）。
        /// </summary>
        private async void BtnSubStyle_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SubtitleStyleWindow(_subtitle?.Style ?? _subtitleStyle) { Owner = this };
            if (_subtitle != null && _subtitle.Data.Rows.Count > 0)
            {
                int i = Math.Clamp(_subtitle.Index, 0, _subtitle.Data.Rows.Count - 1);
                dlg.SampleCells = _subtitle.Data.Rows[i];
                // 老板 2026-10-19 22:15：样式窗里要能看到「前一条 / 现在上屏 / 后一条」的字，
                // 上屏前先核一遍，防止播错字。
                dlg.Table = _subtitle.Data;
                dlg.CurrentIndex = i;
            }

            // 老板 2026-10-19 22:57：「移动字幕条时同步在输出框里显示移动后效果」。
            // 挂上实时通道：窗里一拖，输出框那条字幕当场跟着走；点「取消」原样还原。
            bool canLive = _subtitle?.Channel != null && _subtitle.Data.Rows.Count > 0;
            LiveLog($"style-open: canLive={canLive} rows={_subtitle?.Data.Rows.Count} ch={_subtitle?.Channel?.ChannelIndex}");
            if (canLive) dlg.LiveStyleChanged += OnSubtitleStyleLiveChanged;

            bool applied = dlg.ShowDialog() == true;
            if (canLive) dlg.LiveStyleChanged -= OnSubtitleStyleLiveChanged;

            // 窗口关了 → 先撤掉实时画面（下面应用成功会用正式图重新挂上）
            ClearSubtitleLiveVisual();

            if (!applied)
            {
                StatusText.Text = "已取消：字幕条样式没有改动";
                return;
            }

            var style = dlg.ResultStyle;

            if (_subtitle == null)
            {
                // 还没导表 —— 先把样式记下来，下次「+字幕表」直接用
                _subtitleStyle = style;
                StatusText.Text = "✓ 字幕样式已记住：下次点「+字幕表」导入时就按这个样式生成";
                return;
            }

            StatusText.Text = $"⏳ 正在按新样式重新生成 {_subtitle.Images.Count} 条字幕条…";
            try
            {
                string cacheDir = Path.Combine(Path.GetTempPath(), "cutlance-subtitle", Guid.NewGuid().ToString("N"));
                var images = await TableSubtitleService.RerenderAsync(_subtitle.Data, style, cacheDir);
                if (images.Count == 0)
                {
                    StatusText.Text = "⚠ 重新生成失败：没有产出任何图片";
                    return;
                }

                _subtitle.Images = images;
                _subtitle.Style = style;
                _subtitleStyle = style.Clone();
                int idx = Math.Clamp(_subtitle.Index, 0, images.Count - 1);
                ApplySubtitleRow(idx);
                StatusText.Text = $"✓ 字幕样式已更新（共 {images.Count} 条，当前第 {idx + 1} / {images.Count} 条）";
            }
            catch (Exception ex)
            {
                StatusText.Text = "⚠ 重新生成字幕条失败：" + ex.Message;
            }
        }

        // ================= PPT 进预览（老板 2026-10-19 第 1 条）=================
        // 抓放映窗口：全屏放映 → 取放映窗口的屏幕矩形 → 桌面区域捕获只抓那块。
        // 翻页 / 退出都用 PostMessage 投递给放映窗口，不抢前台、不改焦点。

        private readonly Dictionary<InputSource, PptShowSession> _pptSessions = new();

        private async void BtnAddPpt_Click(object sender, RoutedEventArgs e)
        {
            // 老板 2026-10-19 21:06 第 2 条：「ppt导入 要做选项」。
            // 两种 PPT 的能力边界互补，程序不替他猜 —— 先选方式，再选文件。
            var pick = new PptImportWindow { Owner = this };
            if (pick.ShowDialog() != true) return;

            var dlg = new OpenFileDialog
            {
                Title = pick.Mode == PptImportMode.ImageSequence
                    ? "选择 PPT（每页导出成图片，翻页即换页）"
                    : "选择 PPT（全屏放映 + 抓放映窗口，动画/转场/声音都在）",
                Filter = "PowerPoint 演示文稿|*.pptx;*.ppt;*.ppsx;*.pps|所有文件|*.*"
            };
            if (dlg.ShowDialog(this) != true) return;

            if (pick.Mode == PptImportMode.ImageSequence)
                await ImportPptAsImages(dlg.FileName);
            else
                await ImportPptAsCapture(dlg.FileName, pick.AudioDevice);
        }

        /// <summary>
        /// ① 图片序列：调 PowerPoint 把每页导成 PNG，当图片序列翻页。
        /// 不挡屏、PowerPoint 关掉也能播；动画 / 转场 / 视频 / 声音都没有。
        /// </summary>
        private async Task ImportPptAsImages(string path)
        {
            string cacheDir = Path.Combine(Path.GetTempPath(), "cutlance-office", Guid.NewGuid().ToString("N"));
            StatusText.Text = "⏳ 正在把 PPT 每一页导出成图片…（首次调用 PowerPoint 约几秒）";
            try
            {
                var pages = await OfficePageService.ExportPptSlidesAsync(path, cacheDir);
                if (pages == null || pages.Count == 0)
                {
                    StatusText.Text = "⚠ 这份 PPT 没能导出页面图片，改选「② 视频采集」再试一次";
                    return;
                }

                var src = new InputSource
                {
                    Type = SourceType.File,
                    Location = pages[0],
                    Name = Path.GetFileName(path),
                    OfficePages = pages,
                    OfficePageIndex = 0,
                    OfficeSourcePath = path,
                };
                AddInput(src);
                SetPreview(src);
                StatusText.Text = $"✓ 已加入 PPT（图片序列 {pages.Count} 页）：{src.Name}；"
                                  + "PageUp / PageDown 翻页；这份导入不含动画 / 转场 / 声音";
            }
            catch (Exception ex)
            {
                StatusText.Text = "⚠ PPT 导图失败：" + ex.Message + "（可改选「② 视频采集」再试）";
            }
        }

        /// <summary>
        /// ② 视频采集：启动全屏放映，抓放映窗口那块屏幕。
        /// 动画由 PowerPoint 自己驱动（一次 PageDown = 走一个动画步），
        /// 声音可选一路回环录音设备（抓屏本身只有画面）。
        /// </summary>
        private async Task ImportPptAsCapture(string path, string audioDevice)
        {
            StatusText.Text = "⏳ 正在启动 PowerPoint 放映…（窗口出现后自动对准）";
            try
            {
                // 启动放映是重活（PowerPoint 冷启动可能十几秒），放到后台等，UI 不卡
                var sess = await Task.Run(() => PptShowService.StartShow(path));
                if (sess == null)
                {
                    StatusText.Text = "⚠ 没能等到放映窗口（文件可能被 Office 安全提示挡住，或 PowerPoint 启动失败）。"
                                      + "如果这份 PPT 没有动画，改选「① 图片序列」导入更稳。";
                    return;
                }

                var src = new InputSource
                {
                    Type = SourceType.Desktop,
                    Location = "screen://",
                    Name = Path.GetFileName(path),
                    OfficeSourcePath = path,
                    CaptureLeft = sess.Left,
                    CaptureTop = sess.Top,
                    CaptureWidth = sess.Width,
                    CaptureHeight = sess.Height,
                    CaptureAudioDevice = audioDevice ?? "",
                };
                _pptSessions[src] = sess;
                AddInput(src);
                SetPreview(src);   // 先进预览框；要上输出就点「→」或右侧「把预览加入图层」
                var audioNote = string.IsNullOrWhiteSpace(audioDevice)
                    ? "；声音未接入（PPT 里有音效要另选回环设备）"
                    : $"；声音取自「{audioDevice}」";
                StatusText.Text = $"✓ 已加入 PPT（视频采集 {sess.Width}×{sess.Height}）：{src.Name}；"
                                  + $"动画照播；PageUp / PageDown 翻页{audioNote}；⚠ 放映窗别被别的窗口盖住";
            }
            catch (Exception ex)
            {
                StatusText.Text = "⚠ PPT 放映启动失败：" + ex.Message;
            }
        }

        /// <summary>关掉某一路 PPT 的放映（删除输入 / 关闭程序时调用）。</summary>
        private void ClosePptSession(InputSource src)
        {
            if (_pptSessions.TryGetValue(src, out var sess))
            {
                _pptSessions.Remove(src);
                try { PptShowService.Close(sess); } catch { }
            }
        }

        /// <summary>上次「+文件」所在目录，下次打开对话框直接定位过去。</summary>
        private string? _lastMediaDir;

        private void BtnAddFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "添加媒体文件（Ctrl / Shift 可一次多选）",
                // 过滤器放宽：以前只认 8 种视频扩展名，mkv 之外的 m4v/m2ts/rmvb 等、
                // 以及音频/图片素材都得切到「所有文件」才看得见 —— 很像"想加的文件找不到"。
                Filter = "媒体文件（视频+音频+图片）|"
                       + "*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.flv;*.ts;*.m2ts;*.mts;*.webm;*.m4v;*.mpg;*.mpeg;*.vob;*.rmvb;*.rm;*.3gp;*.ogv;"
                       + "*.mp3;*.wav;*.aac;*.m4a;*.flac;*.ogg;*.wma;"
                       + "*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp"
                       + "|视频文件|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.flv;*.ts;*.m2ts;*.mts;*.webm;*.m4v;*.mpg;*.mpeg;*.vob;*.rmvb;*.rm;*.3gp;*.ogv"
                       + "|音频文件|*.mp3;*.wav;*.aac;*.m4a;*.flac;*.ogg;*.wma"
                       + "|图片文件|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp"
                       + "|所有文件|*.*",
                Multiselect = true,        // 一次能加多个文件（配合「多选一起进预览」直接排成分屏）
                CheckFileExists = true,
                CheckPathExists = true,
                DereferenceLinks = true
            };
            if (!string.IsNullOrEmpty(_lastMediaDir) && Directory.Exists(_lastMediaDir))
                dlg.InitialDirectory = _lastMediaDir;

            // ⚠ 必须显式传 Owner。不传时对话框靠 GetActiveWindow 自己找宿主窗口，
            //   一旦主窗口不是前台（比如刚点过浮层、或切过别的程序），
            //   对话框会跑到屏幕角落甚至主窗口后面 —— 表现就是"点了 +文件 什么也没发生"。
            // ⚠ 同时临时摘掉全局鼠标钩子：钩子的命中测试按主窗口坐标算，对话框是另一个窗口，
            //   焦点在两窗口间切换的瞬间可能把落在对话框上的点击吞掉（选不中文件 / 点「打开」没反应）。
            bool? ok = null;
            RunWithMouseHookDetached(() => ok = dlg.ShowDialog(this));
            if (ok != true) return;

            var files = dlg.FileNames;
            if (files.Length > 0) _lastMediaDir = Path.GetDirectoryName(files[0]);

            foreach (var f in files)
            {
                AddInput(new InputSource
                {
                    Type = SourceType.File,
                    Name = Path.GetFileName(f),
                    Location = f
                });
            }
            StatusText.Text = files.Length == 1
                ? $"已添加文件输入 #{Inputs.Count}：{Path.GetFileName(files[0])}（单击左侧输入项设为 PREVIEW）"
                : $"已一次添加 {files.Length} 个文件（当前共 {Inputs.Count} 个输入）；可 Ctrl+点输入项多选后一起进预览";
        }

        private void BtnAddCamera_Click(object sender, RoutedEventArgs e)
        {
            // 与「+采集卡」合并为同一个面板（老板 2026-10-14 定）。
            // 摄像头与采集卡在 Windows 上本就是同一类 DirectShow 设备，没理由两套界面。
            // 旧面板（CameraSettingsWindow）走 WMI PNPClass='Camera' OR 'Image' 枚举 ——
            // 实测本机 10 个 dshow 视频设备只能列出 1 个，且分辨率/帧率是写死的预设表，
            // 采集卡（GATHER CAM1~4 / 1080p60 / 2560×1600 等真实能力）根本呈现不出来。
            // 新面板改走 ffmpeg 真实枚举，设备与格式都能如实列出，故统一到它。
            OpenCaptureSignal();
        }

        /// <summary>把摄像头采集参数拼成简短后缀，如 " · 1920x1080@30 · MJPG"。</summary>
        private static string CameraParamSummary(CameraSettingsWindow d)
        {
            var s = "";
            if (d.CamWidth > 0 && d.CamHeight > 0) s += $" · {d.CamWidth}x{d.CamHeight}";
            if (d.CamFps > 0) s += "@" + d.CamFps;
            if (!string.IsNullOrWhiteSpace(d.Chroma)) s += " · " + d.Chroma;
            if (!d.Audio) s += " · 静音";
            return s;
        }

        private void BtnAddScreen_Click(object sender, RoutedEventArgs e)
        {
            AddInput(new InputSource { Type = SourceType.Desktop, Name = "桌面捕获", Location = "screen://" });
            StatusText.Text = "已添加桌面捕获输入（30fps，单击左侧输入项设为 PREVIEW）";
        }

        /// <summary>WMI 枚举本机可用摄像头设备名。</summary>
        private static System.Collections.Generic.List<string> GetCameraDevices()
        {
            var result = new System.Collections.Generic.List<string>();
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT Name, Status FROM Win32_PnPEntity WHERE PNPClass='Camera' OR PNPClass='Image'");
                foreach (var o in searcher.Get())
                {
                    using var mo = (System.Management.ManagementBaseObject)o;
                    var name = mo["Name"]?.ToString();
                    var status = mo["Status"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(name) && status == "OK")
                        result.Add(name);
                }
            }
            catch { }
            return result;
        }

        // —— 单击左侧输入项 → 设为 PREVIEW（播到预览播放器）；Ctrl+单击 → 多选 ——
        private void Tile_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.DataContext is not InputSource src) return;

            // 多选：勾选几个输入，再点预览框上（或图层栏）的「多选一起进预览」一次排成分屏
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                src.IsPicked = !src.IsPicked;
                int cnt = Inputs.Count(i => i.IsPicked);
                StatusText.Text = src.IsPicked
                    ? $"✓ 已勾选输入 #{src.Number}：{src.Name}（当前共 {cnt} 个）→ 点「多选一起进预览」"
                    : $"已取消勾选输入 #{src.Number}（当前共 {cnt} 个）";
                return;
            }

            foreach (var i in Inputs) i.IsPicked = false;   // 普通单击 = 单选预览，清掉多选

            // 老板 2026-10-23 定死流程：点左侧文件 = 第一步只进预览框；
            // 要上播出必须显式点「切换」（CUT / AUTO）。所以这里**绝不碰播出画面**。
            //
            // 历史（别再走回头路）：
            //   · 10-22 把「选中播出格 → 点输入」做成换格，但播出框的选中状态会一直留着，
            //     多选进预览后点文件被当成换格，预览纹丝不动（老板报障）；
            //   · 10-23 一度改成按焦点判断，仍会在"刚点过播出格"时劫持点文件；
            //   · 10-23 最终口径：点文件永远只进预览。换格改走左侧输入项右键的
            //     「换入播出框选中的那一格」（TileSwapToSelectedLayer_Click）—— 动作显式，
            //     不会因为残留的选中状态误伤日常操作。
            SetPreview(src);
        }

        /// <summary>
        /// 左侧输入项右键 →「换入播出框选中的那一格」：单格切的显式入口（2026-10-23）。
        ///
        /// 为什么从「点输入就换格」改成右键菜单：
        /// 播出框的选中状态看不见（只有图层列表高亮），而且会一直留着 ——
        /// 老板的日常流程是「点文件 → 进预览 → 点切换 → 进播出」，
        /// 这个残留状态会把点文件劫持成换格，预览完全不动。换成显式菜单项后，
        /// 只有明确点这一下才会动播出画面。
        /// </summary>
        private void TileSwapToSelectedLayer_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as MenuItem)?.DataContext is not InputSource src) return;

            if (_selLayer == null || !Layers.Contains(_selLayer))
            {
                SetStatus("⚠ 先在播出框里点中要换的那一格，再到左侧右键输入 →「换入播出框选中的那一格」", hold: true);
                return;
            }

            SwapLayerSource(_selLayer, src);
        }

        private void SetPreview(InputSource src)
        {
            _previewSource = src;
            foreach (var i in Inputs) i.IsPreview = (i == src);
            try
            {
                // 多路预览组会被整体替换成「这一路铺满」—— 单击输入 = 回到单路监看（老行为不变）。
                PreviewNameText.Text = src.Name;
                LoadInputsIntoPreview(new List<InputSource> { src });

                // 名字里写着「ffmpeg 预处理」但进程没起来时会静默回落 VLC 直连——
                // 必须把原因说出来，否则用户永远在猜（实测踩过：界面看着成功，实际是另一条链路）
                if (InputPreprocessService.NeedsPreprocess(src)
                    && !InputPreprocessService.Instance.IsRunning(src)
                    && !InputPreprocessService.Instance.IsReconnecting(src)
                    && !string.IsNullOrWhiteSpace(InputPreprocessService.Instance.LastError))
                {
                    SetStatus("⚠ 预处理未启动，已回落原始画面（"
                              + InputPreprocessService.Instance.LastError + "）", hold: true);
                }
            }
            catch (Exception ex)
            {
                SetStatus("⚠ 预览播放失败：" + ex.Message, hold: true);
            }
        }

        // —— 转场控制 ——
        private void TransitionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _transition = (TransitionType)TransitionCombo.SelectedIndex;
            if (StatusText != null)
                StatusText.Text = "转场类型：" + _transition;
        }

        private void DurationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            _duration = DurationSlider.Value;
            if (DurationText != null) DurationText.Text = _duration.ToString("0.0") + "s";
        }

        private void BtnCut_Click(object sender, RoutedEventArgs e) => TakeToLayer(TransitionType.Cut);
        private void BtnTake_Click(object sender, RoutedEventArgs e) => TakeToLayer(_transition);
        private void BtnGo_Click(object sender, RoutedEventArgs e) => TakeToLayer(_transition); // 加为输出图层

        // ============ 画面组：整组切 / 单格切（2026-10-22 老板拍板）============
        //
        // 老板报的毛病：「在全部上输出后，再进行切换其他画面，输出框画面都会出错」。
        // 实测定性（复现脚本 vmtest/verify_zorder.py，判据看黑边占比）：
        //   同一组 2 路，先清空再推 → 黑边 51%（干净的两路分屏）；
        //   前面留着 4 宫格直接推 → 黑边 2%（旧满框画面把画布铺满，新分屏被吃掉）。
        // 根因：输出框原来是个**只增不减的图层栈**，切换只是往上 push 一层，
        // 旧画面从来不下线；只有新画面是满框、刚好盖住全部时才「碰巧」看不出问题。
        //
        // 现在改成「槽位表 + 两种切换粒度」（对齐 vMix 的 Layer 槽位 / OBS 的 Scene，
        // 但两边都不照抄 —— vMix 槽位绑死在 Input 内部做不了"一套输入两套布局"，
        // OBS 的源全局共享，换一格会串到别的场景）：
        //   · 输出框 = 一张槽位表：每个 Layer 是一个槽位（几何 + 绑定的输入 + Z）
        //   · 一次整组上输出 = 一套布局 = 一个 GroupId
        //   · **整组切**：新一套上线时旧一套整组下线；槽位**复用**，层数收敛，不再无限累积
        //   · **单格切**：只改某一格绑定的输入，几何/层级/组归属完全不动
        private int _activeGroupId;      // 当前在播的组号（0 = 没有）
        private int _groupSeq;           // 组号发号器
        /// <summary>
        /// 单格切（老板 2026-10-22 要的）：只把某一格的画面换掉，
        /// 位置 / 大小 / 层级 / 所属组完全不动 —— 导播里最常见的正是
        /// 「四宫格在播，其中一格换成另一路」。
        ///
        /// 为什么能这么轻：2026-10-20 起图层不再持有 MediaPlayer，像素由 MonitorBridge
        /// 按「输入源」缓存解码通路（SyncProgram → FeedFor）。所以换一格只要改 Source
        /// 再同步一次，不用销毁重建任何播放器，也不会闪。
        /// </summary>
        private bool SwapLayerSource(Layer? target, InputSource? src)
        {
            if (target == null || src == null) return false;
            int idx = Layers.IndexOf(target) + 1;
            if (ReferenceEquals(target.Source, src))
            {
                SetStatus($"第 {idx} 格已经是「{src.Name}」，没换", hold: true);
                return false;
            }

            string before = target.DisplayName;
            target.Source = src;
            RenderLayer(target);          // 几何/描边/合成器同步一起走
            ApplyAudioRouting();
            SyncMonitorState();
            SelectLayer(target);
            SetStatus($"⇄ 第 {idx} 格：「{before}」→「{src.Name}」"
                      + $"（布局与其余 {Math.Max(0, Layers.Count - 1)} 格未动）", hold: true);
            return true;
        }

        /// <summary>整组下线：把输出框里现在还亮着的画面整体收掉。</summary>
        private int ReleaseProgramGroup()
        {
            int n = 0;
            foreach (var l in Layers.ToList())
            {
                if (!l.Visible) continue;
                l.Visible = false;      // Visible 带变更通知，右侧列表勾选框会自动跟着变
                RenderLayer(l);
                n++;
            }
            _activeGroupId = 0;
            return n;
        }

        /// <summary>
        /// 把一个已有槽位重新绑定到「预览框里的某一路」（整组切时复用槽位，不新建图层）。
        /// 几何、描边、Z 全部按预览槽的口径重算 —— 做到「预览看到什么，输出就是什么」。
        /// </summary>
        private void RebindLayer(Layer l, PreviewSlot s, int z, int groupId)
        {
            l.Source = s.Source;
            l.X = Math.Max(0, s.X);
            l.Y = Math.Max(0, s.Y);
            // ⚠ Scale ×2：预览槽口径（1 = 铺满）→ 输出图层口径（2 = 铺满）
            l.ScaleX = Math.Clamp(s.ScaleX * 2.0, MinLayerScale, MaxLayerScale);
            l.ScaleY = Math.Clamp(s.ScaleY * 2.0, MinLayerScale, MaxLayerScale);
            l.AspectLock = s.AspectLock;
            l.BorderThickness = s.BorderThickness;
            l.BorderRadius = s.BorderRadius;
            l.BorderColor = s.BorderColor;
            l.Z = z;
            l.GroupId = groupId;
            l.Visible = true;
            ClampLayerPos(l);
            RenderLayer(l);
        }

        /// <summary>把当前 PREVIEW 作为一个新输出图层加入（转场作用于该图层容器的淡入）。</summary>
        private void TakeToLayer(TransitionType type)
        {
            if (_previewSource == null)
            {
                StatusText.Text = "⚠ 请先在左侧单击一个输入输入项作为 PREVIEW";
                return;
            }

            // 真转场（2026-10-22）：必须先抓旧帧、再换画面。
            // C 方案下像素在合成器里，对图层元素做 Opacity 动画在播出画面上是看不见的
            // （老实现就是这样，等于硬切），所以转场交给 MonitorBridge 做整帧淡出。
            if (type != TransitionType.Cut && _monitor != null) _monitor.BeginFade(type, _duration);
            else _monitor?.EndFadeOut();

            // 2026-10-22 整组切：CUT / AUTO / GO 的语义是「换这一套画面」——
            // 旧的那一套整组下线（不再只隐藏满框层，分屏小窗也要下线），新画面作为新组上线。
            // 同时**复用槽位**：单路切换只需要一个满框槽位，多余的旧槽位直接删掉，
            // 否则连切 20 次就把图层数顶到上限（旧实现的无底洞）。
            int groupId = ++_groupSeq;
            ReleaseProgramGroup();

            Layer? layer;
            if (Layers.Count > 0)
            {
                layer = Layers[0];
                layer.Source = _previewSource;
                layer.X = 0;
                layer.Y = 0;
                layer.Scale = FullLayerScale;
                layer.Z = 1;
                layer.GroupId = groupId;
                layer.Visible = true;
                layer.IsAudioSource = true;
                foreach (var extra in Layers.Skip(1).ToList()) RemoveLayer(extra);
                ClampLayerPos(layer);
                RenderLayer(layer);
                ApplyAudioRouting();
                SyncMonitorState();
                LayerList.SelectedItem = layer;
                SelectLayer(layer);
            }
            else
            {
                layer = AddLayerFromPreview();
                if (layer == null) return;
                layer.GroupId = groupId;
            }
            _activeGroupId = groupId;

            // 有合成器时转场已由合成器整帧淡出完成；这里只兜住"没有合成器"的老路径。
            if (_monitor == null && layer.View is FrameworkElement fe)
                TransitionEngine.Run(type, _duration, fe, () => { });
            StatusText.Text = $"▶ 已切到 #{layer.Id}：{layer.DisplayName}（转场：{TransName(type)}）"
                              + " · 上一套画面已整组下线";
        }

        // —— FTB（Fade To Black，vMix 标志性功能）：对整屏输出淡出 ——
        private void BtnFtb_Click(object sender, RoutedEventArgs e)
        {
            _isBlack = !_isBlack;

            // 两套实现，按「有没有真在合成」选，别两个一起上（会变成双重变黑、看着更快更假）：
            //  ① 有合成器（C 方案正常出画）：黑必须落在**成品帧**上 ——
            //     推流 / 录制 / NDI / 全屏输出吃的都是内存里那一帧，界面浮层再黑也传不出去。
            //  ② 没有合成器（老编辑视图 / 合成未启动）：退回界面浮层黑矩形，至少屏幕上是黑的。
            bool composed = _monitor != null && _monitor.Running;
            var a = new DoubleAnimation(_isBlack ? 1.0 : 0.0, TimeSpan.FromSeconds(0.4));

            if (composed)
            {
                // 界面浮层这一段交还给合成器，先把它清干净（可能上次非合成模式留下过黑）
                FtbRect.BeginAnimation(UIElement.OpacityProperty, null);
                FtbRect.Opacity = 0;
                _monitor!.SetFadeToBlack(_isBlack, 0.4);
            }
            else
            {
                if (_monitor != null) _monitor.SetFadeToBlack(_isBlack, 0.4);  // 保持状态一致
                FtbRect.BeginAnimation(UIElement.OpacityProperty, a);
            }

            StatusText.Text = _isBlack ? "⏻ FADE TO BLACK" : "▶ 恢复输出";
        }

        // —— 叠加通道快捷切换（仿 vMix overlay 1-8）——
        private void OverlayCh_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Content is string s && int.TryParse(s, out int ch))
                ToggleOverlayChannel(ch);
        }

        /// <summary>切换某路叠加通道的显示/隐藏（制作条按钮与导播键盘共用）。</summary>
        private void ToggleOverlayChannel(int ch)
        {
            if (ch < 1 || ch > 8 || ch > Overlays.Count) return;
            var o = Overlays[ch - 1];
            if (!o.HasContent)
            {
                StatusText.Text = $"OVERLAY {ch} 为空（请先用 +文字/+图片 填充该通道）";
                return;
            }
            o.Visible = !o.Visible;
            StatusText.Text = $"OVERLAY {ch}：{(o.Visible ? "ON" : "OFF")} — {o.DisplayName}";
        }

        // —— 模块3：叠加层管理（固定 8 通道，+文字/+图片 填充首个空通道）——
        private OverlayItem? TakeEmptyChannel()
        {
            foreach (var o in Overlays)
                if (!o.HasContent) return o;
            return null;
        }

        private void BtnAddText_Click(object sender, RoutedEventArgs e)
        {
            var ch = TakeEmptyChannel();
            if (ch == null)
            {
                StatusText.Text = "8 个叠加通道已满，请先删除一个再添加";
                return;
            }
            ch.Type = OverlayType.Text;
            ch.Text = "文字标题";
            ch.FontSize = 36;
            ch.FontFamilyName = "微软雅黑";
            ch.Bold = false;
            ch.Italic = false;
            ch.XPct = 50;
            ch.YPct = 90;
            ch.ColorHex = "#FFFFFFFF";
            ch.HasContent = true;
            ch.Visible = true;
            OverlayList.SelectedItem = ch;
            StatusText.Text = $"已填充通道 {ch.ChannelIndex}（文字叠加，右侧编辑）";
        }

        private void BtnAddImg_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择图片（透明 PNG 最佳）",
                Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|所有文件|*.*"
            };
            // 显式 Owner，避免对话框跑到主窗口后面；同时临时摘掉鼠标钩子，避免点击被吞
            bool? ok = null;
            RunWithMouseHookDetached(() => ok = dlg.ShowDialog(this));
            if (ok != true) return;
            var ch = TakeEmptyChannel();
            if (ch == null)
            {
                StatusText.Text = "8 个叠加通道已满，请先删除一个再添加";
                return;
            }
            ch.Type = OverlayType.Image;
            ch.ImagePath = dlg.FileName;
            ch.XPct = 50;
            ch.YPct = 50;
            ch.HasContent = true;
            ch.Visible = true;
            OverlayList.SelectedItem = ch;
            StatusText.Text = $"已填充通道 {ch.ChannelIndex}（图片叠加）：{Path.GetFileName(dlg.FileName)}";
        }

        private void OverlayDelete_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.DataContext is OverlayItem o)
            {
                ClearOverlay(o);
                StatusText.Text = $"已清空叠加通道 {o.ChannelIndex}";
            }
        }

        /// <summary>清空某个叠加通道（供列表「×」按钮与菜单「删除选中的叠加层」共用）。</summary>
        private void ClearOverlay(OverlayItem o)
        {
            // 这个通道如果是字幕表占着，清空就等于把字幕表一起收掉，翻页键不该再"翻到空通道"上
            if (_subtitle != null && ReferenceEquals(_subtitle.Channel, o)) _subtitle = null;
            o.HasContent = false;
            o.Visible = true;
            o.ImagePath = null;
            if (_selOverlay == o) { _selOverlay = null; OverlayEditor.IsEnabled = false; }
            SyncOverlays();
        }

        /// <summary>
        /// 一起清空所选的叠加通道（老板 2026-10-19 第 3 条：按住 Ctrl 多选叠加层一起退出）。
        ///
        /// ⚠ 必须先把选中项**快照**成 List 再遍历：ClearOverlay 会改 Overlays / 列表状态，
        ///   边遍历 SelectedItems 边改会抛「集合已更改，枚举器无效」。
        /// </summary>
        private void BtnClearSelOverlays_Click(object sender, RoutedEventArgs e)
        {
            // 老板 2026-10-19 21:06：按钮从「清空所选」改名「清空」，语义也跟着放宽 ——
            //   · 有选中（Ctrl / Shift 多选）→ 只清选中的那几路；
            //   · 什么都没选 → 清空全部 8 路（= 老菜单里的「清空全部叠加层」）。
            // ⚠ SelectedItems 是活集合，ClearOverlay() 会改选中状态 → 必须先 ToList() 快照。
            var picked = OverlayList.SelectedItems.OfType<OverlayItem>()
                                    .Where(o => o.HasContent).ToList();

            bool clearAll = false;
            if (picked.Count == 0)
            {
                picked = Overlays.Where(o => o.HasContent).ToList();
                if (picked.Count == 0)
                {
                    StatusText.Text = "叠加层本来就是空的，没有可清的内容";
                    return;
                }
                clearAll = true;
            }

            var labels = string.Join("、", picked.Select(o => "通道 " + o.ChannelIndex));
            foreach (var o in picked) ClearOverlay(o);
            OverlayList.SelectedItems.Clear();
            StatusText.Text = clearAll
                ? $"✓ 已清空全部叠加层（{picked.Count} 路：{labels}）"
                : $"✓ 已一起清空 {picked.Count} 路叠加：{labels}";
        }

        // —— 叠加层拖动换层级（老板 2026-10-17：「1-8 还可以上下拉动调换位置」）——
        //
        // 交互口径：
        //   · 按住列表条目上下拖、松手即完成换层；
        //   · 换完后**按新顺序重排通道号** —— 位置 1 永远是最上层。这样「1 = 最上层」
        //     这条规则与列表顺序、与输出画面的实际层级三者始终一致，不会互相打架；
        //   · 编号重排后，导播键盘按「OVERLAY 3」切到的就是新版第 3 层，语义不变；
        //   · 只换层级不动内容：文字/图片/位置/颜色全都跟着条目一起走。
        // ⚠ 拖到列表空白处 = 移到最末（最底层），这比"什么都不做"更符合直觉。

        private Point _ovDragStart;
        private OverlayItem? _ovDragItem;

        private void OverlayList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _ovDragStart = e.GetPosition(null);
            _ovDragItem = (ItemsControl.ContainerFromElement(OverlayList, e.OriginalSource as DependencyObject)
                           as ListBoxItem)?.DataContext as OverlayItem;
        }

        private void OverlayList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _ovDragItem == null) return;
            var p = e.GetPosition(null);
            // 拖动阈值：不设的话单击也会被判成拖动，选中会变得很不跟手
            if (Math.Abs(p.X - _ovDragStart.X) < 6 && Math.Abs(p.Y - _ovDragStart.Y) < 6) return;
            DragDrop.DoDragDrop(OverlayList, _ovDragItem, DragDropEffects.Move);
        }

        private void OverlayList_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(typeof(OverlayItem)) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private void OverlayList_Drop(object sender, DragEventArgs e)
        {
            try
            {
                if (e.Data.GetData(typeof(OverlayItem)) is not OverlayItem dragged) return;
                int from = Overlays.IndexOf(dragged);
                if (from < 0) return;

                var container = ItemsControl.ContainerFromElement(OverlayList, e.OriginalSource as DependencyObject);
                var targetItem = (container as ListBoxItem)?.DataContext as OverlayItem;
                int to = targetItem == null ? Overlays.Count - 1 : Overlays.IndexOf(targetItem);
                if (to < 0) to = Overlays.Count - 1;
                if (from == to) return;

                Overlays.Move(from, to);
                RenumberOverlays();

                OverlayList.SelectedItem = dragged;
                SyncOverlays();               // 输出画面上的层级跟着换
                RefreshOverlayButtons();      // 右侧通道条按钮的点亮色也跟着换
                StatusText.Text = $"叠加层级已调整：「{dragged.DisplayName}」移到第 {to + 1} 层"
                                  + (to == 0 ? "（最上层）" : "");
            }
            catch
            {
                // 拖动只是锦上添花的功能，出问题绝不能影响直播
            }
            finally
            {
                _ovDragItem = null;
            }
        }

        /// <summary>按列表当前顺序重排叠加通道号（位置 1 = 最上层）。</summary>
        private void RenumberOverlays()
        {
            for (int i = 0; i < Overlays.Count; i++)
                Overlays[i].ChannelIndex = i + 1;
        }

        private void OverlayList_SelectionChanged(object sender, SelectionChangedEventArgs e)
            => SyncOverlayEditor(OverlayList.SelectedItem as OverlayItem);

        /// <summary>
        /// 把「选中哪一路」的现状刷到编辑区（选中变化、翻页、重渲染后都走这里）。
        ///
        /// 老板 2026-10-19 22:15：「通道编辑也不能动字幕条了」——
        /// 字幕条的位置/大小只由「字幕样式」窗控制（烧进 PNG），
        /// 选中字幕那一路时，这里把位置/缩放滑块**锁死并显示 50 / 50 / 100**。
        /// </summary>
        private void SyncOverlayEditor(OverlayItem? o)
        {
            _selOverlay = o;
            if (_selOverlay == null)
            {
                OverlayEditor.IsEnabled = false;
                EdText.IsEnabled = false;
                SetSubtitleChannelLock(false);
                return;
            }
            // 编辑器整体保持可用；仅未填充/图片类型时把文字框锁掉，文字叠加始终可编辑
            OverlayEditor.IsEnabled = true;
            bool editable = _selOverlay.HasContent;

            // 编辑器标题/边框用当前通道色高亮，呼应 OVERLAYS 面板
            EdHeader.Foreground = _selOverlay.ChannelBrush;
            EdHeader.Text = "通道 " + _selOverlay.ChannelIndex + (editable ? " 编辑" : "（空）");
            OverlayEditor.BorderBrush = _selOverlay.ChannelBrush;

            bool locked = IsSubtitleChannel(_selOverlay);

            if (!editable)
            {
                EdText.IsEnabled = false;
                EdTextGroup.Visibility = Visibility.Visible;
                EdImgGroup.Visibility = Visibility.Collapsed;
                SetSubtitleChannelLock(false);
                return;
            }

            bool isText = _selOverlay.Type == OverlayType.Text;
            // 老板 2026-10-18 第 7 条：图片叠加时面板里不出现任何文字相关控件，
            // 改为显示「图片整体大小」；文字叠加反之。
            EdTextGroup.Visibility = isText ? Visibility.Visible : Visibility.Collapsed;
            EdImgGroup.Visibility = isText ? Visibility.Collapsed : Visibility.Visible;

            EdText.Text = _selOverlay.Text;
            EdFont.Value = _selOverlay.FontSize;
            EdX.Value = locked ? 50 : _selOverlay.XPct;
            EdY.Value = locked ? 50 : _selOverlay.YPct;
            EdImgScale.Value = locked ? 100 : _selOverlay.ImageScale;
            if (EdImgScaleVal != null)
                EdImgScaleVal.Text = (int)Math.Round(EdImgScale.Value) + "%";
            foreach (ComboBoxItem item in EdColor.Items)
                if ((item.Tag as string) == _selOverlay.ColorHex) { EdColor.SelectedItem = item; break; }
            if (EdFontFamily.ItemsSource is IEnumerable<string> fontList)
            {
                var hit = fontList.FirstOrDefault(n => n == _selOverlay.FontFamilyName);
                if (hit != null) EdFontFamily.SelectedItem = hit;
            }
            EdBold.IsChecked = _selOverlay.Bold;
            EdItalic.IsChecked = _selOverlay.Italic;
            EdText.IsEnabled = isText;
            SetSubtitleChannelLock(locked);

            // 文字叠加：点通道即准备直接打字。
            // ⚠ 必须排队到布局之后再聚焦：首次选中时 OverlayEditor 刚从禁用变可用、
            //   ScrollViewer 还没排完，直接 Focus() 会失败 —— 表现正是老板报的
            //   「第一个标题进去改不了字，点第二个回来才行」（老板 2026-10-18 第 4 条）。
            if (isText)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_selOverlay == null || _selOverlay.Type != OverlayType.Text) return;
                    EdText.Focus();
                    EdText.SelectAll();
                }), DispatcherPriority.Input);
            }
        }

        /// <summary>选中的这一路是不是「字幕表」占用的通道。</summary>
        private bool IsSubtitleChannel(OverlayItem? o)
            => o != null && _subtitle?.Channel != null && ReferenceEquals(_subtitle.Channel, o);

        /// <summary>锁定 / 解锁通道编辑里的位置与缩放滑块（字幕条只归「字幕样式」管）。</summary>
        private void SetSubtitleChannelLock(bool locked)
        {
            if (EdX != null) EdX.IsEnabled = !locked;
            if (EdY != null) EdY.IsEnabled = !locked;
            if (EdImgScale != null) EdImgScale.IsEnabled = !locked;
            if (EdLockHint != null) EdLockHint.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
        }

        private void EdText_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_selOverlay != null && _selOverlay.Type == OverlayType.Text)
                _selOverlay.Text = EdText.Text;
        }
        private void EdFont_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_selOverlay != null) _selOverlay.FontSize = (int)EdFont.Value;
        }
        private void EdColor_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_selOverlay != null && EdColor.SelectedItem is ComboBoxItem item)
                _selOverlay.ColorHex = item.Tag as string ?? "#FFFFFFFF";
        }
        private void EdFontFamily_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_selOverlay != null && EdFontFamily.SelectedItem is string name && !string.IsNullOrWhiteSpace(name))
                _selOverlay.FontFamilyName = name;
        }

        /// <summary>
        /// 字体下拉 = 系统已安装的全部字体（老板 2026-10-18 第 4 条）。
        /// 原来只写死 4 个常用字体；系统字体几百个，一次性填进去并按名称排序。
        /// ⚠ 只在构造函数里填一次，别在选中叠加项时重建 —— 重建会把当前选中项清掉。
        /// </summary>
        private void LoadSystemFonts()
        {
            if (EdFontFamily == null) return;
            var names = GetSystemFonts();

            // ⚠ 用 ItemsSource 放**字符串**，不要逐个 Add ComboBoxItem：
            //   几百个 ComboBoxItem 是一次性 UI 元素，既不虚拟化；
            //   在自定义 ComboBox 模板下还会让 SelectionBoxItem 走容器渲染路径，很重。
            //   实测（2026-10-19）：逐个 Add ComboBoxItem 会让进程启动十几秒后静默退出。
            EdFontFamily.ItemsSource = names;

            var def = names.FirstOrDefault(n => n == "微软雅黑") ?? names.FirstOrDefault();
            if (def != null) EdFontFamily.SelectedItem = def;
        }

        /// <summary>系统字体只枚举一次，两个字体下拉（叠加层 / 倒计时牌）共用同一份。</summary>
        private static List<string>? _systemFontCache;

        private static List<string> GetSystemFonts()
        {
            if (_systemFontCache != null) return _systemFontCache;

            List<string> names;
            try
            {
                names = System.Windows.Media.Fonts.SystemFontFamilies
                    .Select(f => f.Source)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.CurrentCultureIgnoreCase)
                    .OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            catch { names = new List<string>(); }

            // 兜底：至少要能选到默认字体（某些系统把「微软雅黑」只报英文名）
            if (!names.Any(n => n.Equals("微软雅黑", StringComparison.CurrentCultureIgnoreCase)))
                names.Insert(0, "微软雅黑");

            _systemFontCache = names;
            return names;
        }

        /// <summary>
        /// 倒计时牌的字体下拉（2026-10-24）。出厂值 Consolas（等宽，数字跳变时不抖），
        /// 但老板想换成任何系统字体都行 —— 换完块宽会变，位置仍按拖过的地方摆。
        /// </summary>
        private void LoadCountdownFonts()
        {
            if (CdFontFamily == null) return;
            var names = GetSystemFonts();
            CdFontFamily.ItemsSource = names;

            var def = names.FirstOrDefault(n => string.Equals(n, _cdStyle.FontFamilyName, StringComparison.CurrentCultureIgnoreCase))
                      ?? names.FirstOrDefault(n => n == "Consolas")
                      ?? names.FirstOrDefault();
            if (def != null) CdFontFamily.SelectedItem = def;

            _cdStyleOpen = false;
            if (CdStyleRows != null) CdStyleRows.Visibility = Visibility.Collapsed;
            SyncCountdownStyleUi();
        }
        private void EdBold_Changed(object sender, RoutedEventArgs e)
        {
            if (_selOverlay != null) _selOverlay.Bold = EdBold.IsChecked == true;
        }
        private void EdItalic_Changed(object sender, RoutedEventArgs e)
        {
            if (_selOverlay != null) _selOverlay.Italic = EdItalic.IsChecked == true;
        }
        private void EdX_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_selOverlay == null) return;
            if (IsSubtitleChannel(_selOverlay)) return;   // 字幕条位置只归「字幕样式」管
            _selOverlay.XPct = EdX.Value;
        }
        private void EdY_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_selOverlay == null) return;
            if (IsSubtitleChannel(_selOverlay)) return;
            _selOverlay.YPct = EdY.Value;
        }
        /// <summary>图片整体大小（老板 2026-10-18 第 7 条）：只对图片叠加生效。</summary>
        private void EdImgScale_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (EdImgScaleVal != null) EdImgScaleVal.Text = (int)Math.Round(EdImgScale.Value) + "%";
            if (_selOverlay != null && _selOverlay.Type == OverlayType.Image
                && !IsSubtitleChannel(_selOverlay))
                _selOverlay.ImageScale = EdImgScale.Value;
        }

        /// <summary>把 Overlays 同步到 OUTPUT 上的叠加画布（含字体/粗斜体）。</summary>
        private void SyncOverlays()
        {
            if (OverlayCanvas == null) return;
            OverlayCanvas.Children.Clear();
            // ⚠ 层级顺序：Overlays[0] = 通道 1，必须是**最上层**。而 Canvas 里后加入的子元素才盖在上面，
            //   所以这里**从后往前**添加 —— 通道 8 先加（在最底）、通道 1 最后加（在最顶）。
            //   老板 2026-10-17：「1 在最上层，然后在程序里也照这个顺序排列」。
            for (int i = Overlays.Count - 1; i >= 0; i--)
            {
                var o = Overlays[i];
                if (!o.IsOn) continue;   // 仅渲染“已上线”的通道（有内容且可见）
                UIElement? el = null;
                if (o.Type == OverlayType.Text)
                {
                    var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(o.ColorHex)!;
                    var tb = new TextBlock
                    {
                        Text = o.Text,
                        FontSize = o.FontSize,
                        FontWeight = o.Bold ? FontWeights.Bold : FontWeights.Normal,
                        FontStyle = o.Italic ? FontStyles.Italic : FontStyles.Normal,
                        FontFamily = new FontFamily(o.FontFamilyName),
                        Foreground = brush,
                        TextWrapping = TextWrapping.Wrap,
                        Effect = new DropShadowEffect
                        {
                            Color = Colors.Black,
                            BlurRadius = 4,
                            ShadowDepth = 1,
                            Opacity = 0.8
                        }
                    };
                    el = tb;
                }
                else if (!string.IsNullOrWhiteSpace(o.ImagePath))
                {
                    try
                    {
                        // 老板 2026-10-18 第 7 条：图片整体大小可调。
                        // 100% = 等比铺满画布且不裁切；>100 放大、<100 缩小，宽高比始终不变。
                        double cw = OverlayCanvas.ActualWidth > 2 ? OverlayCanvas.ActualWidth : 360;
                        double chh = OverlayCanvas.ActualHeight > 2 ? OverlayCanvas.ActualHeight : 360;
                        double k = Math.Clamp(o.ImageScale / 100.0, 0.05, 4.0);
                        // LiveImage 有值 = 样式窗正在拖字幕条 → 用内存里那张实时图，
                        // 不读磁盘（也绕开 BitmapImage 按路径缓存不刷新这个老坑）。
                        ImageSource src = o.LiveImage != null
                            ? (ImageSource)o.LiveImage
                            : new BitmapImage(new Uri(o.ImagePath));
                        var img = new Image
                        {
                            Source = src,
                            Stretch = Stretch.Uniform,
                            MaxWidth = cw * k,
                            MaxHeight = chh * k,
                            Tag = o,          // 实时换图时靠它找回这一路的 Image
                        };
                        // 字幕那一路给个固定 AutomationId：离屏自查「实时图挂上了没有」时按它找
                        if (IsSubtitleChannel(o))
                        {
                            try
                            {
                                System.Windows.Automation.AutomationProperties.SetAutomationId(
                                    img, "SubtitleOverlayImage");
                            }
                            catch { }
                        }
                        el = img;
                    }
                    catch { /* 图片加载失败则跳过该项 */ }
                }
                if (el == null) continue;
                OverlayCanvas.Children.Add(el);
                PositionOverlay(el, o);
            }
        }

        /// <summary>按百分比把元素放到画布上（以元素中心对齐到目标点）。</summary>
        private void PositionOverlay(UIElement el, OverlayItem o)
        {
            el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var w = OverlayCanvas.ActualWidth;
            var h = OverlayCanvas.ActualHeight;
            if (w <= 0 || h <= 0) return;
            var x = o.XPct / 100 * w - el.DesiredSize.Width / 2;
            var y = o.YPct / 100 * h - el.DesiredSize.Height / 2;
            Canvas.SetLeft(el, Math.Max(0, x));
            Canvas.SetTop(el, Math.Max(0, y));
        }

        /// <summary>
        /// 刷新制作条上的 OVERLAY 1-8 按钮外观：
        /// 通道处于“叠加(ON)”状态时，按钮点亮为该通道专属颜色（与右侧 OVERLAYS 面板一致）；
        /// 不在叠加状态时恢复原始灰底。
        /// </summary>
        private void RefreshOverlayButtons()
        {
            if (BtnOv1 == null) return; // 设计时或未初始化
            var btns = new[] { BtnOv1, BtnOv2, BtnOv3, BtnOv4, BtnOv5, BtnOv6, BtnOv7, BtnOv8 };
            var offBg = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3D)); // VmButton 原始灰
            var offFg = new SolidColorBrush(Color.FromRgb(0xED, 0xED, 0xED));
            for (int i = 0; i < 8; i++)
            {
                var o = Overlays[i];
                var btn = btns[i];
                if (o.IsOn)
                {
                    btn.Background = o.ChannelBrush;          // 点亮为通道色
                    btn.Foreground = new SolidColorBrush(Colors.Black); // 浅色底配深色字
                    btn.FontWeight = FontWeights.Bold;
                }
                else
                {
                    btn.Background = offBg;                   // 恢复原始灰底
                    btn.Foreground = offFg;
                    btn.FontWeight = FontWeights.Normal;
                }
            }
        }

        // ===================== 模块5：输出图层（画中画/分屏） =====================

        private void BtnAddLayer_Click(object sender, RoutedEventArgs e) => AddLayerFromPreview();

        /// <summary>
        /// 「多选一起进预览」：把左侧 Ctrl+勾选的输入一次性摆进**预览框**并自动分屏。
        /// 数量 → 布局：2 左右各半 / 3 一排三个 / 4 田字（正好铺满）/ 5~6 三列两行 / 7~9 三列网格。
        /// 摆好后再用「把预览加入图层」或预览框上的「全部上输出」推到输出。
        /// 老板 2026-10-19 23:24：这个按钮原来直接铺输出、跳过预览，改成先进预览（能先核对一眼）。
        /// ⚠ 一个都不勾时只写状态栏、不弹模态框 —— 导播中弹框会挡播出画面。
        /// </summary>
        private void BtnPickedToPreview_Click(object sender, RoutedEventArgs e)
        {
            var picked = Inputs.Where(i => i.IsPicked).ToList();
            if (picked.Count == 0)
            {
                SetStatus("⚠ 请先按住 Ctrl 单击左侧输入项勾选多个（出现金色「✓选」），再点「多选一起进预览」", hold: true);
                return;
            }

            LoadInputsIntoPreview(picked);
            foreach (var i in Inputs) i.IsPicked = false;
            if (picked.Count > MaxPreviewSlots)
                SetStatus($"预览框最多同时摆 {MaxPreviewSlots} 路，已放入前 {MaxPreviewSlots} 路", hold: true);
        }

        /// <summary>
        /// 自动分屏布局：返回每个画面在画布上的归一化左上角位置与缩放。
        /// 图层尺寸固定为「画布宽 × 0.5×缩放」×「画布高 × 0.5×缩放」，所以缩放=1 时正好是四宫格大小。
        /// </summary>
        private static List<(double x, double y, double scale)> SplitScreenLayout(int n)
        {
            int cols = n <= 1 ? 1 : n == 2 ? 2 : n == 3 ? 3 : n <= 4 ? 2 : 3;
            int rows = (int)Math.Ceiling(n / (double)cols);
            double scale = Math.Min(2.0 / cols, 2.0 / rows);   // 2 = 1 / 0.5
            double cellW = 1.0 / cols, cellH = 1.0 / rows;
            double lw = 0.5 * scale, lh = 0.5 * scale;

            var list = new List<(double, double, double)>();
            for (int i = 0; i < n; i++)
            {
                int r = i / cols, c = i % cols;
                int inRow = Math.Min(cols, n - r * cols);
                double startX = (1 - inRow * cellW) / 2;                 // 末行不满时居中
                double x = startX + c * cellW + (cellW - lw) / 2;
                double y = r * cellH + (cellH - lh) / 2;
                list.Add((Math.Max(0, x), Math.Max(0, y), scale));
            }
            return list;
        }

        private void BtnClearLayers_Click(object sender, RoutedEventArgs e)
        {
            foreach (var l in Layers.ToList()) RemoveLayer(l);
            StatusText.Text = "已清空所有输出图层";
        }

        /// <summary>
        /// 图层区放大 / 还原（2026-10-23）。
        ///
        /// 起因：图层行加了音量滑杆后（每行 46 DIP），右栏只有 300 DIP 宽、1018 DIP 高，
        /// 四个区（叠加层 / 通道条 / 叠加项编辑 / 图层）挤在一起，四宫格要滚三次才看得全。
        ///
        /// 为什么不是可拖动分隔条：Row0（叠加层）和 Row5（图层）之间夹着两行 Auto
        /// （OVERLAY 通道条 + 选中叠加项编辑器），GridSplitter 只认相邻行，拖不动 Auto；
        /// 要真能拖就得在中间插行、重排所有 Grid.Row 序号 —— 风险远大于收益。
        /// 折叠是同一目的下最小的改动：一次点击把上面三块收掉，图层区独占右栏。
        /// </summary>
        private void BtnLayersExpand_Click(object sender, RoutedEventArgs e)
        {
            SetLayersMaximized(!_layersExpanded, announce: true);
        }

        /// <summary>
        /// 设置「图层区放到最大」的实际状态（2026-10-23 抽出来，工程还原也要用）。
        /// ⚠ 不能用「点两下」的做法还原 —— 会来回抖，还会写出两条互相矛盾的状态栏。
        /// </summary>
        private void SetLayersMaximized(bool maximize, bool announce)
        {
            _layersExpanded = maximize;
            if (maximize)
            {
                OverlayRow.Height = new GridLength(0);
                OverlayStripRow.Height = new GridLength(0);
                OverlayEditorRow.Height = new GridLength(0);
                LayersExpandText.Text = "还原";
                if (announce) SetStatus("图层区已放到最大（叠加层区已折叠）", hold: false);
            }
            else
            {
                OverlayRow.Height = new GridLength(1, GridUnitType.Star);
                OverlayStripRow.Height = GridLength.Auto;
                OverlayEditorRow.Height = GridLength.Auto;
                LayersExpandText.Text = "收起上面";
                if (announce) SetStatus("右栏已还原", hold: false);
            }
        }

        private bool _layersExpanded;

        // ===================================================================
        //  倒计时牌（2026-10-24 老板要求）
        //
        //  位置：右栏图层区上面。给「观众看」的倒计时，与模块B 的到点触发器无关：
        //    · 归零不自动停，继续走负数（红字）= 已经超时多久，直到点「停止」；
        //    · 「恢复」把读数退回设定的那个时间；
        //    · 「叠加」把数字写进**成品帧**（推流 / 录制 / NDI / 全屏都带），
        //      不是界面上画一块 —— 界面上画的东西传不出去（铁律 12）。
        // ===================================================================

        private void CdSet_Click(object sender, RoutedEventArgs e)
        {
            string? input = Views.SimpleInputBox.Show(this, "设定倒计时",
                "输入倒计时时长（例：1:00:00 一小时 / 30:00 半小时 / 90 表示 90 分钟 / 45s 表示 45 秒）",
                _cdClock.SetText);
            if (input == null) return;    // 取消

            if (!CountdownClock.TryParseDuration(input, out double sec))
            {
                SetStatus($"⚠ 「{input}」不是合法时长，倒计时没改（还是 {_cdClock.SetText}）", hold: true);
                return;
            }

            _cdClock.Set(sec);
            SetStatus($"⏱ 倒计时已设为 {_cdClock.SetText}", hold: false);
        }

        private void CdStart_Click(object sender, RoutedEventArgs e)
        {
            _cdClock.Start();
            SetStatus($"⏱ 倒计时开始：{_cdClock.Text}", hold: false);
        }

        private void CdStop_Click(object sender, RoutedEventArgs e)
        {
            _cdClock.Stop();
            SetStatus(_cdClock.IsNegative
                ? $"⏱ 已停止（已超时 {CountdownClock.Format(-_cdClock.Remaining)}）"
                : $"⏱ 已停止在 {_cdClock.Text}", hold: false);
        }

        private void CdRestore_Click(object sender, RoutedEventArgs e)
        {
            _cdClock.Restore();
            SetStatus($"⏱ 已恢复成设定的 {_cdClock.SetText}", hold: false);
        }

        private void CdOverlay_Click(object sender, RoutedEventArgs e)
        {
            _cdOverlayOn = !_cdOverlayOn;
            UpdateCountdownUi();
            SetStatus(_cdOverlayOn
                ? "⏱ 倒计时已叠加进输出画面（推流 / 录制 / NDI / 全屏都会带上）"
                : "⏱ 倒计时已取消叠加（只在右栏显示）", hold: false);
        }

        private void CdFont_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (CdTimeText == null) return;      // XAML 初始化阶段控件还没建好
            _cdFontPx = Math.Round(e.NewValue);
            UpdateCountdownUi();
        }

        /// <summary>「居中」：把叠加的数字放回画面正中（拖歪了 / 换了分辨率之后一句话回位）。</summary>
        private void CdCenter_Click(object sender, RoutedEventArgs e)
        {
            _cdX = _cdY = 0.5;
            UpdateCountdownUi();
            SetStatus("⏱ 倒计时已回到画面正中", hold: false);
        }

        // ── 样式（2026-10-24 老板「做字体/颜色自定义」）──

        /// <summary>展开 / 收起样式两行。默认收起：右栏本来就不宽裕，不常用的东西不占常驻高度。</summary>
        private void CdStyle_Click(object sender, RoutedEventArgs e)
        {
            _cdStyleOpen = !_cdStyleOpen;
            if (CdStyleRows != null)
                CdStyleRows.Visibility = _cdStyleOpen ? Visibility.Visible : Visibility.Collapsed;
            if (CdStyleBtn != null) CdStyleBtn.Content = _cdStyleOpen ? "收起" : "样式";
        }

        /// <summary>同步样式控件时抬起来：期间勾选框的 Checked/Unchecked 一律不写回样式。</summary>
        private bool _cdStyleSyncing;

        private void CdFontFamily_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (CdTimeText == null || _cdStyleSyncing) return;
            if (CdFontFamily?.SelectedItem is string name && !string.IsNullOrWhiteSpace(name))
            {
                _cdStyle.FontFamilyName = name;
                UpdateCountdownUi();
            }
        }

        private void CdFontStyle_Changed(object sender, RoutedEventArgs e)
        {
            if (CdTimeText == null || _cdStyleSyncing) return;
            _cdStyle.Bold = CdBold?.IsChecked == true;
            _cdStyle.Italic = CdItalic?.IsChecked == true;
            UpdateCountdownUi();
        }

        private void CdColor_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (CdTimeText == null || _cdStyleSyncing) return;
            if (CdColor?.SelectedItem is ComboBoxItem it && it.Tag is string hex)
            {
                _cdStyle.TextColorHex = hex;
                UpdateCountdownUi();
            }
        }

        private void CdBack_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (CdTimeText == null || _cdStyleSyncing) return;
            if (CdBack?.SelectedItem is ComboBoxItem it && it.Tag is string hex)
            {
                _cdStyle.BackColorHex = hex;
                UpdateCountdownUi();
            }
        }

        /// <summary>把样式字段的当前值刷到那几个控件上（工程还原 / 初始化都要用）。</summary>
        /// <remarks>
        /// ⚠ 必须抬 <see cref="_cdStyleSyncing"/> 守卫：勾选框一赋值就会触发 Checked/Unchecked，
        ///   而处理器是「两个框一起读」写回样式的 —— 先设 CdBold 会拿着**旧的** CdItalic 把斜体冲成 false，
        ///   等轮到设 CdItalic 时读到的已经是被冲掉的 false（实测：工程里斜体 true，还原后变 false）。
        /// </remarks>
        private void SyncCountdownStyleUi()
        {
            _cdStyleSyncing = true;
            try
            {
                if (CdColor != null)
                {
                    foreach (ComboBoxItem it in CdColor.Items)
                        if ((it.Tag as string) == _cdStyle.TextColorHex) { CdColor.SelectedItem = it; break; }
                }
                if (CdBack != null)
                {
                    foreach (ComboBoxItem it in CdBack.Items)
                        if ((it.Tag as string) == _cdStyle.BackColorHex) { CdBack.SelectedItem = it; break; }
                }
                if (CdBold != null) CdBold.IsChecked = _cdStyle.Bold;
                if (CdItalic != null) CdItalic.IsChecked = _cdStyle.Italic;
                if (CdFontFamily != null && CdFontFamily.ItemsSource is IEnumerable<string> fonts)
                {
                    var hit = fonts.FirstOrDefault(n => string.Equals(n, _cdStyle.FontFamilyName,
                                                                    StringComparison.CurrentCultureIgnoreCase));
                    if (hit != null) CdFontFamily.SelectedItem = hit;
                }
            }
            finally { _cdStyleSyncing = false; }
        }

        /// <summary>
        /// 把当前读数 / 状态刷到右栏，并同步进合成器（叠加开关关着就只是空操作）。
        /// 10Hz 调用，所以这里只做「赋值 + 内容签名没变就跳过渲染」。
        /// </summary>
        private void UpdateCountdownUi()
        {
            if (CdTimeText == null) return;

            string text = _cdClock.Text;
            CdTimeText.Text = text;

            // 面板上直接按当前样式显示（所见即所得：不用盯着输出框才知道颜色/字体对不对）
            try
            {
                CdTimeText.FontFamily = new FontFamily(
                    string.IsNullOrWhiteSpace(_cdStyle.FontFamilyName) ? "Consolas" : _cdStyle.FontFamilyName);
                CdTimeText.FontWeight = _cdStyle.Bold ? FontWeights.Bold : FontWeights.Normal;
                CdTimeText.FontStyle = _cdStyle.Italic ? FontStyles.Italic : FontStyles.Normal;
                var c = Views.CountdownStyle.Parse(
                    _cdClock.IsNegative ? _cdStyle.NegativeColorHex : _cdStyle.TextColorHex, Colors.White);
                CdTimeText.Foreground = new SolidColorBrush(c);
            }
            catch { }   // 字体名/颜色串异常时保留上一次的观感，绝不因为改样式把面板弄崩

            if (CdFontText != null) CdFontText.Text = _cdFontPx.ToString("0");

            // 「叠加」按钮：开着 = 金色（和图层「声音」那个锁定态一个视觉语言）
            if (CdOverlayBtn != null)
            {
                var gold = TryFindResource("Brush.Gold") as Brush ?? Brushes.Gold;
                var goldDim = TryFindResource("Brush.Gold.Dim") as Brush;
                var border = TryFindResource("Brush.Border") as Brush;
                CdOverlayBtn.Background = _cdOverlayOn ? (goldDim ?? gold) : Brushes.Transparent;
                CdOverlayBtn.BorderBrush = _cdOverlayOn ? gold : border;
                CdOverlayBtn.Foreground = _cdOverlayOn ? gold : (TryFindResource("Brush.Text") as Brush ?? Brushes.White);
            }

            // 进成品帧（_monitor 为空 = 合成器没起来，界面照常显示，只是叠不进输出）
            _monitor?.SetCountdown(text, _cdFontPx, _cdOverlayOn, _cdClock.IsNegative, _cdStyle);
            // 位置每次一起推（老板可能刚拖过 / 刚点过「居中」）。只改坐标，不重渲染位图。
            if (_cdOverlayOn) _monitor?.SetCountdownPosition(_cdX, _cdY);
        }

        // ===================================================================
        //  模块 A：播放列表（PlayList）—— 多素材自动顺播（对标 vMix 的 PlayList）
        //
        //  用法：上方挑素材 → 单击输入项设为 PREVIEW →「+ 加入预览源」→ ▶ 开始。
        //  画面呈现：复用「一个专属图层」——切项时只换 Source 再 Play，
        //  不每项都新建/销毁 VideoView（那会闪一下、也吃资源）。
        //  推进方式：项时长 > 0 按秒自动切；= 0 等媒体播完（EndReached）自动切。
        // ===================================================================

        private void InitPlayList()
        {
            _playSvc.ItemActivated += OnPlayItemActivated;

            _playSvc.Finished += () =>
            {
                // 播完停在最后一帧（不删图层），要清掉画面请点 ⏹ 停止
                StatusText.Text = "■ 播放列表已播完最后一项（画面停在最后一帧）";
                PlayStateText.Text = "已结束";
            };

            _playSvc.ProgressTick += (elapsed, remain) =>
            {
                if (!_playSvc.IsRunning) return;
                PlayStateText.Text = remain > 0
                    ? $"{_playSvc.CurrentIndex + 1}/{_playSvc.Count} · 余 {remain:0.0}s"
                    : $"{_playSvc.CurrentIndex + 1}/{_playSvc.Count} · 已 {elapsed:0.0}s";
            };

            _playSvc.StateChanged += RefreshPlayButtons;
            RefreshPlayButtons();
        }

        /// <summary>刷新播放列表的按钮可用态与状态文案。</summary>
        private void RefreshPlayButtons()
        {
            bool run = _playSvc.IsRunning;
            BtnPlayPause.Content = _playSvc.IsPaused ? "▶" : "❙❙";
            BtnPlayStop.IsEnabled = run;
            BtnPlayPause.IsEnabled = run;
            BtnPlayPrev.IsEnabled = run;
            BtnPlayNext.IsEnabled = run;

            if (!run)
                PlayStateText.Text = PlayItems.Count == 0
                    ? "就绪（请先加入素材）"
                    : $"就绪 · 共 {PlayItems.Count} 项";
            else if (_playSvc.IsPaused)
                PlayStateText.Text = "已暂停";
        }

        /// <summary>把当前 PREVIEW 的输入加入播放列表末尾。</summary>
        private void BtnPlayAdd_Click(object sender, RoutedEventArgs e)
        {
            if (_previewSource == null)
            {
                StatusText.Text = "⚠ 请先在左侧单击一个输入输入项作为 PREVIEW，再点「+ 加入预览源」";
                return;
            }
            // 同一素材允许重复加入（连播里重复播同一个片头是常见需求）
            // 老板 2026-10-18 第 2 条：默认时长改成媒体文件的**原始时长**，不再固定 5 秒。
            double dur = ProbeDurationSec(_previewSource);
            PlayItems.Add(new PlayListItem { Source = _previewSource, DurationSec = dur });
            RefreshPlayOrders();
            PlayListBox.SelectedIndex = PlayItems.Count - 1;
            RefreshPlayButtons();
            StatusText.Text = $"✓ 已加入播放列表第 {PlayItems.Count} 项：{_previewSource.Name}"
                              + (dur > 0 ? $"（原始时长 {dur:0.#} 秒）" : "（播完自动下一项）");
        }

        /// <summary>
        /// 读媒体文件的真实时长（秒）。老板 2026-10-18 第 2 条：加入播放列表应为原始时长。
        /// 只有本地文件才有「总时长」概念；摄像头/桌面/网络流返回 0（= 播完模式，不会到点硬切）。
        /// 探测失败也返回 0：对视频文件「播完自动下一项」比瞎猜 5 秒更符合直觉。
        /// </summary>
        private static double ProbeDurationSec(InputSource? src)
        {
            if (src == null || src.Type != SourceType.File) return 0;
            var path = src.Location;
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return 0;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("C:\\FFMPEG\\bin\\ffprobe.exe")
                {
                    Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{path}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    // 铁律：读 ffmpeg 系管道必须用 UTF-8，否则中文路径的报错按 GBK 解码会乱码
                    StandardErrorEncoding = System.Text.Encoding.UTF8,
                };
                using var p = System.Diagnostics.Process.Start(psi);
                if (p == null) return 0;
                string outp = p.StandardOutput.ReadToEnd();
                p.WaitForExit(3000);
                if (double.TryParse(outp.Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var sec) && sec > 0.1)
                    return Math.Round(sec, 2);
            }
            catch { }
            return 0;
        }

        private void BtnPlayDel_Click(object sender, RoutedEventArgs e)
        {
            if (PlayListBox.SelectedItem is not PlayListItem it)
            {
                StatusText.Text = "⚠ 请先在播放列表里点选要移除的那一项";
                return;
            }
            bool wasRunning = _playSvc.IsRunning;
            if (wasRunning) _playSvc.Stop();     // 运行中改列表会打乱下标，先停再说
            PlayItems.Remove(it);
            RefreshPlayOrders();
            RefreshPlayButtons();
            StatusText.Text = wasRunning
                ? $"已移除「{it.DisplayName}」；播放列表运行中不支持改序，已自动停止，请重新点 ▶ 开始"
                : $"已从播放列表移除「{it.DisplayName}」";
        }

        private void BtnPlayStart_Click(object sender, RoutedEventArgs e)
        {
            if (PlayItems.Count == 0)
            {
                StatusText.Text = "⚠ 播放列表为空：先在左侧点选素材，再点「+ 加入预览源」";
                return;
            }
            // ▶ 一律从第 1 项开始播（符合「播放列表从头放」的直觉）。
            // 想从中间某项起播 → 双击那一项。
            _playSvc.Loop = ChkPlayLoop.IsChecked == true;
            _playSvc.Start(PlayItems.ToList(), 0);   // 传快照：运行期改动列表需重开
            StatusText.Text = $"▶ 播放列表开始：共 {PlayItems.Count} 项"
                              + (_playSvc.Loop ? "（循环）" : "（播完最后一项自动结束）");
        }

        private void BtnPlayPause_Click(object sender, RoutedEventArgs e)
        {
            _playSvc.TogglePause();
            StatusText.Text = _playSvc.IsPaused ? "❙❙ 播放列表已暂停" : "▶ 播放列表继续";
        }

        private void BtnPlayStop_Click(object sender, RoutedEventArgs e)
        {
            _playSvc.Stop();
            HideMainLayer();
            StatusText.Text = "■ 播放列表已停止";
        }

        private void BtnPlayPrev_Click(object sender, RoutedEventArgs e) => _playSvc.Prev();
        private void BtnPlayNext_Click(object sender, RoutedEventArgs e) => _playSvc.Next();

        private void ChkPlayLoop_Click(object sender, RoutedEventArgs e)
        {
            _playSvc.Loop = ChkPlayLoop.IsChecked == true;
            StatusText.Text = _playSvc.Loop ? "播放列表：循环播放开" : "播放列表：循环播放关";
        }

        private void BtnPlayDur_Click(object sender, RoutedEventArgs e) => ApplyPlayDuration();

        private void TxtPlayDur_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { ApplyPlayDuration(); e.Handled = true; }
        }

        /// <summary>把「时长」输入框的秒数应用到选中的列表项（0 = 播完自动下一项）。</summary>
        private void ApplyPlayDuration()
        {
            if (PlayListBox.SelectedItem is not PlayListItem it)
            {
                StatusText.Text = "⚠ 请先在播放列表里点选一项，再设置时长";
                return;
            }
            if (!double.TryParse(TxtPlayDur.Text.Trim(), out double sec) || sec < 0)
            {
                StatusText.Text = "⚠ 时长要填非负数字（0 = 播完自动下一项）";
                return;
            }
            it.DurationSec = sec;
            StatusText.Text = sec <= 0
                ? $"「{it.DisplayName}」改为：播完自动下一项"
                : $"「{it.DisplayName}」时长设为 {sec:0.#} 秒";
        }

        /// <summary>
        /// 双击列表项：
        ///   播放中 → 立刻跳到该项；未播放 → 从该项开始整轮播放（▶ 则是从第 1 项开始）。
        /// </summary>
        private void PlayListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            int idx = PlayListBox.SelectedIndex;
            if (idx < 0) return;
            if (!_playSvc.IsRunning)
            {
                _playSvc.Loop = ChkPlayLoop.IsChecked == true;
                _playSvc.Start(PlayItems.ToList(), idx);
                StatusText.Text = $"▶ 播放列表：从第 {idx + 1} 项开始";
                return;
            }
            _playSvc.JumpTo(idx);
        }

        private void RefreshPlayOrders()
        {
            for (int i = 0; i < PlayItems.Count; i++) PlayItems[i].Order = i + 1;
        }

        /// <summary>
        /// 播放列表切到某一项：把该项的输入源推到「主画面图层」上播出来。
        /// </summary>
        private void OnPlayItemActivated(PlayListItem item, int index)
        {
            try
            {
                if (ShowMainLayer(item.Source) == null)
                {
                    StatusText.Text = $"⚠ 已达图层上限（{_engine.MaxLayers}），播放列表无法出画面";
                    _playSvc.Stop();
                    return;
                }
                ProgramNameText.Text = "播放列表：" + item.DisplayName;
                // 结果类提示：加保护窗，免得被随后回来的预览 Opening/Playing 回调覆盖掉
                SetStatus($"▶ 播放列表 {index + 1}/{_playSvc.Count}：{item.DisplayName}"
                          + (item.DurationSec > 0
                                ? $"（{item.DurationSec:0.#} 秒后自动下一项）"
                                : "（播完自动下一项）"), hold: true);
            }
            catch (Exception ex)
            {
                StatusText.Text = "播放列表切项失败：" + ex.Message;
            }
        }

        /// <summary>
        /// 让「主画面图层」显示指定输入：全屏铺满、压在最上层，且**复用同一个图层**。
        /// 播放列表切项、倒计时触发「切换到指定输入」都走这里 —— 复用而不是每次新建，
        /// 既避免越切图层越多，也避免画面闪一下。
        /// ⚠ Scale 必须给 2：图层尺寸 = 画布宽 × 0.5 × Scale（见 LayerRect），
        ///   只有 Scale=2 才等于铺满整个输出框；给 1 只会占四宫格大小的左上 1/4。
        /// </summary>
        private Layer? ShowMainLayer(InputSource src)
        {
            if (_mainLayer == null || !Layers.Contains(_mainLayer))
            {
                // 首次使用，或用户中途把图层清空了 → 重建。
                // 只有画布上一个图层都没有时才让它当音源，避免抢掉用户已配好的音频路由
                _mainLayer = AddLayerFromSource(src, 0, 0, 2, Layers.Count + 1,
                                                true, Layers.Count == 0, false);
                if (_mainLayer != null) AttachPlayListMediaEnd(_mainLayer);
                return _mainLayer;
            }

            // 复用图层：换源重播，并抬到最上层（否则会被别的图层盖住看不见）
            _mainLayer.Source = src;
            _mainLayer.Z = Layers.Count + 1;
            _mainLayer.Visible = true;
            var player = _mainLayer.Player;
            if (player != null)
            {
                try { player.Play(_engine.CreateMedia(src)); }
                catch
                {
                    // 「上一个已播完」时播放器可能停在 Ended 态，先 Stop 再 Play
                    try { player.Stop(); player.Play(_engine.CreateMedia(src)); } catch { }
                }
            }
            _mainLayer.Raise(nameof(Layer.Source));   // 刷新图层列表里显示的名字
            RenderLayer(_mainLayer);
            ApplyAudioRouting();
            return _mainLayer;
        }

        /// <summary>
        /// 给播放列表图层挂「媒体播完」回调 —— 这是「时长=0 自动顺播」的驱动力。
        /// ⚠ VLC 的 EndReached 是在解码线程回调的，里面直接调 Play/Stop 会死锁；
        ///    所以只做一件事：把推进请求丢回 UI 线程异步执行。
        /// </summary>
        private void AttachPlayListMediaEnd(Layer layer)
        {
            var player = layer.Player;

            // A 方案：图层自己没有播放器了，EndReached 要挂到合成器那路解码上，
            // 否则「播完自动下一项」会静默失效（时长=0 的项会一直停在最后一帧）。
            if (player == null && _monitor != null && layer.Source != null)
            {
                try { player = _monitor.FeedFor(layer.Source)?.Player; } catch { }
            }
            if (player == null) return;
            player.EndReached += (_, _) =>
            {
                Task.Run(() => Dispatcher.Invoke(() =>
                {
                    try { _playSvc.NotifyMediaEnded(); } catch { }
                }));
            };
        }

        /// <summary>
        /// 收起播放列表图层：停止并删除。
        /// 删除而不是隐藏 —— 否则「停止」之后画面还挂在输出上，用户会以为没停干净。
        /// 下次点 ▶ 会自动重建。
        /// </summary>
        private void HideMainLayer()
        {
            if (_mainLayer == null) return;
            var l = _mainLayer;
            _mainLayer = null;
            try { l.Player?.Stop(); } catch { }
            RemoveLayer(l);
            ApplyAudioRouting();
        }

        // ===================================================================
        //  模块 B：倒计时 / 触发器（对标 vMix 的 Countdown + OnCountdownTime）
        //
        //  分工：CountdownWindow 只改 CountdownService.Config；
        //        到点要做的动作在这里执行 —— 切输入、开叠加通道、启停推流这些
        //        能力都只有主窗口才有，所以让服务抛事件、主窗口接住。
        //  状态栏常驻显示剩余时间 + 一个 ▶/❙❙ 快捷键，不必开着配置窗口也能看。
        // ===================================================================

        /// <summary>当前正在被倒计时写入文字的叠加通道（0 = 没有）。</summary>
        private int _cdOverlayChannel;

        private void InitCountdown()
        {
            _countdown.Tick += OnCountdownTick;
            _countdown.Fired += ExecuteCountdownAction;
            _countdown.StateChanged += OnCountdownStateChanged;
            OnCountdownTick();
        }

        private void OnCountdownStateChanged()
        {
            BtnCdQuick.Content = _countdown.IsRunning && !_countdown.IsPaused ? "❙❙" : "▶";
            OnCountdownTick();
        }

        private void OnCountdownTick()
        {
            CountdownText.Text = _countdown.Remaining > 0 ? _countdown.RemainingText : "--:--";
            CountdownText.Foreground = new SolidColorBrush(
                _countdown.IsRunning && !_countdown.IsPaused && _countdown.Remaining <= 10
                    ? Color.FromRgb(0xFF, 0x52, 0x52)    // 最后 10 秒转红
                    : Color.FromRgb(0x4F, 0xC3, 0xF7));
            UpdateCountdownOverlay();
        }

        /// <summary>
        /// 把剩余时间写进配置指定的叠加通道（DisplayOverlayChannel > 0 时）。
        /// 通道是空的就自动补一个文字内容 —— 否则用户会以为「设了显示但画面上什么都没有」。
        /// 只有文本真的变了才写回（PropertyChanged 会带动一遍界面同步，不必每 100ms 空跑一次）。
        /// </summary>
        private void UpdateCountdownOverlay()
        {
            int ch = _countdown.Config.DisplayOverlayChannel;
            if (ch < 1 || ch > 8) { _cdOverlayChannel = 0; return; }
            _cdOverlayChannel = ch;

            var o = Overlays[ch - 1];
            if (!o.HasContent || o.Type != OverlayType.Text)
            {
                o.Type = OverlayType.Text;
                o.XPct = 50;
                o.YPct = 12;
                o.FontSize = 48;
                o.ColorHex = "#FFFF5252";
                o.HasContent = true;
            }
            string text = (_countdown.Config.DisplayFormat ?? "倒计时 {t}")
                          .Replace("{t}", _countdown.RemainingText);
            if (o.Text != text) o.Text = text;
        }

        /// <summary>倒计时归零：按配置执行动作。所有失败情形都给出可读的原因，不静默。</summary>
        private void ExecuteCountdownAction()
        {
            var cfg = _countdown.Config;
            string done;

            switch (cfg.Action)
            {
                case CountdownAction.SwitchToInput:
                {
                    int idx = cfg.TargetIndex - 1;
                    if (idx < 0 || idx >= Inputs.Count)
                    {
                        done = $"⚠ 倒计时到点：输入源 #{cfg.TargetIndex} 不存在（当前共 {Inputs.Count} 路）";
                        break;
                    }
                    var src = Inputs[idx];
                    SetPreview(src);
                    // 走主画面图层（全屏铺满），与播放列表共用同一个图层，不会越切越多
                    if (ShowMainLayer(src) == null)
                    {
                        done = $"⚠ 倒计时到点：已达图层上限（{_engine.MaxLayers}），无法切到输入 #{cfg.TargetIndex}";
                        break;
                    }
                    done = $"⏱ 倒计时到点 → 已切到输入 #{cfg.TargetIndex}：{src.Name}";
                    break;
                }

                case CountdownAction.OverlayOn:
                case CountdownAction.OverlayOff:
                case CountdownAction.OverlayToggle:
                {
                    int ch = cfg.TargetIndex;
                    if (ch < 1 || ch > 8)
                    {
                        done = "⚠ 倒计时到点：叠加通道号要填 1–8";
                        break;
                    }
                    var o = Overlays[ch - 1];
                    if (!o.HasContent)
                    {
                        done = $"⚠ 倒计时到点：叠加通道 {ch} 是空的（先用「+文字」放点内容）";
                        break;
                    }
                    if (cfg.Action == CountdownAction.OverlayOn) o.Visible = true;
                    else if (cfg.Action == CountdownAction.OverlayOff) o.Visible = false;
                    else o.Visible = !o.Visible;
                    done = $"⏱ 倒计时到点 → 叠加通道 {ch}：{(o.Visible ? "ON" : "OFF")}";
                    break;
                }

                case CountdownAction.StartOutput:
                    StartStreaming();
                    SyncMenuState();
                    done = "⏱ 倒计时到点 → 已开始推流 / 录制";
                    break;

                case CountdownAction.StopOutput:
                    StreamService.Instance.Stop();
                    SyncMenuState();
                    done = "⏱ 倒计时到点 → 已停止推流 / 录制";
                    break;

                case CountdownAction.FtbToggle:
                    BtnFtb_Click(this, new RoutedEventArgs());
                    done = _isBlack ? "⏱ 倒计时到点 → 淡黑 FTB 开" : "⏱ 倒计时到点 → 淡黑 FTB 关";
                    break;

                case CountdownAction.PlayListItem:
                    if (!_playSvc.IsRunning)
                    {
                        done = "⚠ 倒计时到点：播放列表没在播放，跳不了项";
                        break;
                    }
                    _playSvc.JumpTo(cfg.TargetIndex - 1);
                    done = $"⏱ 倒计时到点 → 跳到播放列表第 {cfg.TargetIndex} 项";
                    break;

                case CountdownAction.Remind:
                {
                    string msg = string.IsNullOrWhiteSpace(cfg.RemindText) ? "请录制推流" : cfg.RemindText.Trim();
                    if (ShowCountdownReminder(msg))
                    {
                        // 提醒框里点了「是」＝ 顺手把推流 / 录制开起来，省得再跑去菜单找
                        StartStreaming();
                        SyncMenuState();
                        done = $"⏱ 倒计时到点 → 已提醒「{msg}」，并开始推流 / 录制";
                    }
                    else
                    {
                        done = $"⏱ 倒计时到点 → 已提醒：{msg}";
                    }
                    break;
                }

                default:
                    done = "⏱ 倒计时到点（配置为「不做动作」）";
                    break;
            }

            // 结果类提示：加保护窗，免得被随后回来的预览 Opening/Playing 回调覆盖掉
            SetStatus(done, hold: true);
        }

        /// <summary>
        /// 倒计时「弹框提醒」：弹一个**置顶**的系统提醒框，正文就是配置里那行字。
        /// 返回 true = 用户点了「是」（＝立即开始推流 / 录制）。
        ///
        /// 用 Win32 MessageBoxW 而不是 WPF 的 MessageBox.Show：只有前者能挂 <c>MB_TOPMOST</c>，
        /// 提醒框才不会被别的程序盖住 —— 人离开电脑/在别的窗口里干活时，这才是"提醒"。
        ///
        /// ⚠ 这是**阻塞**调用（内部跑嵌套消息泵）。调用方 CountdownService 已保证
        ///   「先把状态收干净再抛 Fired」，所以阻塞期间定时器重入不会再叠出第二个框。
        /// </summary>
        private bool ShowCountdownReminder(string text)
        {
            // 顺手把主窗唤回来：它可能被最小化或被别的程序压住，
            // 提醒框要有个明确的落点，用户点完「是」也能立刻看到主界面。
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;

            var hwnd = new WindowInteropHelper(this).Handle;
            SetForegroundWindow(hwnd);

            // 默认按钮给「否」：避免手快回车就把推流开起来（要开必须真的点一下「是」）
            int r = MessageBoxW(hwnd,
                text + "\n\n点「是」＝ 立即开始推流 / 录制\n点「否」＝ 只关掉提醒，我自己处理",
                "⏱ 倒计时提醒",
                MB_YESNO | MB_ICONWARNING | MB_TOPMOST | MB_SETFOREGROUND | MB_DEFBUTTON2);
            return r == IDYES;
        }

        /// <summary>打开倒计时 / 触发器配置窗口（已开着就激活它）。</summary>
        public void OpenCountdown()
        {
            foreach (Window w0 in Application.Current.Windows)
                if (w0 is CountdownWindow cd) { cd.Activate(); return; }
            new CountdownWindow { Owner = this }.Show();
        }

        private void MenuCountdown_Click(object sender, RoutedEventArgs e) => OpenCountdown();

        /// <summary>状态栏那个 ▶/❙❙ 快捷键：没在跑就开跑，在跑就暂停 / 继续。</summary>
        private void BtnCdQuick_Click(object sender, RoutedEventArgs e)
        {
            if (_countdown.IsRunning)
            {
                _countdown.PauseResume();
                StatusText.Text = _countdown.IsPaused ? "⏱ 倒计时已暂停" : "⏱ 倒计时继续";
            }
            else
            {
                if (_countdown.Config.Mode == CountdownMode.UntilTime)
                {
                    // 到指定时刻：先在状态栏挡住非法输入，不静默
                    if (!CountdownService.TryParseTarget(_countdown.Config.TargetTimeText, out var target))
                    {
                        StatusText.Text = $"⚠ 倒计时的目标时刻「{_countdown.Config.TargetTimeText}」格式不对，"
                                          + "请打开「倒计时」窗口改成 21:00 这类写法";
                        return;
                    }
                    _countdown.Start();
                    StatusText.Text = $"⏱ 倒计时开始：到 {CountdownService.DescribeTarget(target)}"
                                      + DescribeCountdownAction();
                }
                else
                {
                    _countdown.Start();
                    StatusText.Text = $"⏱ 倒计时开始：{_countdown.Config.DurationSec:0.#} 秒"
                                      + DescribeCountdownAction();
                }
            }
        }

        /// <summary>把「到点干什么」写成状态栏里一眼能看懂的一句话（提醒文案会原样带出来）。</summary>
        private string DescribeCountdownAction()
        {
            var cfg = _countdown.Config;
            if (cfg.Action == CountdownAction.None) return "（未配置到点动作）";
            if (cfg.Action == CountdownAction.Remind)
            {
                string msg = string.IsNullOrWhiteSpace(cfg.RemindText) ? "请录制推流" : cfg.RemindText.Trim();
                return $"，到点弹框提醒「{msg}」";
            }
            return "，到点自动执行已配置的动作";
        }

        /// <summary>把当前 PREVIEW 输入作为一个新图层加入输出画布。</summary>
        private Layer? AddLayerFromPreview()
        {
            if (_previewSource == null)
            {
                StatusText.Text = "⚠ 请先在左侧单击一个输入输入项作为 PREVIEW";
                return null;
            }
            if (Layers.Count >= _engine.MaxLayers)
            {
                StatusText.Text = $"⚠ 已达图层上限（{_engine.MaxLayers}），请先删除部分图层";
                return null;
            }
            // ⚠ 缩放必须是 2：图层尺寸 = 画布宽 ×0.5×Scale，只有 Scale=2 才正好铺满输出框。
            //   以前这里是 1 → 切/加进来的画面只有四分之一大小、缩在左上角（老板 2026-10-16 实测报回）。
            return AddLayerFromSource(_previewSource, 0, 0, FullLayerScale, Layers.Count + 1,
                                      true, Layers.Count == 0, true);
        }

        /// <summary>
        /// 按指定来源与几何参数新建一个输出图层（「加入预览」与「载入工程」共用）。
        /// </summary>
        private Layer? AddLayerFromSource(InputSource src, double x, double y, double scale, int z,
                                          bool visible, bool isAudio, bool select)
        {
            if (Layers.Count >= _engine.MaxLayers) return null;

            var layer = new Layer
            {
                Source = src,
                X = x,
                Y = y,
                Scale = Math.Clamp(scale > 0 ? scale : 1, MinLayerScale, MaxLayerScale),
                Z = z > 0 ? z : Layers.Count + 1,
                Visible = visible,
                IsAudioSource = isAudio
            };
            // 统一过一遍「取景框」规则：分屏布局算出来的坐标本来就合规；
            // 预览推上来的坐标也照原样保留（两边 keep 常量一致），这一步只兜住老工程里跑飞了的图层。
            ClampLayerPos(layer);
            // A 方案（2026-10-20）：不再建 MediaPlayer / VideoView。
            // 画面由 MonitorBridge 按「输入源」缓存解码通路（预览与节目共用一路，同一个文件不再解两遍），
            // 声音也从那里出（SetAudible）。layer.Player 保持 null —— 所有引用点都已判空。
            Layers.Add(layer);

            var view = CreateLayerView(layer);
            layer.View = view;
            LayersCanvas.Children.Add(view);
            RenderLayer(layer);
            SyncMonitorState();     // 新图层立刻上屏，不用等 500ms 那轮轮询

            ApplyAudioRouting();

            if (layer.IsAudioSource) ProgramNameText.Text = "音频：" + layer.DisplayName;
            if (select) { LayerList.SelectedItem = layer; SelectLayer(layer); }
            return layer;
        }

        /// <summary>
        /// 图层容器。A 方案起它只是个**透明占位元素** —— 像素由合成器画在底下的 Image 上，
        /// 这里保留元素是因为 RenderLayer / 拖拽命中 / 可见性切换全都基于它的宽高与位置，
        /// 那套逻辑（归一化坐标 → 元素矩形）本来就跟"画面从哪来"无关，直接复用最稳。
        /// </summary>
        private UIElement CreateLayerView(Layer layer)
        {
            return new Grid { Background = Brushes.Transparent, IsHitTestVisible = false };
        }

        /// <summary>返回某图层在画布上的像素矩形（用于几何命中测试）。</summary>
        private Rect LayerRect(Layer layer)
        {
            var w = LayersCanvas.ActualWidth;
            var h = LayersCanvas.ActualHeight;
            double dw = w * 0.5 * layer.ViewScaleX;
            double dh = h * 0.5 * layer.ViewScaleY;
            return new Rect(layer.X * w, layer.Y * h, dw, dh);
        }

        /// <summary>几何命中测试：返回最上层、且包含点 p 的图层（无则 null）。</summary>
        private Layer? HitTestLayer(Point p)
        {
            for (int i = Layers.Count - 1; i >= 0; i--)
            {
                var l = Layers[i];
                if (!l.Visible) continue;
                // 命中矩形向外 8px 容差：图层缩小后面积很小，按在边缘也能抓住拖动
                var r = LayerRect(l);
                r.Inflate(8, 8);
                if (r.Contains(p)) return l;
            }
            return null;
        }

        /// <summary>按归一化位置/缩放/层级把图层容器定位到画布上。</summary>
        private void RenderLayer(Layer layer)
        {
            if (layer.View is not FrameworkElement fe) return;
            var w = LayersCanvas.ActualWidth;
            var h = LayersCanvas.ActualHeight;
            if (w <= 0 || h <= 0) return;
            // ⚠ 元素尺寸只到「铺满画布」（Scale=2）为止；>100% 的放大交给 VLC（VlcZoom）。
            //   理由同预览槽：把 HwndHost 撑大会让 D3D 输出窗排版错乱（实测画面缩到 0.75 倍/只露一角）。
            double dw = w * 0.5 * layer.ViewScaleX;
            double dh = h * 0.5 * layer.ViewScaleY;
            Canvas.SetLeft(fe, layer.X * w);
            Canvas.SetTop(fe, layer.Y * h);
            fe.Width = dw;
            fe.Height = dh;
            Canvas.SetZIndex(fe, layer.Z);
            VlcZoom.Apply(layer.Player, layer.Zoom);
            fe.Visibility = layer.Visible ? Visibility.Visible : Visibility.Collapsed;
            // 取景框：图层允许超出输出画布，但超出部分必须真看不见
            // （节目输出按播出框抓屏虽已天然裁掉，但操作员屏幕上不能糊到右侧栏/制作条上）
            if (layer.Visible) HwndClip.Apply(fe, new Rect(0, 0, w, h));

            SyncLayerBorders();      // 描边跟着图层一起动（拖动时每帧都会走到这儿）

            // A 方案：拖动时画面要跟手 —— 每帧把几何推给合成器（只拷几个 double，开销可忽略）
            if (_monitor != null)
            {
                // 描边/圆角/阴影在界面上是 DIP，合成器要按成品帧像素画，得知道画布多宽
                _monitor.CanvasWidthDip = w;
                try { _monitor.SyncProgram(Layers); } catch { }
            }
        }

        /// <summary>
        /// 把图层的原生视频窗 Z 序同步成 Canvas.ZIndex 的顺序。
        ///
        /// ⚠⚠ 2026-10-18 实测**这条路走不通，已停用，别再打开**：
        ///   调用 SetWindowPos(HWND_TOP) 之后，VLC 那几个内部窗（"VLC video main"，本质是
        ///   SetParent 进来的弹出窗，带标题栏）会被一起提到最上层，**直接飘到桌面上**
        ///   —— 4 个带标题栏的 "VLC (Direct3D11 output)" 窗口盖住整个界面，比原问题严重得多。
        ///
        /// 现在的做法：**不改 z 序**，只在"已有图层"时用更保守的策略：
        ///   「全部上输出」前如果输出框已有可见图层，先提示用户（状态栏给出明确说明），
        ///   由用户决定先清空图层。宁可让用户多点一下，也不能让窗口飘出桌面。
        ///
        /// 仍待解决的已知问题（记在这里，等有更安全的方案再做）：
        ///   先切一路满框上输出 → 再「全部上输出」，新推的 4 个小画面会被旧的满框画面压住
        ///   （图层都在、几何也对，只是 WPF 新建的 HwndHost 子窗被排到 z 序底部）。
        /// </summary>
        private void SyncLayerZOrder()
        {
            // 有意留空：见上面的实测结论。
        }

        /// <summary>
        /// 输出图层的描边（精调面板的粗细/圆角/颜色）。
        ///
        /// 画在 ProgramOvRoot 里那个独立顶层浮层的 LayerBorderCanvas 上 ——
        /// 视频是 Win32 子窗口，画在 LayersCanvas 上的边框会被画面整个盖住。
        /// 按 layer.Id 复用元素：拖动时每帧只改位置，不重建（重建会闪）。
        /// </summary>
        private readonly Dictionary<int, Border> _layerBorders = new();

        private void SyncLayerBorders()
        {
            if (LayerBorderCanvas == null) return;
            double w = LayersCanvas.ActualWidth, h = LayersCanvas.ActualHeight;
            if (w <= 1 || h <= 1) return;

            foreach (var l in Layers)
            {
                bool need = l.Visible && l.BorderThickness > 0.01;
                if (!need)
                {
                    if (_layerBorders.TryGetValue(l.Id, out var dead)) { dead.Visibility = Visibility.Collapsed; }
                    continue;
                }

                if (!_layerBorders.TryGetValue(l.Id, out var bd) || bd == null)
                {
                    bd = new Border
                    {
                        Background = Brushes.Transparent,
                        IsHitTestVisible = false
                    };
                    _layerBorders[l.Id] = bd;
                    LayerBorderCanvas.Children.Add(bd);
                }

                var r = LayerRect(l);
                bd.Visibility = Visibility.Visible;
                bd.Width = r.Width;
                bd.Height = r.Height;
                Canvas.SetLeft(bd, r.X);
                Canvas.SetTop(bd, r.Y);
                bd.BorderThickness = new Thickness(l.BorderThickness);
                bd.BorderBrush = new SolidColorBrush(ParseHexColor(l.BorderColor));
                bd.CornerRadius = new CornerRadius(Math.Max(0, l.BorderRadius));
                Canvas.SetZIndex(bd, l.Z);
            }

            // 图层已经删掉的，把描边一起清掉（否则会留个框在画面上）
            foreach (var id in _layerBorders.Keys.ToList())
            {
                if (Layers.Any(x => x.Id == id)) continue;
                LayerBorderCanvas.Children.Remove(_layerBorders[id]);
                _layerBorders.Remove(id);
            }
        }

        // ============ 低级鼠标钩子辅助：预览框 + 输出图层的拖动/缩放 ============
        // 真正的事件来自系统级 WH_MOUSE_LL 钩子（MouseHookProc），本段仅做几何命中判断。
        //
        // ⚠ 坐标系必须自洽（重要修复）：
        //   钩子给的是「屏幕物理像素」，而 WPF 布局是「窗口内 DIP」。150% 缩放（144 DPI）下
        //   两者相差 1.5 倍：以前把左边统一乘 96/dpi 变成 DIP，却又和 PointToScreen 的物理像素
        //   矩形比较，导致「预览框命中区」被放大约 1.5 倍 —— 正好盖住下方制作条上的
        //   CUT / AUTO / FTB / GO / OVERLAY 等切换键，点击被钩子吞掉，表现为「按键不能用了」。
        //   现在统一走：物理屏幕坐标 →（减客户区原点、除 DPI）→ 客户区 DIP，
        //   所有元素矩形也用布局变换（TransformToAncestor）在客户区 DIP 域比较，不再混用单位。

        /// <summary>客户区左上角在屏幕上的物理像素坐标。</summary>
        private Point ClientOriginPhysical()
        {
            EnsureHwnd();
            if (_hwnd != IntPtr.Zero)
            {
                var p = new POINT { X = 0, Y = 0 };
                if (ClientToScreen(_hwnd, ref p)) return new Point(p.X, p.Y);
            }
            return new Point(0, 0);
        }

        /// <summary>设备像素 → DIP 比例（150% 缩放 = 1.5）。</summary>
        private double DevicePixelPerDip()
        {
            try
            {
                var d = VisualTreeHelper.GetDpi(this);
                if (d.DpiScaleX > 0.01) return d.DpiScaleX;
            }
            catch { }
            EnsureHwnd();
            if (_hwnd != IntPtr.Zero)
            {
                uint dpi = GetDpiForWindow(_hwnd);
                if (dpi > 0) return dpi / 96.0;
            }
            return 1.0;
        }

        /// <summary>屏幕物理像素坐标（钩子入参）→ 主窗口客户区 DIP 坐标。</summary>
        private Point ScreenPhysicalToClientDip(POINT physical)
        {
            var o = ClientOriginPhysical();
            double s = DevicePixelPerDip();
            return new Point((physical.X - o.X) / s, (physical.Y - o.Y) / s);
        }

        /// <summary>元素在「主窗口客户区 DIP」里的矩形（用布局变换，不依赖 PointToScreen）。</summary>
        private bool TryElementRect(FrameworkElement fe, out Rect rect)
        {
            rect = Rect.Empty;
            if (fe == null || fe.ActualWidth <= 0 || fe.ActualHeight <= 0) return false;
            try
            {
                var t = fe.TransformToAncestor(this);
                rect = t.TransformBounds(new Rect(0, 0, fe.ActualWidth, fe.ActualHeight));
                return true;
            }
            catch { return false; }
        }

        /// <summary>客户区 DIP 点 → 某元素的局部坐标（DIP）。</summary>
        private Point ToElementLocal(FrameworkElement fe, Point clientDip)
        {
            try
            {
                var t = fe.TransformToAncestor(this);
                var o = t.Transform(new Point(0, 0));
                var ex = t.Transform(new Point(1, 0));
                var ey = t.Transform(new Point(0, 1));
                double sx = ex.X - o.X, sy = ey.Y - o.Y;
                if (Math.Abs(sx) < 1e-6) sx = 1;
                if (Math.Abs(sy) < 1e-6) sy = 1;
                return new Point((clientDip.X - o.X) / sx, (clientDip.Y - o.Y) / sy);
            }
            catch { return clientDip; }
        }

        /// <summary>光标（客户区 DIP）是否落在某元素矩形内。</summary>
        private bool IsOverElement(FrameworkElement fe, Point clientDip)
            => TryElementRect(fe, out var r) && r.Contains(clientDip);

        /// <summary>
        /// 光标是否落在「交互式控件」上（按钮 / 菜单 / 滑块 / 输入框 / 列表项 / 下拉框 …）。
        /// 这道保险确保钩子绝不会吞掉界面控件的点击 —— 一旦命中就完全放行，交给 WPF 正常处理。
        /// </summary>
        private bool IsOverInteractiveUi(Point clientDip)
        {
            try
            {
                var hit = VisualTreeHelper.HitTest(this, clientDip);
                var d = hit?.VisualHit;
                for (int i = 0; i < 32 && d != null; i++, d = VisualTreeHelper.GetParent(d))
                {
                    if (d is System.Windows.Controls.Primitives.ButtonBase        // Button / RadioButton / CheckBox / ToggleButton
                        || d is System.Windows.Controls.Primitives.TextBoxBase    // TextBox / RichTextBox
                        || d is System.Windows.Controls.Primitives.ScrollBar
                        || d is System.Windows.Controls.Primitives.Selector       // ListBox / ComboBox / TabControl 及其子项
                        || d is MenuItem || d is Menu
                        || d is ComboBox || d is ComboBoxItem
                        || d is Slider
                        || d is System.Windows.Controls.Primitives.StatusBar)
                        return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>把客户区 DIP 坐标转成输出画布坐标并做图层几何命中测试。</summary>
        private bool TryHitLayerAt(Point clientDip, out Layer layer, out Point canvasPt)
        {
            layer = null!;
            canvasPt = default;
            if (LayersCanvas.ActualWidth <= 0 || LayersCanvas.ActualHeight <= 0) return false;
            canvasPt = ToElementLocal(LayersCanvas, clientDip);
            var hit = HitTestLayer(canvasPt);
            if (hit == null) return false;
            layer = hit;
            return true;
        }

        // ══════════════ 倒计时数字的拖动（2026-10-24 老板「还要可以挪动位置」）══════════════

        /// <summary>
        /// 这个点是不是按在叠加的倒计时数字上。
        /// 口径：**叠加开着**才算（关着时画面上没有它，绝不能抢鼠标）；
        /// 坐标先换算到输出画布的像素域，再问合成器那一层的矩形（含 8px 容差）。
        /// </summary>
        private bool HitCountdownAt(Point clientDip)
        {
            if (!_cdOverlayOn || _monitor == null) return false;

            var cp = ToElementLocal(LayersCanvas, clientDip);
            double w = LayersCanvas.ActualWidth, h = LayersCanvas.ActualHeight;
            if (w <= 0 || h <= 0) return false;

            int px = (int)Math.Round(cp.X / w * _monitor.OutW);
            int py = (int)Math.Round(cp.Y / h * _monitor.OutH);
            bool inside = cp.X >= 0 && cp.Y >= 0 && cp.X <= w && cp.Y <= h;
            bool hit = inside && _monitor.CountdownContains(px, py);

            // 诊断日志（2026-10-25 老板报「拖不动」之后加的）：
            // 只在「叠加开着、点落在画布内、却没命中」时记一行 —— 这种情况频率很低（画面空白处），
            // 但正好是拖不动时唯一需要的信息：点在哪、画布多大、数字矩形在哪。
            // 一按就能从日志看出是三种里的哪一种：
            //   ① 数字根本不在画面上（可见=False）② 点在画布外 ③ 数字矩形不含这个点（位置算错）
            if (!hit && inside)
            {
                try
                {
                    var r = _monitor.CountdownRect;
                    ErrorLog.Info($"[倒计时拖动] 未命中：client=({clientDip.X:0},{clientDip.Y:0}) " +
                                  $"画布局部=({cp.X:0},{cp.Y:0}) 画布={w:0}x{h:0} " +
                                  $"帧像素=({px},{py}) 数字矩形=({r.X},{r.Y}) {r.W}x{r.H} " +
                                  $"叠加开={_cdOverlayOn} 数字可见={_monitor.CountdownVisible}");
                }
                catch { }
            }
            return hit;
        }

        /// <summary>
        /// 把倒计时挪到鼠标当前位置（归一化）。与图层拖动同一套手感：
        /// 用「按下时的位置 + 位移」算，而不是每次从当前值累加 —— 手抖也不会漂。
        /// 越界由合成器那边夹住（整块数字不许出画布），夹完再读回来，保证下次拖动起点正确。
        /// </summary>
        // ── 拖动采样合并（2026-10-26）──
        // 目的：让钩子回调保持 O(1)，位置更新由 16ms 定时器统一消费。
        private Point _cdPendingPt;                 // 最新一次 MOVE 的画布局部坐标
        private int _cdPending;                     // 1 = 有未消费的新坐标
        private DispatcherTimer? _cdDragTimer;

        // 画布相对窗口的位置/缩放缓存：MOVE 时线性换算，不再碰视觉树。
        private Point _canvasOrigin;
        private double _canvasScaleX = 1, _canvasScaleY = 1;

        /// <summary>把画布在窗口里的位置/缩放量一次（只在按下、窗口尺寸变化时调，成本可忽略）。</summary>
        private void UpdateCanvasTransformCache()
        {
            try
            {
                var t = LayersCanvas.TransformToAncestor(this);
                var o = t.Transform(new Point(0, 0));
                var ex = t.Transform(new Point(1, 0));
                var ey = t.Transform(new Point(0, 1));
                _canvasOrigin = o;
                _canvasScaleX = Math.Abs(ex.X - o.X) < 1e-6 ? 1 : ex.X - o.X;
                _canvasScaleY = Math.Abs(ey.Y - o.Y) < 1e-6 ? 1 : ey.Y - o.Y;
            }
            catch { _canvasOrigin = new Point(0, 0); _canvasScaleX = _canvasScaleY = 1; }
        }

        /// <summary>客户区 DIP → 画布局部（用缓存，不碰视觉树，钩子回调里也能安全用）。</summary>
        private Point ClientToCanvasLocal(Point clientDip) =>
            new((clientDip.X - _canvasOrigin.X) / _canvasScaleX,
                (clientDip.Y - _canvasOrigin.Y) / _canvasScaleY);

        private void EnsureCdDragTimer()
        {
            if (_cdDragTimer != null) return;
            // Input 优先级：比渲染略高，保证拖动手感优先于重绘
            _cdDragTimer = new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _cdDragTimer.Tick += (_, _) =>
            {
                if (_cdPending == 0) return;
                _cdPending = 0;
                if (!_cdDragging) { _cdDragTimer!.Stop(); return; }
                DragCountdownTo(_cdPendingPt);

                // 诊断（限流每 10 次记一条）：能看出基准/画布宽/坐标是否异常
                if (_cdOverlayOn && (++_cdMoveSeen % 10) == 1)
                {
                    var r = _monitor?.CountdownRect ?? (0, 0, 0, 0);
                    _hookDiag.Enqueue(
                        $"[CD拖/帧{_cdMoveSeen}] 画布点=({_cdPendingPt.X:0},{_cdPendingPt.Y:0}) " +
                        $"起点=({_cdDragStart.X:0},{_cdDragStart.Y:0}) " +
                        $"基准=({_cdDragNx0:0.###},{_cdDragNy0:0.###}) 实际=({_cdX:0.###},{_cdY:0.###}) " +
                        $"画布={LayersCanvas.ActualWidth:0}x{LayersCanvas.ActualHeight:0} " +
                        $"矩形=({r.X},{r.Y}) {r.W}x{r.H}");
                }
            };
        }

        private void DragCountdownTo(Point canvasPt)
        {
            double w = LayersCanvas.ActualWidth, h = LayersCanvas.ActualHeight;
            if (w <= 0 || h <= 0 || _monitor == null) return;

            double nx = _cdDragNx0 + (canvasPt.X - _cdDragStart.X) / w;
            double ny = _cdDragNy0 + (canvasPt.Y - _cdDragStart.Y) / h;
            _monitor.SetCountdownPosition(Math.Clamp(nx, 0, 1), Math.Clamp(ny, 0, 1));

            var (cx, cy) = _monitor.CountdownPos;   // 读回夹过的结果
            _cdX = cx; _cdY = cy;
        }

        private void EnsureHwnd()
        {
            if (_hwnd == IntPtr.Zero)
            {
                try { _hwnd = new WindowInteropHelper(this).Handle; } catch { _hwnd = IntPtr.Zero; }
            }
        }

        // ============ 系统级低级鼠标钩子回调：预览框 + 输出图层的拖动/缩放 ============
        // WH_MOUSE_LL 直接拿到全屏鼠标事件（含 LibVLC 子窗口覆盖区），不受 WPF 空气空间影响。
        // 关键约束：
        //  1) 仅当本程序主窗口处于前台时才接管；其它窗口（打开文件对话框、输出设置、其它程序）
        //     一律放行——既不吞掉对话框鼠标事件（修复“图片无法导入”），也顺手清掉拖拽态防卡死。
        //  2) 左键实际已松开却仍是拖拽态（如本窗口外松手）→ 强制清除，杜绝“窗口在框内乱飞”。
        //  3) 坐标统一到「客户区 DIP」后再比较（修复 150% 缩放下命中区外扩、吞掉制作条按键的问题）。
        //  4) 命中按钮/菜单/滑块/输入框等交互控件时一律放行，钩子只负责预览框与输出画布内的拖动缩放。

        private void InstallMouseHook()
        {
            if (_mouseHook != IntPtr.Zero) return;
            _mouseProc = MouseHookProc;   // 保存委托实例，防止被 GC 回收后回调失效
            try { _mouseHook = SetWindowsHookEx(14 /*WH_MOUSE_LL*/, _mouseProc, IntPtr.Zero, 0); } catch { }

            // 2026-10-26：装失败一直是静默的 —— 一旦失败，画布上的拖动（图层 / 倒计时）全都用不了，
            // 而界面上没有任何提示，只能靠猜。现在成功失败都落一条日志，一查就知道。
            try
            {
                ErrorLog.Info(_mouseHook != IntPtr.Zero
                    ? $"[鼠标钩子] 已安装 pid={Environment.ProcessId}"
                    : "[鼠标钩子] 安装失败！画布上的拖动（图层 / 倒计时）将不可用（常见原因：被安全软件拦）");
            }
            catch { }
        }

        private void RemoveMouseHook()
        {
            if (_mouseHook == IntPtr.Zero) return;
            try { UnhookWindowsHookEx(_mouseHook); } catch { }
            _mouseHook = IntPtr.Zero;
        }

        // ============ 系统级低级键盘钩子（WH_KEYBOARD_LL）：外设导播键盘 / 自定义快捷键 ============
        //
        // 触发决策全在 HotkeyMap（动作 → 按键串的映射表），本钩子只负责"认出这个键该不该管"。
        // 三条必须守住的规矩：
        //  1) **回调里绝不干重活**：低级钩子回调超时会被系统直接摘钩（之后所有键都不再经过我们）。
        //     所以匹配上之后只做一件事 —— 把动作丢给 UI 线程（Dispatcher.BeginInvoke）后立刻返回。
        //  2) **要么完全不管，要么连 KEYUP 一起吃掉**：只吃 KEYDOWN 会让系统留下
        //     "按下没抬起"的错误按键状态（比如 Ctrl 卡住），所以用 _swallowedVk 记账配对。
        //  3) **不抢不该抢的键**：正在录键（HotkeyMap.CaptureMode）、关掉了全局生效、
        //     或者"程序在后台 + 用户绑的是纯字母数字单键"这三种情况一律放行。
        //     第三条是保命的：否则老板在 Word 里打字，后台的导播台会跟着 CUT 和切输入。

        private void InstallKeyHook()
        {
            HotkeyMap.EnsureLoaded();   // 先把映射表读进内存：决不能让"读盘"发生在钩子回调里
            if (_keyHook != IntPtr.Zero) return;
            _keyProc = KeyHookProc;
            try { _keyHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyProc, IntPtr.Zero, 0); } catch { }
            // 钩子装上之前就已经按住的修饰键，记账来不及看到，这里补一次初值
            try
            {
                _modCtrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
                _modShift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
                _modAlt = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
            }
            catch { }
        }

        private void RemoveKeyHook()
        {
            if (_keyHook == IntPtr.Zero) return;
            try { UnhookWindowsHookEx(_keyHook); } catch { }
            _keyHook = IntPtr.Zero;
            Array.Clear(_swallowedVk, 0, _swallowedVk.Length);
        }

        private IntPtr KeyHookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0) return CallNextHookEx(_keyHook, nCode, wParam, lParam);

            try
            {
                int msg = wParam.ToInt32();
                bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

                // 非按键消息（理论上不会有，防御性放行）
                if (!isDown && !isUp) return CallNextHookEx(_keyHook, nCode, wParam, lParam);

                var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                int vk = (int)(kb.vkCode & 0xFF);

                // 修饰键状态：先记账再看别的（记账必须在所有 early-return 之前）
                TrackModifier(vk, isDown);

                if (isUp)
                {
                    // 吃掉的键，抬起时一起吃掉，配对收尾
                    if (_swallowedVk[vk]) { _swallowedVk[vk] = false; return (IntPtr)1; }
                    return CallNextHookEx(_keyHook, nCode, wParam, lParam);
                }

                // —— 以下只处理 KEYDOWN ——

                // ① 正在录键 / 设置窗有焦点：完全放行，让设置窗自己收到这个键
                if (HotkeyMap.CaptureMode) return CallNextHookEx(_keyHook, nCode, wParam, lParam);

                // ② 关掉「全局生效」→ 放行，改由主窗的键盘事件处理（只在主窗前台时可用）
                if (!HotkeyMap.GlobalEnabled) return CallNextHookEx(_keyHook, nCode, wParam, lParam);

                // ③ 修饰键自己在按下时不该被当成"某个动作的按键"
                if (vk == VK_SHIFT || vk == VK_CONTROL || vk == VK_MENU ||
                    (vk >= 0xA0 && vk <= 0xA5) || vk == 0x5B || vk == 0x5C)
                    return CallNextHookEx(_keyHook, nCode, wParam, lParam);

                var key = KeyInterop.KeyFromVirtualKey(vk);
                if (key == Key.None) return CallNextHookEx(_keyHook, nCode, wParam, lParam);

                var combo = HotkeyMap.Combo(key, CurrentModifiers());
                var actionId = HotkeyMap.ActionOfCombo(combo);
                if (actionId == null) return CallNextHookEx(_keyHook, nCode, wParam, lParam);

                // ④ 保命闸门：不带修饰的单键（字母/数字/空格…）只在"主窗前台、且不在输入框里打字"时生效。
                //    后台打字、或主窗输入框里打字，都不能被导播动作抢走。
                if (!HotkeyMap.IsBackgroundSafe(combo))
                {
                    bool foreground = IsForegroundSelf();
                    if (!foreground || IsTypingFocus())
                        return CallNextHookEx(_keyHook, nCode, wParam, lParam);
                }

                // 匹配上 → 记账（吃掉配对的 KEYUP）→ 丢给 UI 线程执行 → 立刻返回，绝不在回调里干活
                _swallowedVk[vk] = true;
                var id = actionId;
                Dispatcher.BeginInvoke(new Action(() => RunHotkeyAction(id)));
                return (IntPtr)1;
            }
            catch
            {
                // 钩子里任何异常都不能扩散（否则系统会摘钩）
            }
            return CallNextHookEx(_keyHook, nCode, wParam, lParam);
        }

        /// <summary>修饰键记账（只认按下/抬起，用事件流而非 GetAsyncKeyState）。</summary>
        private void TrackModifier(int vk, bool down)
        {
            switch (vk)
            {
                case VK_SHIFT: case 0xA0: case 0xA1: _modShift = down; break;
                case VK_CONTROL: case 0xA2: case 0xA3: _modCtrl = down; break;
                case VK_MENU: case 0xA4: case 0xA5: _modAlt = down; break;
            }
        }

        private ModifierKeys CurrentModifiers()
        {
            var m = ModifierKeys.None;
            if (_modCtrl) m |= ModifierKeys.Control;
            if (_modAlt) m |= ModifierKeys.Alt;
            if (_modShift) m |= ModifierKeys.Shift;
            return m;
        }

        /// <summary>主窗是不是当前前台窗口。</summary>
        private bool IsForegroundSelf()
        {
            try { EnsureHwnd(); return GetForegroundWindow() == _hwnd; }
            catch { return false; }
        }

        /// <summary>键盘焦点是不是落在可输入控件上（用户在打字）。钩子回调在 UI 线程，可以问 WPF。</summary>
        private static bool IsTypingFocus()
        {
            try
            {
                var el = Keyboard.FocusedElement;
                return el is System.Windows.Controls.Primitives.TextBoxBase
                    or PasswordBox;
            }
            catch { return false; }
        }

        /// <summary>
        /// 在打开系统对话框（打开文件…）期间临时摘掉全局鼠标钩子，并收起预览浮层。
        ///
        /// 两个遮挡源都要处理掉，否则对话框会有"点不动"的死区：
        ///  ① 全局鼠标钩子：命中测试按「主窗口客户区坐标」算，而对话框是另一个窗口。
        ///     钩子靠 GetForegroundWindow() != _hwnd 放行，焦点切换的瞬间这个判定可能失真，
        ///     那一刻落在对话框上的点击会被当成画布操作吞掉（swallow）。
        ///  ② 预览浮层 PreviewOpPopup：它是 WS_EX_TOPMOST 的独立顶层窗口，而系统对话框是普通窗口，
        ///     顶层窗口在命中测试里永远压过普通窗口（实测该浮层 831×465 的矩形正好盖住
        ///     对话框左侧文件列表，区域内 WindowFromPoint 返回的是浮层而不是对话框）。
        ///     失活收起是治本的（见 Deactivated 与 SyncPreviewPopup），这里再显式收一次兜底。
        ///
        /// 模态对话框本就会禁用主窗口，这几百毫秒里根本没有需要钩子/浮层管的画布操作。
        /// </summary>
        private void RunWithMouseHookDetached(Action action)
        {
            RemoveMouseHook();
            // 键盘钩子同理摘掉：对话框（打开文件 / 保存工程）是别的窗口，
            // 全局快捷键在那一刻不但没用，还可能把对话框里的按键抢走。
            RemoveKeyHook();
            HidePreviewOpPopup();
            try { action(); }
            finally
            {
                InstallMouseHook();
                InstallKeyHook();
                SyncPreviewPopup();   // 对话框关掉后按当前激活状态贴回（不活动就不会开）
            }
        }

        private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                try
                {
                    EnsureHwnd();
                    System.Threading.Interlocked.Increment(ref _hookSeen);   // 累计收到多少个钩子事件（诊断用）
                    const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_MOUSEMOVE = 0x0200, WM_MOUSEWHEEL = 0x020A;
                    int msg = wParam.ToInt32();

                    // ⚠ 关键修复（问题3 拖动失效的根因）：
                    // 左键状态改为「由钩子自身的事件流跟踪」，不再用 GetAsyncKeyState 判断。
                    // 低级钩子回调里 GetAsyncKeyState(VK_LBUTTON) 会稳定返回“未按下”（实测 lbDown=False），
                    // 于是原来的“安全网”会在拖动开始后的第一个 MOVE 就把拖拽态清掉 ——
                    // 表现为「预览框/输出图层按住左键拖不动」。钩子能收到全桌面的按键消息，自己记账最可靠。
                    if (msg == WM_LBUTTONDOWN) _leftDown = true;
                    else if (msg == WM_LBUTTONUP) _leftDown = false;

                    var ms = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    var cd = ScreenPhysicalToClientDip(ms.pt);   // 屏幕物理像素 → 客户区 DIP（统一坐标域）

                    // 左键其实已松开却还留着拖拽态（系统偶发丢 UP）→ 就地自愈，防止"数字/图层跟着鼠标跑"。
                    // 这里可以放心用 _leftDown：它由钩子自己的 DOWN/UP 流记账，不依赖 GetAsyncKeyState。
                    if (!_leftDown && (_cdDragging || _dragLayer != null || _resizing))
                        ClearDragState();

                    // 归属判定（2026-10-26 二次根治）：用「鼠标这一点落在哪个窗口」，不看谁是前台。
                    //   前台判定的问题：叠加层浮窗、打开文件对话框、切换瞬间都会让它误判，
                    //   于是"点自己窗口"被当成"点别的程序"整段放行 → 拖不动。
                    //
                    //   ⚠ 但**只在拖动没开始时判**（这就是上一版仍然拖不动的根因）：
                    //     原来这条判定对每条消息都跑，老板按住数字往边上一拖，鼠标出了本程序窗口范围
                    //     （画布外、分隔条、任务栏、副屏…）→ WindowFromPoint 命中别的进程 →
                    //     ClearDragState() 把 _cdDragging 清掉、后续 MOVE 全丢弃 →
                    //     表现就是「数字只能挪一丁点就停住」（日志实测只走了 19 DIP / 画面的 3%）。
                    //   正确语义：归属判定决定**要不要开始**接管；一旦接管，就负责到松手为止。
                    bool midDrag = _cdDragging || _dragLayer != null || _resizing;
                    if (!midDrag)
                    {
                        IntPtr under = IntPtr.Zero;
                        try { under = WindowFromPoint(ms.pt); } catch { }
                        uint underPid = 0;
                        try { if (under != IntPtr.Zero) GetWindowThreadProcessId(under, out underPid); } catch { }
                        if (underPid != 0 && underPid != (uint)Environment.ProcessId)
                        {
                            ClearDragState();
                            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
                        }
                    }

                    bool swallow = false;

                    if (msg == WM_MOUSEMOVE)
                    {
                        // 安全网：左键已松开却仍是拖拽态（例如在本窗口外松手）→ 清除，防止“窗口乱飞”。
                        // 注意用钩子自己跟踪的 _leftDown，不用 GetAsyncKeyState。
                        // ⚠ 2026-10-26：只清图层拖拽，**绝不碰 _cdDragging** ——
                        //   倒计时拖动有自己的生命周期（DOWN 设 / UP 清）。这里兜底会在
                        //   "按下后先到一条 MOVE"时把刚设好的状态清掉，表现就是按住数字拖不动。
                        if (!_leftDown && (_dragLayer != null || _resizing))
                        {
                            _dragLayer = null;
                            _resizing = false;
                        }
                    }

                    if (msg == WM_LBUTTONDOWN)
                    {
                        // 诊断（2026-10-26）：老板报「叠加后仍拖不动」。只在叠加开着时记一条，
                        // 一条就能看出卡在哪一环：前台 / 画布内 / 控件误判 / 命中 / 数字可见。
                        if (_cdOverlayOn) LogCdDragProbe(cd, "DOWN");

                        // ① 倒计时数字浮在所有东西之上 —— **先**判它。
                        //    2026-10-26 调顺序：原来先判 IsOverInteractiveUi，那条链一旦把画面上
                        //    某层误判成"点在控件上"就直接放行，永远轮不到倒计时（点了没反应）。
                        if (HitCountdownAt(cd))
                        {
                            // 先量一次画布位置（拖动全程用这个缓存做线性换算，钩子回调里不再碰视觉树）
                            UpdateCanvasTransformCache();
                            _cdDragging = true;
                            _cdDragStart = ClientToCanvasLocal(cd);
                            _cdDragNx0 = _cdX; _cdDragNy0 = _cdY;
                            _cdMoveSeen = 0;
                            SetStatus("🖱 拖动倒计时：松手即定位；要回正中点右栏「居中」", hold: false);
                            swallow = true;
                        }
                        // ② 落在按钮 / 菜单 / 滑块 / 输入框等交互控件上 → 一律放行，绝不接管
                        else if (IsOverInteractiveUi(cd))
                        {
                            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
                        }
                        else if (TryHitLayerAt(cd, out var layer, out var cp))
                        {
                            SelectLayer(layer);
                            _dragLayer = layer; _resizing = false;
                            var r = LayerRect(layer);
                            if (Math.Abs(cp.X - (r.X + r.Width)) <= 18 && Math.Abs(cp.Y - (r.Y + r.Height)) <= 18)
                            { _resizing = true; _resizeLeft = r.X; _resizeTop = r.Y; }
                            _dragStart = cp; _dragOriginX = layer.X; _dragOriginY = layer.Y;
                            swallow = true;
                        }
                        else if (_selLayer != null)
                        {
                            // 点输出框空白处 = 取消选中格子（精调面板与"换格目标"一起清掉）。
                            // 2026-10-23 起点左侧输入**不再**换格（只进预览），这个出口保留是为了
                            // 把选中状态彻底清干净，免得精调面板一直指着某一格。
                            var c = ToElementLocal(LayersCanvas, cd);
                            if (c.X >= 0 && c.Y >= 0
                                && c.X <= LayersCanvas.ActualWidth && c.Y <= LayersCanvas.ActualHeight)
                            {
                                _selLayer = null;
                                LayerList.SelectedItem = null;
                                RefreshGeoPanel();
                                SetStatus("已取消选中格子 —— 点左侧输入只做预览；要换某一格请右键那一路 →「换入播出框选中的那一格」", hold: true);
                            }
                        }
                    }
                    else if (msg == WM_MOUSEMOVE)
                    {
                        if (_cdDragging)
                        {
                            // ⚠ 2026-10-26 根治「数字乱动、拖不走」：**钩子回调本身跑在 UI 线程上**
                            //   （WH_MOUSE_LL 的回调在安装它的线程泵消息时执行，我们就是 UI 线程）。
                            //   老板的鼠标每秒派发几百~上千条 MOVE，如果每条都做 TransformToAncestor
                            //   （走视觉树）+ 加锁 + 重算位置，UI 线程会被整个挤满 —— 渲染卡顿、
                            //   数字看着乱跳，而且跟不上手。我自检是 35ms 一步（28Hz）所以看不出来。
                            //   做法：回调里只把「最新画布局部坐标」存下来（几条算术），
                            //   由 16ms 的定时器统一消费一次 —— 无论鼠标多快，UI 只按 60Hz 更新。
                            _cdPendingPt = ClientToCanvasLocal(cd);
                            _cdPending = 1;
                            EnsureCdDragTimer();
                            if (!_cdDragTimer!.IsEnabled) _cdDragTimer.Start();
                            swallow = true;
                        }
                        else if (_dragLayer != null)
                        {
                            var cp = ToElementLocal(LayersCanvas, cd);
                            var w = LayersCanvas.ActualWidth;
                            var h = LayersCanvas.ActualHeight;
                            if (_resizing)
                            {
                                double newW = Math.Max(20, cp.X - _resizeLeft);
                                double s = Math.Clamp(newW / (0.5 * w), MinLayerScale, MaxLayerScale);
                                // 锁比例时高度跟着走（解锁后可以单独拉宽/拉高）
                                double ratio = _dragLayer.ScaleX > 1e-6 ? _dragLayer.ScaleY / _dragLayer.ScaleX : 1.0;
                                _dragLayer.ScaleX = s;
                                _dragLayer.ScaleY = _dragLayer.AspectLock
                                    ? Math.Clamp(s * ratio, MinLayerScale, MaxLayerScale)
                                    : _dragLayer.ScaleY;
                            }
                            else
                            {
                                // 先按鼠标位移算出目标位置，再由 ClampLayerPos 统一夹回输出框内
                                // （与预览框同一规则；拖到边界就停住，不会把图层拖丢到框外）
                                double nx = _dragOriginX + (cp.X - _dragStart.X) / w;
                                double ny = _dragOriginY + (cp.Y - _dragStart.Y) / h;
                                var (sx, sy) = SnapLayerDrag(_dragLayer, nx, ny);   // 静默吸附（不画线）
                                _dragLayer.X = sx;
                                _dragLayer.Y = sy;
                            }
                            ClampLayerPos(_dragLayer);
                            RenderLayer(_dragLayer);
                            swallow = true;
                        }
                    }
                    else if (msg == WM_LBUTTONUP)
                    {
                        // 结束拖拽/缩放并清除状态；若确实在拖拽则吞掉本次 UP，避免误触底层视频
                        bool wasDragging = _dragLayer != null || _cdDragging;

                        // 倒计时：松手前把最后一条坐标消费掉 —— 否则「拖到位置就松手」会丢掉最后几像素
                        if (_cdDragging)
                        {
                            if (_cdPending != 0) { _cdPending = 0; DragCountdownTo(_cdPendingPt); }
                            try { _cdDragTimer?.Stop(); } catch { }
                        }

                        ClearDragState();
                        if (wasDragging) swallow = true;
                    }
                    else if (msg == WM_MOUSEWHEEL)
                    {
                        // 保险：滚轮落在交互控件（滑块 / 下拉 / 列表）上 → 放行给 WPF
                        if (IsOverInteractiveUi(cd))
                            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);

                        int delta = (short)((ms.mouseData >> 16) & 0xFFFF);
                        double factor = delta > 0 ? 1.1 : 0.9;
                        // 预览框的滚轮缩放同样由 PreviewOpPopup 接管（见 PreviewOpRoot_MouseWheel）
                        if (TryHitLayerAt(cd, out var layer, out _))
                        {
                            ZoomLayerAt(layer, factor);
                            swallow = true;
                        }
                    }

                    if (swallow) return (IntPtr)1;
                }
                catch { }   // 钩子内绝不抛异常，避免影响系统输入
            }
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        // ============ 预览框：多路组合（老板 2026-10-17 第③条）============
        //
        // 需求原话：「点击多个文件进预览框，应该是拉动不同文件都可以按自设大小和位置进行排列，
        //            点击切换等按键可以所有画面一起输出到输出框。」
        //
        // 实现口径：
        //   · 预览框里可以同时摆 N 路（N ≤ MaxPreviewSlots），每路一个**专属** MediaPlayer + VideoView；
        //   · 每一路都能单独拖动（按住）/ 缩放（滚轮或拖右下角黄块），互不影响；
        //   · 「全部上输出」把这 N 路连同各自的位置与大小一次性推到输出框。
        //
        // ⚠ 单路（老行为）完全保留：SetPreview() 会把预览组清空、只放一路铺满，
        //   所以「单击输入 → 预览 → GO/CUT」的既有流程一模一样。
        //
        // ⚠ 缩放口径换算（最容易错的一处）：预览槽 Scale = 元素宽 ÷ 画布宽（1 = 铺满）；
        //   输出图层 Scale 却定义成「图层宽 = 画布宽 × 0.5 × Scale」（2 = 铺满）。
        //   于是推上输出时必须 **LayerScale = 2 × SlotScale**，否则大小会差一倍。
        //   位置口径两边一致（都是归一化左上角），可以直接照搬。

        /// <summary>从预览组摘掉一路：摘视图、释放播放器与音频轨道、清理浮层选中框。</summary>
        private void RemovePreviewSlot(PreviewSlot s)
        {
            if (s == null) return;
            if (s.View != null) PreviewCanvas.Children.Remove(s.View);
            if (s.HitBox != null) PreviewOpHitCanvas?.Children.Remove(s.HitBox);
            if (s.Player != null)
            {
                VideoEngine.UnregisterPlayer(s.Player);
                try { s.Player.Stop(); } catch { }
                try { s.Player.Dispose(); } catch { }
            }
            // 总线轨道必须显式回收（CreateLayerPlayer 建的），否则反复摆预览会漏一堆空轨道
            if (!string.IsNullOrEmpty(s.TrackKey))
            {
                try { AudioBus.Instance.RemoveTrack(s.TrackKey); } catch { }
            }
            _previewSlots.Remove(s);
            if (ReferenceEquals(_selPreviewSlot, s)) _selPreviewSlot = null;
            RefreshGeoPanel();
        }

        /// <summary>清空预览组：停播、摘掉视图、释放播放器与音频轨道。</summary>
        private void ClearPreviewSlots()
        {
            foreach (var s in _previewSlots.ToList()) RemovePreviewSlot(s);
            _selPreviewSlot = null;
            if (PreviewResizeGrip != null) PreviewResizeGrip.Visibility = Visibility.Collapsed;
            RefreshGeoPanel();
            HideSnapGuides();
        }

        /// <summary>往预览组加一路（独立播放器 + 独立 VideoView + 浮层里的金色选中框）。</summary>
        private PreviewSlot? AddPreviewSlot(InputSource src, double x, double y, double scale, int z)
        {
            if (src == null || _previewSlots.Count >= MaxPreviewSlots) return null;

            int zz = z > 0 ? z : _previewSlots.Count + 1;
            var slot = new PreviewSlot
            {
                Source = src,
                X = x,
                Y = y,
                Scale = Math.Clamp(scale, 0.1, 4),
                Z = zz,
                TrackKey = "preview:" + zz
            };
            // 用户描边（精调面板设的粗细/圆角/颜色）—— 一直在，与"选中金框"分开叠。
            // 先加进 Children，后加的 Gold 选中框会画在它上面（同 Canvas 里按添加顺序）。
            var userBorder = new Border
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                BorderBrush = Brushes.Transparent,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            slot.BorderEl = userBorder;
            PreviewOpHitCanvas?.Children.Add(userBorder);

            // 浮层里的选中框：未选中时全透明（看不见也点不到，命中判定走几何矩形）
            var box = new Border
            {
                Background = Brushes.Transparent,      // 透明但**非 null**：留着做将来直接点它的余地
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                IsHitTestVisible = false
            };
            slot.HitBox = box;
            PreviewOpHitCanvas?.Children.Add(box);

            // A 方案：预览槽不再各建播放器 / VideoView。
            // 画面由 MonitorBridge 按输入源统一出帧（多路分屏 = 合成器把各路按归一化矩形贴开，
            // 同一路素材在预览和节目之间只解一次）。预览一律不出声，与老行为一致。
            var grid = new Grid { Background = Brushes.Transparent, IsHitTestVisible = false };
            slot.View = grid;

            _previewSlots.Add(slot);
            PreviewCanvas.Children.Add(grid);
            RenderPreview();
            SyncMonitorState();        // 新的一路立刻上屏（不等 500ms 那轮轮询）
            return slot;
        }

        /// <summary>
        /// 把一组输入放进预览框并自动分屏（预览组会被整体替换）。
        /// 单路 = 铺满（与「单击输入 → 预览」完全一致）；多路 = 按数量自动排布。
        /// </summary>
        private void LoadInputsIntoPreview(List<InputSource> sources)
        {
            if (sources == null || sources.Count == 0) return;
            var list = sources.Take(MaxPreviewSlots).ToList();

            ClearPreviewSlots();
            try { _engine.PreviewPlayer.Stop(); } catch { }   // 老的共享预览播放器让位（多路各用专属播放器）

            // 复用输出那边的分屏算法，再把口径换算回预览侧（LayerScale ÷ 2 = SlotScale）。
            var layout = SplitScreenLayout(list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                var s = layout[i];
                AddPreviewSlot(list[i], Math.Max(0, s.x), Math.Max(0, s.y),
                               Math.Clamp(s.scale / 2.0, 0.1, 1.0), i + 1);
            }

            _selPreviewSlot = _previewSlots.FirstOrDefault();
            _geoPanelTargetIsPreview = true;
            _previewSource = list[0];
            foreach (var i in Inputs) i.IsPreview = ReferenceEquals(i, _previewSource);

            PreviewNameText.Text = list.Count == 1
                ? list[0].Name
                : $"{list.Count} 路组合：" + string.Join("、", list.Take(4).Select(s => s.Name))
                  + (list.Count > 4 ? "…" : "");

            RenderPreview();
            SyncPreviewPopup();
            RefreshGeoPanel();
            SetStatus(list.Count == 1
                ? $"○ PREVIEW：{list[0].Name}（可拖出预览框：框内那部分就是会推到输出的取景；滚轮/右下角缩放）"
                : $"○ 预览组 {list.Count} 路：点中某一路可单独拖动/缩放（允许超出框）；「全部上输出」一次推到输出框",
                hold: true);
        }

        /// <summary>
        /// 还原预览框（2026-10-23 老板要的「下次打开工程直接回到上次的设置」）。
        /// 与 <see cref="LoadInputsIntoPreview"/> 的区别：那个是**自动分屏**，这个是**照图纸摆**，
        /// 位置/大小/层级/描边全部按工程里记的原样放回去。
        /// </summary>
        /// <param name="list">工程里的预览组；null 或空 = 老工程 / 当时预览框是空的 → 保持清空。</param>
        private void RestorePreviewSlots(List<ProjectPreviewSlot>? list)
        {
            ClearPreviewSlots();
            _previewSource = null;
            PreviewNameText.Text = "";
            foreach (var i in Inputs) i.IsPreview = false;
            if (list == null || list.Count == 0) return;

            var ordered = list
                .Where(x => x.SourceIndex >= 0 && x.SourceIndex < Inputs.Count)
                .OrderBy(x => x.Z)
                .Take(MaxPreviewSlots)
                .ToList();
            if (ordered.Count == 0) return;

            var placed = new List<PreviewSlot>();
            foreach (var ps in ordered)
            {
                var slot = AddPreviewSlot(Inputs[ps.SourceIndex],
                                          Math.Max(0, ps.X), Math.Max(0, ps.Y),
                                          Math.Clamp(ps.ScaleX > 0 ? ps.ScaleX : 1, 0.1, 4.0),
                                          Math.Max(1, ps.Z));
                if (slot == null) break;
                // AddPreviewSlot 只按一个数设 Scale（宽高一起）；工程里宽高可能不等（精调面板解锁过），
                // 所以这里再把两个方向各自的值补回去，否则还原出来的画面会变形。
                slot.ScaleX = Math.Clamp(ps.ScaleX > 0 ? ps.ScaleX : 1, 0.1, 4.0);
                slot.ScaleY = Math.Clamp(ps.ScaleY > 0 ? ps.ScaleY
                                        : (ps.ScaleX > 0 ? ps.ScaleX : 1), 0.1, 4.0);
                slot.AspectLock = ps.AspectLock;
                slot.BorderThickness = ps.BorderThickness;
                slot.BorderRadius = ps.BorderRadius;
                slot.BorderColor = string.IsNullOrWhiteSpace(ps.BorderColor) ? "#FF33C6F5" : ps.BorderColor;
                placed.Add(slot);
            }
            if (placed.Count == 0) return;

            _selPreviewSlot = placed[0];
            _geoPanelTargetIsPreview = true;
            _previewSource = placed[0].Source;
            foreach (var i in Inputs) i.IsPreview = ReferenceEquals(i, _previewSource);
            PreviewNameText.Text = placed.Count == 1
                ? (placed[0].Source?.Name ?? "")
                : $"{placed.Count} 路组合：" + string.Join("、", placed.Take(4).Select(s => s.Source?.Name))
                  + (placed.Count > 4 ? "…" : "");

            RenderPreview();        // 几何 + 用户描边一起按还原后的值重画
            SyncPreviewPopup();
            RefreshGeoPanel();
            // 工程可能在窗口还没排版时载入（预览框宽高 = 0）→ RenderPreview 会直接返回，
            // 预览画面就一直空着。排一次后续渲染兜住。
            Dispatcher.BeginInvoke(new Action(() =>
            {
                RenderPreview();
                SyncMonitorState();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
            SetStatus($"已还原工程：预览框 {placed.Count} 路（排列与保存时一致）", hold: true);
        }

        /// <summary>「多选进预览」：把左侧 Ctrl+勾选的输入一起放进预览框，自动分屏排列。</summary>
        private void BtnPreviewPicked_Click(object sender, RoutedEventArgs e)
        {
            var picked = Inputs.Where(i => i.IsPicked).ToList();
            if (picked.Count == 0)
            {
                // 这里刻意不弹框：导播途中弹模态框会挡住播出画面。提示只落状态栏。
                SetStatus("⚠ 请先按住 Ctrl 单击左侧输入项勾选多个（出现金色「✓选」），再点「多选一起进预览」", hold: true);
                return;
            }
            LoadInputsIntoPreview(picked);
            foreach (var i in Inputs) i.IsPicked = false;
            if (picked.Count > MaxPreviewSlots)
                SetStatus($"预览框最多同时摆 {MaxPreviewSlots} 路，已放入前 {MaxPreviewSlots} 路", hold: true);
        }

        /// <summary>「清空」：清掉预览框里的所有画面（已经推到输出框的画面不受影响）。</summary>
        private void BtnPreviewClear_Click(object sender, RoutedEventArgs e)
        {
            int n = _previewSlots.Count;
            ClearPreviewSlots();
            try { _engine.PreviewPlayer.Stop(); } catch { }
            _previewSource = null;
            foreach (var i in Inputs) i.IsPreview = false;
            PreviewNameText.Text = "";
            RenderPreview();
            SyncPreviewPopup();
            SetStatus(n == 0 ? "预览框已经是空的" : $"已清空预览框（{n} 路）—— 已推到输出框的画面不受影响", hold: true);
        }

        /// <summary>
        /// 「全部上输出」：把预览框里摆好的每一路连同各自的位置与大小一次性推到输出框。
        /// 首个画面作为音源，其余静音（可在右侧图层列表点 ♪ 改）。
        /// </summary>
        private void BtnPreviewToProgram_Click(object sender, RoutedEventArgs e)
        {
            if (_previewSlots.Count == 0)
            {
                SetStatus("⚠ 预览框是空的，请先把输入放进预览（单击输入项，或 Ctrl 多选后点「多选进预览」）", hold: true);
                return;
            }

            // 2026-10-22 整组切（老板拍板「槽位表 + 整组切/单格切」）：
            //   · 旧的一套画面整组下线（不再只隐藏满框层 —— 那正是"切完画面错乱"的根因）
            //   · 槽位**复用**：新布局有几格就留几格，多余的旧槽位删掉，层数收敛
            // 规划与执行分离：算「复用/新建/删除」的是 Services.ProgramLayout（纯逻辑），
            // 这里只负责动 WPF 元素树与合成器。
            var plan = Services.ProgramLayout.PlanGroupSwitch(Layers, _previewSlots, _engine.MaxLayers, ++_groupSeq);
            int wanted = _previewSlots.Count(s => s.Source != null);
            string? truncNote = plan.Cells.Count < wanted
                ? $"图层上限 {_engine.MaxLayers}，本次只推上前 {plan.Cells.Count} 路"
                : null;
            if (plan.Cells.Count == 0)
            {
                SetStatus("⚠ 预览框里没有可用画面", hold: true);
                return;
            }

            // ① 先删掉多余的旧槽位（降序删，避免下标错位）
            foreach (var i in plan.DropIndexes.OrderByDescending(x => x))
                if (i >= 0 && i < Layers.Count) RemoveLayer(Layers[i]);

            // ② 复用现有槽位：改绑输入 + 按预览槽重算几何/描边（不新建图层）
            foreach (var cell in plan.Cells.Where(c => c.ReuseIndex >= 0))
            {
                if (cell.ReuseIndex >= Layers.Count) continue;
                RebindLayer(Layers[cell.ReuseIndex], cell.Slot, cell.Z, plan.GroupId);
            }

            // ③ 现有槽位不够时才新建
            foreach (var cell in plan.Cells.Where(c => c.ReuseIndex < 0))
            {
                var ly = AddLayerFromSource(cell.Slot.Source!, cell.Slot.X, cell.Slot.Y,
                                            Services.ProgramLayout.ToLayerScale(cell.Slot.ScaleX),
                                            cell.Z, true, false, false);
                if (ly == null) break;      // 到上限就停，别半途崩
                // 宽高可能不等（面板里解锁过比例）+ 描边一起带过去：预览看到什么，输出就是什么
                ly.ScaleY = Math.Clamp(Services.ProgramLayout.ToLayerScale(cell.Slot.ScaleY),
                                       MinLayerScale, MaxLayerScale);
                ly.AspectLock = cell.Slot.AspectLock;
                ly.BorderThickness = cell.Slot.BorderThickness;
                ly.BorderRadius = cell.Slot.BorderRadius;
                ly.BorderColor = cell.Slot.BorderColor;
                ly.GroupId = plan.GroupId;
                ClampLayerPos(ly);
                RenderLayer(ly);
            }

            _activeGroupId = plan.GroupId;

            // 音源（2026-10-23 老板改口）：多路一起推上输出时，**每一路都要出声**，
            // 否则"多选几个文件推上去，结果只有第一格有声音，混音台里也看不到其余几路"。
            // 默认按主次铺好：第一格主音（0dB），其余次音（-8dB）——
            // 一推上去就都能听见，又不会几路原声叠在一起直接削顶；
            // 进混音台后老板再逐个改成主音/背景即可。
            //
            // 2026-10-23 老板：「重复推同一批素材会重置主次」→ 改成**按素材沿用上次**：
            // 这一路以前上过播（手动调过主次/音量）就照上次的来，没上过才用默认。
            // 判据看的是「输入源」而不是「第几格」—— 同一批素材换了顺序主次也跟着走，不会串。
            for (int i = 0; i < Layers.Count; i++)
            {
                var L = Layers[i];
                L.IsAudioSource = true;
                var src = L.Source;
                if (src != null && src.HasAudioMemory)
                {
                    L.AudioRole = src.LastAudioRole;
                    L.Volume = src.LastAudioVolume;
                }
                else
                {
                    L.AudioRole = i == 0 ? AudioRole.Main : AudioRole.Secondary;
                    L.Volume = 100;
                }
            }
            ApplyAudioRouting();
            SyncLayerBorders();
            SyncMonitorState();
            var first = Layers.FirstOrDefault();
            if (first != null) SelectLayer(first);

            int onAir = Layers.Count(l => l.IsAudioSource);
            SetStatus((truncNote
                ?? $"▶ 已把预览框里的 {plan.Cells.Count} 路推到输出框（整组替换：上一套已下线，槽位复用）")
                + (onAir > 1 ? $" · {onAir} 路全部出声（第 1 格主音、其余次音，可在混音台调主次）" : ""),
                hold: true);
        }

        // ===================== P3：输出布局预设（2026-10-22）=====================
        //
        // 一张「槽位表」存下来 = 一整套版面（几何 + 每格绑定的输入 + 描边 + 层级）。
        //「切换」= 整组替换：按预设把输出框整套重建，旧的一套先全部下线。
        //
        // ── 为什么下拉选中不立刻生效 ──
        // WPF 的 ComboBox 被鼠标滚轮滑过就会**静默改选中项**。若"选中即切换"，
        // 导播中滚一下页面就可能把播出画面整套换掉 —— 这是事故，不是便利。
        // 所以：选中只代表"接下来要切哪个"，必须点「切换」才动手。
        //
        // ── 为什么整删重建，而不是复用旧槽位 ──
        // A 方案之后图层只是一个透明占位 Grid（layer.Player 恒为 null，画面由合成器按输入源出），
        // 新建一层的成本就是 new 一个 Grid + Add 到 Canvas。整删重建换来的是
        // 「几何/层级/绑定严格等于预设」这点确定性，比省那几毫秒重要得多。

        private readonly List<ProgramLayoutPreset> _programPresets = new();

        /// <summary>上一次「切换」之前的画面快照（只留一层，够救急）。null = 没有可撤回的。</summary>
        private ProgramLayoutPreset? _layoutUndo;
        private string _layoutUndoLabel = "";

        /// <summary>删除布局的两步确认：记住待删名字与点击时间（3 秒内再点才真删）。</summary>
        private string _layoutDeletePending = "";
        private DateTime _layoutDeleteAt = DateTime.MinValue;

        /// <summary>启动时读入已存的布局（读失败当作没有，绝不影响启动）。</summary>
        private void InitProgramLayouts()
        {
            _programPresets.Clear();
            _programPresets.AddRange(ProgramLayoutStore.LoadAll());
            RefreshLayoutCombo();
        }

        /// <summary>重刷下拉；<paramref name="selectName"/> 为空则尽量保留原来的选中项。</summary>
        private void RefreshLayoutCombo(string? selectName = null)
        {
            string? want = selectName ?? (LayoutCombo.SelectedItem as ProgramLayoutPreset)?.Name;
            LayoutCombo.ItemsSource = null;
            LayoutCombo.ItemsSource = _programPresets;

            int idx = want == null
                ? -1
                : _programPresets.FindIndex(p => string.Equals(p.Name, want, StringComparison.OrdinalIgnoreCase));
            LayoutCombo.SelectedIndex = idx >= 0 ? idx : (_programPresets.Count > 0 ? 0 : -1);
            UpdateLayoutButtons();
        }

        private void UpdateLayoutButtons()
        {
            bool has = LayoutCombo.SelectedItem is ProgramLayoutPreset;
            if (BtnLayoutApply != null) BtnLayoutApply.IsEnabled = has;
            if (BtnLayoutDelete != null) BtnLayoutDelete.IsEnabled = has;
            if (BtnLayoutUndo != null) BtnLayoutUndo.IsEnabled = _layoutUndo != null && _layoutUndo.Cells.Count > 0;
        }

        /// <summary>
        /// 吃掉下拉框上的滚轮：不让它悄悄改选中项，但把这次滚动**转给外层滚动条**，
        /// 否则鼠标停在下拉上滚页面会"卡住"。
        /// </summary>
        private void LayoutCombo_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            if (QuickEntryRow == null) return;
            var up = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = QuickEntryRow
            };
            QuickEntryRow.RaiseEvent(up);
        }

        private void LayoutCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _layoutDeletePending = "";      // 换了选中项，之前的"待删"作废
            UpdateLayoutButtons();
        }

        /// <summary>「存」：把输出框当前整套摆法存成布局（同名覆盖）。</summary>
        private void BtnLayoutSave_Click(object sender, RoutedEventArgs e)
        {
            var visible = Layers.Where(l => l != null && l.Visible).ToList();
            if (visible.Count == 0)
            {
                SetStatus("⚠ 输出框是空的，没有可保存的版面：先把画面推上输出框再存布局", hold: true);
                return;
            }

            string def = (LayoutCombo.SelectedItem as ProgramLayoutPreset)?.Name
                         ?? ("布局 " + DateTime.Now.ToString("MMdd-HHmm"));
            string? name = Views.SimpleInputBox.Show(Window.GetWindow(this),
                "存为输出布局", "给这套版面起个名字（已有同名就覆盖它）：", def);
            if (name == null) return;

            var preset = ProgramLayoutStore.Capture(name.Trim(), Layers, DateTime.Now, _transition, _duration,
                                                    StreamService.Instance.AudioChannels,
                                                    StreamService.Instance.Compressor,
                                                    BuildClockPreset());
            if (preset.Cells.Count == 0)
            {
                SetStatus("⚠ 输出框里没有可见画面，没存成", hold: true);
                return;
            }
            if (!ProgramLayoutStore.Upsert(preset, out string err))
            {
                SetStatus("⚠ 布局保存失败：" + err, hold: true);
                return;
            }

            _programPresets.Clear();
            _programPresets.AddRange(ProgramLayoutStore.LoadAll());
            RefreshLayoutCombo(preset.Name);
            SetStatus($"✓ 已存布局「{preset.Name}」：{preset.Cells.Count} 格（含每格绑定的输入 + 音量）"
                      + $" · 混音 {preset.Mixer?.Count ?? 0} 路 · 压缩 {StreamService.Instance.Compressor.LevelText}"
                      + $" · 转场 {TransName(_transition)} {_duration:0.0}s"
                      + (preset.Clock != null ? $" · 倒计时牌 {(_cdOverlayOn ? "已叠加" : "仅右栏")} {_cdFontPx:0}号" : "")
                      + "。直播中选中它 → 点「切换」即可整套换上", hold: true);
        }

        /// <summary>把当前倒计时牌的显示样式打包成布局快照（2026-10-24，不含设定时长）。</summary>
        private ProgramPresetClock BuildClockPreset() => new()
        {
            FontSize = _cdFontPx,
            FontFamily = _cdStyle.FontFamilyName,
            Bold = _cdStyle.Bold,
            Italic = _cdStyle.Italic,
            TextColor = _cdStyle.TextColorHex,
            BackColor = _cdStyle.BackColorHex,
            X = _cdX,
            Y = _cdY,
            OverlayOn = _cdOverlayOn
        };

        /// <summary>「删」：两步确认（避免直播中手滑把布局删掉）。</summary>
        private void BtnLayoutDelete_Click(object sender, RoutedEventArgs e)
        {
            if (LayoutCombo.SelectedItem is not ProgramLayoutPreset p) return;

            // 3 秒内再点一次才真删；弹模态框在导播中是禁忌，所以用状态栏做确认。
            bool armed = _layoutDeletePending == p.Name &&
                         (DateTime.Now - _layoutDeleteAt).TotalSeconds <= 3;
            if (!armed)
            {
                _layoutDeletePending = p.Name;
                _layoutDeleteAt = DateTime.Now;
                SetStatus($"⚠ 再点一次「删」确认删除布局「{p.Name}」（3 秒内有效；当前画面不受影响）", hold: true);
                return;
            }

            _layoutDeletePending = "";
            if (!ProgramLayoutStore.Delete(p.Name, out string err))
            {
                SetStatus("⚠ 删除失败：" + err, hold: true);
                return;
            }
            _programPresets.Clear();
            _programPresets.AddRange(ProgramLayoutStore.LoadAll());
            RefreshLayoutCombo();
            SetStatus($"✓ 已删除布局「{p.Name}」（当前画面不受影响）", hold: true);
        }

        /// <summary>「导出」：把选中这套布局导成独立文件，发给别人 / 存进素材盘。</summary>
        private void BtnLayoutExport_Click(object sender, RoutedEventArgs e)
        {
            if (LayoutCombo.SelectedItem is not ProgramLayoutPreset p)
            {
                SetStatus("⚠ 先在左边下拉里选中要导出的布局", hold: true);
                return;
            }

            var dlg = new SaveFileDialog
            {
                Title = "导出输出布局",
                Filter = "Cutlance 输出布局 (*.clayout)|*.clayout|JSON 文件 (*.json)|*.json",
                DefaultExt = ProgramLayoutStore.ExportExtension,
                AddExtension = true,
                OverwritePrompt = true,
                FileName = SafeFileStem(p.Name) + ProgramLayoutStore.ExportExtension,
                InitialDirectory = EnsureLayoutDir()
            };
            if (dlg.ShowDialog(this) != true) return;

            if (!ProgramLayoutStore.ExportFile(p, dlg.FileName, out string err))
            {
                SetStatus("⚠ 导出失败：" + err, hold: true);
                return;
            }

            var tr = ProgramLayoutStore.ParseTransition(p.Transition);
            SetStatus($"✓ 已导出布局「{p.Name}」：{p.Cells.Count} 格 · 转场 {TransName(tr)} " +
                      $"{ProgramLayoutStore.ClampSeconds(p.TransitionSeconds):0.0}s → {dlg.FileName}" +
                      "（对方用「导入」即可还原，素材按路径/编号/名字自动认）", hold: true);
        }

        /// <summary>
        /// 「导入」：读一套 .clayout 加进本机列表。
        /// 重名不覆盖本机的（导入方的意思通常是"多一套"，不是"替换我那套"），自动加后缀。
        /// </summary>
        private void BtnLayoutImport_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "导入输出布局",
                Filter = "Cutlance 输出布局 (*.clayout)|*.clayout|JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
                CheckFileExists = true,
                InitialDirectory = EnsureLayoutDir()
            };
            if (dlg.ShowDialog(this) != true) return;

            var p = ProgramLayoutStore.ImportFile(dlg.FileName, out string err);
            if (p == null)
            {
                SetStatus("⚠ 导入失败：" + err, hold: true);
                return;
            }

            var all = ProgramLayoutStore.LoadAll();
            string stem = string.IsNullOrWhiteSpace(p.Name) ? "导入的布局" : p.Name.Trim();
            string name = stem;
            int k = 2;
            while (all.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                name = $"{stem} (导入{k++})";
            p.Name = name;

            if (!ProgramLayoutStore.Upsert(p, out string err2))
            {
                SetStatus("⚠ 导入失败：" + err2, hold: true);
                return;
            }

            _programPresets.Clear();
            _programPresets.AddRange(ProgramLayoutStore.LoadAll());
            RefreshLayoutCombo(p.Name);

            bool renamed = !string.Equals(name, stem, StringComparison.Ordinal);
            var tr = ProgramLayoutStore.ParseTransition(p.Transition);
            SetStatus($"✓ 已导入布局「{p.Name}」：{p.Cells.Count} 格 · 转场 {TransName(tr)} " +
                      $"{ProgramLayoutStore.ClampSeconds(p.TransitionSeconds):0.0}s" +
                      (renamed ? $"（本机已有同名，改名避免覆盖）" : "") +
                      "。选中它 → 点「切换」即可用", hold: true);
        }

        /// <summary>把布局名变成合法文件名（去掉 Windows 路径不允许的字符）。</summary>
        private static string SafeFileStem(string? name)
        {
            string s = string.IsNullOrWhiteSpace(name) ? "输出布局" : name.Trim();
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Length > 60 ? s.Substring(0, 60) : s;
        }

        /// <summary>「切换」：把选中的整套布局换上输出框。</summary>
        private void BtnLayoutApply_Click(object sender, RoutedEventArgs e)
        {
            if (LayoutCombo.SelectedItem is not ProgramLayoutPreset p)
            {
                SetStatus("⚠ 还没选中布局：先在下拉里挑一个，再点「切换」", hold: true);
                return;
            }
            ApplyProgramLayout(p);
        }

        /// <summary>
        /// 快捷键「切到第 N 套布局」——序号 = 下拉里的第 N 项（1 基），与 Ctrl+Alt+1..8 对应。
        /// 真正的"一键切"：导播时不用回头看下拉，也不要求窗口在前台（带修饰键走全局钩子）。
        /// </summary>
        private void ApplyLayoutByNumber(int n)
        {
            if (n < 1 || n > _programPresets.Count)
            {
                SetStatus($"⚠ 没有第 {n} 套输出布局（当前存了 {_programPresets.Count} 套）", hold: true);
                return;
            }
            var p = _programPresets[n - 1];
            LayoutCombo.SelectedItem = p;      // 让下拉也同步，操作员一眼看到现在用的是哪套
            ApplyProgramLayout(p);
        }

        /// <summary>「撤回」：回到上一次切换之前的那套画面。</summary>
        private void BtnLayoutUndo_Click(object sender, RoutedEventArgs e)
        {
            var snap = _layoutUndo;
            if (snap == null || snap.Cells.Count == 0)
            {
                SetStatus("没有可撤回的画面（只记最近一次切换）", hold: true);
                return;
            }
            string back = string.IsNullOrWhiteSpace(_layoutUndoLabel) ? "上一次的画面" : _layoutUndoLabel;
            _layoutUndo = null;                          // 只撤一层，撤完就没了（防来回抖）
            _layoutUndoLabel = "";
            ApplyProgramLayout(snap, recordUndo: false);
            SetStatus($"↩ 已撤回：回到「{back}」", hold: true);
        }

        /// <summary>
        /// 把一整套布局换上输出框。
        /// <paramref name="recordUndo"/>=true 时先给当前画面拍快照（供「撤回」）。
        /// </summary>
        private void ApplyProgramLayout(ProgramLayoutPreset preset, bool recordUndo = true)
        {
            if (preset?.Cells == null || preset.Cells.Count == 0)
            {
                SetStatus("⚠ 这套布局里没有格子（0 格），没有可切换的内容", hold: true);
                return;
            }

            var resolved = ProgramLayoutStore.ResolveAll(preset, Inputs);
            int missing = resolved.Count(s => s == null);
            int usable = resolved.Count - missing;
            if (usable == 0)
            {
                SetStatus($"⚠ 这套布局里的 {preset.Cells.Count} 格素材一个都没找到" +
                          "（换过工程 / 素材改名或换了路径？），没有切换。请先把素材加回左侧列表", hold: true);
                return;
            }

            if (recordUndo)
            {
                // 撤回也要能退回"切换前的混音"，所以快照一并拍下角色档与压缩档
                var snap = ProgramLayoutStore.Capture("__undo__", Layers, DateTime.Now, _transition, _duration,
                                                     StreamService.Instance.AudioChannels,
                                                     StreamService.Instance.Compressor);
                _layoutUndo = snap.Cells.Count > 0 ? snap : null;
                _layoutUndoLabel = "切换前的画面";
            }

            // ⓪ 转场（2026-10-22）：必须是「先抓旧帧、再换画面」，顺序反了就抓成新画面了。
            //    C 方案下像素在合成器里，所以转场交给 MonitorBridge 做整帧淡出，
            //    对界面元素做 Opacity 动画是看不见的（老实现就是这个毛病）。
            var trans = ProgramLayoutStore.ParseTransition(preset.Transition);
            double transSec = ProgramLayoutStore.ClampSeconds(preset.TransitionSeconds);
            if (trans != TransitionType.Cut && _monitor != null) _monitor.BeginFade(trans, transSec);
            else _monitor?.EndFadeOut();

            // ① 旧的一套整组下线（拆干净：元素、描边、音频轨道一起摘）
            foreach (var l in Layers.ToList()) RemoveLayer(l);

            // ② 按预设逐格上线
            int groupId = ++_groupSeq;
            int made = 0;
            bool hitCap = false;
            for (int i = 0; i < preset.Cells.Count; i++)
            {
                var cell = preset.Cells[i];
                var src = resolved[i];
                if (src == null) continue;      // 缺素材：这一格留空（几何位置空着，不建黑层）

                var ly = AddLayerFromSource(src, cell.X, cell.Y, cell.ScaleX,
                                            cell.Z > 0 ? cell.Z : i + 1,
                                            true, cell.IsAudioSource, false);
                if (ly == null) { hitCap = true; break; }

                ly.ScaleY = Math.Clamp(cell.ScaleY, MinLayerScale, MaxLayerScale);
                ly.AspectLock = cell.AspectLock;
                ly.BorderThickness = cell.BorderThickness;
                ly.BorderRadius = cell.BorderRadius;
                ly.BorderColor = cell.BorderColor;
                ly.Opacity = cell.Opacity;    // 透明度随布局还原（老布局默认 100）
                ly.Shadow = cell.Shadow;      // 阴影随布局还原（老布局默认 0 = 无阴影）
                ly.ShadowOpacity = cell.ShadowOpacity;
                ly.FitMode = cell.FitMode;    // 填充方式随布局还原（老布局默认 0 = 等比内接）
                ly.GroupId = groupId;
                ly.Volume = cell.Volume;      // 每格音量随布局还原（老布局文件默认 100 = 原音量）
                // 角色档随布局还原（2026-10-23）：老布局没有这一项 → 空串 → 主音（不衰减），
                // 切过去听到的音量跟升级前完全一致。
                ly.AudioRole = Enum.TryParse<AudioRole>(cell.AudioRole, true, out var lr)
                    ? lr : AudioRole.Main;
                ly.Visible = true;
                ClampLayerPos(ly);
                RenderLayer(ly);
                made++;
            }

            _activeGroupId = groupId;

            // 音频：完全按预设批量套用（老板 2026-10-22 要求）。
            // 每格的 IsAudioSource 已随建层传进去，这里只做一次总路由 —— 不要再"兜底让第一格出声"：
            // 那会把一套故意静音的布局切完自动变有声，等于改用户的预设。
            ApplyAudioRouting();
            SyncLayerBorders();
            SyncMonitorState();
            if (Layers.Count > 0) SelectLayer(Layers[0]);
            UpdateLayoutButtons();

            // 混音台：角色档 / 音量 / 静音 / 压缩档（2026-10-23）。
            // 老布局没有这一段 → 整块不动（不猜着把别人的声音关掉）。
            var mix = ProgramLayoutStore.ApplyMixer(preset, StreamService.Instance.AudioChannels,
                                                    StreamService.Instance.Compressor);
            if (mix.LayoutHadMixer)
            {
                // 混音台窗口开着就同步过去，否则操作员看到的是旧的档位
                foreach (Window w in Application.Current.Windows)
                    if (w is AudioMixerWindow am) am.SyncFromModel();
            }

            // 倒计时牌样式（2026-10-24）：只还原「长什么样 / 摆在哪 / 是否叠加」，
            // **不动设定时长、也不启停计时** —— 切布局不该把正在跑的倒计时重置。
            var pc = preset.Clock;
            if (pc != null)
            {
                _cdFontPx = Math.Clamp(pc.FontSize, 40, 300);
                if (!string.IsNullOrWhiteSpace(pc.FontFamily)) _cdStyle.FontFamilyName = pc.FontFamily;
                _cdStyle.Bold = pc.Bold ?? _cdStyle.Bold;
                _cdStyle.Italic = pc.Italic ?? _cdStyle.Italic;
                if (!string.IsNullOrWhiteSpace(pc.TextColor)) _cdStyle.TextColorHex = pc.TextColor;
                if (!string.IsNullOrWhiteSpace(pc.BackColor)) _cdStyle.BackColorHex = pc.BackColor;
                _cdX = Math.Clamp(pc.X, 0, 1);
                _cdY = Math.Clamp(pc.Y, 0, 1);
                _cdOverlayOn = pc.OverlayOn;
                if (CdFontSlider != null) CdFontSlider.Value = _cdFontPx;
                SyncCountdownStyleUi();
                UpdateCountdownUi();
            }

            var onAir = Layers.Where(l => l.IsAudioSource).Select(l => "#" + l.Id).ToList();
            bool wantAudio = preset.Cells.Any(c => c.IsAudioSource);

            string note = $"▶ 已切到布局「{preset.Name}」：{made} 格";
            note += trans == TransitionType.Cut
                ? " · 硬切"
                : $" · 转场 {TransName(trans)} {transSec:0.0}s";
            note += onAir.Count > 0 ? " · 音源 " + string.Join("+", onAir) : " · 静音";
            if (mix.LayoutHadMixer)
            {
                note += $" · 混音 {mix.Applied} 路";
                if (mix.Missing > 0) note += $"（⚠ {mix.Missing} 路设备不在本机）";
                note += $" · 压缩 {mix.CompressorText}";
                // ffmpeg 的音频参数是启动时拼的 → 输出进行中改了不会自己生效
                if (mix.CompressorChanged && StreamService.Instance.IsRunning)
                    note += "（重启输出后生效）";
            }
            if (missing > 0) note += $" · ⚠ {missing} 格没找到素材，已留空";
            if (wantAudio && onAir.Count == 0) note += " · ⚠ 标识出声的那格没找到素材";
            if (hitCap) note += $" · ⚠ 已达图层上限 {_engine.MaxLayers}，后面的格子没上";
            SetStatus(note, hold: true);
        }

        /// <summary>转场的中文名（状态栏用）。</summary>
        private static string TransName(TransitionType t) => t switch
        {
            TransitionType.Cut => "硬切",
            TransitionType.Fade => "淡入淡出",
            TransitionType.AlphaFade => "交叉溶解",
            _ => t.ToString()
        };

        /// <summary>按当前预览框尺寸，把每一路画面摆到画布上。</summary>
        private void RenderPreview()
        {
            double w = PreviewCanvas.ActualWidth;
            double h = PreviewCanvas.ActualHeight;
            if (w <= 0 || h <= 0) return;
            var frame = new Rect(0, 0, w, h);   // 取景框 = 预览框自身
            foreach (var s in _previewSlots)
            {
                if (s.View is not FrameworkElement fe) continue;
                // ⚠ 元素尺寸只取 ≤1 的部分；>1 的放大交给 VLC（见 VlcZoom 的注释：
                //   把 HwndHost 撑大后 D3D 输出窗不会重新排版，画面会缺/缩）。
                fe.Width = w * s.ViewScaleX;
                fe.Height = h * s.ViewScaleY;
                Canvas.SetLeft(fe, s.X * w);
                Canvas.SetTop(fe, s.Y * h);
                Canvas.SetZIndex(fe, s.Z);
                VlcZoom.Apply(s.Player, s.Zoom);
                // 允许超出，但超出预览框的部分要真看不见（WPF 的 ClipToBounds 管不到 HwndHost 视频窗）
                HwndClip.Apply(fe, frame);
            }
            RefreshPreviewHitDecor();
            RefreshScaleBoxes();     // 拖动/缩放后同步数值框（没有选中项时置灰）

            // A 方案：预览画面由合成器出（多路按归一化矩形贴开），拖动时每帧同步一次几何
            if (_monitor != null)
            {
                try { _monitor.SyncPreview(_previewSlots); } catch { }
            }
        }

        /// <summary>把 " #AARRGGBB " 解析成颜色；解析失败给青色（预览框同色）。</summary>
        private static Color ParseHexColor(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return Color.FromRgb(0x33, 0xC6, 0xF5);
            try { return (Color)ColorConverter.ConvertFromString(hex); }
            catch { return Color.FromRgb(0x33, 0xC6, 0xF5); }
        }

        /// <summary>
        /// 刷新浮层上的「当前选中哪一路」视觉：金色描边 + 右下角缩放黄块 + 用户描边。
        /// 描边必须画在浮层里 —— 画面本身是 Win32 子窗口（HwndHost），画在画布上的描边会被它整个盖住。
        /// </summary>
        private void RefreshPreviewHitDecor()
        {
            double w = PreviewCanvas.ActualWidth, h = PreviewCanvas.ActualHeight;
            if (w <= 0 || h <= 0) return;

            Brush gold = TryFindResource("Brush.Gold") as Brush ?? Brushes.Gold;
            foreach (var s in _previewSlots)
            {
                double l = s.X * w, t = s.Y * h;
                double ww = w * s.ViewScaleX, hh = h * s.ViewScaleY;      // 元素真实尺寸（>1 的放大在 VLC 里）

                // ① 用户描边（精调面板）—— 一直在
                if (s.BorderEl is Border ub)
                {
                    bool has = s.BorderThickness > 0.01;
                    ub.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
                    if (has)
                    {
                        ub.Width = ww;
                        ub.Height = hh;
                        Canvas.SetLeft(ub, l);
                        Canvas.SetTop(ub, t);
                        ub.BorderThickness = new Thickness(s.BorderThickness);
                        ub.BorderBrush = new SolidColorBrush(ParseHexColor(s.BorderColor));
                        ub.CornerRadius = new CornerRadius(Math.Max(0, s.BorderRadius));
                    }
                }

                // ② 选中金框
                if (s.HitBox is not Border box) continue;
                bool sel = ReferenceEquals(s, _selPreviewSlot);
                Canvas.SetLeft(box, l);
                Canvas.SetTop(box, t);
                box.Width = ww;
                box.Height = hh;
                box.BorderBrush = sel ? gold : Brushes.Transparent;
            }

            // ③ 三分法网格：有选中就显示（画在整个取景框上）
            DrawPreviewGrid();

            var grip = PreviewResizeGrip;
            if (grip == null) return;
            if (_selPreviewSlot == null) { grip.Visibility = Visibility.Collapsed; return; }
            grip.Visibility = Visibility.Visible;
            Canvas.SetLeft(grip, _selPreviewSlot.X * w + w * _selPreviewSlot.ViewScaleX - grip.Width - 3);
            Canvas.SetTop(grip, _selPreviewSlot.Y * h + h * _selPreviewSlot.ViewScaleY - grip.Height - 3);
        }

        /// <summary>三分法网格（选中时显示）。画在整个取景框上 —— 构图看的是整块画面，不是单个元素。</summary>
        private void DrawPreviewGrid()
        {
            bool on = _selPreviewSlot != null;
            if (GridV1 == null) return;
            double w = PreviewCanvas.ActualWidth, h = PreviewCanvas.ActualHeight;
            var vis = on && w > 1 && h > 1 ? Visibility.Visible : Visibility.Collapsed;
            GridV1.Visibility = GridV2.Visibility = GridH1.Visibility = GridH2.Visibility = vis;
            if (vis != Visibility.Visible) return;

            GridV1.X1 = GridV1.X2 = w / 3.0; GridV1.Y1 = 0; GridV1.Y2 = h;
            GridV2.X1 = GridV2.X2 = w * 2.0 / 3.0; GridV2.Y1 = 0; GridV2.Y2 = h;
            GridH1.Y1 = GridH1.Y2 = h / 3.0; GridH1.X1 = 0; GridH1.X2 = w;
            GridH2.Y1 = GridH2.Y2 = h * 2.0 / 3.0; GridH2.X1 = 0; GridH2.X2 = w;
        }

        /// <summary>显示/隐藏吸附参考线（青色）。null = 那一轴没吸到，收起。</summary>
        private void ShowSnapGuides(double? lineX, double? lineY)
        {
            if (SnapV == null) return;
            double w = PreviewCanvas.ActualWidth, h = PreviewCanvas.ActualHeight;

            if (lineX is double x && w > 1)
            {
                SnapV.Visibility = Visibility.Visible;
                SnapV.X1 = SnapV.X2 = x * w;
                SnapV.Y1 = 0; SnapV.Y2 = h;
            }
            else SnapV.Visibility = Visibility.Collapsed;

            if (lineY is double y && h > 1)
            {
                SnapH.Visibility = Visibility.Visible;
                SnapH.Y1 = SnapH.Y2 = y * h;
                SnapH.X1 = 0; SnapH.X2 = w;
            }
            else SnapH.Visibility = Visibility.Collapsed;
        }

        private void HideSnapGuides()
        {
            if (SnapV != null) SnapV.Visibility = Visibility.Collapsed;
            if (SnapH != null) SnapH.Visibility = Visibility.Collapsed;
        }

        /// <summary>某一路预览在预览画布上的像素矩形。</summary>
        private Rect PreviewSlotRect(PreviewSlot s)
        {
            double w = PreviewCanvas.ActualWidth, h = PreviewCanvas.ActualHeight;
            return new Rect(s.X * w, s.Y * h, w * s.ViewScaleX, h * s.ViewScaleY);
        }

        /// <summary>按几何矩形挑出「点中的是哪一路」：从最上层往下找第一个包含该点的（带 6px 容差）。</summary>
        private PreviewSlot? HitPreviewSlot(Point p)
        {
            foreach (var s in _previewSlots.OrderByDescending(x => x.Z))
            {
                var r = PreviewSlotRect(s);
                r.Inflate(6, 6);
                if (r.Contains(p)) return s;
            }
            return null;
        }

        /// <summary>
        /// 以「元素中心」为锚点缩放**指定的那一路**预览。
        /// 中心锚点这条不能改：以前直接改缩放值而左上角不动 → 元素以左上角为锚点收缩，
        /// 缩小后跑到预览框左上角，用户在原来的位置按住就"拖不动"。
        /// </summary>
        private void ZoomPreviewAt(PreviewSlot slot, double factor)
        {
            double w = PreviewCanvas.ActualWidth;
            double h = PreviewCanvas.ActualHeight;
            if (w <= 0 || h <= 0) return;
            // 锚点 = 元素真实中心（>1 的放大在 VLC 里做，元素本身不变宽）
            double cx = slot.X * w + w * slot.ViewScaleX / 2;
            double cy = slot.Y * h + h * slot.ViewScaleY / 2;
            slot.ScaleX = Math.Clamp(slot.ScaleX * factor, PreviewMinScale, PreviewMaxScale);
            slot.ScaleY = Math.Clamp(slot.ScaleY * factor, PreviewMinScale, PreviewMaxScale);
            slot.X = (cx - w * slot.ViewScaleX / 2) / w;
            slot.Y = (cy - h * slot.ViewScaleY / 2) / h;
            ClampPreviewPos(slot);
            RenderPreview();
            RefreshGeoPanel();
        }

        /// <summary>
        /// 预览框的最少可见比例 —— 元素允许拖出框外（见下），但至少要有这么大一块留在框内，
        /// 否则整块跑出去就再也抓不回来了（命中判定靠几何矩形，看不见等于点不到）。
        /// 想要「完全自由、允许整块移出」，把这里改成 0 即可（代价：得靠手工摆回或重新分屏找回）。
        /// </summary>
        private const double PreviewKeepFrac = 0.12;

        /// <summary>
        /// 是否允许「超出取景框」。老板 2026-10-18 定的最终语义：
        ///   **允许超出；但超出取景框的部分在画面上不显示**（预览框、输出框各自裁自己的）。
        ///
        /// 画面层怎么做到的（三条缺一不可，别再走回头路）：
        ///   ① WPF 的 ClipToBounds 对 HwndHost 视频子窗**彻底无效** —— 这是 airspace 的硬规则；
        ///   ② 所以只能落到 Win32 窗口区域（SetWindowRgn），见 HwndClip；
        ///   ③ 还有两个必然漏点的时序坑，靠 700ms 看守轮询补（HwndClip.RetryAll）：
        ///      · 元素刚 Add 进画布时 HwndHost.Handle 还是 0，那一刀根本下不去；
        ///      · VLC 换媒体/改分辨率会重建 vout 子窗，新窗口没有区域。
        ///   残留"框外白块"的元凶是 "VLC video main" 的窗口类背景刷（#F0F0F0），
        ///   由 VideoBackdrop 改成纯黑刷，配合 HwndClip 的父窗重绘一起擦掉。
        /// </summary>
        private static readonly bool AllowFrameOverflow = true;

        /// <summary>
        /// 预览框的「取景框」约束（2026-10-17 改口径）。
        ///
        /// 老板定的语义：**元素允许超出预览框**，预览框只是取景框 ——
        /// 你在框里看到什么，推到输出框就只显示什么（超出部分由画布裁掉）。
        /// 所以这里不再把元素夹在框内，只保证「至少 PreviewKeepFrac 留在框内」以便抓取。
        ///
        /// 换算成「左上角比例」：元素占框的比例 = scale，于是
        ///   lo = keep - scale，hi = 1 - keep，其中 keep = min(scale, PreviewKeepFrac)。
        ///   · scale = 0.5、keep = 0.12 → X ∈ [-0.38, 0.88]（原来只有 [0, 0.5]，框死一半自由）
        ///   · scale = 2  、keep = 0.12 → X ∈ [-1.88, 0.88]（能随意取左/中/右任意局部）
        /// 两个轴同一套公式。
        /// </summary>
        private void ClampPreviewPos(PreviewSlot slot)
        {
            if (AllowFrameOverflow)
            {
                // ⚠ 用 ViewScale*（元素真实占框）：>1 的放大交给 VLC，元素本身不会变宽，
                //   还按 ScaleX 算的话 4× 时会允许 X 拖到 -3，整块画面直接飞出取景框。
                double ex = slot.ViewScaleX, ey = slot.ViewScaleY;
                double kx = Math.Min(ex, PreviewKeepFrac);
                double ky = Math.Min(ey, PreviewKeepFrac);
                slot.X = Math.Clamp(slot.X, kx - ex, 1.0 - kx);
                slot.Y = Math.Clamp(slot.Y, ky - ey, 1.0 - ky);
                return;
            }
            // 不出框：元素比框小就四边都在框内；比框大就始终铺满框（拖不出黑边）
            double lox = Math.Min(0.0, 1.0 - slot.ScaleX);
            double hix = Math.Max(0.0, 1.0 - slot.ScaleX);
            double loy = Math.Min(0.0, 1.0 - slot.ScaleY);
            double hiy = Math.Max(0.0, 1.0 - slot.ScaleY);
            slot.X = Math.Clamp(slot.X, lox, hix);
            slot.Y = Math.Clamp(slot.Y, loy, hiy);
        }

        /// <summary>
        /// 拖动时的智能吸附（老板 2026-10-18）：
        /// 「拖到画布边缘或另一块边缘 6px 内自动对齐、显示青色参考线」。
        ///
        /// 候选线 = 画框四边 + 中心 + 三分线 + 其它每一路的左/中/右、上/中/下。
        /// 元素的**左/中/右三个点**都参与吸附（贴边、居中、贴另一块的边都能吸住）。
        /// 按住 Alt 临时关闭吸附 —— 需要像素级自由摆放时不跟自己较劲。
        /// </summary>
        private (double x, double y, double? lineX, double? lineY) SnapPreviewDrag(
            PreviewSlot slot, double x, double y)
        {
            double w = PreviewCanvas.ActualWidth, h = PreviewCanvas.ActualHeight;
            if (w <= 0 || h <= 0) return (x, y, null, null);
            if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) return (x, y, null, null);   // Alt = 不吸附

            var xs = LayoutSnap.FrameLines();
            var ys = LayoutSnap.FrameLines();
            foreach (var o in _previewSlots)
            {
                if (ReferenceEquals(o, slot)) continue;
                xs.Add(o.X); xs.Add(o.X + o.ViewScaleX / 2); xs.Add(o.X + o.ViewScaleX);
                ys.Add(o.Y); ys.Add(o.Y + o.ViewScaleY / 2); ys.Add(o.Y + o.ViewScaleY);
            }

            double dx = LayoutSnap.Solve(x, slot.ScaleX, xs, LayoutSnap.DefaultTolerance / w, out var lx);
            double dy = LayoutSnap.Solve(y, slot.ScaleY, ys, LayoutSnap.DefaultTolerance / h, out var ly);
            return (x + dx, y + dy, lx, ly);
        }

        /// <summary>
        /// 输出图层拖动时的**静默**吸附（同一套数学，但一条参考线都不画）。
        ///
        /// 为什么不画：输出画布上的任何东西都会被「抓屏推流 / 录制 / HDMI 镜像」一起播出去，
        /// 给操作员看的参考线跑到观众画面里是事故级问题。所以这边只吸、不显示。
        /// </summary>
        private (double x, double y) SnapLayerDrag(Layer layer, double x, double y)
        {
            double w = LayersCanvas.ActualWidth, h = LayersCanvas.ActualHeight;
            if (w <= 0 || h <= 0) return (x, y);
            if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) return (x, y);

            double fx = 0.5 * layer.ScaleX, fy = 0.5 * layer.ScaleY;

            var xs = LayoutSnap.FrameLines();
            var ys = LayoutSnap.FrameLines();
            foreach (var o in Layers)
            {
                if (ReferenceEquals(o, layer) || !o.Visible) continue;
                xs.Add(o.X); xs.Add(o.X + 0.25 * o.ScaleX); xs.Add(o.X + 0.5 * o.ScaleX);
                ys.Add(o.Y); ys.Add(o.Y + 0.25 * o.ScaleY); ys.Add(o.Y + 0.5 * o.ScaleY);
            }

            double dx = LayoutSnap.Solve(x, fx, xs, LayoutSnap.DefaultTolerance / w, out _);
            double dy = LayoutSnap.Solve(y, fy, ys, LayoutSnap.DefaultTolerance / h, out _);
            return (x + dx, y + dy);
        }

        // ============ 缩放数字微调（2026-10-18 老板要求：可手输，精确到小数点后 2 位）============
        // 口径统一成「占框比例」：1.00 = 正好铺满。预览槽的 Scale 天然是这个口径；
        // 输出图层是「2 = 铺满」，所以显示时 ÷2、提交时 ×2 —— 两边看到的数字含义一样，不会看晕。

        private const double PreviewMinScale = 0.1;
        private const double PreviewMaxScale = 4.0;
        private const double ScaleStep = 0.01;          // ± 按钮步进：1%（两位小数刚好够细）

        /// <summary>把两个数值框刷新成「当前选中项」的缩放。正在输入时不覆盖用户正在打的字。</summary>
        private void RefreshScaleBoxes()
        {
            if (!IsLoaded) return;
            if (PvScaleBox != null && !PvScaleBox.IsKeyboardFocusWithin)
                PvScaleBox.Text = _selPreviewSlot == null ? "" : _selPreviewSlot.Scale.ToString("0.00");
            if (PvScaleBox != null) PvScaleBox.IsEnabled = _selPreviewSlot != null;

            if (PgmScaleBox != null && !PgmScaleBox.IsKeyboardFocusWithin)
                PgmScaleBox.Text = _selLayer == null ? "" : (_selLayer.Scale / 2.0).ToString("0.00");
            if (PgmScaleBox != null) PgmScaleBox.IsEnabled = _selLayer != null;
        }

        /// <summary>按「占框比例」设置当前选中那一路预览的缩放（以元素中心为锚点，位置不跑）。</summary>
        private void ApplyPreviewFracNumber(double frac)
        {
            var s = _selPreviewSlot;
            if (s == null) { RefreshScaleBoxes(); return; }
            double w = PreviewCanvas.ActualWidth, h = PreviewCanvas.ActualHeight;
            if (w <= 0 || h <= 0) return;

            frac = Math.Clamp(frac, PreviewMinScale, PreviewMaxScale);
            if (Math.Abs(frac - s.Scale) < 0.0005) { RefreshScaleBoxes(); return; }

            // 中心锚点：缩小时不会往左上角跑。
            // ⚠ 锚点要用「元素真实中心」（ViewScale），>1 那部分是与窗口同心放大的，中心不变。
            double cx = s.X * w + w * s.ViewScaleX / 2;
            double cy = s.Y * h + h * s.ViewScaleY / 2;
            s.Scale = frac;
            double nvx = Math.Min(frac, 1.0), nvy = Math.Min(frac, 1.0);
            s.X = (cx - w * nvx / 2) / w;
            s.Y = (cy - h * nvy / 2) / h;
            ClampPreviewPos(s);
            RenderPreview();
            RefreshScaleBoxes();
            SetStatus($"预览「{s.DisplayName}」缩放 {s.Scale:0.00}×（占框 {s.Scale * 100:0}%）", hold: true);
        }

        /// <summary>同上，作用于当前选中的输出图层（frac 是占框比例，图层 Scale = frac × 2）。</summary>
        private void ApplyLayerFracNumber(double frac)
        {
            var l = _selLayer;
            if (l == null) { RefreshScaleBoxes(); return; }
            double w = LayersCanvas.ActualWidth, h = LayersCanvas.ActualHeight;
            if (w <= 0 || h <= 0) return;

            frac = Math.Clamp(frac, PreviewMinScale, PreviewMaxScale);
            double sc = Math.Clamp(frac * 2.0, MinLayerScale, MaxLayerScale);

            double cx = l.X * w + w * 0.25 * l.ViewScaleX;
            double cy = l.Y * h + h * 0.25 * l.ViewScaleY;
            double fy = 0.5 * l.ScaleY / Math.Max(1e-6, 0.5 * l.ScaleX);   // 保持当前宽高比
            l.ScaleX = sc;
            l.ScaleY = Math.Clamp(sc * fy, MinLayerScale, MaxLayerScale);
            l.X = (cx - w * 0.25 * l.ViewScaleX) / w;
            l.Y = (cy - h * 0.25 * l.ViewScaleY) / h;
            ClampLayerPos(l);
            RenderLayer(l);
            RefreshScaleBoxes();
            RefreshGeoPanel();
            SetStatus($"图层 #{l.Id}「{l.DisplayName}」缩放 {sc / 2.0:0.00}×（占框 {sc * 50:0}%）", hold: true);
        }

        private static bool TryParseFrac(string text, out double v)
            => double.TryParse((text ?? "").Trim().TrimEnd('×', 'x', 'X'),
                               System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.CurrentCulture, out v)
               || double.TryParse((text ?? "").Trim().TrimEnd('×', 'x', 'X'),
                               System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out v);

        private void PvScaleBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            if (TryParseFrac(PvScaleBox.Text, out double v)) ApplyPreviewFracNumber(v);
            else RefreshScaleBoxes();
            Keyboard.ClearFocus();
            e.Handled = true;
        }

        private void PvScaleBox_LostFocus(object sender, RoutedEventArgs e)
        {
            // 失焦即提交：老板输完直接去拖画面也不会丢输入
            if (!IsLoaded) return;
            if (TryParseFrac(PvScaleBox.Text, out double v)) ApplyPreviewFracNumber(v);
            else RefreshScaleBoxes();
        }

        private void PgmScaleBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            if (TryParseFrac(PgmScaleBox.Text, out double v)) ApplyLayerFracNumber(v);
            else RefreshScaleBoxes();
            Keyboard.ClearFocus();
            e.Handled = true;
        }

        private void PgmScaleBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            if (TryParseFrac(PgmScaleBox.Text, out double v)) ApplyLayerFracNumber(v);
            else RefreshScaleBoxes();
        }

        private void PvScaleStep_Click(object sender, RoutedEventArgs e)
        {
            var s = _selPreviewSlot;
            if (s == null) { SetStatus("⚠ 先在预览框里点一下要调的画面（金框 = 当前选中）", hold: true); return; }
            int dir = (sender as FrameworkElement)?.Tag?.ToString() == "-1" ? -1 : 1;
            ApplyPreviewFracNumber(s.Scale + dir * ScaleStep);
        }

        private void PgmScaleStep_Click(object sender, RoutedEventArgs e)
        {
            var l = _selLayer;
            if (l == null) { SetStatus("⚠ 先在右侧图层列表里选一个图层", hold: true); return; }
            int dir = (sender as FrameworkElement)?.Tag?.ToString() == "-1" ? -1 : 1;
            ApplyLayerFracNumber(l.Scale / 2.0 + dir * ScaleStep);
        }

        /// <summary>
        /// 输出图层缩放范围。上限 8.0 = 画布的 4 倍（层基准尺寸是画布的 1/2 → 0.5×8 = 400%）。
        /// ⚠ 这个上限必须 ≥ 2×「预览侧缩放上限(4)」，否则在预览里放大到 4× 的画面推到输出会被砍成 2×，
        ///    与「预览看到什么输出就显示什么」的取景框语义直接冲突。
        /// </summary>
        private const double MinLayerScale = 0.2;
        private const double MaxLayerScale = 8.0;
        /// <summary>铺满输出框所需的缩放值：图层尺寸 = 画布宽 ×0.5× Scale，故 Scale=2 正好满框。</summary>
        private const double FullLayerScale = 2.0;

        /// <summary>
        /// 输出图层的「取景框」约束 —— 与预览框<b>完全同一套规则</b>（2026-10-17 改口径）：
        /// 图层允许超出输出画布，画报只显示画布内的那一部分（节目输出按播出框抓屏，天然裁掉框外）。
        /// 这里只保证「至少 PreviewKeepFrac 留在画布内」，免得图层整块跑出去抓不回来。
        ///
        /// 换算：图层宽 = 画布宽 × 0.5 × Scale（高同理），所以「占画布比例」frac = 0.5 × Scale：
        ///   keep = min(frac, PreviewKeepFrac)，左上角 ∈ [keep-frac, 1-keep]。
        ///   · Scale = 1（占画布 1/4）→ frac = 0.5 → X ∈ [-0.38, 0.88]（原来只有 [0, 0.5]）
        ///   · Scale = 2（正好铺满）  → frac = 1.0 → X ∈ [-0.88, 0.88]（原来只能 X = 0）
        /// </summary>
        private void ClampLayerPos(Layer layer)
        {
            double fx = 0.5 * layer.ViewScaleX;
            double fy = 0.5 * layer.ViewScaleY;
            if (AllowFrameOverflow)
            {
                double kx = Math.Min(fx, PreviewKeepFrac);
                double ky = Math.Min(fy, PreviewKeepFrac);
                layer.X = Math.Clamp(layer.X, kx - fx, 1.0 - kx);
                layer.Y = Math.Clamp(layer.Y, ky - fy, 1.0 - ky);
                return;
            }
            layer.X = Math.Clamp(layer.X, Math.Min(0.0, 1.0 - fx), Math.Max(0.0, 1.0 - fx));
            layer.Y = Math.Clamp(layer.Y, Math.Min(0.0, 1.0 - fy), Math.Max(0.0, 1.0 - fy));
        }

        /// <summary>以「图层中心」为锚点缩放输出图层（同理：缩小时不再往左上角跑）。</summary>
        private void ZoomLayerAt(Layer layer, double factor)
        {
            var w = LayersCanvas.ActualWidth;
            var h = LayersCanvas.ActualHeight;
            if (w <= 0 || h <= 0) return;
            double cx = layer.X * w + w * 0.25 * layer.ViewScaleX;   // 元素真实半宽（>100% 的放大在 VLC 里）
            double cy = layer.Y * h + h * 0.25 * layer.ViewScaleY;
            layer.ScaleX = Math.Clamp(layer.ScaleX * factor, MinLayerScale, MaxLayerScale);
            layer.ScaleY = Math.Clamp(layer.ScaleY * factor, MinLayerScale, MaxLayerScale);
            // 回填位置也用真实尺寸（元素最多铺满画布；放大部分在 VLC 里同心放大，位置不变）
            layer.X = (cx - w * 0.25 * layer.ViewScaleX) / w;
            layer.Y = (cy - h * 0.25 * layer.ViewScaleY) / h;
            ClampLayerPos(layer);   // 缩放后同样过一遍取景框规则（与预览框同一套）
            RenderLayer(layer);
            RefreshGeoPanel();
        }

        // ============ 自检（自动化回归用，--geotest 触发）============
        //
        // 为什么要有它：老板在用电脑时不能跑"点鼠标"的自动化（会抢焦点、会打扰他）。
        // 所以把「摆 4 路 → 改几何/描边 → 存预设 → 改乱 → 套预设 → 核对」这一整套
        // 写成进程内自检：程序用 --offscreen 起在桌面外，我只需 PrintWindow 截图，
        // 全程不上屏、不碰键鼠。结论写进 %TEMP%\cutlance-geotest.log。
        public void RunGeoTest()
        {
            var log = new List<string>();
            void Chk(string name, bool ok, string detail = "")
                => log.Add($"{(ok ? "PASS" : "FAIL")}\t{name}\t{detail}");

            try
            {
                Chk("输入数≥4", Inputs.Count >= 4, "Inputs=" + Inputs.Count);
                if (Inputs.Count >= 4) LoadInputsIntoPreview(Inputs.Take(4).ToList());
                Chk("预览组=4 路", _previewSlots.Count == 4, "_previewSlots=" + _previewSlots.Count);

                // ① 几何：解锁比例 + 独立宽高
                var s1 = _previewSlots.FirstOrDefault();
                if (s1 != null)
                {
                    s1.X = 0.10; s1.Y = 0.12;
                    s1.AspectLock = false;
                    s1.ScaleX = 0.40; s1.ScaleY = 0.30;
                    s1.BorderThickness = 3;
                    s1.BorderRadius = 6;
                    s1.BorderColor = "#FFF0B93B";
                    RenderPreview();
                    var r = PreviewSlotRect(s1);
                    double w = PreviewCanvas.ActualWidth, h = PreviewCanvas.ActualHeight;
                    Chk("宽=40% 高=30%", Math.Abs(r.Width - w * 0.40) < 1 && Math.Abs(r.Height - h * 0.30) < 1,
                        $"{r.Width:0.0}x{r.Height:0.0} of {w:0}x{h:0}");
                    Chk("描边元素已建", s1.BorderEl is Border bb && bb.Visibility == Visibility.Visible,
                        (s1.BorderEl as Border)?.BorderThickness.ToString() ?? "null");
                }

                // ② 面板：目标 = 预览那一路，读数一致
                _geoPanelTargetIsPreview = true;
                _selPreviewSlot = s1;
                RefreshGeoPanel();
                Chk("面板目标=预览", GeoPanel?.Target is PreviewSlot, GeoPanel?.Target?.PanelTitle ?? "null");
                Chk("面板宽读数≈40", GeoPanel != null && Math.Abs(GeoPanel.Target!.WidthFrac - 0.40) < 0.002,
                    (GeoPanel?.Target?.WidthFrac ?? -1).ToString("0.###"));

                // ③ 吸附数学（纯函数，直接验）
                var lines = LayoutSnap.FrameLines();
                double d = LayoutSnap.Solve(0.332, 0.2, lines, 6.0 / 1000, out var hit);
                Chk("吸附到三分线", hit.HasValue && Math.Abs(hit.Value - 1.0 / 3.0) < 1e-6
                                     && Math.Abs(d - 0.00133) < 0.002,
                    $"d={d:0.0000} line={hit:0.000}");
                double d2 = LayoutSnap.Solve(0.55, 0.2, lines, 6.0 / 1000, out var hit2);
                Chk("超出容差不吸", !hit2.HasValue && Math.Abs(d2) < 1e-9, $"d={d2}");

                // ④ 预览布局：存成独立文件 → 打乱 → 从文件套回来
                var selfPreset = BuildPresetFromPreview("自检布局");
                string selfPath = Path.Combine(Path.GetTempPath(), "cutlance-selftest.vpreset");
                bool saved = false;
                try
                {
                    File.WriteAllText(selfPath,
                        System.Text.Json.JsonSerializer.Serialize(selfPreset, PresetJsonOptions),
                        new System.Text.UTF8Encoding(false));
                    saved = File.Exists(selfPath);
                }
                catch { }
                Chk("布局文件已保存", saved && selfPreset != null && selfPreset.Items.Count == 4,
                    $"{selfPath} items={selfPreset?.Items.Count}");

                LayoutPreset? back = null;
                try
                {
                    back = System.Text.Json.JsonSerializer.Deserialize<LayoutPreset>(
                        File.ReadAllText(selfPath, System.Text.Encoding.UTF8), PresetJsonOptions);
                }
                catch { }
                Chk("布局文件可读回", back != null && back.Items.Count == 4, $"items={back?.Items.Count}");

                if (s1 != null) { s1.X = 0.5; s1.Y = 0.5; s1.ScaleX = 0.2; s1.ScaleY = 0.2; }
                var (ph, pm) = back != null ? ApplyPresetToPreview(back) : (0, 0);
                Chk("载入后恢复几何",
                    s1 != null && Math.Abs(s1.X - 0.10) < 1e-6 && Math.Abs(s1.ScaleX - 0.40) < 1e-6,
                    s1 == null ? "null" : $"X={s1.X:0.000} W={s1.ScaleX:0.000} hit={ph} miss={pm}");
                Chk("载入后恢复描边", s1 != null && Math.Abs(s1.BorderThickness - 3) < 1e-6,
                    s1?.BorderThickness.ToString() ?? "null");

                // ⑤ 推到输出：宽高与描边要一起带过去
                BtnPreviewToProgram_Click(this, new RoutedEventArgs());
                var first = Layers.FirstOrDefault();
                Chk("输出图层=4", Layers.Count == 4, "Layers=" + Layers.Count);
                Chk("输出图层宽=0.8", first != null && Math.Abs(first.ScaleX - 0.8) < 1e-6,
                    first?.ScaleX.ToString("0.###") ?? "null");
                Chk("输出图层高=0.6", first != null && Math.Abs(first.ScaleY - 0.6) < 1e-6,
                    first?.ScaleY.ToString("0.###") ?? "null");
                Chk("输出描边已带过去", first != null && Math.Abs(first.BorderThickness - 3) < 1e-6,
                    first?.BorderThickness.ToString() ?? "null");

                // ⑥ 面板切到输出图层
                if (first != null)
                {
                    SelectLayer(first);
                    Chk("面板目标=图层", GeoPanel?.Target is Layer, GeoPanel?.Target?.PanelTitle ?? "null");
                    // 图层 ScaleX=0.8 → 占画布 0.5×0.8 = 40%（与预览那一路的 40% 对得上）
                    Chk("图层宽读数≈40", GeoPanel != null && Math.Abs(GeoPanel.Target!.WidthFrac - 0.4) < 0.002,
                        (GeoPanel?.Target?.WidthFrac ?? -1).ToString("0.###"));
                }

                // ⑦ 把所有路重新分屏（应回到整齐宫格）
                BtnPreviewResplit_Click(this, new RoutedEventArgs());
                bool grid = _previewSlots.Count == 4 && _previewSlots.All(x => Math.Abs(x.ScaleX - 0.5) < 1e-6);
                Chk("重新分屏=2×2", grid, string.Join(",", _previewSlots.Select(x => x.ScaleX.ToString("0.00"))));

                // ⑧ 改名：只改显示名，且必须发变更通知（Name 是自动属性，不通知则左侧列表不刷新）
                if (Inputs.Count > 0)
                {
                    var victim = Inputs[0];
                    string before = victim.Name;
                    bool notified = false;
                    void OnPc(object? s2, System.ComponentModel.PropertyChangedEventArgs a2)
                    {
                        if (a2.PropertyName == nameof(InputSource.Name)) notified = true;
                    }
                    victim.PropertyChanged += OnPc;
                    victim.Rename("改名自检");
                    victim.PropertyChanged -= OnPc;
                    Chk("改名生效且发出通知", victim.Name == "改名自检" && notified,
                        $"name={victim.Name} notified={notified}");
                    victim.Rename(before);        // 还原，别污染后面的检查
                }
            }
            catch (Exception ex)
            {
                log.Add("EXCEPTION\t" + ex);
            }
            finally
            {
                Chk("进程存活", true);
                try
                {
                    System.IO.File.WriteAllLines(
                        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cutlance-geotest.log"), log);
                }
                catch { }
            }
        }


        // ============ 精调面板 + 组合预设（老板 2026-10-18）============
        //
        // 需求原话：
        //   「加『就地精调面板』：选中某一路后，面板贴在该路旁边或右栏浮出，含 X / Y、宽 / 高、
        //     旋转、裁切 X1Y1X2Y2、边框（颜色、粗细、圆角）、锁定宽高比、重置。
        //     默认停靠右栏，可拖出成浮动大窗。」
        //   「加『组合预设』：把当前这套摆法起个名存下来，下次一键调回。」
        //
        // 为什么旋转/裁切是作用在**源**上：
        //   视频画面是 Win32 子窗口（HwndHost），WPF 的 RotateTransform / Clip 对它完全无效
        //   （这条已经用 SetWindowRgn 与窗口层级反复验证过）。所以这两个操作改的是
        //   InputSource 的 Rotate / CropPct*，由 ffmpeg 预处理层真正作用到画面上，
        //   于是预览与输出同步变化 —— 这也是「输入级」该有的语义。

        /// <summary>接线：右栏的精调面板（只调一次）。</summary>
        private void InitGeometryPanel()
        {
            if (GeoPanel != null)
            {
                GeoPanel.Edited += GeoPanel_Edited;
                GeoPanel.PopOutRequested += FloatGeometryPanel;
                GeoPanel.SetExpanded(false);     // 默认收起：只占一行标题，不抢右侧栏高度
            }
            RefreshGeoPanel();
        }

        /// <summary>面板当前在调谁：预览那一路，还是选中的输出图层。</summary>
        private IFrameItem? GeoPanelTarget
            => _geoPanelTargetIsPreview ? _selPreviewSlot : (IFrameItem?)_selLayer;

        /// <summary>把面板同步到「当前操作对象」（右栏 + 浮动窗口两份都要跟着变）。</summary>
        private void RefreshGeoPanel()
        {
            var t = GeoPanelTarget;
            SyncOnePanel(GeoPanel, t);
            if (_geoFloatWindow != null) SyncOnePanel(_geoFloatWindow.Embedded, t);
        }

        private static void SyncOnePanel(Views.GeometryPanel? p, IFrameItem? t)
        {
            if (p == null) return;
            if (ReferenceEquals(p.Target, t)) p.Refresh();
            else p.Target = t;
        }

        /// <summary>面板改完东西后的统一收口：钳制 → 重绘 → 同步数值框。</summary>
        private void GeoPanel_Edited(GeoEdit kind)
        {
            var t = GeoPanelTarget;
            if (t == null) return;

            if (t is PreviewSlot ps)
            {
                ClampPreviewPos(ps);
                RenderPreview();
            }
            else if (t is Layer ly)
            {
                ClampLayerPos(ly);
                RenderLayer(ly);
                SyncLayerBorders();
            }

            // 旋转 / 裁切 / 去隔行作用于输入源 → 要重开这一路输入的媒体（预览与输出一起更新）
            if (kind == GeoEdit.Rotation || kind == GeoEdit.Crop || kind == GeoEdit.Deinterlace)
            {
                var src = t.Source;
                if (src == null) return;
                if (kind == GeoEdit.Crop
                    && src.UsePctCrop
                    && (src.CropPctLeft + src.CropPctRight >= 95 || src.CropPctTop + src.CropPctBottom >= 95))
                {
                    SetStatus("⚠ 裁切太多了（左右相加、或上下相加必须小于 95%），这次设置已忽略", hold: true);
                    SyncOnePanel(GeoPanel, t);
                    if (_geoFloatWindow != null) SyncOnePanel(_geoFloatWindow.Embedded, t);
                    return;
                }
                // 输入参数变了 → 合成器那一路的解码通路要重建（否则还在播旧的媒体：
                // 去隔行/旋转/裁切都会"勾了没反应"）。延迟释放，别在合成线程读它的时候 Dispose。
                _monitor?.DropFeed(src);
                ReplayInput(src);       // 内部会写状态栏说明
            }

            RefreshGeoPanel();
            RefreshScaleBoxes();
        }

        /// <summary>把面板搬进一个可移动的非模态大窗口（默认贴着主窗口右缘）。</summary>
        private void FloatGeometryPanel()
        {
            if (_geoFloatWindow != null) { _geoFloatWindow.Activate(); return; }

            var win = new GeometryPanelWindow { Owner = this, Left = Left + Width - 320, Top = Top + 40 };
            // 位置：贴在主窗口右缘；放不下就贴右边、再不行就贴着主窗左上
            double right = Left + Width + 8;
            double wa = SystemParameters.WorkArea.Right;
            win.Left = right + 320 < wa ? right : Math.Max(0, wa - 328);
            win.Top = Math.Max(0, Math.Min(Top, SystemParameters.WorkArea.Bottom - 440));

            win.Embedded.Edited += GeoPanel_Edited;
            win.Embedded.PopOutRequested += FloatGeometryPanel;
            win.ClosedByUser += () =>
            {
                _geoFloatWindow = null;
                if (GeoPanel != null) GeoPanel.Visibility = Visibility.Visible;   // 面板回到右栏
                RefreshGeoPanel();
            };
            win.Show();

            _geoFloatWindow = win;
            if (GeoPanel != null) GeoPanel.Visibility = Visibility.Collapsed;      // 避免两份同时显示
            RefreshGeoPanel();
            SetStatus("精调面板已浮出（可拖动/改大小；关掉窗口它就回到右栏）", hold: true);
        }

        // —— 预览布局文件（老板 2026-10-20）——
        //
        // 原来那排「组合预设 + 名字框 + 存 / 下拉 / 应用 / 删」已按老板要求整排删除。
        // 改成「一个布局一个文件」：
        //   · 预览组「保存」按钮、或预览框右键「保存为布局文件…」→ 存成 .vpreset
        //   · 预览框右键「载入布局文件…」→ 选哪个套哪个，直接把摆法调回来
        // 为什么换成文件而不是内部那张索引表：文件能拷给别人、能放进节目素材目录，
        // 名字就是文件名（不必先起名再存），也不会攒出一张越用越乱的下拉列表。

        private static readonly System.Text.Json.JsonSerializerOptions PresetJsonOptions = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>布局文件默认目录（我的文档\Cutlance-布局）。</summary>
        public static string PreviewLayoutDir
        {
            get
            {
                var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (string.IsNullOrWhiteSpace(docs)) docs = Path.GetTempPath();
                return Path.Combine(docs, "Cutlance-布局");
            }
        }

        private static string EnsureLayoutDir()
        {
            try { Directory.CreateDirectory(PreviewLayoutDir); } catch { }
            return PreviewLayoutDir;
        }

        /// <summary>把预览框当前这套摆法收集成一份可落盘的布局（没有画面时返回 null）。</summary>
        private LayoutPreset? BuildPresetFromPreview(string name)
        {
            if (_previewSlots.Count == 0) return null;

            var preset = new LayoutPreset
            {
                Name = name,
                SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm")
            };
            foreach (var s in _previewSlots)
                preset.Items.Add(new PresetItem
                {
                    SourceNumber = s.Source?.Number ?? -1,
                    SourceName = s.Source?.Name ?? "",
                    X = s.X, Y = s.Y,
                    W = s.ScaleX, H = s.ScaleY, Z = s.Z,
                    AspectLock = s.AspectLock,
                    BorderThickness = s.BorderThickness,
                    BorderRadius = s.BorderRadius,
                    BorderColor = s.BorderColor
                });
            return preset;
        }

        /// <summary>
        /// 把一份布局套到预览框现有的几路上。
        /// 认源顺序：先按输入编号，再按输入名兜底（换工程后编号会变，名字通常不变）；
        /// 都对不上的那一路跳过并计数，绝不把摆法硬塞到别的画面上。
        /// </summary>
        private (int hit, int miss) ApplyPresetToPreview(LayoutPreset p)
        {
            int hit = 0, miss = 0;
            foreach (var it in p.Items)
            {
                var slot = _previewSlots.FirstOrDefault(s =>
                    (it.SourceNumber > 0 && s.Source?.Number == it.SourceNumber) ||
                    (!string.IsNullOrEmpty(it.SourceName) && s.Source?.Name == it.SourceName));
                if (slot == null) { miss++; continue; }

                slot.X = it.X; slot.Y = it.Y;
                slot.ScaleX = Math.Clamp(it.W, PreviewMinScale, PreviewMaxScale);
                slot.ScaleY = Math.Clamp(it.H, PreviewMinScale, PreviewMaxScale);
                slot.AspectLock = it.AspectLock;
                slot.BorderThickness = it.BorderThickness;
                slot.BorderRadius = it.BorderRadius;
                slot.BorderColor = it.BorderColor;
                ClampPreviewPos(slot);
                hit++;
            }

            RenderPreview();
            RefreshGeoPanel();
            return (hit, miss);
        }

        private void BtnPreviewSave_Click(object sender, RoutedEventArgs e) => SavePreviewLayoutFile();
        private void MenuPreviewSaveFile_Click(object sender, RoutedEventArgs e) => SavePreviewLayoutFile();
        private void MenuPreviewResplit_Click(object sender, RoutedEventArgs e)
            => BtnPreviewResplit_Click(sender, e);

        /// <summary>把预览框当前摆法存成一个独立布局文件（.vpreset，JSON）。</summary>
        private void SavePreviewLayoutFile()
        {
            if (_previewSlots.Count == 0)
            {
                SetStatus("⚠ 预览框是空的，没有可保存的摆法：先把输入放进预览、摆好位置再保存", hold: true);
                return;
            }

            var dlg = new SaveFileDialog
            {
                Title = "保存预览布局",
                Filter = "Cutlance 布局 (*.vpreset)|*.vpreset|JSON 文件 (*.json)|*.json",
                DefaultExt = ".vpreset",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = "布局-" + DateTime.Now.ToString("MMdd-HHmm"),
                InitialDirectory = EnsureLayoutDir()
            };
            if (dlg.ShowDialog(this) != true) return;

            string name = Path.GetFileNameWithoutExtension(dlg.FileName);
            var preset = BuildPresetFromPreview(name);
            if (preset == null) return;

            try
            {
                File.WriteAllText(dlg.FileName,
                    System.Text.Json.JsonSerializer.Serialize(preset, PresetJsonOptions),
                    new System.Text.UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                SetStatus("⚠ 布局保存失败：" + ex.Message, hold: true);
                return;
            }

            SetStatus($"✓ 已保存布局「{name}」（{preset.Items.Count} 路）→ {dlg.FileName}；" +
                      "下次预览框右键「载入布局文件…」直接调回来", hold: true);
        }

        private void MenuPreviewLoadFile_Click(object sender, RoutedEventArgs e)
        {
            if (_previewSlots.Count == 0)
            {
                SetStatus("⚠ 预览框是空的：先把输入放进预览，再套用布局文件", hold: true);
                return;
            }

            var dlg = new OpenFileDialog
            {
                Title = "载入预览布局",
                Filter = "Cutlance 布局 (*.vpreset;*.json)|*.vpreset;*.json|所有文件 (*.*)|*.*",
                CheckFileExists = true,
                InitialDirectory = EnsureLayoutDir()
            };
            if (dlg.ShowDialog(this) != true) return;

            LayoutPreset? preset;
            try
            {
                preset = System.Text.Json.JsonSerializer.Deserialize<LayoutPreset>(
                    File.ReadAllText(dlg.FileName, System.Text.Encoding.UTF8), PresetJsonOptions);
            }
            catch (Exception ex)
            {
                SetStatus("⚠ 这个文件读不出来（不是 Cutlance 布局文件？）：" + ex.Message, hold: true);
                return;
            }
            if (preset == null || preset.Items.Count == 0)
            {
                SetStatus("⚠ 文件里没有摆法数据（0 路），没有可套用的内容", hold: true);
                return;
            }

            string name = Path.GetFileNameWithoutExtension(dlg.FileName);
            var (hit, miss) = ApplyPresetToPreview(preset);
            SetStatus(miss == 0
                ? $"✓ 已套用布局「{name}」（{hit} 路）"
                : $"已套用布局「{name}」：{hit} 路对上，{miss} 路在当前预览里找不到对应输入（已跳过）",
                hold: true);
        }

        /// <summary>重新分屏：把预览组里现有的几路按数量排成整齐宫格（会清掉手工摆位）。</summary>
        private void BtnPreviewResplit_Click(object sender, RoutedEventArgs e)
        {
            int n = _previewSlots.Count;
            if (n == 0) { SetStatus("⚠ 预览框是空的", hold: true); return; }

            var layout = SplitScreenLayout(n);
            for (int i = 0; i < n && i < layout.Count; i++)
            {
                var s = layout[i];
                var slot = _previewSlots[i];
                slot.X = Math.Max(0, s.x);
                slot.Y = Math.Max(0, s.y);
                slot.ScaleX = slot.ScaleY = Math.Clamp(s.scale / 2.0, PreviewMinScale, 1.0);
                slot.AspectLock = true;
                ClampPreviewPos(slot);
            }
            RenderPreview();
            RefreshGeoPanel();
            SetStatus($"已把 {n} 路重新排成宫格", hold: true);
        }

        // ============ 预览放大（老板 2026-10-20）============
        //
        // 需求原话：「放大 把预览界面放大 方便精调」。
        // 做法：把中区那三列里的「输出列 + 中间箭头列」收成 0，预览独占整个中区。
        // 为什么不是新开一个大窗：预览画布里每一路都是 HwndHost（VLC 原生子窗口），
        //   跨窗口搬动会重建 vout 子窗 —— 那是黑屏 / 花屏的高发点，还得陪着清裁剪区域。
        //   只改列宽，元素树一个都不动，最安全。
        // 代价：放大时输出框被收起 —— 输出走内存成品帧，画面不受影响，
        //   但操作员看不见节目画面（等于盲播），所以**推流 / 录制进行中一律不让放大**。

        private void BtnPreviewZoom_Click(object sender, RoutedEventArgs e) => TogglePreviewZoom();
        private void MenuPreviewZoom_Click(object sender, RoutedEventArgs e) => TogglePreviewZoom();

        private void BtnProgramZoom_Click(object sender, RoutedEventArgs e) => ToggleProgramZoom();
        private void MenuProgramZoom_Click(object sender, RoutedEventArgs e) => ToggleProgramZoom();

        /// <summary>
        /// 输出画面放大（2026-10-25 老板要的「单画面放大模式」）。
        ///
        /// ── 和「放大预览」不是一回事 ──
        ///   · 放大预览：摆素材时精调，要求**先停输出**（看不到节目画面等于盲播）；
        ///   · 放大输出：播的时候看清节目画面，**推流 / 录制中照样能开** ——
        ///     它恰恰是让你看到「播出去的东西」，盲播的顾虑在这里不存在。
        ///
        /// 实现上只是把预览那一列收成 0 宽，不动任何元素树：
        /// 输出画布（LayersCanvas）宽度变大后，图层的归一化坐标自动跟着放大，
        /// 所以不用逐格改数值，也不会像"挪窗口"那样把 HwndHost 弄乱。
        /// </summary>
        private void ToggleProgramZoom()
        {
            if (!_programZoomed && _previewZoomed)
            {
                _previewZoomed = false;
                ApplyPreviewZoom();     // 先把预览放大退掉，再进输出放大
            }
            _programZoomed = !_programZoomed;
            ApplyProgramZoom();
        }

        private void ApplyProgramZoom()
        {
            if (PvwCol == null || GoCol == null || PgmCol == null) return;
            if (PreviewCell == null || ProgramCell == null) return;

            if (_programZoomed)
            {
                // 输出本身被「视图」菜单隐藏了 → 先显示出来，否则"放大"等于放了个空列
                if (ProgramCell.Visibility != Visibility.Visible)
                {
                    ProgramCell.Visibility = Visibility.Visible;
                    if (MiShowProgram != null) MiShowProgram.IsChecked = true;
                }
                _pvwVisibleBeforeZoom = PreviewCell.Visibility == Visibility.Visible;
                PvwCol.Width = new GridLength(0);
                GoCol.Width = new GridLength(0);
                PreviewCell.Visibility = Visibility.Collapsed;
                if (BtnGo != null) BtnGo.Visibility = Visibility.Collapsed;
                if (BtnProgramZoom != null) BtnProgramZoom.Content = "还原";
                SetStatus("输出画面已放大（预览暂时收起；推流 / 录制不受影响）。点「还原」回到双画面", hold: true);
            }
            else
            {
                PvwCol.Width = new GridLength(1, GridUnitType.Star);
                GoCol.Width = new GridLength(44);
                PreviewCell.Visibility = _pvwVisibleBeforeZoom ? Visibility.Visible : Visibility.Collapsed;
                if (BtnGo != null) BtnGo.Visibility = Visibility.Visible;
                if (BtnProgramZoom != null) BtnProgramZoom.Content = "放大";
                SetStatus("已还原双画面布局", hold: true);
            }

            // 列宽变了：画面宽高比、浮层位置都要重算（与 ApplyPreviewZoom 同一套收尾）
            FitMonitors();
            RenderPreview();
            SyncPreviewPopup();
            SyncProgramOverlayPopup();
            SyncMenuState();
        }

        private void TogglePreviewZoom()
        {
            // 两个放大态互斥：进一个先退另一个。两套逻辑都在改 PvwCol/GoCol/PgmCol 的宽度，
            // 同时开着会互相覆盖，画面大小就会跳。
            if (!_previewZoomed && _programZoomed)
            {
                _programZoomed = false;
                ApplyProgramZoom();
            }

            // 要进入放大态时先做两道拦截
            if (!_previewZoomed)
            {
                if (StreamService.Instance.IsRunning)
                {
                    SetStatus("⚠ 正在推流 / 录制，不能放大预览：" +
                              "放大会把播出框收起来，虽然输出吃的是内存合成帧、画面不受影响，" +
                              "但你就看不到节目画面、等于盲播。先停输出，或等这档播完再放大精调。", hold: true);
                    return;
                }
                if (PreviewCell != null && PreviewCell.Visibility != Visibility.Visible)
                {
                    // 预览本身被「视图」菜单隐藏了，放大就先把它显示出来
                    PreviewCell.Visibility = Visibility.Visible;
                    if (MiShowPreview != null) MiShowPreview.IsChecked = true;
                }
                // 预览是空的也允许放大：放大后是个空大框，正好往里面摆第一路
            }

            _previewZoomed = !_previewZoomed;
            ApplyPreviewZoom();
        }

        private void ApplyPreviewZoom()
        {
            if (PvwCol == null || GoCol == null || PgmCol == null) return;
            if (PreviewCell == null || ProgramCell == null) return;

            if (_previewZoomed)
            {
                _pgmVisibleBeforeZoom = ProgramCell.Visibility == Visibility.Visible;
                PgmCol.Width = new GridLength(0);
                GoCol.Width = new GridLength(0);
                ProgramCell.Visibility = Visibility.Collapsed;
                if (BtnGo != null) BtnGo.Visibility = Visibility.Collapsed;
                if (BtnPreviewZoom != null) BtnPreviewZoom.Content = "还原";
                SetStatus("预览已放大（输出框暂时收起，方便一格一格精调）；调完点「还原」回到双画面布局", hold: true);
            }
            else
            {
                PgmCol.Width = new GridLength(1, GridUnitType.Star);
                GoCol.Width = new GridLength(44);
                ProgramCell.Visibility = _pgmVisibleBeforeZoom ? Visibility.Visible : Visibility.Collapsed;
                if (BtnGo != null) BtnGo.Visibility = Visibility.Visible;
                if (BtnPreviewZoom != null) BtnPreviewZoom.Content = "放大";
                SetStatus("已还原双画面布局", hold: true);
            }

            // 列宽变了：预览框 / 输出框的宽高比要重算，浮层要重新贴位置。
            // 预览槽位存的是「占框比例」，框一大自己就跟着变大，不必逐路改数值。
            FitMonitors();
            RenderPreview();
            SyncPreviewPopup();
            SyncProgramOverlayPopup();
            SyncMenuState();
        }

        // ============ 操作浮层：叠加层显示 + 预览拖动 ============
        // 视频图层是 Win32 子窗口（LibVLCSharp 的 VideoHwndHost），按 Windows 的规则，子窗口
        // 永远画在父窗口的 WPF 内容之上 —— 同一个窗口里画的文字/图片一定被视频盖住（空气空间限制）。
        // 顶层窗口不受此限制，所以叠加层与预览拖动面都做成贴在监视器上的独立顶层窗口。
        // 附带好处：预览拖动改用标准 WPF 鼠标事件，不再依赖全局低级鼠标钩子（钩子在部分环境下会失效）。

        private DispatcherTimer? _popupReanchor;

        /// <summary>把叠加层窗口对齐到输出画布（尺寸与坐标系和画布完全一致）。</summary>
        private void SyncProgramOverlayPopup()
        {
            if (ProgramOvPopup == null || ProgramOvRoot == null || LayersCanvas == null) return;
            double w = LayersCanvas.ActualWidth, h = LayersCanvas.ActualHeight;
            if (!IsLoaded || w <= 1 || h <= 1 || !IsVisible || WindowState == WindowState.Minimized)
            {
                ProgramOvPopup.IsOpen = false;
                return;
            }
            ProgramOvRoot.Width = w;
            ProgramOvRoot.Height = h;
            OverlayCanvas.Width = w;
            OverlayCanvas.Height = h;
            if (!ProgramOvPopup.IsOpen) ProgramOvPopup.IsOpen = true;
            SyncOverlays();
        }

        /// <summary>
        /// 把预览操作窗口对齐到预览画布（尺寸与坐标系和画布完全一致）。
        ///
        /// ⚠ 只在「主窗口处于活动状态」时才展开 —— 这条判断是修「打开文件对话框点不动」的关键：
        ///   WPF 的 Popup 会创建带 WS_EX_TOPMOST 的独立顶层窗口，而系统「打开文件…」对话框
        ///   是普通窗口（无 TOPMOST）。顶层窗口在命中测试里永远压过普通窗口，于是一旦浮层还在，
        ///   对话框与它重叠的那一块就收不到任何鼠标消息 —— 点文件没反应、拖动对话框挪出死区后又正常。
        ///   失活时收起浮层，既解决了遮挡，也不影响任何输出（浮层只是预览框的交互层，
        ///   ProgramOvPopup 才是参与抓屏的叠加层，那个不能关，靠 IsHitTestVisible=False 穿透点击）。
        /// </summary>
        private void SyncPreviewPopup()
        {
            if (PreviewOpPopup == null || PreviewOpRoot == null || PreviewCanvas == null) return;
            double w = PreviewCanvas.ActualWidth, h = PreviewCanvas.ActualHeight;
            if (!IsLoaded || w <= 1 || h <= 1 || !IsVisible
                || WindowState == WindowState.Minimized || !IsActive)
            {
                PreviewOpPopup.IsOpen = false;
                return;
            }
            PreviewOpRoot.Width = w;
            PreviewOpRoot.Height = h;
            if (!PreviewOpPopup.IsOpen) PreviewOpPopup.IsOpen = true;
            RefreshPreviewHitDecor();   // 浮层跟随画布尺寸变化时，选中框也要跟着重算
        }

        /// <summary>立即收起预览操作浮层（不做任何尺寸/激活判断，用于失活或即将弹模态对话框时）。</summary>
        private void HidePreviewOpPopup()
        {
            if (PreviewOpPopup != null) PreviewOpPopup.IsOpen = false;
        }

        /// <summary>WPF 的 Popup 不会跟着窗口移动，移动/缩放后重开一次让它重新贴合。</summary>
        private void SchedulePopupReanchor()
        {
            _popupReanchor ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _popupReanchor.Stop();
            _popupReanchor.Start();
        }

        private void ReanchorPopups()
        {
            if (ProgramOvPopup != null) ProgramOvPopup.IsOpen = false;
            if (PreviewOpPopup != null) PreviewOpPopup.IsOpen = false;
            SyncPreviewPopup();
            SyncProgramOverlayPopup();
        }

        // —— 预览框拖动 / 缩放（浮层里的标准 WPF 事件，鼠标坐标就是画布坐标）——

        private void PreviewOpRoot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            Activate();   // 顶层窗口不抢焦点：点它时主动把主窗口带到前台
            double w = PreviewCanvas.ActualWidth, h = PreviewCanvas.ActualHeight;
            if (w <= 0 || h <= 0) return;

            // 多路预览：先按几何矩形挑出「点中的是哪一路」（从最上层往下找）。
            // 点空白处 = 取消选中，既不拖也不缩 —— 不这样处理的话，用户在空白处一按
            // 还是会拖动「上一次选中的那一路」，像是拖动失灵/乱飞。
            var p = e.GetPosition(PreviewOpRoot);
            var hit = HitPreviewSlot(p);
            if (!ReferenceEquals(hit, _selPreviewSlot))
            {
                _selPreviewSlot = hit;
                _geoPanelTargetIsPreview = true;    // 点预览 = 精调面板跟着切到这一路
                RenderPreview();      // 立刻刷新金色选中框（不然要等下一次拖动才看得到）
                RefreshGeoPanel();
            }
            if (hit == null) { e.Handled = true; return; }

            _pvStart = p; _pvOX = hit.X; _pvOY = hit.Y;
            _pvDrag = true; _pvResize = false;
            var r = PreviewSlotRect(hit);
            // 右下角 18px 见方 = 缩放（与那颗黄块的位置一致）
            if (Math.Abs(p.X - (r.X + r.Width)) <= 18 && Math.Abs(p.Y - (r.Y + r.Height)) <= 18)
            { _pvResize = true; _pvDrag = false; _pvRL = r.X; _pvRT = r.Y; }
            PreviewOpRoot.CaptureMouse();
            e.Handled = true;
        }

        private void PreviewOpRoot_MouseMove(object sender, MouseEventArgs e)
        {
            var slot = _selPreviewSlot;
            if (slot == null || (!_pvDrag && !_pvResize)) return;
            if (e.LeftButton != MouseButtonState.Pressed) { _pvDrag = _pvResize = false; return; }
            double w = PreviewCanvas.ActualWidth, h = PreviewCanvas.ActualHeight;
            if (w <= 0 || h <= 0) return;
            var p = e.GetPosition(PreviewOpRoot);
            if (_pvResize)
            {
                double newW = Math.Max(20, p.X - _pvRL);
                double newH = Math.Max(20, p.Y - _pvRT);
                double sx = Math.Clamp(newW / w, PreviewMinScale, PreviewMaxScale);
                double sy = Math.Clamp(newH / h, PreviewMinScale, PreviewMaxScale);
                if (slot.AspectLock) { double f = Math.Max(sx, sy); sx = sy = f; }   // 锁比例：取大的那边，不变形
                slot.ScaleX = sx;
                slot.ScaleY = sy;
                ClampPreviewPos(slot);
            }
            else
            {
                // 拖到哪算到哪 → 过一遍取景框规则（允许超出，只保证留得下一小块）→ 再吸附
                double nx = _pvOX + (p.X - _pvStart.X) / w;
                double ny = _pvOY + (p.Y - _pvStart.Y) / h;
                slot.X = nx; slot.Y = ny;
                ClampPreviewPos(slot);

                var (sx2, sy2, lx, ly) = SnapPreviewDrag(slot, slot.X, slot.Y);
                slot.X = sx2; slot.Y = sy2;
                ClampPreviewPos(slot);      // 吸附后再夹一次（吸到框外的情况兜住）
                ShowSnapGuides(lx, ly);
            }
            RenderPreview();
            RefreshGeoPanel();
            StatusText.Text = $"预览「{slot.DisplayName}」：位置 {(int)Math.Round(slot.X * 100)}% , "
                            + $"{(int)Math.Round(slot.Y * 100)}% · 宽 {slot.ScaleX:0.00}× 高 {slot.ScaleY:0.00}×";
            e.Handled = true;
        }

        private void PreviewOpRoot_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            bool was = _pvDrag || _pvResize;
            _pvDrag = _pvResize = false;
            HideSnapGuides();        // 松手就收参考线（留着会让人以为那条线是画面的一部分）
            if (PreviewOpRoot.IsMouseCaptured) PreviewOpRoot.ReleaseMouseCapture();
            if (was) { RefreshGeoPanel(); e.Handled = true; }
        }

        private void PreviewOpRoot_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            // ⚠ 缩放对象必须是「指针底下的那一路」——就按指针位置现场命中，而不是"上次选中的那一路"。
            //   实测踩过：先拖过第 3 路（它就成了当前选中），再把指针移到第 4 路滚轮，
            //   结果缩的是第 3 路 —— 与手感完全相反。命中不到才退回当前选中（指针在空白处微调）。
            var hit = HitPreviewSlot(e.GetPosition(PreviewOpRoot));
            if (hit != null)
            {
                if (!ReferenceEquals(hit, _selPreviewSlot))
                {
                    _selPreviewSlot = hit;
                    _geoPanelTargetIsPreview = true;
                    RenderPreview();
                    RefreshGeoPanel();
                }
            }
            else
            {
                hit = _selPreviewSlot;
                if (hit == null) { e.Handled = true; return; }
            }

            ZoomPreviewAt(hit, e.Delta > 0 ? 1.1 : 0.9);
            StatusText.Text = $"预览「{hit.DisplayName}」缩放 {hit.Scale:0.00}×（滚轮可继续缩放）";
            e.Handled = true;
        }

        private void PreviewOpRoot_LostMouseCapture(object sender, MouseEventArgs e)
            => _pvDrag = _pvResize = false;

        /// <summary>
        /// 把当前工程状态推给合成器（UI 线程，500ms 一轮 + 拖动时立即调）。
        ///
        /// 只做"读工程 → 写快照"，不碰像素；像素在合成线程里算。
        /// 任何一侧抛异常都只写日志：监视器黑一帧不能把导播拖垮。
        /// </summary>
        private void SyncMonitorState()
        {
            if (_monitor == null) return;

            // 帧率跟工程的「输出设置」走 —— 25fps 工程如果按 30 合成，
            // 画面会比声音快 20%（P4 第一版就是这么错的，三处硬编码 30）。
            try
            {
                double fps = StreamService.Instance.Fps;
                if (fps > 0) _monitor.Fps = fps;
            }
            catch { }

            // 描边/圆角/阴影的 DIP → 像素换算基准（画布还没排版出来时先用默认值，排完会再同步一次）
            try
            {
                if (LayersCanvas.ActualWidth > 1) _monitor.CanvasWidthDip = LayersCanvas.ActualWidth;
            }
            catch { }

            try { _monitor.SyncProgram(Layers); }
            catch (Exception ex) { try { ErrorLog.Info("[监视器] 图层同步失败：" + ex.Message); } catch { } }

            try { _monitor.SyncPreview(_previewSlots); }
            catch (Exception ex) { try { ErrorLog.Info("[监视器] 预览同步失败：" + ex.Message); } catch { } }

            try
            {
                if (Overlays != null && _monitor.SyncOverlays(Overlays) > 0)
                    ErrorLog.Info($"[监视器] 叠加层重渲染（共 {Overlays.Count} 通道）");
            }
            catch (Exception ex) { try { ErrorLog.Info("[监视器] 叠加同步失败：" + ex.Message); } catch { } }
        }

        private void SelectLayer(Layer layer)
        {
            _selLayer = layer;
            _geoPanelTargetIsPreview = false;   // 点图层 = 精调面板跟着切到这一层
            // 选中反馈：靠右侧图层列表高亮（VideoView 的 HwndHost 会遮挡画布上的描边，故用列表呈现）
            LayerList.SelectedItem = layer;
            // 选中一格的语义（2026-10-23 起）：成为「换格目标」+ 精调面板对象。
            // ⚠ 点左侧输入**不会**自动换格（那只进预览）—— 老板的流程是
            //   「点文件 → 进预览 → 点切换 → 进播出」，残留的选中状态不能把点文件劫持成换格。
            //   换格走左侧输入项右键的「换入播出框选中的那一格」，动作显式。
            int idx = Layers.IndexOf(layer) + 1;
            StatusText.Text = $"已选中第 {idx} 格 · {layer.DisplayName}"
                              + "（要换这一格的画面：到左侧右键那一路 →「换入播出框选中的那一格」；拖动可移动、滚轮缩放）";
            RefreshScaleBoxes();
            RefreshGeoPanel();
        }

        /// <summary>
        /// 音频路由（方案 C）：只有被锁定为音源的图层出声，其余一律静音。
        /// 走 AudioBus —— 非音源图层的 PCM 在总线上直接丢弃，既不出声卡也不进推流；
        /// 且播放器创建时就已静音，不会再出现"新文件进来先响一下"。
        /// </summary>
        private void ApplyAudioRouting()
        {
            foreach (var l in Layers)
            {
                var t = AudioBus.Instance.FindTrack("layer:" + l.Id);
                if (t != null)
                {
                    bool on = l.IsAudioSource && l.Visible;
                    t.Mute = !on;
                    t.Volume = on ? l.VolumePercent : 0f;   // 每格音量（2026-10-22）
                    t.ToSoundCard = on;   // 输出框的声音进声卡
                    t.ToStream = on;      // 同时进推流/录制
                }
                l.Raise(nameof(Layer.IsAudioSource));

                // 顺手记到「这一路素材」头上（2026-10-23）：重推同一批素材时沿用上次主次，
                // 不再被打回默认。只记真正出声的格子 —— 被静音的不算"老板认可过的设置"。
                if (l.IsAudioSource && l.Visible && l.Source != null)
                    l.Source.RememberAudio(l.AudioRole, l.Volume);
            }

            ApplyMonitorAudible();
            AudioMixerWindow.NotifyProgramChanged();   // 混音台开着时跟着刷「画面声音」（异步、无窗口时零成本）
        }

        /// <summary>
        /// 混音台改了「画面声音」的主次/音量/静音后回推（2026-10-23）。
        /// 走全量 <see cref="ApplyAudioRouting"/>：要顺带刷新右栏图层的 ♪ 标记与滑杆，
        /// 否则混音台静音了某一路，右栏还显示"出声"，两边对不上。
        /// </summary>
        internal void ApplyAudioRoutingFromMixer() => ApplyAudioRouting();

        /// <summary>
        /// 只把「哪几路出声、各自多大声」推给合成器（轻量，拖音量滑杆时专用）。
        /// 与 <see cref="ApplyAudioRouting"/> 分开：那个还要刷总线 Mute/路由与界面通知，
        /// 拖动滑杆时每秒会触发几十次，全量跑一遍会让列表抖。
        /// </summary>
        private void ApplyMonitorAudible()
        {
            // A 方案：声音也归合成器这边管 —— 按「输入源」而不是「图层」，
            // 因为同一个输入只解一次（预览和节目共用），声音本来也只有一份。
            // 2026-10-22：每格音量一起带过去（同一输入被多格引用时，合成器那边取最大值）。
            try
            {
                var audible = Layers
                    .Where(l => l.IsAudioSource && l.Visible && l.Source != null)
                    .Select(l => (Src: l.Source!, Vol: l.VolumePercent * l.RoleGain))
                    .ToList();
                _monitor?.SetAudibleVolumeed(audible);
            }
            catch (Exception ex)
            {
                try { ErrorLog.Info("[监视器] 音频路由失败：" + ex.Message); } catch { }
            }
        }

        /// <summary>
        /// ⚠ 2026-10-23 起**已停用**（XAML 里不再有滑杆）：老板要求「把右下的单独声音大小控制
        /// 合并进混音台一起处理」—— 右栏只显示「主音 · 100%」结果，改动一律走混音台。
        /// 别再把它挂回图层行：两处同时能改同一个值，正是当初"拖了不知道哪边生效"的根源。
        /// 下面这段留作历史实现，混音台侧的同类逻辑见 <see cref="ApplyAudioRoutingFromMixer"/>。
        /// </summary>
        private void LayerVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_volumeApplyPending) return;
            _volumeApplyPending = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _volumeApplyPending = false;
                ApplyMonitorAudible();
                AudioMixerWindow.NotifyProgramChanged();   // 右栏拖滑杆，混音台那边同步跟着动（同一份设置）
                var lay = (sender as FrameworkElement)?.DataContext as Layer;
                if (lay != null && lay.IsAudioSource)
                    SetStatus($"🔊 图层 #{lay.Id} 音量 {lay.VolumeText}", hold: false);
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private bool _volumeApplyPending;

        /// <summary>
        /// 右栏图层行的「调音」按钮（2026-10-23 老板：「把右下的单独声音大小控制合并进混音台」）。
        /// 右栏不再放音量滑杆 —— 主次 + 音量 + 静音统一到混音台「画面声音」里调，
        /// 一处调完就是最终混音，不会出现"两处都能改、拖了不知道哪边生效"。
        /// </summary>
        private void LayerOpenMixer_Click(object sender, RoutedEventArgs e)
        {
            OpenAudioMixer();
            if ((sender as FrameworkElement)?.DataContext is Layer l)
                SetStatus($"🔊 到混音台「画面声音」里调第 {Layers.IndexOf(l) + 1} 格（{l.DisplayName}）"
                          + $" —— 当前 {l.RoleLabel} {l.VolumeText}", hold: false);
        }

        /// <summary>
        /// 图层「声音」按钮：切换该图层是否加入输出声音。
        /// 支持同时选多个（老板要的"选择某个声音或多个声音"），再点一次即关闭该路。
        /// </summary>
        private void LayerAudio_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.DataContext is not Layer l) return;
            l.IsAudioSource = !l.IsAudioSource;
            ApplyAudioRouting();
            var on = Layers.Where(x => x.IsAudioSource).Select(x => "#" + x.Id).ToList();
            ProgramNameText.Text = on.Count == 0 ? "音频：静音" : "音频：" + string.Join(" + ", on);
            StatusText.Text = l.IsAudioSource
                ? $"🔊 图层 #{l.Id}（{l.DisplayName}）已加入输出声音"
                : $"🔇 图层 #{l.Id}（{l.DisplayName}）已从输出声音移除";
        }

        /// <summary>锁定某图层为声音输出源（其余自动静音）。</summary>
        private void SetAudioLayer(Layer layer)
        {
            foreach (var l in Layers) l.IsAudioSource = (l == layer);
            ApplyAudioRouting();
            ProgramNameText.Text = "音频：" + layer.DisplayName;
            StatusText.Text = $"🔊 已锁定图层 #{layer.Id}（{layer.DisplayName}）的声音输出";
        }

        private void LayerDelete_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.DataContext is Layer l) RemoveLayer(l);
        }

        private void LayerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LayerList.SelectedItem is Layer l) SelectLayer(l);
        }

        private void RemoveLayer(Layer l)
        {
            if (l.View != null) LayersCanvas.Children.Remove(l.View);
            if (l.Player != null) VideoEngine.UnregisterPlayer(l.Player);
            try { l.Player?.Stop(); } catch { }
            try { l.Player?.Dispose(); } catch { }
            AudioBus.Instance.RemoveTrack("layer:" + l.Id);   // 同步释放该图层在总线上的轨道
            Layers.Remove(l);
            SyncLayerBorders();                               // 把它的描边一起摘掉（否则画面上留个空框）
            if (ReferenceEquals(_selLayer, l)) { _selLayer = null; RefreshGeoPanel(); }
            if (l.IsAudioSource && Layers.Count > 0)
                Layers[0].IsAudioSource = true;   // 音频源被删，自动让第一个图层接棒
            ApplyAudioRouting();
        }

        // ===================== 模块9：工程文件（*.cutlance）保存 / 载入 =====================
        // 把「当前调用的所有素材与设置」整体存成一个 .cutlance 文件（JSON），下次直接载入还原：
        // 输入源（含摄像头采集参数）· 输出图层布局 · 8 路叠加层 · 四路音频混音 ·
        // 画面效果（调色/美颜/美体）· 人体测算数据 · 输出设置（引擎/编码器/分辨率/推流地址/录制目录…）。

        /// <summary>把当前界面状态收集成工程对象。</summary>
        private ProjectFile CollectProject()
        {
            var svc = StreamService.Instance;
            var p = new ProjectFile();

            foreach (var s in Inputs)
                p.Inputs.Add(new ProjectInput
                {
                    Name = s.Name,
                    Type = s.Type.ToString(),
                    Location = s.Location,
                    CamWidth = s.CamWidth,
                    CamHeight = s.CamHeight,
                    CamFps = s.CamFps,
                    CamChroma = s.CamChroma,
                    CamAudio = s.CamAudio,
                    // 模块E：采集卡 / 摄像机信号
                    CamAudioDevice = s.CamAudioDevice,
                    CamLink = s.CamLink.ToString(),
                    // 模块E B-2：去隔行 / 低延迟
                    CamInterlaced = s.CamInterlaced,
                    CamDeinterlace = s.CamDeinterlace.ToString(),
                    CamLowLatency = s.CamLowLatency,
                    Deinterlace = s.Deinterlace,     // 通用去隔行（2026-10-24）
                    // 模块D：网络流输入
                    NetProtocol = s.NetProtocol.ToString(),
                    NetRtspTransport = s.NetRtspTransport.ToString(),
                    NetSrtLatency = s.NetSrtLatency,
                    NetSrtMode = s.NetSrtMode.ToString(),
                    NetSrtPassphrase = s.NetSrtPassphrase,
                    NetTimeoutMs = s.NetTimeoutMs,
                    // 模块C：输入级裁切 / 旋转 / 镜像
                    CropTop = s.CropTop,
                    CropBottom = s.CropBottom,
                    CropLeft = s.CropLeft,
                    CropRight = s.CropRight,
                    UsePctCrop = s.UsePctCrop,
                    CropPctLeft = s.CropPctLeft,
                    CropPctTop = s.CropPctTop,
                    CropPctRight = s.CropPctRight,
                    CropPctBottom = s.CropPctBottom,
                    Rotate = s.Rotate,
                    FlipH = s.FlipH,
                    FlipV = s.FlipV,
                    // 模块C A-2：抠像换背景
                    ChromaEnabled = s.ChromaEnabled,
                    ChromaKeyColor = s.ChromaKeyColor,
                    ChromaSimilarity = s.ChromaSimilarity,
                    ChromaBlend = s.ChromaBlend,
                    BgType = s.BgType.ToString(),
                    BgColor = s.BgColor,
                    BgWidth = s.BgWidth,
                    BgHeight = s.BgHeight,
                    BgPath = s.BgPath,
                    // 办公文档（表格分页）：存每页图片 + 当前页 + 原文件
                    OfficePages = new List<string>(s.OfficePages),
                    OfficePageIndex = s.OfficePageIndex,
                    OfficeSourcePath = s.OfficeSourcePath,
                    CaptureLeft = s.CaptureLeft,
                    CaptureTop = s.CaptureTop,
                    CaptureWidth = s.CaptureWidth,
                    CaptureHeight = s.CaptureHeight,
                    CaptureAudioDevice = s.CaptureAudioDevice ?? ""
                });

            // 模块A：播放列表（SourceIndex 指向刚写好的 Inputs 顺序）
            foreach (var pi in PlayItems)
                p.PlayList.Add(new ProjectPlayItem
                {
                    SourceIndex = Inputs.IndexOf(pi.Source),
                    DurationSec = pi.DurationSec
                });
            p.PlayListLoop = ChkPlayLoop.IsChecked == true;

            // 模块B：倒计时 / 触发器配置
            p.Countdown = new ProjectCountdown
            {
                Mode = (int)_countdown.Config.Mode,
                TargetTimeText = _countdown.Config.TargetTimeText,
                DurationSec = _countdown.Config.DurationSec,
                Action = (int)_countdown.Config.Action,
                TargetIndex = _countdown.Config.TargetIndex,
                RemindText = _countdown.Config.RemindText,
                Repeat = _countdown.Config.Repeat,
                DisplayOverlayChannel = _countdown.Config.DisplayOverlayChannel,
                DisplayFormat = _countdown.Config.DisplayFormat
            };

            foreach (var l in Layers)
                p.Layers.Add(new ProjectLayer
                {
                    SourceIndex = l.Source == null ? -1 : Inputs.IndexOf(l.Source),
                    X = l.X,
                    Y = l.Y,
                    Scale = l.ScaleX,        // 兼容老读取方：Scale 永远等于宽度缩放
                    ScaleX = l.ScaleX,
                    ScaleY = l.ScaleY,
                    Z = l.Z,
                    Visible = l.Visible,
                    IsAudioSource = l.IsAudioSource,
                    Volume = l.Volume,
                    AudioRole = l.AudioRole.ToString(),
                    AspectLock = l.AspectLock,
                    BorderThickness = l.BorderThickness,
                    BorderRadius = l.BorderRadius,
                    BorderColor = l.BorderColor,
                    GroupId = l.GroupId,
                    Opacity = l.Opacity,       // 图层透明度（2026-10-24）
                    Shadow = l.Shadow,         // 阴影（2026-10-25）
                    ShadowOpacity = l.ShadowOpacity,
                    FitMode = l.FitMode        // 填充方式（异形屏）
                });

            // 预览框（2026-10-23 老板要的）：单路 / 多选分屏的**排列**全部记下来，
            // 下次打开工程直接就回到这张图，不用重新一个个点。
            // 存的是预览侧口径（ScaleX = 元素宽 ÷ 画布宽），与输出图层的 ×2 口径无关。
            p.Preview = new List<ProjectPreviewSlot>();
            foreach (var s in _previewSlots)
                p.Preview.Add(new ProjectPreviewSlot
                {
                    SourceIndex = s.Source == null ? -1 : Inputs.IndexOf(s.Source),
                    X = s.X,
                    Y = s.Y,
                    ScaleX = s.ScaleX,
                    ScaleY = s.ScaleY,
                    Z = s.Z,
                    AspectLock = s.AspectLock,
                    BorderThickness = s.BorderThickness,
                    BorderRadius = s.BorderRadius,
                    BorderColor = s.BorderColor
                });

            foreach (var o in Overlays)
                p.Overlays.Add(new ProjectOverlay
                {
                    ChannelIndex = o.ChannelIndex,
                    HasContent = o.HasContent,
                    Visible = o.Visible,
                    Type = o.Type.ToString(),
                    Text = o.Text,
                    ImagePath = o.ImagePath,
                    XPct = o.XPct,
                    YPct = o.YPct,
                    FontSize = o.FontSize,
                    ColorHex = o.ColorHex,
                    FontFamilyName = o.FontFamilyName,
                    Bold = o.Bold,
                    Italic = o.Italic,
                    ImageScale = o.ImageScale
                });

            foreach (var a in svc.AudioChannels)
                p.Audio.Add(new ProjectAudio
                {
                    Index = a.Index,
                    DeviceName = a.DeviceName,
                    Volume = a.Volume,
                    Muted = a.Muted,
                    Role = a.Role.ToString().ToLowerInvariant(),
                    Compress = a.Compress.ToString()
                });

            // 输出总线压缩档（2026-10-23）。写进工程，否则重启程序就变回「关闭」，等于没设。
            p.Compressor = new ProjectCompressor
            {
                Level = svc.Compressor.Level.ToString().ToLowerInvariant(),
                ThresholdDb = svc.Compressor.ThresholdDb,
                Ratio = svc.Compressor.Ratio,
                MakeupDb = svc.Compressor.MakeupDb
            };

            var f = svc.Fx;
            p.Fx = new ProjectFx
            {
                Brightness = f.Brightness,
                Contrast = f.Contrast,
                Saturation = f.Saturation,
                Gamma = f.Gamma,
                Temperature = f.Temperature,
                Whiten = f.Whiten,
                Smooth = f.Smooth,
                Slim = f.Slim,
                Stretch = f.Stretch,
                SegmentedBody = f.SegmentedBody,
                WaistY = f.WaistY,
                HipY = f.HipY,
                SlimSeg = f.SlimSeg,
                LegSeg = f.LegSeg,
                WarpCenterX = f.WarpCenterX,
                WarpWidth = f.WarpWidth
            };

            var b = BodyProfileStore.Load();
            p.Body = new ProjectBody
            {
                Height = b.Height,
                NavelHeight = b.NavelHeight,
                SittingHeight = b.SittingHeight,
                Waist = b.Waist,
                Hip = b.Hip,
                IsFemale = b.IsFemale,
                UseManouvrierForLeg = b.UseManouvrierForLeg
            };

            // 界面状态（2026-10-23 老板「都存入」）：右栏折叠状态 + 当时选中第几格
            p.Ui = new ProjectUi
            {
                LayersMaximized = _layersExpanded,
                SelectedLayerIndex = _selLayer == null ? -1 : Layers.IndexOf(_selLayer)
            };

            // 倒计时牌（2026-10-24）：设定值 + 叠加字号 + 是否叠加 + 叠加位置 + 样式
            p.Clock = new ProjectClock
            {
                SetSeconds = _cdClock.SetSeconds,
                FontSize = _cdFontPx,
                OverlayOn = _cdOverlayOn,
                X = _cdX,
                Y = _cdY,
                FontFamily = _cdStyle.FontFamilyName,
                Bold = _cdStyle.Bold,
                Italic = _cdStyle.Italic,
                TextColor = _cdStyle.TextColorHex,
                NegativeColor = _cdStyle.NegativeColorHex,
                BackColor = _cdStyle.BackColorHex
            };

            p.Output = new ProjectOutput
            {
                Engine = svc.OutputEngine,
                Encoder = svc.Encoder,
                Container = svc.Container,
                Fps = svc.Fps,
                // 切换台帧率单独存（double）：int 装不下 29.97 / 59.94。
                // 0 = 跟随 Fps，老工程读出来就是 0，语义正好。
                SynthFps = svc.SynthFps,
                BitrateKbps = svc.BitrateKbps,
                // 分辨率写「待生效」优先：合成器尺寸在启动时锁定，菜单里选的竖屏要等重启才真生效，
                // 但工程必须记住用户的选择，否则重启读回来还是横屏（见 MenuRes_Click）。
                Width = _pendingResW > 0 ? _pendingResW : svc.OutputWidth,
                Height = _pendingResH > 0 ? _pendingResH : svc.OutputHeight,
                RtmpUrl = svc.RtmpUrl,
                RecordFolder = svc.RecordFolder,
                EnableStream = svc.EnableStream,
                EnableRecord = svc.EnableRecord,
                IncludeMic = svc.IncludeMic,
                // 模块F：NDI 输出
                NdiOutputEnabled = svc.EnableNdi,
                NdiOutputName = svc.NdiSourceName,
                NdiOutputFps = svc.NdiFps,
                NdiExtraIps = NdiService.Instance.ExtraDiscoveryIps ?? "",
                HardwareDecoding = _engine.HardwareDecoding
            };

            return p;
        }

        /// <summary>把工程对象还原到界面（会先停止输出、清空现有素材）。</summary>
        private void ApplyProject(ProjectFile p)
        {
            var svc = StreamService.Instance;
            p.Fx ??= new ProjectFx();
            p.Body ??= new ProjectBody();
            p.Output ??= new ProjectOutput();
            p.PlayList ??= new List<ProjectPlayItem>();

            // ① 先停输出（输出引擎持有旧素材 / 区域引用）
            try { svc.Stop(); } catch { }
            StopHdmiOutput();

            // ② 清空图层、输入与叠加层
            //    先停掉输入预处理层的所有 ffmpeg —— 载入新工程会把 InputSource 全部换掉，
            //    按输入引用缓存的进程就再也回收不到了（会变成孤儿一直编码）。
            InputPreprocessService.Instance.StopAll();
            foreach (var l in Layers.ToList()) RemoveLayer(l);
            _mainLayer = null;                    // 上面的清空把播放列表图层也删了，字段同步置空
            if (_playSvc.IsRunning) _playSvc.Stop();
            Inputs.Clear();
            ClearPreviewSlots();                  // 预览组也可能引用了旧输入，一并清掉（否则留下悬空引用）
            try { _engine.PreviewPlayer.Stop(); } catch { }
            _previewSource = null;
            PreviewNameText.Text = "";
            foreach (var o in Overlays) ClearOverlay(o);

            // ③ 还原输入源
            foreach (var pi in p.Inputs)
                AddInput(new InputSource
                {
                    Name = pi.Name,
                    Type = ParseSourceType(pi.Type),
                    Location = pi.Location,
                    CamWidth = pi.CamWidth,
                    CamHeight = pi.CamHeight,
                    CamFps = pi.CamFps,
                    CamChroma = pi.CamChroma,
                    CamAudio = pi.CamAudio,
                    // 模块E：采集卡 / 摄像机信号
                    CamAudioDevice = pi.CamAudioDevice,
                    CamLink = ParseCameraLink(pi.CamLink),
                    // 模块E B-2：去隔行 / 低延迟
                    CamInterlaced = pi.CamInterlaced,
                    CamDeinterlace = ParseDeinterlace(pi.CamDeinterlace),
                    CamLowLatency = pi.CamLowLatency,
                    Deinterlace = pi.Deinterlace,    // 老工程缺 → false（不去隔行，与升级前一致）
                    // 模块D：网络流输入
                    NetProtocol = ParseNetProtocol(pi.NetProtocol),
                    NetRtspTransport = ParseRtspTransport(pi.NetRtspTransport),
                    NetSrtLatency = pi.NetSrtLatency,
                    NetSrtMode = ParseSrtMode(pi.NetSrtMode),
                    NetSrtPassphrase = pi.NetSrtPassphrase,
                    NetTimeoutMs = pi.NetTimeoutMs,
                    // 模块C：输入级裁切 / 旋转 / 镜像
                    CropTop = pi.CropTop,
                    CropBottom = pi.CropBottom,
                    CropLeft = pi.CropLeft,
                    CropRight = pi.CropRight,
                    UsePctCrop = pi.UsePctCrop,
                    CropPctLeft = pi.CropPctLeft,
                    CropPctTop = pi.CropPctTop,
                    CropPctRight = pi.CropPctRight,
                    CropPctBottom = pi.CropPctBottom,
                    Rotate = pi.Rotate,
                    FlipH = pi.FlipH,
                    FlipV = pi.FlipV,
                    // 模块C A-2：抠像换背景
                    ChromaEnabled = pi.ChromaEnabled,
                    ChromaKeyColor = string.IsNullOrWhiteSpace(pi.ChromaKeyColor) ? "0x00B140" : pi.ChromaKeyColor,
                    ChromaSimilarity = pi.ChromaSimilarity,
                    ChromaBlend = pi.ChromaBlend,
                    BgType = ParseChromaBg(pi.BgType),
                    BgColor = string.IsNullOrWhiteSpace(pi.BgColor) ? "0x1E1E1E" : pi.BgColor,
                    BgWidth = pi.BgWidth > 0 ? pi.BgWidth : 1920,
                    BgHeight = pi.BgHeight > 0 ? pi.BgHeight : 1080,
                    BgPath = pi.BgPath,
                    // 办公文档（表格分页）：页图片还在就接着用；临时目录被清了就退回第 1 页
                    OfficePages = (pi.OfficePages ?? new List<string>())
                                  .Where(f => !string.IsNullOrWhiteSpace(f) && File.Exists(f)).ToList(),
                    OfficePageIndex = Math.Max(0, pi.OfficePageIndex),
                    OfficeSourcePath = pi.OfficeSourcePath ?? "",
                    CaptureLeft = pi.CaptureLeft,
                    CaptureTop = pi.CaptureTop,
                    CaptureWidth = pi.CaptureWidth,
                    CaptureHeight = pi.CaptureHeight,
                    CaptureAudioDevice = pi.CaptureAudioDevice ?? ""
                });

            // ④ 还原 8 路叠加层
            foreach (var po in p.Overlays)
            {
                var o = Overlays.FirstOrDefault(x => x.ChannelIndex == po.ChannelIndex);
                if (o == null) continue;
                o.Type = po.Type.Equals("Image", StringComparison.OrdinalIgnoreCase)
                    ? OverlayType.Image : OverlayType.Text;
                o.Text = po.Text;
                o.ImagePath = po.ImagePath;
                o.XPct = po.XPct;
                o.YPct = po.YPct;
                o.FontSize = po.FontSize;
                o.ColorHex = po.ColorHex;
                o.FontFamilyName = po.FontFamilyName;
                o.Bold = po.Bold;
                o.Italic = po.Italic;
                o.ImageScale = po.ImageScale > 0 ? po.ImageScale : 100;
                o.Visible = po.Visible;
                // 内容存在性按实际素材复核（文字非空 / 图片路径存在），避免还原出空通道
                o.HasContent = po.HasContent && (o.Type == OverlayType.Text
                    ? !string.IsNullOrWhiteSpace(o.Text)
                    : !string.IsNullOrEmpty(o.ImagePath));
            }
            SyncOverlays();
            RefreshOverlayButtons();

            // ⑤ 还原输出图层（按输入源下标重新绑定，重建播放器与视图）
            foreach (var pl in p.Layers)
            {
                if (pl.SourceIndex < 0 || pl.SourceIndex >= Inputs.Count) continue;
                // ScaleX/ScaleY 是 2026-10-18 新增的（宽高可分别设）；老工程只写了 Scale，
                // 此时 ScaleX/ScaleY 都是 0 → 回落到 Scale（保持老工程原样）。
                double sx = pl.ScaleX > 0 ? pl.ScaleX : pl.Scale;
                double sy = pl.ScaleY > 0 ? pl.ScaleY : pl.Scale;
                var ly = AddLayerFromSource(Inputs[pl.SourceIndex], pl.X, pl.Y, sx, pl.Z,
                                            pl.Visible, pl.IsAudioSource, false);
                if (ly == null) continue;
                ly.ScaleY = Math.Clamp(sy, MinLayerScale, MaxLayerScale);
                ly.AspectLock = pl.AspectLock;
                ly.BorderThickness = pl.BorderThickness;
                ly.BorderRadius = pl.BorderRadius;
                ly.BorderColor = pl.BorderColor;
                ly.Shadow = pl.Shadow;                 // 阴影（老工程缺字段 = 0 = 无阴影）
                ly.ShadowOpacity = pl.ShadowOpacity;
                ly.FitMode = pl.FitMode;               // 填充方式（老工程缺字段 = 0 = 等比内接）
                ly.GroupId = pl.GroupId;          // 画面组（老工程为 0，按同一套布局处理）
                // 透明度（2026-10-24）：老工程没有这一项 → 默认 100 = 不透明，画面与升级前一致
                ly.Opacity = pl.Opacity;
                // 老工程没有 Volume 字段 → 反序列化后是默认值 100（原音量），正好不用特判；
                // 新工程存了 0 就是静音，也照原样读回。
                ly.Volume = pl.Volume;
                // 角色档（2026-10-23）：老工程没有 → 空串 → 主音不衰减，音量与升级前一致。
                ly.AudioRole = Enum.TryParse<AudioRole>(pl.AudioRole, true, out var prole)
                    ? prole : AudioRole.Main;
                ClampLayerPos(ly);
                RenderLayer(ly);
            }
            SyncLayerBorders();

            // ⑤a 还原预览框（2026-10-23 老板要的：多选进预览的排列也属于"工程设置"）
            RestorePreviewSlots(p.Preview);
            // 组号发号器跟工程对齐：否则重开工程后第一次整组切会从 1 号重新发，
            // 与工程里已有的组号（可能已经是 3）撞号 —— 现在 _activeGroupId 只是记账，
            // 但记账乱了以后排查「哪一套在播」会很难，顺手对齐。
            int maxGroup = Layers.Count == 0 ? 0 : Layers.Max(l => l.GroupId);
            _groupSeq = Math.Max(_groupSeq, maxGroup);
            _activeGroupId = maxGroup;

            // ⑤b 还原播放列表（模块A）：列表项按输入源下标重新绑定（不自动开播）
            PlayItems.Clear();
            foreach (var pp in p.PlayList)
            {
                if (pp.SourceIndex < 0 || pp.SourceIndex >= Inputs.Count) continue;
                PlayItems.Add(new PlayListItem
                {
                    Source = Inputs[pp.SourceIndex],
                    DurationSec = pp.DurationSec
                });
            }
            RefreshPlayOrders();
            ChkPlayLoop.IsChecked = p.PlayListLoop;
            _playSvc.Loop = p.PlayListLoop;
            RefreshPlayButtons();

            // ⑤c 还原倒计时 / 触发器配置（模块B）：复位到满时长，不自动开跑
            var cdp = p.Countdown ?? new ProjectCountdown();
            _countdown.Stop();
            _countdown.Config.Mode = (CountdownMode)cdp.Mode;
            if (!string.IsNullOrWhiteSpace(cdp.TargetTimeText))
                _countdown.Config.TargetTimeText = cdp.TargetTimeText;
            _countdown.Config.DurationSec = cdp.DurationSec > 0 ? cdp.DurationSec : 60;
            _countdown.Config.Action = (CountdownAction)cdp.Action;
            _countdown.Config.TargetIndex = cdp.TargetIndex;
            if (!string.IsNullOrWhiteSpace(cdp.RemindText))
                _countdown.Config.RemindText = cdp.RemindText;
            // 「循环」已从「到指定时刻」面板移除 → 该模式下不再认工程里存着的 Repeat，
            // 免得变成界面上看不见、也关不掉的隐形循环（服务层另有一道同样的兜底）
            _countdown.Config.Repeat = cdp.Repeat && (CountdownMode)cdp.Mode == CountdownMode.Duration;
            _countdown.Config.DisplayOverlayChannel = Math.Clamp(cdp.DisplayOverlayChannel, 0, 8);
            if (!string.IsNullOrWhiteSpace(cdp.DisplayFormat))
                _countdown.Config.DisplayFormat = cdp.DisplayFormat;
            _countdown.Reset();

            // ⑥ 还原音频混音（八路）
            for (int i = 0; i < svc.AudioChannels.Count; i++)
            {
                var a = svc.AudioChannels[i];
                var pa = p.Audio.FirstOrDefault(x => x.Index == a.Index)
                         ?? (i < p.Audio.Count ? p.Audio[i] : null);
                if (pa == null) continue;
                a.DeviceName = pa.DeviceName;
                a.Volume = pa.Volume;
                a.Muted = pa.Muted;
                // 老工程没有 Role 字段 → 按主音（0dB），音量与之前完全一致，不会突然变小声
                a.Role = Enum.TryParse<AudioRole>(pa.Role, true, out var role) ? role : AudioRole.Main;
                // 逐路压缩（2026-10-24）：老工程没有 → 空串 → 关闭，输出与升级前一样
                a.Compress = Enum.TryParse<CompressorLevel>(pa.Compress, true, out var ccl)
                    ? ccl : CompressorLevel.Off;
            }

            // ⑥b 输出总线压缩档：老工程没有这一段 → 一律按「关闭」，
            //     保证"升级后打开旧工程，输出跟以前一模一样"。
            var cmp = svc.Compressor;
            if (p.Compressor == null)
            {
                cmp.Level = CompressorLevel.Off;
            }
            else
            {
                cmp.Level = ProgramLayoutStore.ParseCompressorLevel(p.Compressor.Level);
                cmp.ThresholdDb = p.Compressor.ThresholdDb;
                cmp.Ratio = p.Compressor.Ratio;
                cmp.MakeupDb = p.Compressor.MakeupDb;
            }

            // ⑦ 还原画面效果
            var f = svc.Fx;
            f.Brightness = p.Fx.Brightness;
            f.Contrast = p.Fx.Contrast;
            f.Saturation = p.Fx.Saturation;
            f.Gamma = p.Fx.Gamma;
            f.Temperature = p.Fx.Temperature;
            f.Whiten = p.Fx.Whiten;
            f.Smooth = p.Fx.Smooth;
            f.Slim = p.Fx.Slim;
            f.Stretch = p.Fx.Stretch;
            f.SegmentedBody = p.Fx.SegmentedBody;
            f.WaistY = p.Fx.WaistY;
            f.HipY = p.Fx.HipY;
            f.SlimSeg = p.Fx.SlimSeg;
            f.LegSeg = p.Fx.LegSeg;
            f.WarpCenterX = p.Fx.WarpCenterX;
            f.WarpWidth = p.Fx.WarpWidth;

            // ⑧ 还原人体测算数据（供「画面效果」窗口的美学比例测算读取）
            BodyProfileStore.Save(new BodyProportion
            {
                Height = p.Body.Height,
                NavelHeight = p.Body.NavelHeight,
                SittingHeight = p.Body.SittingHeight,
                Waist = p.Body.Waist,
                Hip = p.Body.Hip,
                IsFemale = p.Body.IsFemale,
                UseManouvrierForLeg = p.Body.UseManouvrierForLeg
            });

            // 通知已打开的「画面效果 / 音频混音台」窗口同步刷新（工程里的人体数据与音频设置已变）
            foreach (Window w in Application.Current.Windows)
            {
                if (w is VideoFxWindow vfx) vfx.ReloadBodyFromStore();
                else if (w is AudioMixerWindow am) am.SyncFromModel();
            }

            // ⑨ 还原输出设置
            var oo = p.Output;
            svc.OutputEngine = oo.Engine;
            svc.Encoder = oo.Encoder;
            svc.Container = oo.Container;
            svc.Fps = oo.Fps;
            // 老工程迁移（2026-10-21）：这一版之前「输出帧率」是代码写死的 30，
            // 只有切换台的 SynthFps 记着老板真正选的档位。工程里 Fps 还是默认 30
            // 而 SynthFps 另有值时，说明是那一版存的 → 以 SynthFps 为准。
            if (oo.SynthFps > 0 && Math.Abs(oo.Fps - 30) < 0.01) svc.Fps = oo.SynthFps;
            // 0 = 跟随 Fps（SynthWindow 的读法）。工程里原本写的就是 0 时保持 0 ——
            // 不要在这里补成实数，否则「存 → 重开 → 再存」会多出一条 SynthFps 差异
            // （功能等价，但往返账目对不上，2026-10-23 往返自检实测抓到）。
            svc.SynthFps = oo.SynthFps > 0 ? svc.Fps : 0;
            svc.BitrateKbps = oo.BitrateKbps;
            svc.RtmpUrl = oo.RtmpUrl;
            svc.RecordFolder = oo.RecordFolder;
            svc.EnableStream = oo.EnableStream;
            svc.EnableRecord = oo.EnableRecord;
            svc.IncludeMic = oo.IncludeMic;
            // 模块F：NDI 输出（老工程文件里没有这几个字段 → 反序列化后是 null，给兜底）
            svc.EnableNdi = oo.NdiOutputEnabled;
            svc.NdiSourceName = oo.NdiOutputName ?? "";
            svc.NdiFps = oo.NdiOutputFps;
            NdiService.Instance.ExtraDiscoveryIps = oo.NdiExtraIps ?? "";
            // 分辨率：工程里存的值只有**下次启动**才有意义 —— 合成器（画面缓冲/预览位图/
            // 离屏画布）已经按当前尺寸建好了，运行时没法重建。直接写进 svc 会造成
            // 「设定值 ≠ 实际画面」：点开始输出被静默拽回，或者按错误尺寸编码拉变形。
            // 所以尺寸不一致时只记 pending + 提示重启，svc 保持实际值不动。
            int ow = oo.Width > 0 ? oo.Width : svc.OutputWidth;
            int oh = oo.Height > 0 ? oo.Height : svc.OutputHeight;
            if (_monitor != null && (ow != _monitor.Width || oh != _monitor.Height))
            {
                _pendingResW = ow;
                _pendingResH = oh;
                _resBarDismissed = false;   // 换工程后重新浮出提示条（这不是用户点「稍后」收起的那一次）
                SetStatus($"⚠ 工程要求的输出尺寸是 {ow} × {oh}，当前合成器为 {_monitor.Width} × {_monitor.Height}" +
                          " —— 需重启程序后生效（画面仍按当前尺寸出）", hold: true);
            }
            else
            {
                svc.OutputWidth = ow;
                svc.OutputHeight = oh;
                _pendingResW = _pendingResH = 0;
            }
            _engine.SetHardwareDecoding(oo.HardwareDecoding);

            FitMonitors();
            RefreshAudioFxHint();

            // ⑩ 全量还原完必须重算一次声音路由（2026-10-23 补）：
            // 图层的 AudioRole / Volume 是在 AddLayerFromSource **之后**才写进去的，而
            // ApplyAudioRouting 早在里面就按默认「主音 + 100%」算过一遍 —— 不补这一下，
            // 重开工程后角色档（次音 −8dB / 背景 −14dB）与每格音量都不生效，
            // 得等手动动一下才恢复，属于典型的「设了但没用」。
            ApplyAudioRouting();
            if (Layers.Count > 0)
            {
                var onAir = Layers.Where(x => x.IsAudioSource).Select(x => "#" + x.Id).ToList();
                ProgramNameText.Text = onAir.Count == 0 ? "音频：静音" : "音频：" + string.Join(" + ", onAir);
            }

            // ⑪ 还原界面状态（2026-10-23 老板「都存入」）：右栏折叠 + 当时选中第几格。
            //    「选中第几格」只影响换格目标与精调面板指向，不影响播出画面。
            var ui = p.Ui ?? new ProjectUi();
            SetLayersMaximized(ui.LayersMaximized, announce: false);
            if (ui.SelectedLayerIndex >= 0 && ui.SelectedLayerIndex < Layers.Count)
            {
                var sel = Layers[ui.SelectedLayerIndex];
                _selLayer = sel;
                _geoPanelTargetIsPreview = false;
                LayerList.SelectedItem = sel;
                RefreshScaleBoxes();
                RefreshGeoPanel();
            }

            // ⑫ 倒计时牌（2026-10-24）：设定值 / 字号 / 叠加开关回到工程里的样子。
            //    ⚠ 只还原设置，**不自动开始计时** —— 重开工程不该让数字自己跑起来。
            var clock = p.Clock;
            _cdClock.Set(clock?.SetSeconds ?? 3600);
            _cdFontPx = Math.Clamp(clock?.FontSize ?? 120, 40, 300);
            _cdOverlayOn = clock?.OverlayOn ?? false;
            _cdX = Math.Clamp(clock?.X ?? 0.5, 0, 1);     // 老板拖过的位置也一起回来
            _cdY = Math.Clamp(clock?.Y ?? 0.5, 0, 1);

            // 样式：空串 / null → 保留出厂值（老工程不会因为升级突然变成黑底宋体）
            if (!string.IsNullOrWhiteSpace(clock?.FontFamily)) _cdStyle.FontFamilyName = clock!.FontFamily;
            _cdStyle.Bold = clock?.Bold ?? true;
            _cdStyle.Italic = clock?.Italic ?? false;
            if (!string.IsNullOrWhiteSpace(clock?.TextColor)) _cdStyle.TextColorHex = clock!.TextColor;
            if (!string.IsNullOrWhiteSpace(clock?.NegativeColor)) _cdStyle.NegativeColorHex = clock!.NegativeColor;
            if (!string.IsNullOrWhiteSpace(clock?.BackColor)) _cdStyle.BackColorHex = clock!.BackColor;
            SyncCountdownStyleUi();

            if (CdFontSlider != null) CdFontSlider.Value = _cdFontPx;
            UpdateCountdownUi();

            SyncMenuState();
        }

        private static SourceType ParseSourceType(string s) => s switch
        {
            "Camera" => SourceType.Camera,
            "Desktop" => SourceType.Desktop,
            "Network" => SourceType.Network,
            "Ndi" => SourceType.Ndi,
            _ => SourceType.File
        };

        /// <summary>工程文件里的协议字符串 → 枚举（未知/空 → Auto，兼容旧工程）。</summary>
        private static NetworkProtocol ParseNetProtocol(string? s) => s switch
        {
            "Rtsp" => NetworkProtocol.Rtsp,
            "Rtmp" => NetworkProtocol.Rtmp,
            "Http" => NetworkProtocol.Http,
            "Srt" => NetworkProtocol.Srt,
            "Rist" => NetworkProtocol.Rist,
            "Udp" => NetworkProtocol.Udp,
            "Ndi" => NetworkProtocol.Ndi,
            _ => NetworkProtocol.Auto
        };

        /// <summary>工程文件里的 RTSP 承载方式 → 枚举（未知/空 → Auto）。</summary>
        private static RtspTransport ParseRtspTransport(string? s) => s switch
        {
            "Tcp" => RtspTransport.Tcp,
            "Udp" => RtspTransport.Udp,
            _ => RtspTransport.Auto
        };

        /// <summary>工程文件里的 SRT 模式 → 枚举（未知/空 → Caller）。</summary>
        private static SrtMode ParseSrtMode(string? s) => s switch
        {
            "Listener" => SrtMode.Listener,
            _ => SrtMode.Caller
        };

        /// <summary>工程文件里的采集链路字符串 → 枚举（未知/空 → Auto，兼容旧工程）。</summary>
        private static CameraLink ParseCameraLink(string? s) => s switch
        {
            "VlcDirect" => CameraLink.VlcDirect,
            "Ffmpeg" => CameraLink.Ffmpeg,
            _ => CameraLink.Auto
        };

        /// <summary>工程里的去隔行档位。旧工程没有这个字段 → 默认「自动」（与当时的默认一致）。</summary>
        private static DeinterlaceMode ParseDeinterlace(string? s) => s switch
        {
            "On" => DeinterlaceMode.On,
            "Off" => DeinterlaceMode.Off,
            _ => DeinterlaceMode.Auto
        };

        /// <summary>背景类型字符串 → 枚举（模块C A-2）。认不出的值退回「纯色」，不会把工程载崩。</summary>
        private static ChromaBackground ParseChromaBg(string? s) => s switch
        {
            "Image" => ChromaBackground.Image,
            "Video" => ChromaBackground.Video,
            _ => ChromaBackground.Color
        };

        /// <summary>窗口标题跟随当前工程。</summary>
        private void UpdateTitle()
        {
            Title = string.IsNullOrEmpty(_projectPath)
                ? "Cutlance"
                : "Cutlance — " + Path.GetFileNameWithoutExtension(_projectPath);
        }

        // —— 文件菜单：工程 ——
        private void MenuNewProject_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(
                    "新建工程会清空当前所有输入源、图层与叠加层。\n如需保留请先「保存工程」。\n\n确定继续？",
                    "Cutlance · 新建工程", MessageBoxButton.OKCancel, MessageBoxImage.Question)
                != MessageBoxResult.OK) return;

            ApplyProject(new ProjectFile());
            _projectPath = null;
            UpdateTitle();
            StatusText.Text = "已新建空白工程（输入 0 路 · 图层 0 层）";
        }

        private void MenuOpenProject_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "打开工程",
                Filter = ProjectService.FileFilter
            };
            var startDir = _projectPath != null ? Path.GetDirectoryName(_projectPath) : null;
            dlg.InitialDirectory = !string.IsNullOrEmpty(startDir) && Directory.Exists(startDir)
                ? startDir
                : (Directory.Exists(ProjectService.DefaultDir) ? ProjectService.DefaultDir : "");
            bool? ok = null;
            RunWithMouseHookDetached(() => ok = dlg.ShowDialog(this));   // 同「+文件」：显式 Owner + 临时摘钩
            if (ok != true) return;
            LoadProjectFrom(dlg.FileName);
        }

        /// <summary>从指定 .cutlance 文件载入工程。</summary>
        public void LoadProjectFrom(string path)
        {
            var p = ProjectService.Load(path, out var err);
            if (p == null)
            {
                MessageBox.Show(err, "Cutlance · 打开工程", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!string.Equals(p.App, "Cutlance", StringComparison.OrdinalIgnoreCase))
            {
                if (MessageBox.Show("该文件标识不是 Cutlance 工程，仍要尝试载入？",
                        "Cutlance · 打开工程", MessageBoxButton.OKCancel, MessageBoxImage.Question)
                    != MessageBoxResult.OK) return;
            }

            ApplyProject(p);
            _projectPath = path;
            UpdateTitle();
            RecentProjects.Add(path);
            RefreshRecentMenu();

            int missing = p.Inputs.Count(i =>
                i.Type == "File" && !string.IsNullOrEmpty(i.Location) && !File.Exists(i.Location));
            StatusText.Text =
                $"已载入工程：{Path.GetFileName(path)}（输入 {p.Inputs.Count} 路 · " +
                $"图层 {Layers.Count} 层 · 叠加 {p.Overlays.Count(x => x.HasContent)} 路）" +
                (missing > 0 ? $" ⚠ 有 {missing} 个素材文件已不在原位置" : "");
        }

        /// <summary>重建「最近工程」子菜单（打开/保存工程后刷新）。</summary>
        private void RefreshRecentMenu()
        {
            if (MiRecentProjects == null) return;
            MiRecentProjects.Items.Clear();

            var list = RecentProjects.Load();
            if (list.Count == 0)
            {
                MiRecentProjects.Items.Add(new MenuItem { Header = "（暂无最近工程）", IsEnabled = false });
                return;
            }

            foreach (var rp in list)
            {
                var mi = new MenuItem
                {
                    Header = Path.GetFileNameWithoutExtension(rp),
                    ToolTip = rp,
                    Tag = rp
                };
                mi.Click += RecentItem_Click;
                MiRecentProjects.Items.Add(mi);
            }

            MiRecentProjects.Items.Add(new Separator());
            var clear = new MenuItem { Header = "清空最近工程列表" };
            clear.Click += (_, _) => { RecentProjects.Clear(); RefreshRecentMenu(); };
            MiRecentProjects.Items.Add(clear);
        }

        private void RecentItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as MenuItem)?.Tag is string path) LoadProjectFrom(path);
        }

        private void MenuSaveProject_Click(object sender, RoutedEventArgs e) => SaveProject(false);

        private void MenuSaveProjectAs_Click(object sender, RoutedEventArgs e) => SaveProject(true);

        /// <summary>保存工程；saveAs 或尚无路径时弹「选择保存目录 + 文件名」对话框。</summary>
        private void SaveProject(bool saveAs)
        {
            var path = _projectPath;

            if (saveAs || string.IsNullOrEmpty(path))
            {
                try { Directory.CreateDirectory(ProjectService.DefaultDir); } catch { }

                var dlg = new SaveFileDialog
                {
                    Title = "保存工程（可另选保存目录）",
                    Filter = ProjectService.FileFilter,
                    DefaultExt = "." + ProjectService.Extension,
                    AddExtension = true,
                    FileName = string.IsNullOrEmpty(path)
                        ? $"Cutlance工程_{DateTime.Now:yyyyMMdd_HHmm}"
                        : Path.GetFileName(path)
                };
                var startDir = path != null ? Path.GetDirectoryName(path) : null;
                dlg.InitialDirectory = !string.IsNullOrEmpty(startDir) && Directory.Exists(startDir)
                    ? startDir
                    : ProjectService.DefaultDir;
                bool? ok = null;
                RunWithMouseHookDetached(() => ok = dlg.ShowDialog(this));   // 同「+文件」：显式 Owner + 临时摘钩
                if (ok != true) return;
                path = dlg.FileName;
            }

            var p = CollectProject();
            if (!ProjectService.Save(p, path!, out var err))
            {
                MessageBox.Show("保存工程失败：\n" + err, "Cutlance",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _projectPath = path;
            UpdateTitle();
            RecentProjects.Add(path!);
            RefreshRecentMenu();
            StatusText.Text =
                $"工程已保存：{path}（输入 {p.Inputs.Count} 路 · 图层 {p.Layers.Count} 层 · " +
                $"叠加 {p.Overlays.Count(o => o.HasContent)} 路）";
            // 顺手让所有打开着的工作框显示「已保存 HH:mm:ss」——
            // Ctrl+S 会被全局键盘钩子先吃掉（转成"保存工程"动作），工作框自己的 KeyBinding 收不到，
            // 所以提示由这里统一广播，按钮和快捷键的表现才一致。
            Cutlance.Services.WorkFrame.NotifyAllSaved();
        }

        /// <summary>
        /// 退出程序时自动保存工程（老板要求：不用手动 Ctrl+S）。
        ///
        /// 存到哪：
        ///   · 本次会话保存过工程 → 直接覆盖回那个 .cutlance；
        ///   · 从没保存过 → 写到「我的文档\Cutlance 工程\自动保存.cutlance」，并加入「最近工程」，
        ///     下次从「文件 → 最近工程」一键找回（按老板拍板：启动仍是空白工程，不自动载入）。
        ///
        /// 三条纪律：
        ///   1) 完全空的会话（没输入、没图层、没叠加、没播放项）不落盘 —— 不凭空生成没用的文件；
        ///   2) 全程吞异常 —— 保存失败也绝不能挡住退出、更不能弹框；
        ///   3) 同步、毫秒级完成 —— ShutdownAll 的看门狗 1.2 秒后就会硬杀进程，没有异步补救机会。
        /// </summary>
        private void AutoSaveOnExit()
        {
            try
            {
                bool empty = Inputs.Count == 0 && Layers.Count == 0 && PlayItems.Count == 0
                             && !Overlays.Any(o => o.HasContent);
                if (empty && string.IsNullOrEmpty(_projectPath)) return;

                var path = _projectPath;
                bool isAutoFile = false;
                if (string.IsNullOrEmpty(path))
                {
                    path = Path.Combine(ProjectService.DefaultDir, "自动保存.cutlance");
                    isAutoFile = true;
                }

                var p = CollectProject();
                if (!ProjectService.Save(p, path!, out _)) return;
                if (isAutoFile) RecentProjects.Add(path!);
            }
            catch
            {
                // 退出路径口的自动保存：静默失败，绝不阻塞关闭
            }
        }

        private void MenuOpenProjectFolder_Click(object sender, RoutedEventArgs e)
        {
            var dir = _projectPath != null ? Path.GetDirectoryName(_projectPath) : null;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                try { Directory.CreateDirectory(ProjectService.DefaultDir); } catch { }
                dir = ProjectService.DefaultDir;
            }
            try
            {
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法打开目录：\n" + ex.Message, "Cutlance",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // —— 模块4：推流 / 录制 ——
        /// <summary>打开「输出设置」窗口（菜单「流 / 设置 → 输出设置」与工具栏按钮共用）。</summary>
        private void OpenOutputSettings()
        {
            // 已有设置窗口则前置，避免重复开
            foreach (Window w0 in Application.Current.Windows)
            {
                if (w0 is OutputWindow ow)
                {
                    // 顺手把 HDMI 真实状态回填一次：否则窗里的按钮还停在打开时的旧状态
                    try { ow.SetHdmiRunning(_hdmiWindow != null); } catch { }
                    ow.Activate();
                    return;
                }
            }
            var w = new OutputWindow { Owner = this };
            // ⚠ 打开时就必须按真实状态初始化（HDMI 很可能已经从菜单开着了），
            // 否则按钮显示「开启」、关闭窗口时也不会去停它 —— 老板 10-18 反馈的正是这个。
            w.SetHdmiRunning(_hdmiWindow != null);
            w.StartRequested += (_, _) => StartStreaming();
            w.StopRequested += (_, _) => StreamService.Instance.Stop();
            // 模块7：HDMI 全屏输出（独立启停）
            // 老板 2026-10-18：HDMI 开启入口已统一收到「流」菜单，设置窗不再发起开启请求。
            w.HdmiStopRequested += (_, _) =>
            {
                StopHdmiOutput();   // 内部已把菜单勾选与所有打开的输出设置窗回填成「未输出」
            };
            w.Show();
        }

        // ===================== 顶部菜单：每一项都链接到对应操作按钮/功能 =====================

        // —— 文件 ——
        private void MenuAddFile_Click(object sender, RoutedEventArgs e) => BtnAddFile_Click(sender, e);
        private void MenuAddCamera_Click(object sender, RoutedEventArgs e) => BtnAddCamera_Click(sender, e);
        private void MenuAddScreen_Click(object sender, RoutedEventArgs e) => BtnAddScreen_Click(sender, e);
        private void MenuExit_Click(object sender, RoutedEventArgs e) => Close();

        // —— 编辑 ——
        private void MenuNewText_Click(object sender, RoutedEventArgs e) => BtnAddText_Click(sender, e);
        private void MenuNewImage_Click(object sender, RoutedEventArgs e) => BtnAddImg_Click(sender, e);

        private void MenuDeleteOverlay_Click(object sender, RoutedEventArgs e)
        {
            if (_selOverlay == null)
            {
                StatusText.Text = "⚠ 请先在右侧「叠加层 OVERLAYS」列表里选中一个通道";
                return;
            }
            var o = _selOverlay;
            ClearOverlay(o);
            StatusText.Text = $"已清空叠加通道 {o.ChannelIndex}";
        }

        private void MenuClearOverlays_Click(object sender, RoutedEventArgs e)
        {
            foreach (var o in Overlays) ClearOverlay(o);
            StatusText.Text = "已清空全部 8 个叠加通道";
        }

        private void MenuAddLayer_Click(object sender, RoutedEventArgs e) => BtnAddLayer_Click(sender, e);
        private void MenuClearLayers_Click(object sender, RoutedEventArgs e) => BtnClearLayers_Click(sender, e);

        // —— 视图 ——
        private void MenuShowPreview_Click(object sender, RoutedEventArgs e)
        {
            PreviewCell.Visibility = MiShowPreview.IsChecked ? Visibility.Visible : Visibility.Collapsed;
            FitMonitors();
            SyncPreviewPopup();
            SyncMenuState();
            StatusText.Text = "预览监视器：" + (MiShowPreview.IsChecked ? "显示" : "隐藏");
        }

        private void MenuShowProgram_Click(object sender, RoutedEventArgs e)
        {
            // 放大预览时输出那一列的宽度是 0，光把 Visibility 打开是看不见的：
            // 先退出放大（并让它显示输出），再照常处理。
            if (_previewZoomed && MiShowProgram != null && MiShowProgram.IsChecked)
            {
                _previewZoomed = false;
                _pgmVisibleBeforeZoom = true;
                ApplyPreviewZoom();
                return;
            }
            bool show = MiShowProgram?.IsChecked == true;
            ProgramCell.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            FitMonitors();
            SyncProgramOverlayPopup();
            SyncMenuState();
            StatusText.Text = "输出监视器：" + (show ? "显示" : "隐藏");
        }

        /// <summary>
        /// 切换输出分辨率（横屏 / 竖屏）—— **只记下，重启程序才生效**。
        ///
        /// 为什么不能立即生效：合成器（MonitorBridge）的画面缓冲、预览位图、离屏渲染画布
        /// 都在主窗构造时按尺寸一次性分配，运行时没有重建路径。以前这里直接改
        /// StreamService.OutputWidth 并宣称"已切换"，结果点「开始输出」又被拽回合成器尺寸，
        /// 是个假开关（2026-10-20 老板拍板方案 B：把它变诚实）。
        ///
        /// 现在：值记进 _pendingRes*，顺手塞进工程（CollectProject 优先取它）；
        ///      重启程序时 App.OnStartup 会先读工程尺寸再建合成器 —— 那时才真变。
        /// </summary>
        private void MenuRes_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if ((sender as MenuItem)?.Tag is not string tag) return;
                var q = tag.Split('x');
                if (q.Length != 2 || !int.TryParse(q[0], out var w) || !int.TryParse(q[1], out var h)) return;
                SetOutputResolution(w, h);
            }
            catch (Exception ex)
            {
                try { ErrorLog.Info("[分辨率] 切换失败：" + ex); } catch { }
                SetStatus("⚠ 切换分辨率失败：" + ex.Message, hold: true);
            }
            finally
            {
                SyncMenuState();
            }
        }

        /// <summary>
        /// 播放列表收起 / 展开（2026-10-25 布局整理）。
        /// 收起后只留 26 DIP 标题行，省下的 186 DIP 全给上面的输入列表 ——
        /// 输入多的时候能一眼看全，不用滚。
        /// ⚠ 收起 ≠ 停止：正在自动顺播时收起照常播，标题行的状态字（就绪/播放中）还在。
        /// </summary>
        private void BtnPlayCollapse_Click(object sender, RoutedEventArgs e)
        {
            bool expand = PlaylistPanel.Height > 40;
            PlaylistPanel.Height = expand ? 26 : 212;
            PlaylistBody.Visibility = expand ? Visibility.Collapsed : Visibility.Visible;
            if (BtnPlayCollapse != null) BtnPlayCollapse.Content = expand ? "展开" : "收起";
            SetStatus(expand
                ? "播放列表已收起（省出的高度给了输入列表；正在播的话照常播）"
                : "播放列表已展开", hold: false);
        }

        /// <summary>
        /// 叠加项编辑区的展开 / 收起（2026-10-25 布局整理）。
        ///
        /// 为什么要给这个开关：编辑区常驻时高度可观，把下面的通道列表挤到一行都看不见 ——
        /// 而列表才是「现在到底叠了什么」的全局视图。默认仍展开（不改变老板的习惯），
        /// 想看列表就点一下收起，想看文字就再点开。
        /// 与标题栏那个「收起上面」不冲突：那个是把整个叠加层区（含通道列表）都折掉。
        /// </summary>
        private void BtnOverlayEdit_Click(object sender, RoutedEventArgs e)
        {
            bool expand = OverlayEditorRow.Height.Value <= 0.5;
            OverlayEditorRow.Height = expand ? GridLength.Auto : new GridLength(0);
            if (BtnOverlayEdit != null) BtnOverlayEdit.Content = expand ? "收起" : "编辑";
            SetStatus(expand
                ? "叠加项编辑区已展开（文字 / 字体 / 颜色 / 位置）"
                : "叠加项编辑区已收起 —— 通道列表能多显示几行，需要改内容时点「编辑」", hold: false);
        }

        /// <summary>子菜单每次展开都刷一次状态：勾选、「当前画面」、「立即重启」的显隐都跟真实值走。</summary>
        private void MenuRes_SubmenuOpened(object sender, RoutedEventArgs e) => SyncMenuState();

        /// <summary>
        /// 「自定义尺寸…」——异形屏 / LED 拼接屏的出口（2026-10-25）。
        ///
        /// 为什么让老板手输而不是只给几档预设：
        ///   现场 LED 的像素排布由工程方定（3840×1080、2560×1080、5760×1080…），
        ///   档位永远列不全。给输入框 + 一个 32:9 常用档，比猜需求稳。
        /// 分隔符认 x / X / × / * / 逗号 / 空格 —— 老板很可能是从报价单或微信上抄过来的。
        /// </summary>
        private void MenuResCustom_Click(object sender, RoutedEventArgs e)
        {
            string? input = Views.SimpleInputBox.Show(this, "输出分辨率（异形屏）",
                "填「宽 x 高」（像素），例如 3840x1080。\n" +
                "常用：3840x1080（32:9 双拼）、2560x1080（21:9）、5760x1080（48:9 三拼）\n" +
                $"当前画面 {ActiveOutputWidth} × {ActiveOutputHeight}，改完要重启才生效",
                $"{ActiveOutputWidth}x{ActiveOutputHeight}");

            if (string.IsNullOrWhiteSpace(input)) return;

            if (!TryParseResolution(input, out int w, out int h, out string why))
            {
                SetStatus("⚠ " + why, hold: true);
                return;
            }

            SetOutputResolution(w, h);
            SyncMenuState();
        }

        /// <summary>
        /// 解析「宽 x 高」并做**编码器能接受**的校验。
        ///
        /// 为什么卡偶数：H.264 / H.265 的 4:2:0 色度抽样要求宽高都是偶数，
        /// 奇数会让 ffmpeg 直接报错（现场才发现就来不及了，这里先挡）。
        /// </summary>
        private static bool TryParseResolution(string text, out int w, out int h, out string why)
        {
            w = h = 0;
            why = "";

            var t = (text ?? "").Trim().ToLowerInvariant()
                .Replace('×', 'x').Replace('*', 'x')
                .Replace(',', 'x').Replace('，', 'x')
                .Replace(" ", "");
            var parts = t.Split('x', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) { why = "格式不对，要写成 3840x1080"; return false; }
            if (!int.TryParse(parts[0], out w) || !int.TryParse(parts[1], out h))
            { why = "宽和高都必须是整数"; return false; }

            if (w < 64 || h < 64 || w > 8192 || h > 8192)
            { why = "宽高要在 64 – 8192 之间（再大合成缓冲会吃光内存）"; return false; }

            if ((w & 1) != 0 || (h & 1) != 0)
            { why = "宽高必须是偶数 —— H.264/H.265 的色度抽样要求，奇数会编码失败"; return false; }

            double ar = (double)w / h;
            if (ar > 12 || ar < 1.0 / 12)
            { why = $"长宽比 {ar:0.#}:1 太极端了，确认一下是不是多打了个 0"; return false; }

            return true;
        }

        /// <summary>
        /// 菜单里的「保存文件立即重启」（老板 2026-10-20：原来在输出设置窗口里，
        /// 要求整体搬到「视图 → 输出分辨率」子菜单；文案固定，横/竖屏同一个按钮）。
        ///
        /// 与旧按钮同一套语义：存盘 → 用同一工程拉起新实例 → 关掉自己。
        /// 菜单点完会先收起，所以用 Dispatcher.Background 让它收完再动作。
        /// </summary>
        private void MenuResRestart_Click(object sender, RoutedEventArgs e)
        {
            if (!GetPendingRes(out _, out _))
            {
                SetStatus("先在上面选一个分辨率，再点这里重启", hold: true);
                return;
            }
            DoSaveAndRestart();
        }

        /// <summary>
        /// 主窗浮动条上的「保存文件立即重启」（老板 2026-10-20 22:55：这个按钮要单独浮出来，
        /// 不要藏在两层菜单里 —— 选完立刻能点）。
        ///
        /// 与菜单项走同一个 `DoSaveAndRestart`，两处只有入口不同，动作完全一致。
        /// </summary>
        private void ResRestartBar_Click(object sender, RoutedEventArgs e) => DoSaveAndRestart();

        /// <summary>浮动条的「稍后」：只收起提示，不动待生效的选择（菜单里还有一份入口）。</summary>
        private void ResRestartLater_Click(object sender, RoutedEventArgs e)
        {
            _resBarDismissed = true;
            SyncMenuState();
            SetStatus("已收起提示条；选择已记下，随时可从「视图 → 输出分辨率 → 保存文件立即重启」再点");
        }

        /// <summary>
        /// 把「保存文件立即重启」浮动条摆到它原本所在的位置 —— 菜单附近
        /// （老板 2026-10-20 23:12：「为什么按钮要在最下面 应该在原菜单附近」）。
        ///
        /// 摆法：左缘对齐「视图」菜单项、上沿紧贴菜单栏（Row 1 顶端 = 菜单栏下沿）。
        /// 那个下拉菜单就是从这里展开的，选完菜单一收、提示条落在原地，
        /// 视线不用从屏幕顶部跳到底部。
        ///
        /// 两点注意：
        /// ① 平时 Collapsed 的元素不参与布局，ActualWidth 不可信 → 先 Measure 一次拿 DesiredSize；
        /// ② 窗口缩到下限（1093 DIP）时可能顶到右边界 → 夹一下，别让按钮被切掉。
        /// </summary>
        private void PositionResBar()
        {
            if (ResRestartBar == null || RootGrid == null || MiViewMenu == null) return;
            try
            {
                double availW = Math.Max(RootGrid.ActualWidth, 320);
                double availH = Math.Max(RootGrid.ActualHeight, 200);
                ResRestartBar.Measure(new Size(availW, availH));
                double w = ResRestartBar.DesiredSize.Width;

                Point p = MiViewMenu.TranslatePoint(new Point(0, 0), RootGrid);
                double x = double.IsNaN(p.X) ? 8 : p.X;

                double limit = availW - w - 8;    // 右边界留 8 DIP
                if (limit > 8 && x > limit) x = limit;
                if (x < 8) x = 8;

                ResRestartBar.Margin = new Thickness(x, 6, 0, 0);
            }
            catch { }
        }

        /// <summary>
        /// 「保存文件立即重启」的实际动作：存盘 → 用同一工程拉起新实例 → 关掉自己。
        /// 菜单项和浮动条共用这一份，避免两处行为跑偏。
        /// </summary>
        private void DoSaveAndRestart()
        {
            try
            {
                // IsStarting 也要拦：NDI 首启要 12~60 秒，这段"还没算跑起来"的时间重启，
                // 会在旧进程里留下一个建到一半的发送器，徒增不确定。
                if (StreamService.Instance.IsRunning || NdiOutputService.Instance.IsRunning
                    || NdiOutputService.Instance.IsStarting)
                {
                    SetStatus("⚠ 推流 / 录制 / NDI 还在跑，请先停止再重启", hold: true);
                    return;
                }

                SetStatus("正在保存并重启 Cutlance…", hold: true);

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (RestartWithResolution()) return;   // 成功时本进程已被终止，走不到下一行
                    SetStatus("未重启：工程没有保存（已取消），或新实例启动失败", hold: true);
                    SyncMenuState();
                }), DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                try { ErrorLog.Info("[分辨率] 重启失败：" + ex); } catch { }
                SetStatus("⚠ 重启失败：" + ex.Message, hold: true);
            }
        }

        /// <summary>
        /// 请求把输出分辨率改成 w×h（**重启程序后生效**）。
        ///
        /// 「视图」菜单和「输出设置」窗口里那两个横屏/竖屏选项都走这里 ——
        /// 两处各写一套的话，迟早出现"窗口里点了、菜单勾没跟上"。
        /// </summary>
        public bool SetOutputResolution(int w, int h)
        {
            // 已经是这个尺寸（且没有别的待生效项）→ 不做无意义的写盘
            // （已经选过同一个待生效值也走这条，返回 true：界面不必报错）
            if (w == ActiveOutputWidth && h == ActiveOutputHeight)
            {
                if (_pendingResW == 0) return true;
            }

            if (StreamService.Instance.IsRunning)
            {
                SetStatus("⚠ 输出进行中不能改分辨率，请先停止输出", hold: true);
                return false;
            }

            if (w == ActiveOutputWidth && h == ActiveOutputHeight)
            {
                // 把待生效项撤掉（用户又选回当前尺寸）
                _pendingResW = _pendingResH = 0;
                SetStatus("已取消待生效的尺寸变更，当前画面不变");
                return true;
            }

            _pendingResW = w;
            _pendingResH = h;
            _resBarDismissed = false;   // 每次新选择都重新浮出提示条（点过「稍后」也不影响下一次）

            if (string.IsNullOrEmpty(_projectPath))
            {
                // 没保存过工程 → 重启后无处可读，必须说清楚
                SetStatus($"已设为 {w} × {h}，重启后生效；但工程还没保存过 —— 先 Ctrl+S 存工程，否则重启会回到 {ActiveOutputWidth} × {ActiveOutputHeight}", hold: true);
            }
            else
            {
                SaveProject(false);   // 有路径时不弹框，直接写盘
                SetStatus($"已记下 {w} × {h} 并存进工程 —— 重启 Cutlance 后生效（当前画面仍是 {ActiveOutputWidth} × {ActiveOutputHeight}）", hold: true);
            }
            return true;
        }

        /// <summary>
        /// 「保存文件立即重启」的落地：存盘 → 用同一个工程拉起新实例 → 关掉自己。
        ///
        /// 返回 false = 没重启（用户取消了另存为、或新实例启动失败），调用方要把按钮恢复。
        /// 返回 true 时本进程已经被 ShutdownAll 终止，调用方不该再碰任何 UI。
        ///
        /// 为什么必须存盘：新实例是靠工程里的 `Output.Width/Height` 决定合成器尺寸的
        /// （见 App.OnStartup 的 PresetOutputSizeFromProject）—— 不存就等于白重启。
        /// </summary>
        public bool RestartWithResolution()
        {
            try
            {
                if (StreamService.Instance.IsRunning || NdiOutputService.Instance.IsRunning
                    || NdiOutputService.Instance.IsStarting)
                {
                    SetStatus("⚠ 输出还在跑（推流 / 录制 / NDI），请先停止再重启", hold: true);
                    return false;
                }

                // ① 存盘。没保存过工程时会弹「另存为」—— 重启必须有工程可读。
                SaveProject(false);
                if (string.IsNullOrEmpty(_projectPath)) return false;   // 用户取消了

                // ② 拉起新实例：同一工程 + 原有 --参数（--offscreen / --noactivate 等要透传，
                //    否则自动测试里点一下重启，新进程会跑到屏幕上来）
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return false;

                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = AppContext.BaseDirectory,
                    UseShellExecute = false
                };
                psi.ArgumentList.Add(_projectPath!);
                foreach (var a in Environment.GetCommandLineArgs().Skip(1))
                {
                    if (string.IsNullOrWhiteSpace(a)) continue;
                    // 工程路径已经加过了，别重复（重复的结果是新进程可能开错工程）
                    if (a.EndsWith(".cutlance", StringComparison.OrdinalIgnoreCase)) continue;
                    if (a.EndsWith(".vmlite", StringComparison.OrdinalIgnoreCase)) continue;
                    psi.ArgumentList.Add(a);
                }

                // ⚠ 截图/自检类环境变量（CUTLANCE_SHOT_*）绝不能带给新实例：
                //   它们会让新进程再走一遍「开输出设置窗 → 点竖屏 → 点重启」，
                //   变成无限重启（2026-10-20 写自检时就差点踩进去）。
                foreach (var k in psi.Environment.Keys.ToList())
                    if (k.StartsWith("CUTLANCE_SHOT_", StringComparison.OrdinalIgnoreCase))
                        psi.Environment.Remove(k);

                var child = Process.Start(psi);
                try { ErrorLog.Info($"[重启] 新实例 pid={child?.Id}，工程={_projectPath}，尺寸={_pendingResW}×{_pendingResH}"); } catch { }

                // ③ 关自己：走既有收尾（杀 ffmpeg / 停合成器 / 硬终止），
                //    放到 Dispatcher.Background 是为了先让日志写出去、状态栏刷一下。
                Dispatcher.BeginInvoke(new Action(ShutdownAll), DispatcherPriority.Background);
                return true;
            }
            catch (Exception ex)
            {
                try { ErrorLog.Info("[重启] 失败：" + ex); } catch { }
                SetStatus("⚠ 重启失败：" + ex.Message, hold: true);
                return false;
            }
        }

        private void MenuFtb_Click(object sender, RoutedEventArgs e)
        {
            BtnFtb_Click(sender, e);   // 复用 FTB 按钮逻辑（整屏淡黑）
            SyncMenuState();
        }

        private void MenuFullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

        /// <summary>
        /// P4：打开「监视窗 + 切换条」（内存合成切换台）。
        ///
        /// 它是**独立窗口**，不是把主界面换掉 —— 老板每天导播的流程一点不受影响，
        /// 可以一边用老界面、一边开切换台对照。等切换台把图层/叠加/字幕都接过来，
        /// 再决定要不要让主界面切过去。
        /// 单例：重开时把已有的窗口提到前面，不重复建（每建一个就多一套解码器，内存吃不住）。
        /// </summary>
        private void MenuSynthWindow_Click(object sender, RoutedEventArgs e) => OpenSynthWindow();

        /// <summary>打开切换台窗口（菜单点、或启动参数 --synth 都走这里）。</summary>
        internal void OpenSynthWindow()
        {
            try
            {
                if (_synthWindow != null && _synthWindow.IsLoaded)
                {
                    if (_synthWindow.WindowState == WindowState.Minimized)
                        _synthWindow.WindowState = WindowState.Normal;
                    _synthWindow.Activate();
                    return;
                }

                // 帧率：优先用工程里记住的切换台帧率（老板 2026-10-21：改过就要记住），
                // 没记过（0）才跟随输出设置（PAL 25 / 网络 30 / 高帧率 50-60）。
                // 切换台以前写死 30，工程设 25 时会出现「按 30 出帧、按 25 写文件」的错位。
                double outFps = StreamService.Instance.Fps;
                double synthFps = StreamService.Instance.SynthFps > 0
                    ? StreamService.Instance.SynthFps
                    : (outFps > 0 ? outFps : 30);
                _synthWindow = new Views.SynthWindow(Inputs, synthFps, Overlays, Layers) { Owner = this };
                _synthWindow.Closed += (s, a) => _synthWindow = null;

                // 测试/自动化时主窗在屏幕外，子窗不能再按 Owner 居中（会跑到屏幕外看不见）
                if (Left < -1000 || Top < -1000)
                {
                    _synthWindow.WindowStartupLocation = WindowStartupLocation.Manual;
                    _synthWindow.Left = Left + 20;
                    _synthWindow.Top = Top + 20;
                }

                _synthWindow.Show();
            }
            catch (Exception ex)
            {
                SetStatus("切换台打开失败：" + ex.Message, hold: true);
                ErrorLog.Info("[切换台] 打开失败：" + ex);
            }
        }

        private Views.SynthWindow? _synthWindow;

        private void ToggleFullscreen()
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        // —— 流 ——
        private void MenuOutputSettings_Click(object sender, RoutedEventArgs e) => OpenOutputSettings();
        private void MenuStartOutput_Click(object sender, RoutedEventArgs e) { StartStreaming(); SyncMenuState(); }
        private void MenuStopOutput_Click(object sender, RoutedEventArgs e) { StreamService.Instance.Stop(); SyncMenuState(); }

        /// <summary>菜单里直接启停 HDMI 全屏输出（默认取第一块非主屏，与「输出设置」默认一致）。</summary>
        private void MenuHdmi_Click(object sender, RoutedEventArgs e)
        {
            if (_hdmiWindow == null)
            {
                var monitors = DisplayService.GetMonitors();
                int idx = monitors.Count > 1 ? 1 : 0;
                if (!StartHdmiOutput(idx)) MiHdmi.IsChecked = false;
            }
            else
            {
                StopHdmiOutput();
            }
            SyncMenuState();
        }

        // —— 设置 ——
        private void MenuCameraSettings_Click(object sender, RoutedEventArgs e) => BtnAddCamera_Click(sender, e);

        // ================= 模块E：采集卡 / 摄像机信号 =================
        //
        // 流程（老板 2026-10-14 定）：
        //   第①步 先展示「输入信号的格式」→ 用户看完点「关闭」→ 再进入下一步（配置参数并添加）。
        //   所以这里先只负责把面板打开；添加逻辑等下一步确认后再接。
        //
        // 为什么单独做这个面板（而不是复用「+摄像头」）：
        //   原「+摄像头」用 WMI 的 PNPClass='Camera' OR 'Image' 枚举设备，实测本机 10 个
        //   DirectShow 视频设备只能列出 1 个，采集卡基本列不出来；且分辨率/帧率/格式是写死的
        //   预设表，采集卡真实能力（1080p60 / 4K 等）无法如实呈现。本面板改走 ffmpeg 真实枚举。

        private void BtnAddCapture_Click(object sender, RoutedEventArgs e) => OpenCaptureSignal();

        private void MenuAddCapture_Click(object sender, RoutedEventArgs e) => OpenCaptureSignal();

        private void MenuCaptureSignal_Click(object sender, RoutedEventArgs e) => OpenCaptureSignal();

        /// <summary>
        /// 打开「输入信号」面板（模块E）：① 展示真实信号格式 → ② 定采集设置（音频 / 链路）
        /// → 点「添加为输入」直接加入左侧输入列表。
        /// </summary>
        private void OpenCaptureSignal()
        {
            var w = new CaptureSignalWindow { Owner = this };
            // 多卡管理：把已占用的设备名传给面板 —— 面板据此标注「已在输入列表中」，
            // 并阻止重复添加（采集设备独占，加两遍只会得到一个打不开的输入）。
            w.UsedDeviceNames = Inputs.Where(i => i.Type == SourceType.Camera)
                                      .Select(i => i.Location)
                                      .ToList();
            // 与其它对话框一致：打开前摘掉全局鼠标钩子、收起预览浮层，关闭后按激活状态贴回
            bool? ok = null;
            RunWithMouseHookDetached(() => ok = w.ShowDialog());
            if (ok != true) return;
            if (w.SelectedDevices.Count == 0) return;

            int added = 0;
            var willAdd = w.SelectedDevices
                           .Where(d => !Inputs.Any(i => i.Type == SourceType.Camera && i.Location == d.Name))
                           .ToList();
            foreach (var dev in willAdd)
                if (AddCaptureInput(dev, w)) added++;

            if (added == 0) return;

            // 多选批量添加时，只有「面板右侧正在展示的那一路」被实测过信号 —— 其余设备
            // 隔行/逐行未知。⚠ 但只有「自动」档才吃这个结果：档位是「强制开启/关闭」时
            // 用户已经明确表态，不因未实测而有任何不确定性，不该去打扰他。
            int unprobed = willAdd.Count(d => w.SelectedDeinterlace == DeinterlaceMode.Auto
                                              && w.GetInterlaced(d.Name) == null);
            if (unprobed > 0)
                SetStatus($"已添加 {added} 路采集输入（输入列表 {Inputs.Count}）　—　"
                          + $"其中 {unprobed} 路未实测信号，去隔行「自动」档暂按「不启用」处理；"
                          + "可在面板里单独选中该设备点「重新检测」后再添加", hold: true);
            else
                SetStatus($"已添加 {added} 路采集输入（输入列表 {Inputs.Count}）　—　单击左侧输入项设为 PREVIEW",
                          hold: true);
        }

        /// <summary>把一路采集设备加为输入。返回是否真的加进去了。</summary>
        private bool AddCaptureInput(CaptureDevice dev, CaptureSignalWindow panel)
        {
            if (Inputs.Any(i => i.Type == SourceType.Camera && i.Location == dev.Name))
            {
                SetStatus($"⚠ 跳过 {dev.Name}：已在输入列表中（采集设备独占，不能重复添加）", hold: true);
                return false;
            }

            var fmt = panel.SelectedFormat;
            var audio = panel.SelectedAudio;
            var link = panel.SelectedLink;
            // null = 这一路没实测过信号（多选批量添加时常见）→ 去隔行「自动」档按「无」处理
            var interlaced = panel.GetInterlaced(dev.Name);

            var src = new InputSource
            {
                Type = SourceType.Camera,
                Location = dev.Name,
                // 「自动」（选项首行，默认）：一律不强制分辨率/帧率，交给设备按当前进来的信号协商。
                // 实测教训：能力表最高档 2560×1600@30，而当前信号是 1920×1080@60 —— 照能力表
                // 挑最高档会拿一个没被喂进来的格式去要求设备，大概率采不到画面。
                CamWidth = fmt != null && !fmt.IsAuto ? fmt.Width : 0,
                CamHeight = fmt != null && !fmt.IsAuto ? fmt.Height : 0,
                CamFps = fmt != null && !fmt.IsAuto ? (int)Math.Round(fmt.FpsMax) : 0,
                // 实测：采集卡（GATHER 四路，全 yuyv422 无压缩）不指定编码格式最稳 ——
                // 指定 -vcodec/-pixel_format 反而可能匹配不到 pin。交给设备自动协商。
                CamChroma = "",
                CamAudio = audio != null && !audio.NoAudio,
                CamAudioDevice = audio?.DeviceName ?? "",
                CamLink = link,
                // 模块E B-2：去隔行 / 低延迟
                CamInterlaced = interlaced,   // 三态直接透传：null = 没测/判不出来，别压成 false
                CamDeinterlace = panel.SelectedDeinterlace,
                CamLowLatency = panel.SelectedLowLatency,
                // 模块C A-2：抠像换背景
                ChromaEnabled = panel.SelectedChromaEnabled,
                ChromaKeyColor = panel.SelectedChromaKeyColor,
                ChromaSimilarity = panel.SelectedChromaSimilarity,
                ChromaBlend = panel.SelectedChromaBlend,
                BgType = panel.SelectedBgType,
                BgColor = panel.SelectedBgColor,
                BgPath = panel.SelectedBgPath,
                BgWidth = panel.SelectedBgWidth,
                BgHeight = panel.SelectedBgHeight
            };

            // 抠像做在 ffmpeg 预处理层，VLC 直连链路下根本执行不到 —— 兜底强制切链路。
            // 面板在勾选「启用抠像」时已经切过一次，但用户可以再手动改回来；
            // 与其让用户配完看不见效果（且毫无线索），不如在这里直接纠正并说明。
            if (src.NeedChromaKey && src.CamLink == CameraLink.VlcDirect)
            {
                src.CamLink = CameraLink.Ffmpeg;
                link = CameraLink.Ffmpeg;
                SetStatus("抠像需要 ffmpeg 预处理链路，已把该输入的采集链路改为 ffmpeg 预处理", hold: true);
            }

            var fmtText = fmt == null ? ""
                        : fmt.IsAuto ? " · 自动格式"
                        : $" · {fmt.ResolutionText.Replace(" ", "")}@{src.CamFps}";
            var tags = new List<string> { link == CameraLink.VlcDirect ? "VLC 直连" : "ffmpeg 预处理" };
            if (src.NeedDeinterlace) tags.Add("去隔行");
            if (src.CamLowLatency) tags.Add("低延迟");
            if (src.NeedChromaKey) tags.Add("抠像");
            src.Name = $"{dev.KindText}：{dev.Name}{fmtText} · {string.Join(" · ", tags)}";

            AddInput(src);

            // 名字里写着「ffmpeg 预处理」不等于它跑起来了：起不来会静默回落 VLC 直连。
            // 实测踩过：错误原因被「已添加」提示顶掉，界面上看着像成功，实际走的是另一条链路。
            var via = link == CameraLink.VlcDirect
                ? "VLC 直连"
                : (InputPreprocessService.Instance.IsRunning(src)
                    ? "ffmpeg 预处理已生效"
                    : "⚠ 预处理未启动，已回落 VLC 直连"
                      + (string.IsNullOrWhiteSpace(InputPreprocessService.Instance.LastError)
                          ? "" : "（" + InputPreprocessService.Instance.LastError + "）"));

            var audioText = !src.CamAudio
                ? "静音"
                : (string.IsNullOrWhiteSpace(src.CamAudioDevice) ? "系统默认音频" : src.CamAudioDevice);
            var extraText = "";
            if (src.NeedDeinterlace) extraText += " · 去隔行已启用（yadif）";
            else if (src.CamDeinterlace == DeinterlaceMode.Off && interlaced == true)
                extraText += " · ⚠ 隔行信号 + 去隔行关闭，画面会拉丝";
            else if (interlaced == null) extraText += " · 未实测信号，去隔行「自动」档不启用";
            if (src.CamLowLatency) extraText += " · 低延迟档";
            if (src.NeedChromaKey) extraText += " · 抠像换背景已启用";
            else if (src.ChromaEnabled) extraText += " · ⚠ 抠像未生效（背景素材缺失）";

            SetStatus($"已添加 {dev.KindText}输入 #{Inputs.Count}：{dev.Name}"
                    + $"（{via} · 音频：{audioText}{extraText}）", hold: true);
            return true;
        }

        // ==================== 模块D：网络流输入（RTSP / SRT / RTMP / HTTP / RIST / UDP） ====================
        //
        // 为什么单独做面板、而不是弹一个「请输入地址」的输入框：
        //   采集卡插错了能在设备列表里看见（设备根本没出现），但网络流地址错了**什么都看不见** ——
        //   就是黑屏。黑屏可能是地址写错、源没开、端口不通、被防火墙挡、格式不对、被别处占了，
        //   六七种原因长得一模一样。所以面板的核心价值是「把黑屏拆成一句人话」：
        //   粘进来立刻回显识别结果、能真连一次读回分辨率/编码、认不出的协议直接说明原因。

        private void BtnAddNetwork_Click(object sender, RoutedEventArgs e) => OpenNetworkStream();

        private void MenuAddNetwork_Click(object sender, RoutedEventArgs e) => OpenNetworkStream();

        /// <summary>
        /// 打开「网络流输入」面板：粘地址 → 看识别结果 → 测试连接 → 添加为输入。
        /// 支持一次粘多条（每行一条），批量建输入。
        /// </summary>
        private void OpenNetworkStream()
        {
            var w = new NetworkStreamWindow { Owner = this };
            // 把已在输入列表里的地址传进去 —— 面板据此标注并拦截重复添加
            // （同一条流加两遍 = 对源多占一个连接，很多摄像机只有 1~2 路上限）
            w.UsedUrls = Inputs.Where(i => i.Type == SourceType.Network)
                               .Select(i => i.Location)
                               .ToList();

            bool? ok = null;
            RunWithMouseHookDetached(() => ok = w.ShowDialog());
            if (ok != true) return;
            if (w.Urls.Count == 0) return;

            int added = 0;
            foreach (var url in w.Urls)
                if (AddNetworkInput(url, w)) added++;

            if (added == 0) return;

            var linkText = w.SelectedLink == CameraLink.VlcDirect ? "VLC 直连" : "ffmpeg 预处理";
            SetStatus($"已添加 {added} 条网络流输入（输入列表 {Inputs.Count}）· {linkText}"
                      + "　—　点左侧输入项设为 PREVIEW 即开始取流（首次取流约 1~2 秒）", hold: true);
        }

        /// <summary>把一条网络流地址加为输入。返回是否真的加进去了。</summary>
        private bool AddNetworkInput(string url, NetworkStreamWindow panel)
        {
            if (Inputs.Any(i => i.Type == SourceType.Network &&
                                string.Equals(i.Location, url, StringComparison.OrdinalIgnoreCase)))
            {
                SetStatus($"⚠ 跳过：该地址已在输入列表中（同一路流重复添加会对源多占一个连接）", hold: true);
                return false;
            }

            // null = 这条地址没实测过（只测了第一条时其余都是 null）→ 去隔行「自动」档按「不启用」处理
            var interlaced = panel.GetInterlaced(url);

            var src = new InputSource
            {
                Type = SourceType.Network,
                Location = url,
                // 网络流参数
                NetProtocol = panel.SelectedProtocol,
                NetRtspTransport = panel.SelectedRtspTransport,
                NetSrtLatency = panel.SrtLatency,
                NetSrtMode = panel.SelectedSrtMode,
                NetSrtPassphrase = panel.SrtPassphrase,
                NetTimeoutMs = panel.TimeoutMs,
                // 接入方式：链路 / 去隔行 / 低延迟（这三个字段与采集输入共用，语义完全一致）
                CamLink = panel.SelectedLink,
                CamInterlaced = interlaced,   // 三态直接透传：null = 没测/判不出来，别压成 false
                CamDeinterlace = panel.SelectedDeinterlace,
                CamLowLatency = panel.SelectedLowLatency
            };

            var linkText = panel.SelectedLink == CameraLink.VlcDirect ? "VLC 直连" : "ffmpeg 预处理";
            src.Name = NetworkStreamService.BuildName(src, linkText);

            AddInput(src);

            // ⚠ 这时**还没取过流**，所以不能拿 IsRunning 去判断「预处理是否生效」——
            //   那必然是 false，会显示成「预处理未启动」（= 失败），而实际上只是还没开始。
            //   这与「添加完成」是两码事，如实说成后者。
            var protoText = NetworkStreamService.ProtocolText(src.EffectiveProtocol);
            var extra = "";
            if (src.NeedDeinterlace) extra += " · 去隔行已启用（yadif）";
            else if (interlaced == null && src.CamDeinterlace == DeinterlaceMode.Auto)
                extra += " · 未实测信号，去隔行「自动」档暂不启用";
            if (src.CamLowLatency) extra += " · 低延迟档";

            SetStatus($"已添加 {protoText} 输入 #{Inputs.Count}：{src.Name}"
                      + $"（{linkText}{extra}）", hold: true);
            return true;
        }

        // ==================== 模块F：NDI 输入 ====================
        //
        // 链路（沿用「预处理」那一套，只是入口不同）：
        //   NDI 源 → .NET 接收器收帧(BGRA) → 写进 ffmpeg 的 stdin → 编码成组播 TS → VLC 播。
        //
        // 为什么不让 ffmpeg / VLC 直接吃 NDI：**两边都没有 NDI 能力** ——
        //   ffmpeg 官方因许可冲突把 NDI 从源码里移除了（要自己打补丁重编译，且那份二进制不能分发），
        //   VLC 3.0 也没有 NDI 插件。所以只能自己用 NDI SDK 收帧，再喂进现有链路。
        //
        // 收帧走「私有链路」而不是 VLC 直连：NDI 帧不是 URL，VLC 无从播起；
        //   而且过一遍预处理层后，**状态灯 / 断流重连 / 几何 / 抠像 / 低延迟全部自动适用**。

        private void BtnAddNdi_Click(object sender, RoutedEventArgs e) => OpenNdiInput();

        private void MenuAddNdi_Click(object sender, RoutedEventArgs e) => OpenNdiInput();

        /// <summary>打开 NDI 面板：发现源 → 选中 → 添加为输入。</summary>
        private void OpenNdiInput()
        {
            var w = new NdiWindow { Owner = this };

            bool? ok = null;
            RunWithMouseHookDetached(() => ok = w.ShowDialog());
            if (ok != true) return;

            var name = w.SelectedSourceName;
            if (string.IsNullOrWhiteSpace(name)) return;

            if (Inputs.Any(i => i.Type == SourceType.Ndi &&
                                string.Equals(i.Location, name, StringComparison.OrdinalIgnoreCase)))
            {
                SetStatus("⚠ 跳过：该 NDI 源已在输入列表中（同一路源重复接收会白占带宽）", hold: true);
                return;
            }

            AddNdiInput(name);
        }

        /// <summary>把一个 NDI 源加为输入。</summary>
        private void AddNdiInput(string sourceName)
        {
            var src = new InputSource
            {
                Type = SourceType.Ndi,
                Location = sourceName,                 // Location 存的就是完整 NDI 源名
                // NDI 只能走 ffmpeg 预处理链路（VLC 播不了 NDI），这里显式写死不留歧义
                CamLink = CameraLink.Ffmpeg,
                // NDI 本来就是局域网低延迟传输，默认给低延迟档；
                // 若发现源端抖动大（画面偶发花顿），可在输入上把它关掉换稳定。
                CamLowLatency = true
            };
            src.Name = "NDI：" + sourceName;

            AddInput(src);

            SetStatus($"已添加 NDI 输入 #{Inputs.Count}：{sourceName}"
                      + "　—　点左侧输入项设为 PREVIEW 开始接收"
                      + "（链路：NDI 收帧 → ffmpeg 编码 → 组播 → VLC，首次约 1~2 秒出画）", hold: true);
        }

        // ==================== 模块E B-3：热插拔 / 断流重连 / 多卡管理 ====================

        /// <summary>
        /// 挂上系统的 WM_DEVICECHANGE 监听（必须等窗口句柄就绪，所以放在 Loaded 里）。
        /// 顺带建立设备初始快照 —— 否则第一次插拔会把现有设备全报成"新增"。
        /// </summary>
        private void InstallDeviceChangeHook()
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                System.Windows.Interop.HwndSource.FromHwnd(hwnd)?.AddHook(WndProcHook);

                _ = CaptureDeviceService.EnumerateDevicesAsync().ContinueWith(t =>
                {
                    if (t.Status == TaskStatus.RanToCompletion && t.Result != null)
                        DeviceWatcher.Seed(t.Result.Devices.Where(d => d.IsVideo).Select(d => d.Name));
                });
            }
            catch { }
        }

        private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == DeviceWatcher.WM_DEVICECHANGE)
            {
                try { DeviceWatcher.OnDeviceChange(wParam.ToInt32()); } catch { }
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// 设备插拔后的处理：报变化，并把「之前断了、现在设备又回来了」的采集输入自动拉起来。
        /// 拔掉的那一侧不用在这里管 —— 设备没了 ffmpeg 自然会退，
        /// 由 InputPreprocessService 的自动重连按退避节奏重试。
        /// </summary>
        private void OnCaptureDevicesChanged(DeviceChangeInfo info)
        {
            var parts = new List<string>();
            if (info.Added.Count > 0) parts.Add("新增 " + string.Join("、", info.Added));
            if (info.Removed.Count > 0) parts.Add("移除 " + string.Join("、", info.Removed));
            if (parts.Count == 0) return;

            SetStatus("🔌 检测到采集设备变化：" + string.Join("；", parts), hold: true);

            foreach (var src in Inputs.Where(i => i.Type == SourceType.Camera).ToList())
            {
                // 设备不在（刚被拔掉）→ 什么都不做，等自动重连接管
                if (!info.Devices.Any(d => d.IsVideo && d.Name == src.Location)) continue;

                if (src.CamLink == CameraLink.VlcDirect) continue;          // 直连链路没有常驻进程
                if (InputPreprocessService.Instance.IsRunning(src)) continue;
                InputPreprocessService.Instance.Reconnect(src, out _);
            }
        }

        // ==================== 系统监控（CPU / 内存 / 显存）====================
        //
        // 老板 2026-10-17：「在下面加一个系统监控提醒：CPU 内存 显存 报警」。
        // 位置选在主窗状态栏右下角（那是最不挡事、又一直看得见的地方）。
        //
        // 报警口径（可改，改这几个常量即可）：
        //   · 阈值：CPU 85% 警告 / 95% 严重；内存 88% / 95%；显存 88% / 96%。
        //   · 防抖：**连续 3 轮（约 6 秒）越线才算数** —— 编码线程瞬时打满、
        //     临时分配大缓冲都会让读数尖一下，不加防抖会天天误报，很快就没人看了。
        //   · 表现：对应数字变琥珀/红并加粗 → 状态栏那个小圆点开始闪 → 右侧出现告警文字。
        //     **刻意不用弹窗**：导播过程中任何模态框都是在挡播出画面，属于事故级设计。
        //   · 阈值目前是代码常量、不落工程文件；要改成界面可调的话，
        //     挂进「输出设置」或做成独立小窗都行（等老板定）。

        /// <summary>告警阈值（百分比）：达到 Warn 变琥珀，达到 Crit 变红。</summary>
        private const double CpuWarnPct = 85, CpuCritPct = 95;
        private const double MemWarnPct = 88, MemCritPct = 95;
        private const double GpuWarnPct = 88, GpuCritPct = 96;

        /// <summary>连续越线多少轮（每轮 2 秒）才升级为报警。</summary>
        private const int SysHotRounds = 3;

        private DispatcherTimer? _sysTimer;
        private int _cpuHot, _memHot, _gpuHot;
        private bool _sysBlinkOn;

        /// <summary>
        /// 刷新状态栏的系统监控。整段包在 try/catch 里：这是每 2 秒跑一次、
        /// 且发生在直播过程中的代码，任何异常都绝不能冒泡出去干扰输出。
        /// </summary>
        private void RefreshSystemMonitor()
        {
            if (CpuText == null) return;    // 设计器 / 尚未初始化
            try
            {
                var s = SystemMonitor.Sample();

                // ---- 长期判定（10 分钟窗口）：经常性越线才升级成「建议升级电脑」----
                // 放在最前面：它决定下面状态栏那句「建议」说什么。
                var verdict = PerfAdvice.Update(s);
                if (!PerfAdvice.Suppressed && PerfAdvice.ShouldPrompt(verdict))
                {
                    PerfAdvice.LogVerdict(verdict);
                    ShowPerfBanner(verdict);
                }

                int cpuLv = StepLevel(ref _cpuHot, s.CpuPercent, CpuWarnPct, CpuCritPct);
                int memLv = StepLevel(ref _memHot, s.MemPercent, MemWarnPct, MemCritPct);
                int gpuLv = s.GpuAvailable ? StepLevel(ref _gpuHot, s.GpuPercent, GpuWarnPct, GpuCritPct) : 0;

                // 首轮还没有 CPU 基线（GetSystemTimes 要两次求差），显示 -- 而不是假的 0%
                ApplySysLevel(CpuText, s.CpuValid ? $"CPU {s.CpuPercent:F0}%" : "CPU --", cpuLv);
                ApplySysLevel(MemText, $"内存 {s.MemPercent:F0}%", memLv);
                ApplySysLevel(GpuText, s.GpuAvailable ? $"显存 {s.GpuPercent:F0}%" : "显存 --", gpuLv);

                // ---- 状态栏灯 + 告警文字 ----
                int worst = Math.Max(cpuLv, Math.Max(memLv, gpuLv));
                if (worst == 0)
                {
                    SysAlertText.Visibility = Visibility.Collapsed;
                    SysDot.Fill = (Brush)FindResource("Brush.Ok");
                    SysDot.Opacity = 1.0;
                    _sysBlinkOn = false;
                }
                else
                {
                    bool crit = worst >= 2;
                    var parts = new List<string>();
                    if (cpuLv > 0) parts.Add($"CPU {s.CpuPercent:F0}%");
                    if (memLv > 0) parts.Add($"内存 {s.MemPercent:F0}%");
                    if (gpuLv > 0) parts.Add($"显存 {s.GpuPercent:F0}%");

                    if (verdict.Stressed)
                    {
                        // 长期不足：别再劝人调参数了，直接给硬件结论。
                        SysAlertText.Text = "⚠ " + PerfAdvice.Headline(verdict);
                    }
                    else
                    {
                        // 告警文案按「最要命的那个」给建议，而不是罗列三个数字了事
                        string advice =
                            gpuLv > 0 ? "显存吃紧会导致硬解失败、输出变黑，建议减少图层或关掉硬解"
                            : memLv > 0 ? "内存吃紧会开始换页，画面会卡顿，建议关掉用不上的输入"
                            : "CPU 吃紧会掉帧，建议切到显卡编码或降低预处理路数";

                        SysAlertText.Text = (crit ? "⚠ 资源紧张：" : "⚠ 占用偏高：")
                                            + string.Join(" · ", parts) + " —— " + advice;
                    }
                    SysAlertText.Visibility = Visibility.Visible;

                    // 闪：每轮取反。静态变色在导播台上很容易被忽略，跳动才抓得住眼睛。
                    _sysBlinkOn = !_sysBlinkOn;
                    SysDot.Fill = (Brush)FindResource(crit ? "Brush.Pgm" : "Brush.Warn");
                    SysDot.Opacity = _sysBlinkOn ? 1.0 : 0.25;
                }

                // ---- 明细放 ToolTip：状态栏太窄，塞不下总量 / 显卡型号 ----
                SysTipText.Text =
                    "系统资源监控（每 2 秒刷新）\n" +
                    $"\nCPU   {s.CpuPercent:F1}%　警告 ≥{CpuWarnPct:F0}% · 严重 ≥{CpuCritPct:F0}%" +
                    $"\n　　  本程序 {s.OwnCpuPercent:F1}%（合成 + 界面，不含编码子进程）" +
                    $"\n内存  {s.MemUsedGb:F1} / {s.MemTotalGb:F1} GB（{s.MemPercent:F1}%）" +
                    $"　警告 ≥{MemWarnPct:F0}% · 严重 ≥{MemCritPct:F0}%" +
                    (s.GpuAvailable
                        ? $"\n显存  {s.GpuUsedMb / 1024.0:F2} / {s.GpuTotalMb / 1024.0:F2} GB（{s.GpuPercent:F1}%）" +
                          $"\n　　  {s.GpuName}　警告 ≥{GpuWarnPct:F0}% · 严重 ≥{GpuCritPct:F0}%"
                        : "\n显存  未检测到 NVIDIA 显卡（nvml.dll 不可用），该项暂不监控") +
                    $"\n\n口径：CPU 为全系统占用（两次 GetSystemTimes 求差）；" +
                    $"\n连续 {SysHotRounds} 轮（约 {SysHotRounds * 2} 秒）越线才报警，避免瞬时峰值误报。" +
                    $"\n\n长期判定：最近 {verdict.WindowMinutes} 分钟 {verdict.Samples} 次采样里，" +
                    $"\n　CPU 越线占 {verdict.CpuHotRate:F0}% · 本程序越线占 {verdict.OwnHotRate:F0}% · 内存越线占 {verdict.MemHotRate:F0}%" +
                    $"\n　（越线占比过半 = 经常性不足 → 建议升级电脑）";
            }
            catch
            {
                // 监控本身出问题就静静待着，绝不打扰直播
            }
        }

        // ============================== 性能不足横幅 ==============================
        //
        // 老板 2026-10-20：「使用 CPU 要注意一点，监控系统参数，经常性出现 CPU 报警，
        // 告诉用户『建议升级您的电脑』」。
        //
        // 为什么用横幅而不是弹窗：
        //   · 模态框会挡住播出画面（铁律：导播中不弹模态框）；
        //   · 顶层独立窗会盖到别的程序上，而这条提示常和老板手上在干的事无关；
        //   · 横幅贴在状态栏上方，看得见、不抢焦点、不出主窗，程序一关就走。

        private DispatcherTimer? _perfBannerTimer;
        private Verdict? _perfVerdict;

        /// <summary>
        /// 收起横幅。
        /// 为什么收起时必须清空文本：WPF 里 Collapsed 的元素**仍然留在 UIA 树里**，而离屏测试时
        /// 窗口本身就在屏外、IsOffscreen 恒为 true —— 光看可见性根本分不出「还在」和「收了」。
        /// 文本框清空后读不到内容，这个判定在离屏下也绝对可靠（顺带也省了内存里的长字符串）。
        /// </summary>
        private void CollapsePerfBanner()
        {
            try
            {
                _perfBannerTimer?.Stop();
                PerfBanner.Visibility = Visibility.Collapsed;
                PerfBannerTitle.Text = "";
                PerfBannerSub.Text = "";
            }
            catch { }
        }

        /// <summary>展开「这台电脑不够用」横幅，15 秒后自动收起（状态栏那句结论会留着）。</summary>
        private void ShowPerfBanner(Verdict v)
        {
            try
            {
                _perfVerdict = v;

                // 横幅空间够，就用完整的一句；状态栏用短版（Headline），免得挤掉推流指示。
                PerfBannerTitle.Text = v.Cause == "mem"
                    ? "这台电脑的内存不够用 —— 建议加内存条，或升级您的电脑"
                    : "这台电脑的运算能力跟不上当前的导播设置 —— 建议升级您的电脑";

                string cpu = string.IsNullOrWhiteSpace(v.CpuName)
                    ? $"{v.CpuCores} 线程"
                    : $"{v.CpuName}（{v.CpuCores} 线程）";
                PerfBannerSub.Text =
                    $"近 {v.WindowMinutes} 分钟：CPU 平均 {v.CpuAvg:F0}%，有一半以上时间在 {PerfAdvice.CpuHotPct:F0}% 以上；" +
                    $"本程序占用 {v.OwnAvg:F0}%（峰值 {v.OwnMax:F0}%）；" +
                    $"内存 {v.MemUsedGb:F1} / {v.MemTotalGb:F1} GB。\n" +
                    $"{cpu}　·　画面会开始掉帧、推流会掉码率，严重时直接断流。" +
                    $"这不是设置没调好，是这台机器的算力不够。";

                PerfBanner.Visibility = Visibility.Visible;

                // 15 秒自动收起：横幅压在主区底部，长期挂着会挡事。
                _perfBannerTimer?.Stop();
                _perfBannerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
                _perfBannerTimer.Tick += (_, _) => CollapsePerfBanner();
                _perfBannerTimer.Start();
            }
            catch { }
        }

        private void PerfBannerDetail_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var v = _perfVerdict ?? PerfAdvice.Latest;
                new HelpWindow("这台电脑跑得动吗 · Cutlance", PerfAdvice.BuildReport(v))
                { Owner = this }.ShowDialog();
            }
            catch (Exception ex)
            {
                SetStatus("⚠ 打不开性能报告：" + ex.Message, true);
            }
        }

        private void PerfBannerDismiss_Click(object sender, RoutedEventArgs e)
        {
            PerfAdvice.SuppressForSession();
            CollapsePerfBanner();
            SetStatus("已收起性能提示：本次运行不再弹；重启程序后如仍不够用会再提醒", true);
        }

        /// <summary>菜单入口：随时查这台电脑跑不跑得动（不用等到报警）。</summary>
        private void MenuPerfReport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                new HelpWindow("这台电脑跑得动吗 · Cutlance", PerfAdvice.BuildReport(PerfAdvice.Latest))
                { Owner = this }.ShowDialog();
            }
            catch (Exception ex)
            {
                SetStatus("⚠ 打不开性能报告：" + ex.Message, true);
            }
        }

        /// <summary>
        /// 越线计步：返回 0=正常 / 1=警告 / 2=严重。
        /// 连续 <see cref="SysHotRounds"/> 轮越线才给出非 0 —— 这就是防抖。
        /// </summary>
        private static int StepLevel(ref int hot, double value, double warn, double crit)
        {
            int raw = value >= crit ? 2 : value >= warn ? 1 : 0;
            if (raw == 0) { hot = 0; return 0; }
            if (hot < SysHotRounds) hot++;
            return hot >= SysHotRounds ? raw : 0;
        }

        /// <summary>按告警等级给一项监控数字上色（正常灰 / 警告琥珀 / 严重红）。</summary>
        private void ApplySysLevel(TextBlock tb, string text, int level)
        {
            tb.Text = text;
            if (level >= 2)
            {
                tb.Foreground = (Brush)FindResource("Brush.Pgm");
                tb.FontWeight = FontWeights.Bold;
            }
            else if (level == 1)
            {
                tb.Foreground = (Brush)FindResource("Brush.Warn");
                tb.FontWeight = FontWeights.Bold;
            }
            else
            {
                tb.Foreground = (Brush)FindResource("Brush.Text.Secondary");
                tb.FontWeight = FontWeights.Normal;
            }
        }

        // ==================== 模块E B-2：采集输入信号状态灯 ====================

        private DispatcherTimer? _camStatusTimer;

        /// <summary>
        /// 刷新每路采集输入的信号状态灯。
        ///
        /// 为什么轮询而不是纯事件驱动：链路死活有多种来源 —— ffmpeg 自己退出、看门狗在退避重连、
        /// 用户手动 Stop、设备热插拔、预处理起不来静默回落。逐一挂钩容易漏掉某条路径，
        /// 而 2 秒一次的轮询只读进程句柄状态，代价可忽略，拿到的却一定是「此刻的真相」。
        /// </summary>
        private void RefreshCaptureStatus()
        {
            // 覆盖所有「实时输入」：采集卡与网络流。文件/桌面没有信号这个概念，不参与。
            foreach (var src in Inputs.Where(i => i.IsLiveInput))
            {
                CaptureStatus st;
                if (src.CamLink == CameraLink.VlcDirect)
                {
                    // 直连链路没有常驻进程可查 —— 既不谎报「信号正常」，也不谎报「无信号」
                    st = CaptureStatus.Direct;
                }
                else
                {
                    st = InputPreprocessService.Instance.GetState(src) switch
                    {
                        InputPreprocessService.PreprocessState.Running => CaptureStatus.Live,
                        InputPreprocessService.PreprocessState.Reconnecting => CaptureStatus.Reconnecting,
                        InputPreprocessService.PreprocessState.Down => CaptureStatus.Down,
                        _ => CaptureStatus.Unknown          // 还没取过画面
                    };
                }
                src.CamStatus = st;
            }
        }

        // ================= 模块C：输入级裁切 / 旋转 / 镜像（已接通，走方案A） =================
        // 实现路径：输入预处理层（InputPreprocessService）。
        //   需要处理的输入 → 常驻 ffmpeg 做裁切/旋转/镜像 → 本地组播 MPEG-TS → VLC 播放。
        //
        // 为什么不用 VLC 滤镜：VLC 3.0 只在 LibVLC 实例级认 --video-filter，挂在单个 Media 上的
        // ":video-filter=…" 被完全忽略（invert / croppadd 双向实测确认），而实例级滤镜对该实例
        // 所有播放统一生效，做不到「不同输入各带各的裁切」。
        //
        // 代价：每个被处理的输入多一个常驻 ffmpeg、+400~500ms 延迟（实测 460ms）、
        //       文件类输入会变成直播流（不能拖进度条）。详见 InputPreprocessService 的类注释。

        private void MenuInputGeometry_Click(object sender, RoutedEventArgs e)
            => OpenInputGeometry(_previewSource);

        private void TileCrop_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is InputSource s)
                OpenInputGeometry(s);
        }

        /// <summary>
        /// 左侧输入项右键「改名」（老板 2026-10-20）。
        /// 改完要把四处显示同步过来：左侧列表、预览组标题、图层列表、播放列表
        /// —— 它们显示的都是「输入名」，不同步就会出现「左边改了、右边还是老名字」。
        /// </summary>
        private void TileRename_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as MenuItem)?.DataContext is not InputSource src) return;

            var dlg = new RenameWindow(src.Name,
                $"#{src.Number} {src.TypeShort}") { Owner = this };
            if (dlg.ShowDialog() != true) return;

            string name = dlg.ResultName;
            if (string.IsNullOrWhiteSpace(name) || name == src.Name) return;

            string old = src.Name;
            src.Rename(name);

            // ① 左侧输入列表（Name 是普通属性，靠整表刷新才显示新名字）
            try { InputTiles?.Items.Refresh(); } catch { }
            // ② 图层列表 / 播放列表（它们显示 Layer.DisplayName = Source.Name）
            try { LayerList?.Items.Refresh(); } catch { }
            try { PlayListBox?.Items.Refresh(); } catch { }
            // ③ 预览组标题
            if (_previewSlots.Any(s => ReferenceEquals(s.Source, src)))
            {
                PreviewNameText.Text = _previewSlots.Count == 1
                    ? _previewSlots[0].DisplayName
                    : $"{_previewSlots.Count} 路组合";
            }

            SetStatus($"✓ 输入 #{src.Number} 已改名：「{old}」→「{name}」", hold: true);
        }

        /// <summary>
        /// 打开某路输入的裁切 / 旋转窗口。同一个输入不重复开窗（已开着就激活它）。
        /// 窗口是非模态的：改一个数值就即时应用到画面，方便对着预览框微调。
        /// </summary>
        public void OpenInputGeometry(InputSource? src)
        {
            if (src == null)
            {
                SetStatus("⚠ 请先在左侧单击一个输入（设为 PREVIEW），再设置裁切 / 旋转");
                return;
            }

            foreach (Window w in Application.Current.Windows)
                if (w is InputGeometryWindow g && ReferenceEquals(g.Tag, src))
                { g.Activate(); return; }

            var win = new InputGeometryWindow(
                src,
                "源分辨率：以预览框中实际画面为准；裁切按源画面像素计算",
                ReplayInput)
            { Owner = this, Tag = src };
            win.Show();
            SetStatus($"已打开输入 #{src.Number}「{src.Name}」的裁切 / 旋转");
        }

        /// <summary>
        /// 右键菜单：把这一路输入从列表里删掉（只删列表项，不动磁盘上的原始文件）。
        ///
        /// ⚠ 必须「连根拔」——预览、引用它的输出图层、播放列表项、常驻预处理进程
        ///   都要一起收干净；否则会留下指向已不存在输入的悬空引用（下次存工程就写坏了）。
        /// </summary>
        private void TileDelete_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as MenuItem)?.DataContext is not InputSource src) return;

            var answer = MessageBox.Show(
                $"确定要从列表中删除输入 #{src.Number}「{src.Name}」吗？\n\n" +
                "· 输出里引用它的图层会一并移除\n" +
                "· 播放列表里引用它的项会一并移除\n" +
                "· 磁盘上的原始文件不会被删除\n" +
                "（此项无法撤销）",
                "Cutlance · 删除输入", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;

            // ① 引用它的输出图层
            foreach (var l in Layers.Where(x => x.Source == src).ToList()) RemoveLayer(l);

            // ② 播放列表里引用它的项（PlayListItem 直接持有输入对象引用，按引用比即可）
            foreach (var it in PlayItems.Where(x => x.Source == src).ToList()) PlayItems.Remove(it);
            RefreshPlayOrders();

            // ③ 正在预览 → 把引用它的那几路摘掉（多路预览里可能只有其中一路是它，
            //    剩下几路要继续播，不能一删输入就把整个预览组清空）
            var deadPreview = _previewSlots.Where(x => ReferenceEquals(x.Source, src)).ToList();
            if (deadPreview.Count > 0)
            {
                foreach (var s in deadPreview) RemovePreviewSlot(s);
                if (_previewSlots.Count == 0)
                {
                    try { _engine.PreviewPlayer.Stop(); } catch { }
                    _previewSource = null;
                    PreviewNameText.Text = "";
                }
                else
                {
                    _previewSource = _previewSlots[0].Source;
                    PreviewNameText.Text = _previewSlots.Count == 1
                        ? _previewSlots[0].DisplayName
                        : $"{_previewSlots.Count} 路组合";
                }
                foreach (var i in Inputs) i.IsPreview = ReferenceEquals(i, _previewSource);
                RenderPreview();
                SyncPreviewPopup();
            }

            // ④ 常驻预处理进程（采集卡 / 网络流 / 抠像这类走链路 A 的）也要停掉，别留着占设备
            try { InputPreprocessService.Instance.Stop(src); } catch { }

            // ⑤ 摘掉列表项 + 重排编号（保证「切到输入 N」这类动作与看到的号仍然对得上）
            ClosePptSession(src);      // 这一路如果是 PPT，把它的放映窗口一起关掉
            Inputs.Remove(src);
            RenumberInputs();

            StatusText.Text = $"已从列表中删除输入「{src.Name}」（剩余 {Inputs.Count} 路）";
        }

        // ================= 输入列表拖动排序（老板 2026-10-19「输入列表也可以上下拖动」）=================
        // 按住任意输入项上下拖动 → 松手后按新顺序重排编号（1 基连续）。
        // 为什么拖完必须重排编号：导播键盘「切到输入 N」是按列表次序取 Inputs[N-1] 的，
        //   编号与次序一旦错位，按下去就切错路（见 RenumberInputs 的注释）。
        // 为什么重排不会打乱播出：图层 / 播放列表 / 预览持有的都是 InputSource **对象引用**，
        //   不是下标；存工程时再按 Inputs.IndexOf(...) 现算下标。

        private Point _inDragStart;
        private InputSource? _inDragItem;

        private void InputTiles_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _inDragStart = e.GetPosition(null);
            var container = ItemsControl.ContainerFromElement(InputTiles, e.OriginalSource as DependencyObject);
            _inDragItem = (container as ContentPresenter)?.DataContext as InputSource;
        }

        private void InputTiles_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _inDragItem == null) return;
            var p = e.GetPosition(null);
            // 拖动阈值：不设的话单击也会被判成拖动，选中/切预览会变得很不跟手
            if (Math.Abs(p.X - _inDragStart.X) < 6 && Math.Abs(p.Y - _inDragStart.Y) < 6) return;
            DragDrop.DoDragDrop(InputTiles, _inDragItem, DragDropEffects.Move);
        }

        private void InputTiles_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(typeof(InputSource)) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private void InputTiles_Drop(object sender, DragEventArgs e)
        {
            try
            {
                if (e.Data.GetData(typeof(InputSource)) is not InputSource dragged) return;
                int from = Inputs.IndexOf(dragged);
                if (from < 0) return;

                var container = ItemsControl.ContainerFromElement(InputTiles, e.OriginalSource as DependencyObject);
                var target = (container as ContentPresenter)?.DataContext as InputSource;
                // 落在空白处 = 移到末尾（和叠加层拖动的口径一致）
                int to = target == null ? Inputs.Count - 1 : Inputs.IndexOf(target);
                if (to < 0) to = Inputs.Count - 1;
                if (from == to) return;

                Inputs.Move(from, to);
                RenumberInputs();
                StatusText.Text = $"输入顺序已调整：「{dragged.Name}」移到第 {to + 1} 位";
            }
            catch (Exception ex)
            {
                // 拖动只是锦上添花，出问题绝不能影响直播
                try { StatusText.Text = "⚠ 拖动排序失败：" + ex.Message; } catch { }
            }
            finally
            {
                _inDragItem = null;
            }
        }

        /// <summary>
        /// 输入列表编号重排（1 基连续）。
        /// 与 vMix 的「删除后编号不动」不同 —— 本程序的「切到输入 1-8」是可绑定动作，
        /// 编号必须与列表次序一致，否则导播键盘按下去会切错路。
        /// </summary>
        private void RenumberInputs()
        {
            for (int i = 0; i < Inputs.Count; i++) Inputs[i].Number = i + 1;
            // Number 是普通属性（不发变更通知），重排/删除后必须整表刷一次，
            // 否则 tile 上显示的还是旧号（拖完顺序变了、号没变 = 更容易切错路）。
            try { InputTiles?.Items.Refresh(); } catch { }
        }

        /// <summary>
        /// 输入级几何（裁切 / 旋转 / 镜像）改动后，把该输入正在播出的每个位置重建媒体重播。
        ///
        /// ⚠ 这一步不能省，而且要做两件事：
        ///   ① 预处理层的 ffmpeg 是按「当时的参数」起的常驻进程 —— 参数变了必须把它停掉，
        ///      下次播放才会用新的滤镜链重新拉起来；不停的话画面还是旧的。
        ///   ② 滤镜是挂在媒体上的，改了设置必须换一个 Media 才会生效。
        ///   同一个输入可能同时出现在预览框和多个输出图层里，所以这里要逐处重建。
        /// </summary>
        private void ReplayInput(InputSource src)
        {
            if (src == null) return;

            // ⓪ 停掉旧的预处理进程（参数已变，旧进程作废）
            InputPreprocessService.Instance.Stop(src);

            // ① 预览框（多路预览里可能有好几路引用同一个输入，逐路重建）
            foreach (var s in _previewSlots)
            {
                if (!ReferenceEquals(s.Source, src) || s.Player == null) continue;
                try { s.Player.Play(_engine.CreateMedia(src)); }
                catch
                {
                    // 播放器可能停在 Ended 态，先 Stop 再 Play
                    try { s.Player.Stop(); s.Player.Play(_engine.CreateMedia(src)); } catch { }
                }
            }

            // ② 输出图层（可能有多层引用同一个输入）
            foreach (var l in Layers)
            {
                if (!ReferenceEquals(l.Source, src) || l.Player == null) continue;
                try { l.Player.Play(_engine.CreateMedia(src)); }
                catch
                {
                    // 「上一个已播完」时播放器停在 Ended 态，先 Stop 再 Play
                    try { l.Player.Stop(); l.Player.Play(_engine.CreateMedia(src)); } catch { }
                }
            }

            // 提示文案加保护窗：预览重建会触发 Opening/Playing 回调，别让它把提示顶掉。
            // 顺带说清「这一路是走预处理层、还是因为起不来回落到了原始画面」。
            var via = InputPreprocessService.Instance.IsRunning(src)
                        ? "（预处理层已生效，预览与输出看到的都是处理后的画面）"
                        : (src.HasGeometry ? "（⚠ 预处理未启动，画面按原始显示）" : "");
            SetStatus($"✓ 输入几何已更新：{src.Name} —— {src.GeometrySummary}{via}", hold: true);
        }

        // （原「播放硬件解码」「推流 / 录制编码器」「播出保护 ×2」的菜单处理器已删：
        //   前两项只在「输出设置…」窗里留一个权威入口；播出保护不再对外暴露开关，
        //   BroadcastGuard 由后台按「合成器是否在跑」自动决定是否生效。）

        // —— 帮助 ——
        private void MenuManual_Click(object sender, RoutedEventArgs e)
        {
            new HelpWindow("操作手册 · Cutlance", BuildManualText(),
                "本手册只讲「怎么操作」：按钮在哪、点了会怎样、要注意什么。全文可选中复制（Ctrl+C）；" +
                "按干活顺序排列，找不到就直接看最后一节「出问题了怎么查」。")
            { Owner = this }.ShowDialog();
        }

        private void MenuShortcuts_Click(object sender, RoutedEventArgs e)
        {
            // 老板 2026-10-20：原来用 MessageBox.Show，70 多行文本把弹框撑到 3385 像素高
            // （屏幕才 1600），底部内容根本看不到、也滚不动 —— 实测 rect 高 3385。
            // 改成可滚动的只读文本框窗口：不超屏、能滚、能整段选中复制。
            new HelpWindow("快捷键一览 · Cutlance", BuildShortcutTable()) { Owner = this }.ShowDialog();
        }

        /// <summary>快捷键表（纯文本，键位靠空格对齐，所以别改用 RichTextBox/HTML）。</summary>
        private static string BuildShortcutTable() => """
        快捷键一览（下表是程序默认值；除菜单自带的以外，全部可在「设置 → 导播键盘 / 快捷键…」里改成外设上的键）

        一、工程与素材
          Ctrl+N            新建工程
          Ctrl+Shift+O      打开工程（*.cutlance）
          Ctrl+S            保存工程
          Ctrl+Shift+S      工程另存为（可选保存目录）
          Ctrl+O            添加视频文件
          Ctrl+T            新建文字叠加
          Ctrl+I            新建图片叠加
          Ctrl+L            把预览加入输出图层

        二、导播
          F11               全屏 / 还原
          F7                播放列表：从第 1 项开始播放
          F8                播放列表：停止
          F5                倒计时：开始 / 暂停继续
          F6                倒计时 / 触发器：打开配置窗口
          F9                导播键盘 / 快捷键：打开设置窗口
          Alt+F4            退出

        三、文档 / 表格 / PPT 翻页
          PageDown          翻下一条 / 下一页（字幕表在播时翻字幕表，否则翻表格 / PPT）
          PageUp            翻上一条 / 上一页（同上）
          Ctrl+PageDown     强制翻表格 / PPT 的下一页（跳过字幕表）
          Ctrl+PageUp       强制翻表格 / PPT 的上一页（跳过字幕表）

        四、鼠标
          单击左侧输入项                  设为预览（PREVIEW）
          Ctrl + 单击左侧输入项           多选（出现「✓选」），可「多选一起进预览」
          左侧输入项上下拖动              调整输入顺序（编号跟着重排）
          右键左侧输入项                  改名 / 裁切·旋转 / 从列表删除
          预览框、播出框内拖动            移动画面位置
          预览框、播出框内滚轮            缩放画面大小
          叠加层列表 Ctrl / Shift + 点    多选，点「清空」只清这几路
          叠加层列表上下拖动              换叠加层级（1 最上层）
          预览框上右键                    保存布局 / 载入布局 / 重新分屏 / 放大预览

        说明：以上都是默认键位。若与别的软件冲突，或想用导播键盘的实体键，
              去「设置 → 导播键盘 / 快捷键…」逐条改绑即可，改完立即生效。
        """;

        /// <summary>
        /// 操作手册全文（老板 2026-10-20：「程序帮助文件全部重新写，只写和程序操作等有关事项」）。
        ///
        /// 只写「按钮在哪、点了会怎样、要注意什么」，不写实现原理、不写与其它软件的对比。
        /// 段落顺序 = 实际干活顺序：加画面 → 导播 → 叠字幕 → 出声 → 调色 → 推出去 → 出问题怎么办。
        /// 纯文本，靠空格对齐；正文改动时注意别把行宽拉太长（1366 宽屏上要能一行看完）。
        /// </summary>
        private static string BuildManualText() => """
        Cutlance 操作手册

        ────────────────────────────────────────────────────────────
        一、界面分区（先认清六块地方）
        ────────────────────────────────────────────────────────────
        顶部菜单栏   文件 / 编辑 / 视图 / 流 / 设置 / 帮助，最右边是当前时间。
        左栏上       输入 INPUTS：一排「+文件 +摄像头 +桌面 +采集卡 +网络流 +NDI +表格 +PPT」
                     是加素材的入口；下面是已加进来的输入列表。
        左栏下       播放列表 PLAYLIST：把几段素材排成顺序自动播。
        中区左       预览框 PVW：下一枪要打出去的画面，在这里摆好。
        中区中       CUT / AUTO / FTB 三个导播键 + 转场时长 + 转场方式。
        中区右       播出框 PGM：真正推流、录制、上大屏的画面。
        右栏         图层 LAYERS（画中画/分屏）/ 叠加层 OVERLAYS（1 = 最上层）/ 精调面板。
        底栏         状态栏：提示信息、系统资源（CPU / 内存 / 显存）、推流·录制状态与时长、倒计时。


        ────────────────────────────────────────────────────────────
        二、第一步：把画面加进来
        ────────────────────────────────────────────────────────────
        点左栏上方的按钮，或走「文件」菜单。八种输入各管一件事：

        +文件    本地视频 / 图片。加进来就在输入列表里，点一下进预览。
        +摄像头  普通 USB 摄像头 / 笔记本内置摄像头。
                 弹窗里选：设备 → 分辨率 → 帧率 → 采集格式（像素格式）→ 是否同时采它的声音。
                 拿不准就全选「默认」，交给设备自己协商，兼容性最好；
                 MJPG 通常能上更高分辨率 / 帧率，但占带宽更高。
        +采集卡  采集卡、摄像机（HDMI/SDI 进电脑那一路）。弹窗分三段：
                 ① 输入信号格式：左边选设备，下方先看「当前输入信号」——这是实测这根线
                    现在正送进来什么（分辨率 / 像素格式 / 帧率），再决定要不要强制某一档。
                    不确定就选「自动」。换了线或换了信号源，点「重新检测」。
                 ② 采集设置：音频设备（选中视频设备后会自动匹配配套音频）、采集链路、去隔行、延迟档。
                    采集链路可以各加一路对比实测；去隔行只在信号确实隔行时才开，逐行信号开了反而糊。
                 ③ 抠像换背景：绿幕 / 蓝幕抠掉换背景（纯色 / 图片 / 视频）。可调键色、相似度、
                    混合度、背景类型与文件、合成画布尺寸。
                    ⚠ 抠像必须走 ffmpeg 预处理链路；采集链路选「VLC 直连」时抠像不生效。
                 设好后点下方「添加为输入」。
        +网络流  RTSP / SRT / RTMP / HTTP 地址。可一次粘多条（每行一条），下方会实时告诉你
                 它把地址认成了什么（协议、主机端口、路径、是否带鉴权）——先核对再添加。
                 RTSP 承载、连接超时、SRT latency / 模式 / 口令 按协议自动启用。
                 不确定通不通就点「测试第一行地址」或「测试全部」，真连一次。
                 接入方式推荐保持「ffmpeg 预处理」：网络源大多限制并发连接，
                 直连时「预览和输出同看一路」容易把源连爆。常用地址可以「收藏当前地址」。
        +NDI     局域网里的 NDI 源。点「刷新」扫描，选中后点「检测」先看画面规格。
                 跨机刷不到时在「②额外发现 IP」里填对方 IP（逗号分隔）。
                 NDI 源名形如「主机名 (源名)」，例如 PC-01 (Camera 1)，也可在「③手动输入源名」里粘贴。
        +桌面    桌面捕获（把整个屏幕当一路画面）。
        +表格    整页表格上屏（投屏看报表用）。选 xlsx / xlsm / xls，按 Excel 的打印分页
                 逐页渲染成图片。分页怎么看：Excel 里「视图 → 分页预览」，看到的每一块就是一页。
        +PPT     见第六节，先选导法再导入。

        输入列表里怎么操作：
          · 单击 = 设为预览；Ctrl + 单击 = 多选（出现金色「✓选」）
          · 按住条目上下拖动 = 换顺序，编号跟着重排
          · 右键 = 改名… / 裁切·旋转… / 从列表中删除
            改名只改显示名，不动磁盘上的文件。


        ────────────────────────────────────────────────────────────
        三、导播：把预览推出去
        ────────────────────────────────────────────────────────────
        1. 单击左侧任一路输入 → 它进「预览框 PVW」。
        2. 确认画面没问题，按三个导播键之一：
             CUT    硬切，立刻切过去（无过渡）
             AUTO   按当前设定的「转场时长」和「转场方式」过渡过去
             FTB    淡黑（Fade To Black）：整幅画面压黑，再按一次恢复。
                    直播出状况（说错话、画面不慎）时的总闸 —— 推流 / 录制 / NDI /
                    全屏输出收到的画面同样变黑（不只眼前这一块黑）。
                    黑屏期间照样可以切画面、切布局，出来还是黑的，恢复时直接到新画面。
                    淡入淡出各 0.4 秒。
        3. 转场时长框（默认 1.0s）与转场方式下拉（Cut 硬切 / Fade 淡出 / AlphaFade 交叉溶解）
           随时可改，改完下一次 AUTO 就按新参数走。
           转场做在成品画面上（旧画面按透明度让位给新画面），所以推流 / 录制 / NDI 收到的也是
           带转场的画面，不是只在界面上看着有。转场期间合成耗时约多 8~12ms（1080p 实测）。
           Fade 和 AlphaFade 不是一回事（2026-10-22 起才真正分开）：
             Cut        一帧就换过去，最干脆。切机位、切画面内容时用。
             Fade       先淡到全黑，再从黑里淡出新画面。中点是真的黑屏。
                        适合换场景、换环节（比如"抽奖环节"切到"颁奖环节"），
                        中间那段黑等于给观众一个呼吸。
             AlphaFade 新旧画面交叉溶解，全程不黑。适合同一个场景里换机位、换角度，
                        观众看不出接缝。时长 0.6~1.2s 之间最自然。
           ⚠ 这两者的区别就是"中间黑不黑"：要黑用 Fade，不要黑用 AlphaFade。
           切「输出布局」时用的是存在布局里的那套转场（见第四节第 6 点），
           不会改动你现在工具栏上的选择 —— 手动 CUT / AUTO 仍按工具栏的走。
        4. 播出框 PGM 里看到的就是最终推出去的内容；推流 / 录制 / HDMI / NDI 出的都是这一块的成品帧。

        另外两个常被问到的入口：
          · 「把预览加入图层」（Ctrl+L / 中间那个圆形 → 按钮 / 右栏图层里的同名按钮）
            = 再叠一层上去，每一层的位置大小各自独立 → 用来做画中画。
          · 「全部上输出」（预览框标题栏上）= 把预览里摆好的每一路，连同位置大小一次性推上去
            → 用来做分屏。两者区别就在这：一个是「再加一层」，一个是「整个都推上去」。


        ────────────────────────────────────────────────────────────
        四、多画面：分屏与画中画
        ────────────────────────────────────────────────────────────
        1. 多选一起进预览
           按住 Ctrl 点左侧多路输入（出现「✓选」）→ 点预览框上的「多选一起进预览」，
           这几路会按数量自动排成预览分屏（2 路 = 左右各半，3 路 = 一排三个，4 路 = 田字……）。
           ⚠ 这一步只进预览、不直接上输出。先核对一眼再推，免得把错画面播出去。
        2. 推上输出（整组切）
           摆好了再按「全部上输出」，位置和大小会原样带过去。
           ⚠ 这一下是「整组替换」：播出框里上一套画面会整组下线，不会留在底下漏出来。
           想以后再叠一层，就用「把预览加入图层」。
        3. 播出中换其中一格（单格切，2026-10-22 新增；2026-10-23 改了入口）
           多画面正在播、只想换掉其中一格，别清空重摆：
             ① 在播出框里点一下那一格 —— 状态栏会显示「已选中第 N 格」
             ② 到左侧输入列表里，右键要换进去的那一路 →「换入播出框选中的那一格」
           只有这一格换内容，其余格子的位置、大小、边框一个都不动。
           ⚠ 为什么不是「点一下左侧输入」了（2026-10-23）：
              点左侧文件的第一步永远是「进预览框」，要点「切换」才进播出 ——
              这是日常流程。而播出框的选中状态会一直留着、又看不见，
              很容易出现"只想预览一下、结果把播出换掉了"，所以改成右键菜单里显式点。
           ⚠ CUT / AUTO 按钮始终是「整组切」（整套换掉），不会只换一格 ——
              直播中只想换一格，请走上面这两步，免得误把整个布局切没。
        3b. 每格声音大小（2026-10-22 新增）
           右栏图层列表每一行，类型文字右边有一条细滑杆，就是这一格的声音大小：
             0        静音（滑杆右边的数字会直接写「静音」，比写 0% 看得懂）
             100      原音量（默认）
             150      最大，比原音量大一半
           和左边那个小喇叭按钮的分工（很多人在这一步搞混）：
             喇叭按钮   管「出不出去」—— 点亮才是这一格在出声，暗的整格静音
             滑杆       管「多大声」—— 只在喇叭点亮时起作用
           调法：拖滑杆，松手前就能听到变化；状态栏会显示「图层 #N 音量 80%」。
           ⚠ 同一个素材被两格同时放（比如同一段片子左右各一次）时，声音只有一份，
              这时两格的滑杆会取「较大的那个」—— 把其中一格调小声不会让声音变小，
              要小声就把两格都调小。
           ⚠ 音量会跟着「输出布局」一起存（见第 6 点）：存布局时每格多大声音都记住了，
              切回来原样还原。所以"安静采访用一套布局、热闹环节用另一套"能一键切。
        3c. 空间不够怎么办（2026-10-25 起有三处可折，互不影响）
           ① 图层区标题栏「收起上面」：把上面的叠加层区整块折起来，图层列表立刻长高，
              四宫格四行连同各自的音量一次看全。按钮会变成「还原」，再点一下回来。
              ⚠ 只是腾地方，不删任何东西 —— 折叠期间叠加层照常出画、照常上下层。
           ② 叠加通道条右侧「编辑 / 收起」：只折「选中叠加项编辑」那个框，
              通道列表就能多显示几行（看清到底叠了什么）。要改文字时点「编辑」再展开。
           ③ 左栏播放列表标题行「收起 / 展开」：收起后只留一行标题，省出的高度全给输入列表，
              输入多了不用滚。
              ⚠ 收起不等于停止：正在自动顺播时照常播，标题行上的状态字还在。
        4. 精调面板（右栏最下方，默认收成一行标题）
           选中哪一路就调哪一路：预览里点中的那一路，或输出里点中的那一层，面板标题会显示目标名。
             X / Y        左上角位置（占画框百分比）
             宽 / 高       尺寸（100 = 正好铺满）
             描边 / 圆角   外观。2026-10-25 起直接画进输出画面（推流 / 录制 / NDI / 全屏都带），
                          以前只画在界面取景框上、观众看不到。点色块循环换颜色（青 / 白 / 金 / 红 / 黑）。
             填充          画面怎么填进这一格：等比内接（留黑边）/ 铺满裁切（不变形、填满）/
                          拉伸铺满（会变形）。异形屏（超宽 LED）选后两项，见第四节 4b。
             阴影 / 浓淡   向外扩散的投影，0 = 无阴影；浓淡 0–100 越大越黑。
                          四周均匀外扩（不做偏移）—— 分屏时格子贴边，偏移式投影会一边深一边被裁掉。
                          ⚠ 描边和阴影都画在「实际画面」上（留黑边的素材会贴着画面边缘，不贴格子外框）；
                            分屏挨得紧时阴影会压到隔壁一格上，这是正常的，嫌重就把「浓淡」调小。
             不透明        这一格在输出画面里的透明程度（100 = 完全不透明）
             去隔行        隔行源（1080i）画面有横丝时才勾
             旋转 / 裁切   作用在这一路输入本身，预览、输出、推流一起变（见十二节）
           上面这些改动都是直接改成品帧，直播中调也不会重开媒体、画面不闪。
           点「浮出」能把它变成独立小窗口，拖到不挡画面的地方；关掉那个小窗它就回到右栏。
        5. 布局文件（.vpreset）
           一套摆法反复用，就别每次重摆：
             「保存」  把预览框当前这套摆法存成布局文件。默认存到「我的文档\Cutlance-布局」，
                      文件名就是布局名。一个布局一个文件，可以拷给别人。
             「载入」  直接调回来用。
             「分屏」  不套布局，直接把预览里现有的几路重排成整齐宫格。
             「放大」  预览放到最大，方便一格一格精调；再点（按钮变「还原」）回双画面。
                      ⚠ 推流 / 录制进行中不允许放大 —— 放大要把播出框收起来，
                        输出本身不受影响（吃的是内存成品帧），但你就看不到节目画面了，
                        等于盲播。停掉输出再放大最稳。
           这四项在预览框上右键也能找到，不用去标题栏找按钮。

        6. 输出布局预设（一键换整套版面，2026-10-22 新增）
           一套版面反复用（如「主讲满屏」「双人对话」「四宫格圆桌」），存下来一键切：
             ① 在播出框里把这一套摆好（位置、大小、边框都行）
             ② 点「存」→ 起个名字（如「双人对话」）。同名会覆盖。
             ③ 以后要换：在「输出布局」下拉里选中它 → 点「切换」。
           记住的是：每一格的「位置 + 大小 + 层级 + 边框 + 绑的是哪一路输入 + 这一格出不出声 + 这一格音量」，
             外加「转场类型与时长」，外加「混音台的八路角色档 / 音量 / 静音」和「输出总线压缩档」（2026-10-23）。
             为什么连声音一起记：一套版面往往配一套声音 ——「双人对话」是两支麦都出声、
             「主讲满屏」只留主讲，切版面时不用再冲去混音台改一遍。
             混音按「设备名」认领：换了机器、采集卡插在别的口，只要设备名没变就能对上；
             本机没有那个设备的那一路会跳过（状态栏报「⚠ N 路设备不在本机」），不会瞎关别人的声音。
           ⚠ 老布局文件里没有混音这一段 → 切它时完全不动混音台（不猜、不重置）。
           ⚠ 混音与压缩是 ffmpeg 的启动参数：输出进行中切了布局，状态栏会提示「重启输出后生效」。
             换工程后只要素材还在列表里，按「路径 → 编号+名字 → 名字」自动认回来；
             实在认不到的那一格会「留空」，状态栏会报「N 格没找到素材」。
           ⚠ 下拉选中「不会」立刻换画面，必须点「切换」才生效 ——
             滚轮滑过下拉会改选中项，这样设计才不会滚一下就把播出画面换掉。
           「删」   删掉选中的布局（要连点两下确认，当前画面不受影响）。
           「撤回」 切错了点这里，回到切之前那套画面（只记最近一次）。
           「导出」 把选中的布局存成一个 .clayout 文件（可以发给别人、拷到别的电脑）。
           「导入」 读别人给的 .clayout，加进本机列表；重名会自动变成「xxx (导入2)」，不覆盖你的。
             素材换机器后路径变了也没关系，会按「编号+名字」「名字」继续认。
           声音：一套版面里哪几格出声也一起存。切过去就照它执行 ——
             想让两格都出声就先在两格上点「声音」再存；想整套静音就全部关掉再存，
             切到这套时不会自动把第一格打开（那是旧版本的行为，容易在直播里突然出声）。
           混音台也一起存（2026-10-23）：那八路的角色档（主音 / 次音 / 背景）、音量、静音，
             以及底部的输出总线压缩档，都记进这套布局。所以「双人对话」切「主讲满屏」时，
             声音会跟着换 —— 不再需要切完再去混音台手动改一遍。
             ⚠ 老布局文件里没有这一段：切过去「什么声音设置都不动」（不会因为升级把别人的麦关掉）。
             ⚠ 设备换了名（换了声卡/采集卡）：那一路上找不到就跳过，状态栏会报「几路设备不在」，
                其余照常切换，绝不会把别的通道顶上去。
           转场：四宫格这种多画面建议用「硬切」更利落；单人换双人、换主讲适合「交叉溶解 0.6~1.0 秒」。
             转场是在成品画面上做的交叉溶解，转场期间合成耗时约多 8~12ms（1080p 实测），
             不影响推流/录制帧率；出画顺序仍是「先抓旧画面 → 再换布局」，不会闪黑。
           快捷键  Ctrl+Alt+1 到 Ctrl+Alt+8 = 切到下拉里第 1 到第 8 套（播出去一秒换版面）；
                   Ctrl+Alt+0 = 撤回。
           这几个键在「导播键盘 / 快捷键」里可以改成自己顺手的。
           与上面第 5 点的 .vpreset 不是一回事：
             .vpreset     存的是「预览框」里的摆法，用来排版，不绑定播什么。
             输出布局     存的是「播出框」整套版面，连着每格播哪一路一起记，用来直播中换版面。


        ────────────────────────────────────────────────────────────
        五、叠加层：文字 / 图片 / 字幕表
        ────────────────────────────────────────────────────────────
        右栏「叠加层 OVERLAYS（1 = 最上层）」：
          +文字      在叠加层里新建一条文字
          +图片      选一张图当叠加
          清空       先 Ctrl / Shift 多选几路 → 只清这几路；什么都没选 → 一次清空全部 8 路
          +字幕表    选一张 xlsx，表头下每一行做成一条字幕条（见下面第 4 点）
          ◀ ▶        字幕表：上一条 / 下一条
          字幕样式   调字幕条的大小 / 位置 / 颜色

        1. 叠加通道 1-8 那一排按钮
           点一下点亮 = 该路叠加到输出；再点熄灭 = 从输出撤掉。
           亮色 = 正在叠加。叠加内容在编辑区里改，改完输出框立刻跟着变。
        2. 选中叠加项编辑
           文字内容、字体（列出系统已安装的全部字体）、字号、粗体 / 斜体、
           文字颜色（白 / 黄 / 红 / 绿 / 黑）、水平位置 %、垂直位置 %、
           图片整体大小 %（100 = 等比铺满输出框，不裁切）。
        3. 叠加层列表
           Ctrl / Shift 可多选；上下拖动 = 换叠加层级（位置 1 在最上层）。
        4. 字幕表（一行一条，播字幕用，和「+表格」不是一回事）
           「+字幕表」选一张 xlsx：
             第 1 列 = 大字主字幕     第 2 列 = 小字副字幕     第 3 列起拼成第三行
           一次导入整张表，几十上百条字幕全备好，直播时只管按翻页。
           翻页三种方式随你用：
             ① 「◀」「▶」按钮 —— 鼠标点一定翻得动，不会误翻到 PPT / 表格（最稳）
             ② PageDown / PageUp —— 字幕表在播时优先翻字幕表
             ③ 菜单「视图 → 文档翻页：下一页 / 上一页」
             ⚠ 要翻表格 / PPT 又不想动字幕表：用 Ctrl+PageDown / Ctrl+PageUp
        5. 字幕样式窗
             主字幕字号、副字幕字号、条子宽度、距画面底部、左右偏移、底色不透明度都是滑块；
             五种颜色（主字幕 / 副字幕 / 第三行 / 左侧竖条 / 条子底色）可手输十六进制
             （如 #FFFFD54F），也可点「选色」挑。
             ★ 拖滑块时主窗的输出框同步跟着动 —— 字幕条就压在你正在播的画面上，挪到哪看到哪。
             点「应用」才真正重新生成所有字幕条（行多时要几秒）；点「取消」输出框原样还原。
             「表格内容对照」把【前一条 / 现在上屏 / 后一条】的字一起列出来，上屏前扫一眼
             就能发现串行、错行；点某一条只换上面的预览，不会真的换掉正在上屏的那条。
        6. 三条容易踩的坑
           · 字幕表挂的是「叠加通道」（默认找第一个空通道）：要它上屏，得把对应 OVERLAY 通道
             打开；清空那个通道 = 字幕表一起收掉（再点「▶」会自动重新挂上）。
           · 字幕条的位置 / 大小只归「字幕样式」管：在叠加层列表选中字幕那一路时，
             编辑区里的「位置 X / Y」「图片大小」是锁定的（滑块变灰 + 有提示），
             免得翻页时字幕条跑位。想挪就回「字幕样式」里调，那里一次调好、所有条统一生效。
           · 表格页图与字幕条图缓存在临时目录，工程里存的是图片路径。
             换电脑或清了临时目录，重新导入一次即可。


        ────────────────────────────────────────────────────────────
        六、文档上屏：表格与 PPT
        ────────────────────────────────────────────────────────────
        1. 表格（+表格）
           按 Excel 的打印分页逐页渲染成图片，一页一张图。
           翻页：Ctrl+PageDown / Ctrl+PageUp；没有字幕表在播时，PageDown / PageUp 也能翻。
        2. PPT（+PPT，导入前先选导法）
           ① 图片序列 —— PPT 没有动画、没有转场、没有内嵌视频、没有声音时选这个。
              把每一页导成一张图，翻页 = 换一张图。
              好处：不挡屏，PowerPoint 关掉也能继续播，还可以设自动播放。
              代价：动画、转场、视频、音效都不会出现。
           ② 视频采集 —— PPT 有动画、转场、内嵌视频或声音时选这个。
              启动 PowerPoint 全屏放映，再把放映窗口那块的画面采进来。
              动画由 PowerPoint 自己驱动：按一次 PageDown 走一个动画步，动画播完才翻到下一页。
              代价：放映窗口必须一直摆在最前面、不能被别的窗口盖住（抓的是屏幕像素）；
                    被盖住会把盖住它的窗口一起抓进画面。双屏时建议让放映独占一块屏。
              声音：抓屏本身只有画面。要让 PPT 里的音效 / 视频声音进直播，得有一路「回环」
                    录音设备 —— 把系统播放设备指到它（如 VB-Audio Cable、立体声混音），
                    再在选项窗里选中它。没有回环设备就选「不采集声音」，画面照样播。
        3. 翻页笔 / 演示器
           去「设置 → 导播键盘 / 快捷键…」把它的上一页 / 下一页绑到
           「文档翻页：下一页 / 上一页」这两条上，并勾「全局生效」，就能在后台翻页。
           （这两条就是 PageDown / PageUp：字幕表在播时翻字幕表；要专门翻表格 / PPT，
             就绑 Ctrl+PageDown / Ctrl+PageUp 那两条。）


        ────────────────────────────────────────────────────────────
        七、声音：音频混音台（8 路采集 + 画面声音）
        ────────────────────────────────────────────────────────────
        入口：设置菜单 / 播出框下方「音频混音台（8 路采集）」按钮 / 输出设置窗里的「音频混音台…」。
        采集卡同时送进多路信号时，各路电平天然不一样，所以要分开调：
          1. 先给每一路选「角色档」定主次：主音 0 dB / 次音 −8 dB / 背景 −14 dB。
          2. 再用右侧滑杆微调，最终数值看 dB 栏。
          3. 滑杆最左端那个「关」是紧急开关：点一下立刻断掉这一路，再点恢复。
        八路混音后统一进入「推流 + 录制」。
        参数改动在重新开始输出后生效；输出进行中可以直接点「重启输出」立即应用。
        换了设备（拔插采集卡、换声卡）点「刷新设备列表」重新枚举。

        4. 输出总线压缩（2026-10-23 新增，混音台窗口最下面那一栏）
           八路各自调完再混，很容易一开嗓就超过 0 dB —— 一超过就是削顶破音，观众那边「噼」一下。
           最上面那个下拉就是总闸，四档：
             关闭        不处理，与旧版本完全一样（拿不准就先关着）
             轻压        只压最尖的峰值，几乎听不出变化（访谈、单人说话）
             中压        日常直播的稳妥档（人声为主）
             强压        现场吵闹 / 多人抢话，宁可扁一点也不破音
           下面三条滑杆是微调（换档会先灌回该档的出厂值，之后可以自己改）：
             阈值        −40 ~ −3 dB，超过这条线才开始压
             压缩比      1 ~ 20，3 就是「超出 3 dB 只放 1 dB 出去」
             增益补偿    0 ~ +12 dB，压完整体变小了，这里补回来一点
           末尾固定再串一级 −1 dB 限幅兜底（实测：放大 40 倍时不做处理峰值顶到 0.0 dB，
             挂上这条链后稳定在 −1.0 dB），所以压缩档选「关闭」时也不会有这一级。
           ⚠ 这是 ffmpeg 的启动参数：改了要点「重启输出」才生效，直播中不会突然变。
           ⚠ 压缩只解决「顶不顶」，谁大声还是靠角色档与滑杆 —— 别拿压缩去救录得太小声的话筒。

        5. 画面声音（2026-10-23 新增，混音台窗口中间那一段）
           预览框多选几个文件一起推到输出后，这几路文件各自的声音会出现在这里，
           一行一路，跟上面对采集卡的做法完全一样：角色档 + 音量滑杆 + 静音。
           推上输出时的默认是「第 1 格主音、其余次音」——
             一推上去就都听得见，又不会几路原声叠在一起直接削顶；
             想换主次就在这一段里改，改完立刻生效（不用重启输出）。
           这一段和右侧图层列表里的音量滑杆是同一份设置：
             在哪边改，另一边都跟着动。不会出现「两个地方各记一个数」的情况。
           同一个文件被两格引用时只列一行（标着 ×2）：
             因为一个输入本来就只有一份声音，调这一行两格一起变。
           静音只让这一路不出声，画面照常；要连画面一起下，去输出框里关掉那一格。
           存布局时角色档也一起存（见第四节），切回来连主次都原样还原。


        ────────────────────────────────────────────────────────────
        八、画面效果：调色 / 美颜 / 美体
        ────────────────────────────────────────────────────────────
        入口：设置菜单 / 播出框下方「画面效果」按钮。
        调色     亮度 −30~30、对比度 50~200%、饱和度 0~250%、伽马 50~250、色温（左冷 / 右暖）−100~100
        美颜     美白 0~10（提亮肤色）、磨皮 0~10（柔化皮肤细节）
        美体（分段局部形变，推荐）
                 ① 腰线位置（画面高度 %，从顶部算）  ② 臀线位置（画面高度 %）
                 ③ 腰段收缩（腰线→臀线 横向压窄）    ④ 腿段拉伸（臀线以下 纵向拉长）
                 ⑤ 作用中心（画面宽度 %，对准人物站立位置）
                 ⑥ 作用宽度（画面宽度 %，越大影响范围越宽、边缘越不易看出）
                 腰段整段横向压窄、腿段自臀线向下拉长，头部与胸部保持原样（曲线过渡、无接缝）。
                 腿段拉长会把画面下沿以外部分裁掉（脚可能出画）；
                 作用宽度外的人物 / 背景不受影响，所以要用「作用中心」对准人。
        腰臀线出厂值（横屏 / 竖屏各一套）
                 横屏  腰线 46 · 臀线 62        竖屏  腰线 52 · 臀线 66
                 竖屏人物占画面更大、整体更靠下，同一组数字（屏幕高度 %）落到的身体部位会更靠上，
                 所以竖屏单独给了一套。窗口顶部有提示和「套用竖屏默认」按钮，点一下按当前方向重套。
                 自动套用发生在三处：开始输出时、打开本窗时、点「恢复默认」之后。
                 ⚠ 只对「没手动调过」的生效 —— 值还停在任一套出厂值上才套用，你自己拖过的
                   数字一律不覆盖，调好的参数不会被悄悄改掉。
        ★ 现场换机位 / 换景别后，腰线一定要重调
                 腰线、臀线是「画面高度 %」，不是「身高 %」——只有人物顶天立地（头顶在 0%、
                 脚在 100%）时两者才等价。实拍素材里同一段视频，远景全身镜头腰在 62%，
                 切到近景大半身就跑到 52%。所以出厂值只覆盖「最常见的那种构图」，现场必须微调：
                   ① 先看画面里腰最细的那条线落在什么高度；
                   ② 把「① 腰线位置」拖过去压住它；「② 臀线位置」拖到胯最宽处稍下（裙摆最宽处）；
                   ③ 拖动时对照「抓帧对比预览」，别只看滑杆数字。
                 两条经验：拉腿幅度越大越容易看穿；瘦腰在转身、弯腰时最容易露馅，宁可少收一点。
                 参考：人体解剖上腰最细处约在身高 38%~42%、胯最宽处约在 50%~53%，
                       换算到画面里还要叠加人物在画面中的占比与上下位置，别直接照搬这两个数。
                 拖错了点该滑杆右边的「↺」单项还原，会回到当前方向（横屏 / 竖屏）的出厂值。
        美体（整幅近似形变，旧方式）
                 瘦身 0~10（镜头桶形，越大人越瘦，背景随动）
                 拉长 0~10（垂直放大后裁上下，人显高挑）
                 整幅画面一起形变（含头、背景），只能整体缩放 / 拉伸，不能只改某一部位。
        美学比例测算
                 填身高、肚脐高、坐高、腰围、臀围，选性别与腿身比口径，
                 点「应用建议值到美体滑块」直接把建议值填进去。
                 改任意一项，建议值实时更新。
        对比预览
                 打开窗口会自动抓一次对比图（原图 / 效果图）；改完参数随时点「抓帧对比预览」重抓（1~3 秒）。
        注意：画面效果作用于「播出框」整幅画面，因此会同时进入推流与录制；
              参数改动在重新开始输出后生效。


        ────────────────────────────────────────────────────────────
        九、输出：推流 / 录制 / HDMI / NDI
        ────────────────────────────────────────────────────────────
        入口：「设置 → 输出设置…」或「流」菜单。
        1. 推流
           填 RTMP 推流地址，勾「启用推流」。
        2. 本地录制
           选录制文件夹，勾「启用本地录制」。
           「格式」下拉：mkv（推荐，中途断电/强退也不易坏文件）/ mp4（兼容性好，
           但强退可能导致整个文件打不开）。
        3. 输出引擎
             FFmpeg（推荐）   四路混音 + 美颜美体，功能全
             LibVLC 兼容      仅单路麦克风；只有 FFmpeg 出问题时才用它兜底
           注意：「带麦克风声音」那个勾只对 LibVLC 兼容引擎有效；
                用 FFmpeg 引擎时，声音请在「音频混音台」里选设备与音量。
        4. 输出分辨率（横屏 / 竖屏）★ 2026-10-20 起搬到「视图」菜单
           入口：「视图 → 输出分辨率」—— 不在输出设置窗里（原来两处各有一个入口，已合并到这一处）。
             1920 × 1080（横屏）  /  1080 × 1920（竖屏）
           选了跟当前不一样的一档，程序会在「视图」菜单正下方浮出一条提示 +
           「保存文件立即重启」按钮：点它 = 先存盘、再自动重开，重开就按新尺寸出图。
           点「稍后」只收起提示条，选择还记着 —— 随时能从
           「视图 → 输出分辨率 → 保存文件立即重启」再来一次。
           为什么要重启：画面缓冲、预览位图、离屏画布在启动时就按这个尺寸分配好，
           运行中换尺寸没有重建路径，重启是唯一不留隐患的做法（约 3 秒）。
           菜单里那行「当前画面：1920 × 1080（横屏）」写的是真正在出的尺寸：
           选了竖屏还没重启时，它跟上面的勾选会不一致 —— 这是如实显示，不是没改成功。
           预览框和播出框按所选宽高比显示（自动加黑边居中），
           推流 / 录制 / NDI / HDMI 全部锁定为该分辨率。

        4b. 异形屏 / 超宽 LED（2026-10-25 新增，对标 vMix 的自定义 Output Size）
           适用：弧形 LED、多块拼接屏、超宽条屏 —— 凡是「分辨率不是 16:9 / 9:16」的屏。
           LED 的像素排布是工程方定的，3840×1080（32:9 双拼）、2560×1080（21:9）、
           5760×1080（48:9 三拼）都常见。

           ★ 三步搞定
           ① 设尺寸：「视图 → 输出分辨率 → 自定义尺寸…」
                        填「宽 x 高」，例如 3840x1080。
                        分隔符认 x / X / × / * / 逗号 / 空格（从报价单、微信上抄来都行）。
                        常用档「3840 × 1080（32:9 超宽 LED）」也在菜单里，一键选。
                        ⚠ 宽高必须是偶数 —— H.264/H.265 的色度抽样要求，奇数会编码失败，
                          程序会先挡住并告诉你原因。
                        ⚠ 范围 64 – 8192；长宽比超过 12:1 会被当成输错挡下来。
           ② 重启：点上浮出来的「保存文件立即重启」（约 3 秒），重开就按新尺寸出图。
           ③ 铺满：点中每一格 → 右栏「精调」→「填充」选一种：
                        · 等比内接 = 保持比例、留黑边（出厂默认）
                        · 铺满裁切 = 填满这一格且不变形，多出来的从「中间」裁掉（推荐）
                        · 拉伸铺满 = 填满但会变形（要跟 LED 物理像素 1:1 对位时用）
                     16:9 素材放在 32:9 的屏上：等比内接会左右各留 25% 黑边（实测 50% 是黑的），
                     换成「铺满裁切」后黑边归零 —— 这是异形屏最常踩的一步。

           ★ 现场注意事项
           · 屏幕要先被系统认出来：Windows「显示设置」里能看到这块屏、且分辨率对得上。
             认不出就是 EDID 问题（部分 LED 发送卡需要先在显卡驱动里建自定义分辨率），
             这一步在 Cutlance 之外，得先解决。
           · 超宽屏下预览框会变成一条很扁的窗口（按真实比例显示，不拉伸）——
             这是如实反映，不是坏了；点「放大预览」看不播的时候会更好操作。
           · 非标准尺寸的编码开销会上升（3840×1080 是 1080p 的 2 倍像素），
             本机实测软编也能跑，但更建议在输出设置里选显卡硬编。
           · 输出文件 / 推流分辨率会跟着变：录出来就是 3840×1080，
             不是先录 1080p 再让别人拉 —— 验收判据就是看录像文件的实际宽高。
           · 一块屏当两路用（左右各一路画面）：把它当普通超宽画布，两格各占一半
             （X=0 / X=0.5，宽 50%）即可，不用特殊设置。
        5. 开始 / 停止
           「流 → 开始推流 / 录制」「流 → 停止推流 / 录制」；也可以绑到导播键盘上。
           底栏会显示推流 / 录制的开关状态与已播时长。
        6. HDMI 全屏输出（镜像节目画面）
           选目标显示器，再在「流」菜单里打钩「HDMI 全屏输出」。
           要求：机箱通过 HDMI / DP 外接一块扩展屏，并在系统「显示设置」里设为「扩展」。
           副屏上显示的是合成器出的成品帧（跟推流、录制同一份像素），
           主窗口最小化、被别的窗口盖住都不影响。
        7. NDI 输出（把节目画面广播到局域网）
           勾「启用 NDI 输出」；源名留空则为「Cutlance Output (主机名)」；
           广播帧率可选 跟随输出帧率 / 30 / 25 / 60 fps。
           NDI 与推流 / 录制同源（都取同一份成品帧），可以只开 NDI，也可以同时开。
           ⚠ 首次开 NDI 要等十几秒（NDI 在做网络注册），这期间界面不卡，
             状态栏会显示「NDI 输出正在启动…」；看到「● NDI 输出已开启」才算真的在广播。
             等的时候别反复开关，重复点会被当成「已在启动」挡掉。
           局域网里别的机器用 NDI Studio Monitor / OBS / vMix 就能看到这个源。
           NDI 走帧内压缩、无重编码，1080p60 约占 100~150 Mbps 内网带宽。
        8. 显卡加速（入口：输出设置窗，唯一入口 —— 设置菜单里那两份重复项已删）
           「播放硬件解码」勾选 = 让显卡解码视频，降低 CPU；若开启后黑屏，取消勾选。
           「推流/录制编码」下拉：NVIDIA NVENC（显卡，默认）/ 软件 x264（最兼容）/ Intel QuickSync（核显）。
           默认走 NVENC 大幅降 CPU；若机器没有 N 卡导致推流启动报错，改选「软件 x264」。
        9. 输出帧率 / 视频码率（2026-10-21 新增；同日码率改为可手填）
           「输出设置 → 显卡加速」组里的两行：
             输出帧率：下拉，25 / 29.97 / 30 / 50 / 59.94 / 60
             码率：直接填数字（kbps，500~50000 都收），右边「参考 ▾」有常用档位
          帧率是合成器出帧与文件时间戳共用的那一档：选 25，录像文件就是 25fps。
           以前这里写死 30 —— 工程设 25 时合成器按 25 出帧、编码器按 30 打时间戳，
           12 秒录出来只有 10 秒、声音却还是 12 秒，越录越不同步（已修，实测 25fps 12.00 秒吻合）。
           码率对 NVENC / QuickSync / 软件 x264 / LibVLC 兼容模式全部生效
           （以前软件 x264 固定 3000k、兼容模式固定 4000/2500，选什么都不管用）。
           码率参考（H.264 直播通用量级）：
             2000  720p30 低带宽保底
             3000  720p30 普通 / 1080p30 纯课件
             4500  1080p30 普通直播
             6000  1080p30 高清（推荐起点）
             8000  1080p30 高动态 / 1080p60 普通
             10000 1080p60 高动态 / 局域网内网
             15000 局域网高画质（公网基本用不上）
           ⚠ 经验规则：码率别超过上行带宽的 70%。上行 10Mbps → 不过 7000；20Mbps → 不过 14000。
           ⚠ 填了不合法（字母、超范围）不会硬用：会退回上一次的有效值，并把真值写回框里，
             所以框里看到的数字永远等于实际下发的数字。
           切换台窗口的「帧率」下拉改的是同一个值，两处永远一致。
           ⚠ 正在录制时改帧率：已经开着的那一段不会变，要停掉重开才按新档录。
        10. 分段录制（长会 / 长课必备）
           「输出设置 → 分段录制」选 每 30 分钟 / 1 小时 / 2 小时 / 4 小时换新文件。
           到点自动封包并接着录下一个文件 —— 录制不中断，不是停掉再开，
           所以不会丢画面，也不会出现"停了忘了开"的事故。
           文件名形如 record_20261020_2130_000.mkv / _001.mkv，序号连续。
        11. 输出已经不依赖屏幕了 —— 重要
           2026-10-20 起，推流 / 录制 / NDI / HDMI 全部改成直接吃内存合成器的成品帧
           （以前是抓「播出框」那块屏幕）。区别：
             · 以前：窗口一被盖住、一最小化，观众就看到遮挡物或黑屏
             · 现在：窗口最小化、被别的窗口挡住、甚至拖到屏幕外，输出一切照旧
           实测：主窗口最小化后连续录 14 秒，420 帧一帧不少。
           从此不再有「禁止最小化」「遮挡报警」这类开关 —— 它们已经没必要了，菜单里也已删掉。
           （只有合成器起不来、退回抓屏兜底的那种异常情况，程序才会自己临时拦一下最小化。）
        12. 画面卡住时右键「刷新画面」（2026-10-21 新增）
           预览框、输出框、副屏全屏窗，任意一个上右键都能看到「刷新画面」。
           预览这边点画面本身就行（2026-10-23 修：以前那层「点得到、看不见」的透明操作层
           把右键吃掉了，只有点标题栏才有反应；现在画面区域也挂上了同一份菜单）。
           什么时候用：显卡驱动复位后花屏/花块、切换显示器或改缩放比例后画面不重绘、
           从最小化恢复后停在旧帧、副屏热插拔后画面不动或位置歪了。
           它做的是「把画面重新贴一遍」（顺带把副屏窗口重新摆正），不是重新截图，
           所以画面内容还是合成器的成品帧，跟推流/录制是同一份像素。
           不会闪黑、不是模态框，导播中也能用。
           ⚠ 如果刷新后还是不动，那是合成器线程停了 —— 状态栏会直接告诉你，
             这时候去看「视图 → 切换台」里的状态，而不是反复点刷新。
        13. 工作框一律「最高权限」（2026-10-21 补齐）
           所有工具窗（输出设置 / 混音台 / 画面效果 / 字幕样式 / PPT 导入方式 / 切换台 …）
           打开即在最上层，盖在播出框和主界面之上；窗口背景强制不透明，后面不会"透出来"；
           同时被排除在抓屏之外 —— 压在播出框上也不会被一起推流/录进节目里。
           Esc 关闭，Ctrl+S 保存（没有可保存内容的窗口只响应 Esc）。


        ────────────────────────────────────────────────────────────
        十、倒计时（右栏倒计时牌）与触发器
        ────────────────────────────────────────────────────────────
        ★ 右栏「⏱ 倒计时」（在图层区上面，导播时随时看得见）
            · 「设定」  自由输入时长，默认一小时。可填：1:00:00（时分秒）/ 30:00（分:秒）
                        / 90（纯数字 = 90 分钟）/ 45s（秒），也认「1小时30分钟」的中文写法。
                        填错会提示、不会把倒计时悄悄改成 0。
            · 「开始」  从设定的时间往下走。
            · 走到 0 不会停：继续走成负数并变红（= 已经超时多久），点「停止」才停。
                        停止后读数冻住，方便看「晚了多久」。
            · 「恢复」  读数退回设定的那个时间（顺带停表，等于回到起点）。
            · 「叠加」  把数字写进输出画面（推流 / 录制 / NDI / 全屏都带，监看框里也能看到），
                        再点一次取消。数字默认在画面正中，半透明黑底 + 等宽白字，超时变红。
            · 「挪位置」叠加开着时，直接在输出框里按住数字拖到想要的地方（松手即定位）；
                        数字整块不会被拖出画面外。要回正中点「居中」。
                        没开叠加时拖不到它 —— 那时按下去还是拖图层，不会互相抢。
            · 「居中」  一键把数字放回画面正中（换过分辨率、拖歪了都点它）。
            · 「样式」  点开露出两行（默认收起，不占右栏高度）：
                        字体 = 系统里装了的全部字体（出厂 Consolas 等宽，数字跳动不抖）+ 粗体 / 斜体；
                        字色 = 白/黄/橙/红/绿/青/蓝/黑（超时那一刻另有专用警示红）；
                        底板 = 半透黑 / 较黑 / 实黑 / 半透白 / 无底板（画面干净时可以去掉底衬）。
                        改完右栏大字立刻跟着变，不用盯着输出框确认。
            · 「字号」  40–300，只影响叠加到画面上的大小（按 1080p 基准等比换算，竖屏不会突然变两倍大）。
            · 设定值 / 字号 / 叠加开关 / 数字位置 / 字体颜色 都会存进工程文件，重开工程直接回到上次的样子
                        （不会自己跑起来）。

        下面是「到点自动做事」的那套（与上面的倒计时牌相互独立）：
        入口：设置菜单 / 状态栏的倒计时区域 / F6。
        1. 计时方式二选一
             按时长倒计时        填秒数（可填小数）；勾「循环」则到点立刻重新开始下一轮
             到指定时刻          按电脑时间倒数。只填时间（如 21:00）= 今天，已过则自动顺延到明天；
                                也可填 10-20 21:00 或 2026-10-20 21:00:00 指定日期
           开始 / 暂停 / 复位；另有「+1 分钟」「+5 分钟」「下个整点」快捷修正。
        2. 到点执行的动作
           选一个动作 + 填目标序号（输入源序号 / 叠加通道号 / 播放列表项序号）：
             切到指定输入 / 叠加开·关·切换 / 开始·停止输出 / FTB / 跳播放列表项 / 弹框提醒
           「弹框提醒」会在屏幕最上层弹框（压过其它程序），文字可自定义（默认「请录制推流」），
           并带一个「立即开始推流 / 录制」按钮；默认停在「否」上，不确认就不动作。
        3. 显示到画面上（可选）
           填「第 N 号叠加通道」（0 = 不显示）与显示格式，{t} 会被替换成剩余时间
           （≥1 分钟显示 mm:ss，不足 1 分钟显示 ss.s）。
           要显示到画面，请先用主界面的「+文字」在该编号的叠加通道里放点内容（空的通道会自动补一个文字）。
        4. 状态栏会实时显示剩余时间，归零即触发所选动作。


        ────────────────────────────────────────────────────────────
        十一、播放列表
        ────────────────────────────────────────────────────────────
        左栏底部「播放列表 PLAYLIST」：
          「+ 加入预览源」   把当前预览的这一路追加到列表
          「− 删除」         移除选中项
          时长框 + 「应用」  该项限时播放的秒数；填 0 或留空 = 播完自动下一项
          「循环」           播完末项回到第 1 项
          ▶ ⏸ ⏹ ⏮ ⏭        开始 / 暂停继续 / 停止 / 上一项 / 下一项
          双击列表项         直接从该项起播
        播出的画面全屏铺满输出，与手动切输入互不干扰。


        ────────────────────────────────────────────────────────────
        十二、输入裁切 / 旋转
        ────────────────────────────────────────────────────────────
        入口：右键左侧任一输入项 →「裁切 / 旋转…」，或「设置 → 输入裁切 / 旋转…」（对当前预览的输入）。
        上 / 下 / 左 / 右 四个方向填裁掉多少像素（按源画面尺寸算）；旋转 0 / 90 / 180 / 270；
        另可水平镜像、垂直镜像。改错点「全部清零」。
        要点：
          · 改的是「这一路输入」本身 —— 预览框、输出图层、播放列表凡播它都带同样处理。
          · 四边裁切之和不能超过源画面尺寸，超了就看不见画面。
          · 代价：这一路会比别的输入晚约 0.5 秒（要另起一个 ffmpeg 处理后再播），
            不宜与未处理的机位硬切同步。
          · 文件类输入经处理后变成直播流，不能再拖动进度条。
          · 全部清零后该输入回到直接播放，不占额外进程与延迟。

        ★ 去隔行（1080i 有横丝时用，2026-10-24 加）
           入口：右栏下方「精调」面板里那个「去隔行」勾（先选中这一路；预览槽不支持）。
           什么时候勾：画面里的「横向移动物体边缘出现锯齿 / 横丝」（典型 1080i 采集卡或 1080i 片源）。
              逐行的素材勾了反而会把垂直细节糊掉 —— 所以默认不勾。
           勾上之后：这一路改用 ffmpeg 的 yadif 做场插值，输出逐行；预览与输出同时生效。
           代价：会重开这一路媒体（画面闪一下），并且和裁切/旋转一样要另起 ffmpeg 处理，
              这一路会比未处理晚约 0.5 秒 —— 别和未处理的机位硬切同步。
           采集卡还有一档「自动」：加入时实测到隔行信号才启用，见「采集信号」窗口。


        ────────────────────────────────────────────────────────────
        十三、导播键盘 / 外设按键
        ────────────────────────────────────────────────────────────
        入口：设置菜单 / F9。先把外设（USB 小键盘 / 脚踏开关 / 带宏的键盘）插上。
        1. 先过兼容性提示
           需要厂商 SDK / 专用驱动的外设键盘不支持 —— 这类设备不向 Windows 发送标准键盘按键，
           程序收不到，也无法通过任何设置绕过。
           怎么判断：插上外设，打开「记事本」按一下它的键 —— 屏幕上打出了字符（数字、字母或功能键），
           说明它是「标准键盘」，可以正常绑定；什么都没出现，那多半需要厂商 SDK。
           绝大多数导播键盘、USB 小键盘、脚踏开关在系统里就是「标准键盘」，不受此限。
        2. 绑定
           点某行的「设定」→ 按键格里出现「请按外设上的键…」→ 直接按下那个键即可（Esc 取消）。
           录键那一小段时间里全局快捷键暂停响应，录完立刻恢复；撞键会先问你是否改绑。
        3. 全局生效
           勾上：程序在后台（主窗没有焦点）时也响应，且匹配到的按键不再传给别的程序。
                 但后台只认【带修饰键的组合】（如 Ctrl+1、Alt+CUT）与【F1-F24 / 小键盘键】；
                 纯字母、纯数字、空格这类单键在后台不响应 —— 否则你在别的软件里打字会连着触发导播动作。
           不勾：只有主窗在前台时才响应（最保险）。
                 主窗在前台时，无论勾不勾，全部照常响应。
        4. 「全部恢复默认」「全部清空」随时可用。
           配置存在 %APPDATA%\Cutlance\hotkeys.json，与工程文件无关，换工程照用。


        ────────────────────────────────────────────────────────────
        十四、工程文件与自动保存
        ────────────────────────────────────────────────────────────
        · 后缀 .cutlance（JSON，可直接查看 / 手工修改）。
        · 保存内容 = 当前调用的全部素材与设置：输入源、输出图层布局、
          预览框里的排列（多选一起进预览摆好的位置、大小、描边，一起存）、
          每格绑的是哪一路、每格的主次音角色档与音量、每格的描边 / 圆角 / 阴影 / 不透明度、
          每格是否去隔行、8 路叠加层、
          八路音频混音（含每路的角色档与逐路压缩）、输出总线压缩档、画面效果、人体测算数据、
          倒计时牌的设定值 / 样式 / 位置（重开回到上次设置、但不会自己开始计时）、
          右栏是不是「放到最大」、当前选中第几格、
          输出设置（编码器 / 推流地址 / 录制目录）、输出分辨率（含异形屏自定义尺寸）。
        · 打开工程 = 一键还原上述全部状态，素材按原路径重新载入：
            预览框直接回到保存时摆好的样子（不用再一个个点选 / 重新分屏）；
            播出框的格数、每格画面、每格主次音与音量、压缩档也照原样恢复。
          但不会自动开播 —— 输出（推流 / 录制 / NDI）仍要手动点「开始」。
        · 关闭程序时会自动保存（不用手动 Ctrl+S）：
            这次存过工程 → 覆盖回原文件
            从没保存过   → 写到「我的文档\Cutlance 工程\自动保存.cutlance」并进「最近工程」，
                           下次从「文件 → 最近工程」就能找回
          启动仍是一个空白新工程，不会自动打开上次的工程。
        · 布局文件（.vpreset）是另一套东西：单独把预览框里那几路画面的位置与大小导出成文件
          （方便给别的机器 / 别的工程直接用），不含其它设置。
          工程文件里已经自带当前预览框的排列，日常换工程不用另外导出。
        · 输出布局（「输出布局」下拉 + 存/切换/删/撤回）存的是播出框整套版面，
          连每格播哪一路一起记；存在 %APPDATA%\Cutlance\program-layouts.json，跨工程可用。
        · 「文件 → 打开工程所在目录」/「工程另存为…」都能指定保存目录。


        ────────────────────────────────────────────────────────────
        十五、出问题了怎么查
        ────────────────────────────────────────────────────────────
        1. 错误日志目录在哪
           菜单「帮助 → 打开错误日志目录」直接打开。目录按优先级取第一个能写的位置：
             · 程序目录\logs                        （装在 C:\Cutlance、D:\Cutlance 这类可写位置时）
             · %LOCALAPPDATA%\Cutlance\logs          （装在 Program Files 里，普通用户无写权限时）
             · %TEMP%\Cutlance-logs                  （上面都不行时的兜底）
           报错弹框里也会写明这次的实际路径。
        2. 目录里有什么
             errors.log              所有错误的累积账本，含时间、版本、系统、日志目录与完整堆栈
             error-时间.txt          单个致命错误的完整快照（整份发出去就能定位）
             run-时间.log            本次运行的会话日志：启动时的机器环境 + 关键动作 + 错误 + 退出
           出差错时把整个 logs 目录打包发出来就行 —— 环境、时间、堆栈全在里面，
           不用再逐条问「什么系统、多大屏、有没有显卡」。
        3. 常见故障速查（都先按这里试一遍再发日志）
           · 推流启动就报错    多半是没 N 卡。输出设置 → 编码器改「软件 x264」。
           · 播放黑屏          取消「播放硬件解码」，再重新载入这一路。
           · 采集卡没画面      设置 →「输入信号 / 采集设备…」→ 点「重新检测」，
                               看「当前输入信号」有没有值；换一条「采集链路」再试。
           · 采集卡有画面没声音 音频混音台里确认这一路设备选对了、没被「关」掉。
           · NDI 刷不到源      在「②额外发现 IP」里填对方 IP；确认两台在同一网段、防火墙放行。
           · NDI 卡顿          广播帧率降到 30；或改用「跟随输出帧率」以外的低档。
           · PPT 没声音        需要一路回环录音设备（VB-Audio Cable / 立体声混音），
                               并把系统播放设备指到它。
           · PPT 画面被挡住    视频采集方式抓的是屏幕像素 —— 放映窗口不能被别的窗口盖住。
           · 表格 / 字幕条图没了 临时目录被清过。重新导入一次即可（工程里存的是图片路径）。
           · 推流画面里少了叠加层 检查对应 OVERLAY 通道有没有点亮（右栏）。输出走内存成品帧，不要求播出框可见；只有合成器起不来、回落抓屏兜底且播出框被遮挡时才会丢。
           · 字幕条不动 / 跑位  字幕条的位置大小只归「字幕样式」窗，别在叠加层编辑区调。
           · 美体收腰位置不对  腰线 / 臀线是「画面高度 %」，换机位、换景别就要重调。
                               先看画面里腰最细的那条线在什么高度，再把「① 腰线位置」拖过去。
                               横屏出厂 46 / 62、竖屏 52 / 66，详见第八章末尾的现场提醒。
           · 叠加层不显示      检查右栏对应 OVERLAY 通道有没有点亮。
           · 多画面只想换一格  先在播出框里点中要换的那一格 → 到左侧要换进去的那一路点右键
                               →「换入播出框选中的那一格」。CUT / AUTO 是整组切，
                               会把整套布局换掉，不是换一格。
                               注意：点左侧文件本身只会进预览，永远不动播出画面。
           · 正常切换流程     点左侧文件 → 先在预览框里出现（这时可以先核对画面）
                               → 点「切换」（CUT / AUTO）才进播出框。
                               播出画面只认这个显式动作，不会被"点文件"顺手改掉。
           · 预览里放了好几路，想换回单路  直接点左侧要的那一路就行 ——
                               预览会整体换成这一路铺满，跟平常不多选时完全一样。
           · 预览 / 播出画面卡住、花屏  在画面上点右键 → 「刷新画面」。
                               预览的右键菜单挂在整块画面上，多选摆了好几路时一样能右键。
           · 这一格声音太大/太小  右栏图层那行显示「主音 · 100%」，点旁边的「调音」
                               进音频混音台 → 上面第一块「画面声音（来自输出框）」就是这几路，
                               那里调主次（主音/次音/背景）+ 音量 + 静音，几路一起混成一路。
                               喇叭没点亮就不出声 —— 先点亮喇叭，再进混音台调大小。
           · 同一批素材推过又推  主次与音量会沿用上次（按素材记，不按第几格记），
                               不会每次都打回「第一格主音、其余次音」白调一遍。
           · 整套版面想一键换  先把这套摆好 → 「存」起个名字；之后下拉选中 + 「切换」。
                               要真·一键：Ctrl+Alt+1..8 直接切第 1..8 套，Ctrl+Alt+0 撤回。
                               切完有一格是黑的？那格素材在列表里找不到了（改过名/换过路径），
                               状态栏会报「N 格没找到素材」，把素材加回来再切一次。
                               下拉选了画面却不动？这是故意的：滚轮会改选中项，
                               所以必须点「切换」。切错了点「撤回」。
           · 布局要给别人 / 换台机器  选中它 → 「导出」存成 .clayout；对方「导入」即可。
                               重名不覆盖，会自动变成「xxx (导入2)」。素材改名也能按名字认回。
           · 切过去没声音 / 突然有声  声音是跟着布局存的：存的时候哪几格亮着「声音」就出声。
                               要静音就先把各格声音关掉再「存」；切到全静音的布局不会再自动开第一格。
           · 键盘绑了没反应    先按十三节第 1 点判断外设是不是「标准键盘」；
                               后台不响应时检查「全局生效」有没有勾、按的是不是带修饰键的组合。
           · 窗口太大 / 按钮看不到  程序按 1080p@150% 设计，最小支持 1366×768@125%；
                               屏幕缩放超过 150% 或分辨率过低时，部分对话框会自动加滚动条。
           · 画面卡顿 / 掉帧    先看状态栏右下角的 CPU / 内存 / 显存数字，见下面第 4 点。
        4. 这台电脑跑得动吗
           状态栏右下角常驻三个数字：CPU · 内存 · 显存。正常是灰的，越线变琥珀、再加红。
           鼠标停上去能看到明细：总量、显卡型号、本程序自己占了多少 CPU、最近 10 分钟的越线比例。
           程序还会做「长期判定」：最近 10 分钟里，如果有一半以上的时间 CPU ≥ 85%、或内存 ≥ 88%，
           就说明这台机器长期不够用 —— 这时底部会弹一条横幅，直接告诉你「建议升级您的电脑」，
           并给出实测数据、现象和参考配置。状态栏同时留一句短结论，标题不会自己消失（15 秒后横幅收起，
           结论留在状态栏）。点「查看详情」看完整报告，点「知道了」本次运行不再弹。
           想随时查：菜单「帮助 → 这台电脑跑得动吗（性能评估）」，不用等报警。
           注意两点：
             · 瞬时峰值不会报「升级电脑」。程序只对「经常性」越线下这个结论，
               打开一张大表格卡两秒属于正常，不会误判。
             · 报告里的「本程序占用」不含编码进程（编码跑在独立子进程里），
               所以系统 CPU 高而本程序不高时，说明是别的软件在抢 CPU。
           掉帧先按上面第 3 点速查逐条排，确认不是设置问题再考虑换机器。


        ────────────────────────────────────────────────────────────
        十六、快捷键
        ────────────────────────────────────────────────────────────
        完整键位表见「帮助 → 快捷键一览」。最常用的几条：
          Ctrl+N / Ctrl+Shift+O / Ctrl+S   新建 / 打开 / 保存工程
          Ctrl+O        添加视频文件        Ctrl+T  新建文字叠加      Ctrl+I  新建图片叠加
          Ctrl+L        把预览加入输出图层
          F5 / F6       倒计时 开始·暂停 / 打开倒计时设置
          F7 / F8       播放列表 开始 / 停止
          F9            导播键盘设置        F11     全屏 / 还原
          Ctrl+Alt+1~8  一键切到第 1~8 套输出布局     Ctrl+Alt+0  撤回布局切换
          PageUp / PageDown   字幕表 / 表格 / PPT 翻页
        """;



        private void MenuAbout_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "Cutlance 2.0\n" +
                "轻量级直播导播台\n\n" +
                "· 输入：文件 / 摄像头（可设分辨率·帧率·格式）/ 桌面捕获\n" +
                "· 导播：PREVIEW → 输出图层，Cut / Fade / AlphaFade 转场，FTB 淡黑\n" +
                "· 叠加：8 路叠加通道（文字 / 图片），4 种字体 + 粗斜体\n" +
                "· 图层：多图层画中画 / 分屏，可拖拽移动与缩放，逐层声音锁定\n" +
                "· 音频：八路采集混音（独立音量 / 静音），统一进推流与录制\n" +
                "· 画面：调色 / 美白 / 磨皮 / 瘦身 / 拉长（NVENC 下生效），\n" +
                "        美体支持「美学比例测算」——录入身高·肚脐高·坐高·三围，\n" +
                "        按黄金比例(0.618) / 腰臀比(0.70) / 腿身比(54%~57%) 自动给出建议值\n" +
                "· 输出：RTMP 推流 + 本地 mkv/mp4 录制（NVENC / QuickSync / x264）\n" +
                "· 输出参数：帧率 25 / 29.97 / 30 / 50 / 59.94 / 60，码率手填 500~50000 kbps（带参考档）\n" +
                "· 大屏：HDMI 全屏播到副屏；输出分辨率 1920×1080 / 1080×1920\n" +
                "· 快捷：预览框 / 输出框 / 副屏全屏窗都能右键「刷新画面」\n" +
                "· 工程：*.cutlance 一键保存/载入全部素材与设置，支持最近工程\n\n" +
                "技术栈：.NET 8 WPF + LibVLCSharp（VLC 3.x）+ FFmpeg",
                "关于 Cutlance", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void MenuOpenLog_Click(object sender, RoutedEventArgs e)
        {
            // 老板 2026-10-20：错误要落在一个「找得到、发得出」的目录里。
            // 实际路径由 Services/ErrorLog 探测（程序目录\logs → %LOCALAPPDATA% → %TEMP%），
            // 这里只负责打开它，并把路径摆到状态栏上 —— 老板截图时能直接看到路径。
            string dir = ErrorLog.Directory;
            if (ErrorLog.OpenFolder())
            {
                SetStatus("错误日志目录：" + dir, hold: true);
                return;
            }

            MessageBox.Show("无法自动打开资源管理器。\n错误日志目录是：\n" + dir,
                "Cutlance", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // —— 窗口快捷键 ——
        //
        // 改造说明：以前这里是一串硬编码的 if（一个键都改不了）；现在统一查 HotkeyMap 这张映射表，
        // 于是「导播键盘 / 快捷键」设置窗里改成什么，这里就跟着是什么。
        //
        // 什么时候会走到这里？「全局生效」开着时，绑定的键已经被 WH_KEYBOARD_LL 钩子吃掉（见 KeyHookProc），
        // 走不到这；能走到的是两种情况：① 用户关掉了「全局生效」；② 钩子没能装上（极少数环境）。
        // 所以这里用同一张表再兜一次底 —— 保证快捷键在任何情况下都不会"整体失效"。
        /// <summary>
        /// 文档翻页（菜单入口，与 PageUp / PageDown 快捷键同一套逻辑）。
        /// 为什么菜单里也要有一份：PageUp / PageDown 是**滚动类按键**，焦点落在某个
        /// ScrollViewer / ListBox 上时会被那个控件当滚动吃掉（窗口级 KeyDown 收不到）。
        /// 翻页笔发的是物理键，走的是全局键盘钩子那条路，不受影响；
        /// 但「全局生效」关掉时，菜单就是最稳的那条路。
        /// </summary>
        private void MenuDocNext_Click(object sender, RoutedEventArgs e) => FlipOfficePage(+1);
        private void MenuDocPrev_Click(object sender, RoutedEventArgs e) => FlipOfficePage(-1);

        /// <summary>
        /// PageUp / PageDown 的**隧道兜底**（老板 2026-10-19 21:42 报「字幕条没法翻页」）。
        ///
        /// 只靠 Window_KeyDown 不够：PageUp/PageDown 是**滚动类按键**，
        /// 焦点落在 ListBox / ScrollViewer / ComboBox 上时会被子控件在冒泡阶段吃掉
        /// （`e.Handled = true`），根本到不了 Window_KeyDown。
        /// 隧道（Preview）阶段是从路由根往下走，Window 一定先接到 —— 这里直接处理掉。
        ///
        /// 三道闸门：
        ///   ① 带修饰键的不归这里管（交给 HotkeyMap 那条路，Ctrl+PageDown 就是强制翻 PPT）；
        ///   ② 焦点在输入框里打字时放行（不能抢走用户翻文本框的键）；
        ///   ③ 主窗不在前台时放行（后台按 PageDown 不能去翻老板正在看的网页）。
        /// </summary>
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (HotkeyMap.CaptureMode) return;

            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key != Key.PageDown && key != Key.PageUp) return;
            if (Keyboard.Modifiers != ModifierKeys.None) return;
            if (IsTypingFocus()) return;
            if (!IsForegroundSelf()) return;

            e.Handled = true;
            FlipOfficePage(key == Key.PageDown ? +1 : -1);
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (HotkeyMap.CaptureMode) return;   // 设置窗正在录键

            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.None || HotkeyMap.IsModifierKey(key)) return;

            var combo = HotkeyMap.Combo(key, Keyboard.Modifiers);

            // 在输入框里打字时，不带修饰的单键不抢（与全局钩子同一套闸门，见 HotkeyMap.IsBackgroundSafe）
            if (IsTypingFocus() && !HotkeyMap.IsBackgroundSafe(combo)) return;

            var id = HotkeyMap.ActionOfCombo(combo);
            if (id == null) return;

            e.Handled = true;
            RunHotkeyAction(id);
        }

        /// <summary>
        /// 执行一个导播快捷键动作（动作 ID 见 Services/HotkeyMap.cs 的 Actions 目录）。
        ///
        /// 一律在 UI 线程执行：钩子路径用 Dispatcher.BeginInvoke 投递过来，键盘事件路径本来就在 UI 线程。
        /// 整个方法包在 try 里 —— 快捷键触发绝不能因为某个动作抛异常就把键盘钩子拖挂。
        /// </summary>
        private void RunHotkeyAction(string id)
        {
            try
            {
                switch (id)
                {
                    // —— 导播切换 ——
                    case "trans.cut":   TakeToLayer(TransitionType.Cut); return;
                    case "trans.auto":  TakeToLayer(_transition); return;
                    case "trans.ftb":   BtnFtb_Click(this, new RoutedEventArgs()); return;
                    case "layer.add":   AddLayerFromPreview(); return;
                    case "layer.clear": BtnClearLayers_Click(this, new RoutedEventArgs()); return;

                    // —— 输出布局预设（P3）：Ctrl+Alt+1..8 一键整套换版面 ——
                    case "layout.undo": BtnLayoutUndo_Click(this, new RoutedEventArgs()); return;

                    // —— 输入切换 ——
                    case "src.next": StepPreview(+1); return;
                    case "src.prev": StepPreview(-1); return;

                    // —— 文档翻页（字幕表优先；docinput.* 强制翻表格 / PPT）——
                    case "doc.next": FlipOfficePage(+1); return;
                    case "doc.prev": FlipOfficePage(-1); return;
                    case "docinput.next": FlipDocInputPage(+1); return;
                    case "docinput.prev": FlipDocInputPage(-1); return;

                    // —— 输出 ——
                    case "out.start": StartStreaming(); SyncMenuState(); return;
                    case "out.stop":  StreamService.Instance.Stop(); SyncMenuState(); return;

                    // —— 倒计时 / 播放列表 ——
                    case "cd.toggle": BtnCdQuick_Click(this, new RoutedEventArgs()); return;
                    case "cd.window": OpenCountdown(); return;
                    case "pl.start":  BtnPlayStart_Click(this, new RoutedEventArgs()); return;
                    case "pl.stop":   BtnPlayStop_Click(this, new RoutedEventArgs()); return;
                    case "pl.next":   _playSvc.Next(); StatusText.Text = "▶ 播放列表：下一项"; return;
                    case "pl.prev":   _playSvc.Prev(); StatusText.Text = "⏮ 播放列表：上一项"; return;

                    // —— 窗口 / 设置 ——
                    case "win.fullscreen": ToggleFullscreen(); return;
                    case "view.preview":   TogglePreviewCell(); return;
                    case "view.program":   ToggleProgramCell(); return;
                    case "app.hotkeys":    OpenHotkeyWindow(); return;

                    // —— 工程 / 素材 ——
                    case "proj.new":    MenuNewProject_Click(this, new RoutedEventArgs()); return;
                    case "proj.open":   MenuOpenProject_Click(this, new RoutedEventArgs()); return;
                    case "proj.save":   SaveProject(false); return;
                    case "proj.saveAs": SaveProject(true); return;
                    case "in.file":     BtnAddFile_Click(this, new RoutedEventArgs()); return;
                    case "in.text":     BtnAddText_Click(this, new RoutedEventArgs()); return;
                    case "in.image":    BtnAddImg_Click(this, new RoutedEventArgs()); return;
                }

                // —— 带序号的动作：src.N / ov.N / ly.N ——
                if (id.StartsWith("src.", StringComparison.Ordinal) && int.TryParse(id.AsSpan(4), out int sn))
                {
                    if (sn >= 1 && sn <= Inputs.Count) SetPreview(Inputs[sn - 1]);
                    else StatusText.Text = $"⚠ 没有第 {sn} 路输入（当前共 {Inputs.Count} 路）";
                    return;
                }
                if (id.StartsWith("ov.", StringComparison.Ordinal) && int.TryParse(id.AsSpan(3), out int ov))
                {
                    ToggleOverlayChannel(ov);
                    return;
                }
                if (id.StartsWith("ly.", StringComparison.Ordinal) && int.TryParse(id.AsSpan(3), out int ly))
                {
                    if (ly >= 1 && ly <= Layers.Count)
                    {
                        var l = Layers[ly - 1];
                        l.Visible = !l.Visible;
                        StatusText.Text = $"图层 {ly}：{(l.Visible ? "显示" : "隐藏")} — {l.DisplayName}";
                    }
                    else StatusText.Text = $"⚠ 没有第 {ly} 层输出图层（当前共 {Layers.Count} 层）";
                    return;
                }
                // P3：布局序号 = 下拉里的第几项（1 基）
                if (id.StartsWith("layout.", StringComparison.Ordinal) && int.TryParse(id.AsSpan(7), out int ln))
                {
                    ApplyLayoutByNumber(ln);
                    return;
                }
            }
            catch (Exception ex)
            {
                try { SetStatus("⚠ 快捷键动作执行失败：" + ex.Message, hold: true); } catch { }
            }
        }

        /// <summary>预览切到上 / 下一路输入（到头循环）。source 为空时从第一路开始。</summary>
        private void StepPreview(int delta)
        {
            int n = Inputs.Count;
            if (n == 0) { StatusText.Text = "⚠ 还没有输入源"; return; }
            int cur = _previewSource == null ? -1 : Inputs.IndexOf(_previewSource);
            int idx = cur < 0 ? (delta > 0 ? 0 : n - 1) : ((cur + delta) % n + n) % n;
            SetPreview(Inputs[idx]);
        }

        /// <summary>显示 / 隐藏预览监视器（快捷键用；与菜单项同一效果）。</summary>
        private void TogglePreviewCell()
        {
            bool show = PreviewCell.Visibility != Visibility.Visible;
            PreviewCell.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (MiShowPreview != null) MiShowPreview.IsChecked = show;
            FitMonitors(); SyncPreviewPopup(); SyncMenuState();
            StatusText.Text = "预览监视器：" + (show ? "显示" : "隐藏");
        }

        /// <summary>显示 / 隐藏输出监视器（快捷键用）。</summary>
        private void ToggleProgramCell()
        {
            bool show = ProgramCell.Visibility != Visibility.Visible;
            ProgramCell.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (MiShowProgram != null) MiShowProgram.IsChecked = show;
            FitMonitors(); SyncProgramOverlayPopup(); SyncMenuState();
            StatusText.Text = "输出监视器：" + (show ? "显示" : "隐藏");
        }

        /// <summary>打开「导播键盘 / 快捷键」设置窗（非模态单例：已经开着就把它提到前面）。
        /// ★ 首次进入前先弹「外设键盘说明」确认框：只有单击「确定」才放行（取消 / 关窗 = 不进入）。</summary>
        private void OpenHotkeyWindow()
        {
            if (_hotkeyWindow != null)
            {
                try { _hotkeyWindow.Activate(); } catch { }
                return;
            }

            // 闸门：需要 SDK 的外设键盘本软件接不到，进入设置前必须先确认这一点
            if (new HotkeyNoticeWindow { Owner = this }.ShowDialog() != true) return;

            _hotkeyWindow = new HotkeyWindow { Owner = this };
            _hotkeyWindow.Closed += (_, _) => _hotkeyWindow = null;
            _hotkeyWindow.Show();
        }

        private void MenuHotkeys_Click(object sender, RoutedEventArgs e) => OpenHotkeyWindow();

        /// <summary>把菜单里的勾选/可用状态与程序真实状态对齐（每秒随状态栏一起刷新）。</summary>
        private void SyncMenuState()
        {
            if (TopMenu == null) return;
            var svc = StreamService.Instance;
            try
            {
                if (MiShowPreview != null) MiShowPreview.IsChecked = PreviewCell.Visibility == Visibility.Visible;
                if (MiShowProgram != null) MiShowProgram.IsChecked = ProgramCell.Visibility == Visibility.Visible;
                if (MiFtb != null) MiFtb.IsChecked = _isBlack;
                if (MiStartOutput != null) MiStartOutput.IsEnabled = !svc.IsRunning;
                if (MiStopOutput != null) MiStopOutput.IsEnabled = svc.IsRunning;
                if (MiHdmi != null) MiHdmi.IsChecked = _hdmiWindow != null;
                // 打勾按「待生效优先」：选了竖屏还没重启时，勾要跟着用户的选择走
                int tw = _pendingResW > 0 ? _pendingResW : ActiveOutputWidth;
                int th = _pendingResH > 0 ? _pendingResH : ActiveOutputHeight;
                if (MiResH != null) MiResH.IsChecked = tw == 1920 && th == 1080;
                if (MiResV != null) MiResV.IsChecked = tw == 1080 && th == 1920;
                if (MiResU != null) MiResU.IsChecked = tw == 3840 && th == 1080;

                // 菜单里的「当前画面」与「立即重启」（原输出设置窗口那一块搬过来的）：
                // 当前画面写**真实**出图尺寸，勾选写**选择值** —— 选了竖屏没重启时两者不同，
                // 分开显示才不骗人。
                if (MiResNow != null)
                {
                    int aw = ActiveOutputWidth, ah = ActiveOutputHeight;
                    string ash = OrientName(aw, ah);
                    MiResNow.Header = (_pendingResW > 0 && _pendingResH > 0)
                        ? $"当前画面：{aw} × {ah}（{ash}）　已选 {_pendingResW} × {_pendingResH}，重启后生效"
                        : $"当前画面：{aw} × {ah}（{ash}）";
                }
                if (MiResRestart != null)
                {
                    // 只要选了跟当前不同的尺寸就露出来。文案固定，不跟横/竖屏变 ——
                    // 横屏竖屏点下去都是「存盘 + 重启」，没必要写两个名字让人以为动作不同。
                    bool pend = _pendingResW > 0 && _pendingResH > 0;
                    MiResRestart.Visibility = pend ? Visibility.Visible : Visibility.Collapsed;
                }

                // 主窗浮动条（老板 2026-10-20 22:55：按钮要单独浮出来，别藏在菜单里）。
                // 跟菜单项同一份状态：有 pending 就浮，点「稍后」只收起本条提示、不清 pending。
                if (ResRestartBar != null && ResRestartText != null)
                {
                    bool pend = _pendingResW > 0 && _pendingResH > 0;
                    bool show = pend && !_resBarDismissed;
                    ResRestartBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                    if (show)
                    {
                        string sh = OrientName(_pendingResW, _pendingResH);
                        ResRestartText.Text =
                            $"已选 {_pendingResW} × {_pendingResH}（{sh}），重启后生效" +
                            $"（当前 {ActiveOutputWidth} × {ActiveOutputHeight}）";
                        PositionResBar();   // 贴着「视图」菜单项浮出，不是贴在屏幕底
                    }
                }
                RefreshAudioFxHint();
            }
            catch { }
        }

        /// <summary>
        /// 尺寸 → 人话（横屏 / 竖屏 / 方形 / 异形屏 32:9）。
        /// 菜单、浮动条、状态栏都用这一份，别各写一套 —— 三处口径不一致时老板会以为出错了。
        /// </summary>
        private static string OrientName(int w, int h)
        {
            if (w <= 0 || h <= 0) return "未知";
            if (w == 1920 && h == 1080) return "横屏";
            if (w == 1080 && h == 1920) return "竖屏";
            if (w == 3840 && h == 1080) return "32:9 超宽";
            if (w == 2560 && h == 1080) return "21:9 超宽";
            if (w == 5760 && h == 1080) return "48:9 三拼";
            if (w == h) return "方形";
            double ar = (double)Math.Max(w, h) / Math.Max(1, Math.Min(w, h));
            if (ar <= 2.05) return w > h ? "横屏" : "竖屏";
            return $"异形屏 {((double)w / h):0.##}:1";
        }

        /// <summary>制作条上的音画设置摘要（几路音频、有无画效、当前输出引擎）。</summary>
        private void RefreshAudioFxHint()
        {
            if (AudioFxHint == null) return;
            var svc = StreamService.Instance;
            int used = svc.AudioChannels.Count(a => a.InUse);
            var parts = new List<string>
            {
                used == 0 ? "音频：未启用采集" : $"音频：{used} 路混音",
                svc.Fx.Any ? "画效：" + svc.Fx.Summary : "画效：无",
                "引擎：" + (svc.OutputEngine.Equals("vlc", StringComparison.OrdinalIgnoreCase) ? "LibVLC 兼容" : "FFmpeg")
            };
            AudioFxHint.Text = string.Join(" · ", parts);
        }

        // ===================== 模块7：HDMI 全屏镜像输出 =====================

        /// <summary>在所选显示器上开无边框全屏窗口，镜像「播出框」画面。返回是否成功。</summary>
        private bool StartHdmiOutput(int monitorIndex)
        {
            StopHdmiOutput();

            var monitors = DisplayService.GetMonitors();
            if (monitors.Count < 2)
            {
                MessageBox.Show("未检测到第二块显示器。\n\nHDMI 播出输出需要一块扩展屏：" +
                    "请把 HDMI/DP 线接到外接屏，并在系统「显示设置」里设为「扩展」而非「复制」。",
                    "Cutlance", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (monitorIndex < 0 || monitorIndex >= monitors.Count) monitorIndex = 1;
            var target = monitors[monitorIndex];

            // 防止输出投到主窗口所在的屏：那会把操作界面连同输出一起拍进去（画面自反馈）
            var mainHwnd = new WindowInteropHelper(this).Handle;
            if (target.Index == DisplayService.GetMonitorIndexForWindow(mainHwnd))
            {
                MessageBox.Show("所选显示器就是主窗口所在的屏，会造成画面自反馈（拍到自己）。\n\n" +
                    "请改选另一块屏作为 HDMI 输出。", "Cutlance",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            var (left, top, width, height) = GetOutputScreenRect();
            if (_monitor == null && (width <= 0 || height <= 0))
            {
                StatusText.Text = "⚠ 无法获取「播出框」区域，请先确保窗口已显示在屏幕上";
                return false;
            }

            // 2026-10-24：全屏窗搬到自己的一条 STA 线程（Views.FullscreenOutputHost），
            // 主界面卡不再拖副屏；帧由合成器 sink 推过去（每帧多拷一份 ≈8MB）。
            // 合成器起不来（_monitor == null）时宿主拿不到帧，副屏会是黑的 —— 但那种情况下
            // 抓屏兜底本来也拿不到成品帧，主体输出已经不可用，属于"先修根因"的场景。
            if (_monitor == null)
            {
                StatusText.Text = "⚠ 合成器没起来（输出侧不可用），HDMI 全屏输出无法出画";
                return false;
            }

            var host = new Views.FullscreenOutputHost(target, _monitor, _monitor.OutW, _monitor.OutH);
            _hdmiWindow = host;
            // ⚠ 老板 10-18 要求：全屏输出窗口被关掉 = 输出结束，所有「HDMI 输出」按钮/勾选必须立刻跟着复位。
            // 不能只把字段置空 —— 否则菜单勾选、设置窗按钮还停在「输出中」，得再点一次才认。
            host.Closed += () =>
            {
                if (!ReferenceEquals(_hdmiWindow, host)) return;   // 主动 StopHdmiOutput 已清理过，不重复
                // 在 UI 线程上复位（Closed 来自窗口线程）
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!ReferenceEquals(_hdmiWindow, host)) return;
                    _hdmiWindow = null;
                    if (MiHdmi != null) MiHdmi.IsChecked = false;
                    SyncHdmiToOutputWindows();
                    SetStatus("HDMI 全屏输出已关闭（输出窗口已关）", hold: true);
                }));
            };
            SyncHdmiToOutputWindows();
            StatusText.Text = $"HDMI 全屏输出已开启 → 显示器 {target.Index + 1}（{target.Width}×{target.Height}）"
                              + " · 独立线程渲染（主界面卡不影响副屏）";
            return true;
        }

        private void StopHdmiOutput()
        {
            if (_hdmiWindow == null) { SyncHdmiToOutputWindows(); return; }
            var w = _hdmiWindow;
            _hdmiWindow = null;
            try { w.Close(); } catch { }
            if (MiHdmi != null) MiHdmi.IsChecked = false;
            SyncHdmiToOutputWindows();
            StatusText.Text = "HDMI 全屏输出已关闭";
        }

        /// <summary>
        /// 把「HDMI 是否正在输出」这个唯一真值（`_hdmiWindow != null`）回填到所有打开着的输出设置窗，
        /// 让按钮文案与状态文字始终跟真实输出一致 —— 不管输出是从菜单开的还是从设置窗开的。
        /// </summary>
        private void SyncHdmiToOutputWindows()
        {
            bool on = _hdmiWindow != null;
            foreach (Window w in Application.Current.Windows)
            {
                if (w is not OutputWindow ow || !ow.IsLoaded) continue;
                try { ow.SetHdmiRunning(on); } catch { }
            }
        }

        /// <summary>窗口移动/缩放时去抖（300ms）更新 HDMI 捕获区域，避免拖拽过程中疯狂重开媒体。</summary>
        private void ScheduleHdmiReposition()
        {
            if (_hdmiWindow == null) return;
            _hdmiRepositionTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _hdmiRepositionTimer.Stop();
            _hdmiRepositionTimer.Tick -= HdmiRepositionTick;
            _hdmiRepositionTimer.Tick += HdmiRepositionTick;
            _hdmiRepositionTimer.Start();
        }

        private void HdmiRepositionTick(object? sender, EventArgs e)
        {
            _hdmiRepositionTimer?.Stop();
            if (_hdmiWindow == null) return;
            // 2026-10-24：全屏窗改成合成器直供 + 独立线程，跟"播出框在屏幕上的位置"无关了；
            // 这里只需在显示器排列/分辨率变化后把窗口重新摆正一次。
            _hdmiWindow.Reposition(DisplayService.GetMonitors()
                .FirstOrDefault(m => m.IsPrimary == false) ?? DisplayService.GetMonitors().FirstOrDefault());
        }

        // —— 按输出分辨率约束预览框/播出框宽高比（居中加黑边，避免拉伸变形）——
        private void FitMonitors()
        {
            var svc = StreamService.Instance;
            if (svc.OutputWidth <= 0 || svc.OutputHeight <= 0) return;
            double aspect = (double)svc.OutputWidth / svc.OutputHeight;
            const double headerH = 22;   // 监视器标题行高度
            if (PreviewCell != null)
                FitAspect(PreviewBox, PreviewCell.ActualWidth, PreviewCell.ActualHeight - headerH, aspect);
            if (ProgramCell != null)
                FitAspect(ProgramBorder, ProgramCell.ActualWidth, ProgramCell.ActualHeight - headerH, aspect);
        }

        private static void FitAspect(FrameworkElement box, double availW, double availH, double aspect)
        {
            if (box == null || availW <= 1 || availH <= 1 || aspect <= 0) return;
            double w = availW, h = w / aspect;
            if (h > availH) { h = availH; w = h * aspect; }
            box.Width = Math.Max(1, w);
            box.Height = Math.Max(1, h);
            box.HorizontalAlignment = HorizontalAlignment.Center;
            box.VerticalAlignment = VerticalAlignment.Center;
        }

        // ===================== 模块8：音频混音台 / 画面效果 =====================

        /// <summary>播出框在屏幕上的物理像素区域（供画面效果窗口抓帧预览用）。</summary>
        public Rect ProgramScreenRectPhysical()
        {
            var (l, t, w, h) = GetOutputScreenRect();
            return new Rect(l, t, Math.Max(0, w), Math.Max(0, h));
        }

        /// <summary>打开音频混音台（最多八路音频采集，独立音量，混音进推流+录制）。</summary>
        public void OpenAudioMixer()
        {
            foreach (Window w0 in Application.Current.Windows)
                if (w0 is AudioMixerWindow am) { am.Activate(); return; }
            var w = new AudioMixerWindow(this) { Owner = this };
            w.RestartRequested += (_, _) => RestartOutput();
            w.Show();
        }

        /// <summary>打开画面效果窗口（调色 / 美颜 / 美体，NVENC 时启用）。</summary>
        public void OpenVideoFx()
        {
            foreach (Window w0 in Application.Current.Windows)
                if (w0 is VideoFxWindow vf) { vf.Activate(); return; }
            new VideoFxWindow { Owner = this }.Show();
        }

        private void BtnAudioMixer_Click(object sender, RoutedEventArgs e) => OpenAudioMixer();
        private void BtnVideoFx_Click(object sender, RoutedEventArgs e) => OpenVideoFx();
        private void MenuAudioMixer_Click(object sender, RoutedEventArgs e) => OpenAudioMixer();
        private void MenuVideoFx_Click(object sender, RoutedEventArgs e) => OpenVideoFx();

        /// <summary>以最新音/画参数重启输出（先停后起，录制会新开文件）。</summary>
        public void RestartOutput()
        {
            var svc = StreamService.Instance;
            if (!svc.IsRunning) { StartStreaming(); return; }
            svc.Stop();
            // 等 ffmpeg 退出并释放采集设备/端口后再起
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            t.Tick += (_, _) => { t.Stop(); StartStreaming(); };
            t.Start();
        }

        private void StartStreaming()
        {
            var svc = StreamService.Instance;

            // ══════════════════ C 方案：输出直接吃合成器成品帧 ══════════════════
            // 只要合成器在跑，推流/录制/NDI 就都不再关心「播出框」在哪、有没有被挡。
            // 合成器起不来（极少数环境）时这里传 null，StreamService 自动回落到老 gdigrab 抓屏。
            svc.ProgramFrames = _monitor;

            double left = 0, top = 0, width = 0, height = 0;
            if (_monitor == null)
            {
                (left, top, width, height) = GetOutputScreenRect();
                if (width <= 0 || height <= 0)
                {
                    StatusText.Text = "⚠ 无法获取「输出监视器」区域，请先确保窗口已显示在屏幕上";
                    return;
                }
            }
            else if (svc.OutputWidth != _monitor.Width || svc.OutputHeight != _monitor.Height)
            {
                // 合成器尺寸在构造时定死：输出分辨率必须跟着它走，否则 filter 会把画面拉变形
                svc.OutputWidth = _monitor.Width;
                svc.OutputHeight = _monitor.Height;
                SetStatus($"合成器按 {_monitor.Width}×{_monitor.Height} 出图，输出分辨率已跟随" +
                          "（改分辨率需重启程序）", hold: true);
            }

            // 老板 2026-10-18 第 4 条：美颜美体要跟着「进来的信号」走。
            // 每次开始输出时按当前节目内容算一次有效宽高比，供形变/磨皮按有效画面定位。
            svc.Fx.ContentAspect = ComputeProgramContentAspect();

            // 老板 2026-10-22：竖屏单独一套腰臀线默认值（横屏 46/62、竖屏 52/66）。
            // 放在这里是为了「不打开画面效果窗也会生效」；用户自己调过的值不会被覆盖。
            svc.Fx.ApplyOrientationDefaults(svc.OutputHeight > svc.OutputWidth);

            // 帧率必须跟**合成器实际出帧的节拍**一致（2026-10-21 实测教训）：
            // 这里原来写死 30，而合成器按 svc.Fps 出帧 —— 设 25 时 12 秒只有 300 帧，
            // ffmpeg 却按 30fps 打时间戳，录出来只有 10 秒、音画差 2 秒。
            // 合成器在跑就用它的真实节拍；抓屏兜底（_monitor==null）才用服务里的值。
            svc.Start(left, top, width, height, _monitor?.Fps ?? svc.Fps);
        }

        /// <summary>
        /// 当前节目画面的**内容**宽高比（宽 ÷ 高）。0 = 拿不到 → 按整帧处理。
        ///
        /// 只在「恰好一路可见图层」时返回真实比例：多图层（分屏/画中画）时整幅形变本来就
        /// 无法对准某一个人，硬算反而会拿第一路的比例去扭曲整个合成画面。
        /// </summary>
        private double ComputeProgramContentAspect()
        {
            try
            {
                var vis = Layers.Where(l => l.Visible).ToList();
                if (vis.Count != 1) return 0;
                var tracks = vis[0].Player?.Media?.Tracks;
                if (tracks == null) return 0;
                // ⚠ LibVLCSharp 3.9 里 MediaTrack 是**结构体**，视频尺寸挂在
                //   `MediaTrack.Data.Video`（VideoTrack 结构体）上，不能强转 VideoTrack。
                foreach (var t in tracks)
                {
                    if (t.TrackType != TrackType.Video) continue;
                    var v = t.Data.Video;
                    if (v.Width == 0 || v.Height == 0) return 0;
                    return (double)v.Width / v.Height;
                }
                return 0;
            }
            catch { return 0; }
        }

        /// <summary>取「输出监视器」在屏幕上的物理像素区域（含 DPI 缩放换算）。</summary>
        private (double left, double top, double width, double height) GetOutputScreenRect()
        {
            var dpi = VisualTreeHelper.GetDpi(ProgramBorder);
            // 用布局变换取元素在窗口客户区内的 DIP 偏移，再叠加客户区原点的屏幕物理坐标，
            // 避免 PointToScreen 单位约定不一致造成 150% 缩放下抓屏区域偏移。
            Point off;
            try { off = ProgramBorder.TransformToAncestor(this).Transform(new Point(0, 0)); }
            catch { off = new Point(0, 0); }
            var origin = ClientOriginPhysical();
            double left = origin.X + off.X * dpi.DpiScaleX;
            double top = origin.Y + off.Y * dpi.DpiScaleY;
            double width = ProgramBorder.ActualWidth * dpi.DpiScaleX;
            double height = ProgramBorder.ActualHeight * dpi.DpiScaleY;
            return (left, top, width, height);
        }

        /// <summary>刷新状态栏推流/录制指示灯与计时。</summary>
        private void UpdateStreamStatus()
        {
            var svc = StreamService.Instance;
            if (svc.IsRunning)
            {
                StreamDot.Fill = new SolidColorBrush(Colors.Orange);
                StreamLbl.Text = "推流 " + (svc.EnableStream ? "开" : "关");
                StreamLbl.Foreground = new SolidColorBrush(Colors.Orange);
                RecDot.Fill = new SolidColorBrush(Colors.Red);
                RecLbl.Text = "录制 " + (svc.EnableRecord ? "开" : "关");
                RecLbl.Foreground = new SolidColorBrush(Colors.Red);
                StreamElapsed.Text = svc.StartedAt.HasValue
                    ? "· " + (DateTime.Now - svc.StartedAt.Value).ToString(@"hh\:mm\:ss") : "";
            }
            else
            {
                StreamDot.Fill = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
                StreamLbl.Text = "推流 关"; StreamLbl.Foreground = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));
                RecDot.Fill = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
                RecLbl.Text = "录制 关"; RecLbl.Foreground = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));
                StreamElapsed.Text = "";
            }
            SyncMenuState();   // 菜单勾选/可用状态与真实状态保持一致
        }

        // —— 媒体错误提示 ——
        private static void ShowMediaError(string mrl)
        {
            if (mrl.StartsWith("dshow"))
                MessageBox.Show("摄像头打开失败。\n\n常见原因：\n" +
                    "1. 摄像头被其他程序占用（vMix/会议软件/相机应用）\n" +
                    "2. 设备被禁用或驱动异常\n" +
                    "3. 隐私设置关闭了摄像头访问（设置 → 隐私和安全性 → 相机）\n\n" +
                    "详情见 %TEMP%\\Cutlance_vlc.log",
                    "Cutlance", MessageBoxButton.OK, MessageBoxImage.Error);
            else
                MessageBox.Show("VLC 内部错误（媒体：" + mrl +
                                "）\n详情见 %TEMP%\\Cutlance_vlc.log",
                    "Cutlance", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
