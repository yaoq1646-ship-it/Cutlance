using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Cutlance.Services
{
    /// <summary>
    /// 把「画面比例 ≠ 画框比例」时的留白区刷成**纯黑**（就是导播界说的"黑边 / 信箱边"）。
    ///
    /// ── 问题（老板 2026-10-16 报：16:9 信号进竖屏画框，留白处是浅灰白块）──
    /// LibVLCSharp.WPF 的做法是：先建一个**类名为 "static"** 的宿主子窗（VideoHwndHost），
    /// 再把 VLC 的 Direct3D11 输出窗嵌进去。窗口层级实测为三层：
    ///     Static(宿主, 全尺寸) → "VLC video main"(全尺寸) → "VLC video output"(只占视频那一块)
    /// 视频只画在最里面那层，**外面那圈留白由 "VLC video main" 补底**；
    /// 而它的窗口类背景刷是 NULL → 系统退回默认的 COLOR_3DFACE，
    /// 实测取到的像素正好是 **(240,240,240) = #F0F0F0** —— 这就是那块"白块"的来源。
    ///
    /// ── 修法 ──
    /// 把 "VLC video main" 这个窗口的**类背景刷**换成纯黑，再让它重绘一次。
    /// 之后凡是需要擦除留白区（换台、改分辨率、拉伸画框）系统都会用黑刷子填 → 永远是黑边。
    ///
    /// ⚠ 三条实测纪律（都是踩出来的，别改）：
    ///   ① **绝对不能碰 "VLC video output"（真正的视频画面窗）** —— 对它 InvalidateRect
    ///      会把 Direct3D11 的画面打乱（实测整块变成一片灰白，等于把视频搞没了）。
    ///      只 Invalidate 它的父窗 "VLC video main" 是安全的（有子窗裁剪，视频区不受影响）。
    ///   ② **不要动 "static" 宿主窗** —— "STATIC" 是系统共享窗口类，
    ///      SetClassLongPtr 改它的背景刷等于把进程里所有 static 控件一起改掉（跨模块副作用）。
    ///      实测只改 "VLC video main" 就够（那个类的名字里带 vout 指针，天生一窗一类，改它零副作用）。
    ///   ③ VLC 换媒体／改分辨率会**重建** vout 窗（类名里的指针变了）→ 修好的刷子会丢，
    ///      所以这里用「看守轮询」而不是一次性设置：每 700ms 扫一遍，发现没打过补丁的视频窗就补上。
    ///      扫的是本进程自己的顶层窗（按 PID 过滤），开销可忽略。
    /// </summary>
    internal static class VideoBackdrop
    {
        private const int GCLP_HBRBACKGROUND = -10;
        private const uint WM_INVALIDATE_ALL = 0;   // InvalidateRect 的 lpRect = NULL（整窗）

        /// <summary>
        /// 每个视频主窗上次见到的尺寸。
        /// ⚠ 为什么必须按尺寸判断（2026-10-18 实测踩到）：VLC 的 vout 窗改尺寸后**不会自动重画整块**
        ///   （窗口类没有 CS_HREDRAW/CS_VREDRAW），新露出来的留白会留着旧像素 = #F0F0F0 白块。
        ///   而"改尺寸"发生在 VLC 的播放线程里，时机比 WPF 布局晚 —— HwndClip 在布局完那一刻擦一次
        ///   可能擦在它前面，之后白块又被画回去。所以交给这个 700ms 看守：尺寸一变就再擦一遍。
        /// </summary>
        private static readonly Dictionary<IntPtr, (int W, int H)> Sizes = new();

        private static IntPtr _blackBrush = IntPtr.Zero;
        private static DispatcherTimer? _timer;

        /// <summary>启动看守（幂等）。在主窗构造末尾调一次即可，全局所有视频窗（含 HDMI 全屏窗）都会被覆盖。</summary>
        public static void Start()
        {
            if (_timer != null) return;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _timer.Tick += (_, _) => Sweep();
            _timer.Start();
            Sweep();
        }

        /// <summary>扫一遍本进程的所有视频窗，把还没处理的补上黑底。可随时手动调（例如刚加了图层）。</summary>
        public static void Sweep()
        {
            try
            {
                uint myPid = (uint)Environment.ProcessId;
                EnumWindows((h, lp) =>
                {
                    GetWindowThreadProcessId(h, out uint pid);
                    if (pid != myPid) return true;          // 只管自己的窗口
                    EnumChildWindows(h, ChildCallback, IntPtr.Zero);
                    return true;
                }, IntPtr.Zero);

                // 顺手清理已销毁的句柄，避免表无限增长（窗口不多，超过阈值才扫）
                if (Sizes.Count > 64)
                {
                    var dead = new List<IntPtr>();
                    foreach (var h in Sizes.Keys) if (!IsWindow(h)) dead.Add(h);
                    foreach (var h in dead) Sizes.Remove(h);
                }
            }
            catch { /* 看守绝不能因异常影响主流程 */ }

            // 同一拍里补裁剪：元素刚建时 Handle 还没生出来、VLC 换媒体会重建 vout 子窗，
            // 这两种情况都只有这个轮询能兜住（见 HwndClip.RetryAll 的注释）。
            HwndClip.RetryAll();
        }

        private static bool ChildCallback(IntPtr hwnd, IntPtr _)
        {
            try
            {
                var cls = GetClassNameString(hwnd);
                // ⚠ 只认 "VLC video main"（留白补底那层）；"VLC video output" 是真正的画面窗，绝不能碰
                if (!cls.StartsWith("VLC video main", StringComparison.Ordinal)) return true;

                if (!GetWindowRect(hwnd, out RECT r)) return true;
                int ww = r.Right - r.Left, hh = r.Bottom - r.Top;
                if (ww <= 0 || hh <= 0) return true;

                bool sizeChanged = !Sizes.TryGetValue(hwnd, out var old) || old.W != ww || old.H != hh;

                if (_blackBrush == IntPtr.Zero) _blackBrush = CreateSolidBrush(0x000000);
                if (_blackBrush == IntPtr.Zero) return true;

                // ⚠ 用「类背景刷是否已经是我们的黑刷」判断，而不是记 hwnd 集合：
                //   窗口销毁后句柄会被复用，靠 HashSet 记"已打过补丁"会把新窗口误判成旧窗口而跳过。
                bool needBrush = GetClassBackground(hwnd) != _blackBrush;
                if (needBrush) SetClassBackground(hwnd, _blackBrush);

                // 换过刷子要立刻重擦（否则要等下次系统擦除才变黑）；尺寸变了也要重擦（见 Sizes 的注释）
                if (needBrush || sizeChanged) InvalidateRect(hwnd, IntPtr.Zero, true);

                Sizes[hwnd] = (ww, hh);
            }
            catch { }
            return true;
        }

        private static string GetClassNameString(IntPtr h)
        {
            var buf = new System.Text.StringBuilder(256);
            int n = GetClassName(h, buf, buf.Capacity);
            return n > 0 ? buf.ToString() : "";
        }

        /// <summary>把窗口所属类的背景刷设成指定刷子（x86 下没有 SetClassLongPtrW，退回 SetClassLongW）。</summary>
        private static void SetClassBackground(IntPtr hwnd, IntPtr brush)
        {
            if (IntPtr.Size == 8) SetClassLongPtrW(hwnd, GCLP_HBRBACKGROUND, brush);
            else SetClassLongW(hwnd, GCLP_HBRBACKGROUND, (uint)brush.ToInt32());
        }

        /// <summary>读当前类背景刷（用来判断"是不是已经被我们换成黑刷了"）。</summary>
        private static IntPtr GetClassBackground(IntPtr hwnd)
        {
            return IntPtr.Size == 8
                ? GetClassLongPtrW(hwnd, GCLP_HBRBACKGROUND)
                : (IntPtr)GetClassLongW(hwnd, GCLP_HBRBACKGROUND);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        // ================= Win32 =================
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint crColor);

        [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW", SetLastError = true)]
        private static extern IntPtr SetClassLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [DllImport("user32.dll", EntryPoint = "SetClassLongW", SetLastError = true)]
        private static extern uint SetClassLongW(IntPtr hWnd, int nIndex, uint dwNewLong);
        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW", SetLastError = true)]
        private static extern IntPtr GetClassLongPtrW(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "GetClassLongW", SetLastError = true)]
        private static extern uint GetClassLongW(IntPtr hWnd, int nIndex);
    }
}
