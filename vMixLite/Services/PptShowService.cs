using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Cutlance.Services
{
    /// <summary>
    /// PPT 进预览：用「抓放映窗口」的方式（老板 2026-10-19 明确选的这条路）。
    ///
    /// 链路：
    ///   ① `POWERPNT.EXE /S "文件"` 启动全屏放映；
    ///   ② 按窗口类名 `screenClass` 找到放映窗口，取其屏幕矩形；
    ///   ③ 输入源用「桌面区域捕获」只抓那块矩形 → 播出的就是放映画面；
    ///   ④ 翻页 = 给放映窗口**投递** PageDown / PageUp（PostMessage，不抢前台、不动焦点）；
    ///   ⑤ 收工 = 投递 Esc 退出放映。
    ///
    /// 为什么翻页用 PostMessage 而不是模拟按键：
    ///   模拟按键（keybd_event / SendInput）打给**当前焦点窗口** —— 老板正在别的窗口打字时
    ///   会把 PageDown 打进他的文档里（2026-10-18 投诉过的正是这类行为）。
    ///   PostMessage 是直接投递到那个窗口的消息队列，与前台无关，也不改变焦点。
    ///
    /// ⚠ 这条路线的固有约束（老板已确认接受）：放映窗口必须**一直可见、不能被别的窗口遮挡**，
    ///   因为抓的是屏幕像素，被挡住就会抓到挡它的那个窗口。
    /// </summary>
    public sealed class PptShowSession
    {
        public Process Process { get; }
        public IntPtr Hwnd { get; set; }
        public int Left { get; set; }
        public int Top { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }

        /// <summary>
        /// 真正持有放映窗口的那个进程号。
        /// ⚠ 不一定等于 <see cref="Process"/>.Id：PowerPoint 已经在运行时，
        ///   `POWERPNT.EXE /S` 起的那个壳会立刻退出、放映窗挂到既有进程里。
        ///   收工时靠这个值判断「这个放映窗是不是我们自己开的」，避免误杀老板正在编辑的稿子。
        /// </summary>
        public int HostPid { get; set; }

        /// <summary>我们自己 Process.Start 出来的那个壳进程号（可能已退出）。</summary>
        public int LaunchedPid { get; set; }

        internal PptShowSession(Process p) { Process = p; }

        public bool Next() => PptShowService.PostKey(Hwnd, VkNext);
        public bool Prev() => PptShowService.PostKey(Hwnd, VkPrior);
        public bool ExitShow() => PptShowService.PostKey(Hwnd, VkEscape);

        internal const int VkNext = 0x22;    // PageDown
        internal const int VkPrior = 0x21;   // PageUp
        internal const int VkEscape = 0x1B;  // Esc
    }

    public static class PptShowService
    {
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private const string ShowWindowClass = "screenClass";   // PowerPoint 全屏放映窗口的类名

        /// <summary>找本机的 PowerPoint 可执行文件（App Paths → 常见安装目录 → PATH）。</summary>
        public static string? FindPowerPointExe()
        {
            try
            {
                var v = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\POWERPNT.EXE",
                    null, null) as string;
                if (!string.IsNullOrWhiteSpace(v) && File.Exists(v)) return v;
            }
            catch { }

            foreach (var root in new[] { @"C:\Program Files\Microsoft Office\root\Office16",
                                         @"C:\Program Files (x86)\Microsoft Office\root\Office16" })
            {
                var p = Path.Combine(root, "POWERPNT.EXE");
                if (File.Exists(p)) return p;
            }
            return null;   // 交给调用方给提示
        }

        /// <summary>启动全屏放映，并等放映窗口出现（最长 timeoutMs）。失败返回 null。</summary>
        public static PptShowSession? StartShow(string pptPath, int timeoutMs = 20000)
        {
            var exe = FindPowerPointExe()
                      ?? throw new InvalidOperationException("这台电脑没有找到 PowerPoint（POWERPNT.EXE）。");

            // 记下启动前已经在跑的 PowerPoint，用来判断「这个放映窗是不是我们开的」
            var before = new HashSet<int>();
            try
            {
                foreach (var p in Process.GetProcessesByName("POWERPNT")) before.Add(p.Id);
            }
            catch { }

            var psi = new ProcessStartInfo(exe, "/S \"" + pptPath + "\"")
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(pptPath) ?? Environment.CurrentDirectory,
            };
            var proc = Process.Start(psi);
            if (proc == null) return null;

            var session = new PptShowSession(proc) { LaunchedPid = proc.Id };
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            // 第一程：按自己的 PID 找（自己起的实例，最准）
            var pidDeadline = DateTime.UtcNow.AddMilliseconds(Math.Min(6000, timeoutMs));
            while (DateTime.UtcNow < pidDeadline)
            {
                if (TryLocateShowWindow(proc.Id, session, out _))
                {
                    session.HostPid = proc.Id;
                    return session;
                }
                Thread.Sleep(250);
            }

            // 第二程：PowerPoint 本来就在跑（`/S` 会挂到既有进程），或者壳进程已经换掉 ——
            // 这时候按 PID 找永远是空的，改成「全桌面找那个铺满屏幕的放映窗」。
            while (DateTime.UtcNow < deadline)
            {
                if (FindAnyShowWindow(session, out int hostPid, out _))
                {
                    // 只认「启动前不存在」的 PowerPoint 进程，避免抓到老板原本就在放的另一个放映
                    if (!before.Contains(hostPid))
                    {
                        session.HostPid = hostPid;
                        return session;
                    }
                }
                Thread.Sleep(250);
            }
            return null;   // 放映窗口没等到（可能文件损坏 / Office 弹了确认框）
        }

        /// <summary>全桌面范围内找放映窗口（类名 screenClass，取面积最大的那个）。</summary>
        public static bool FindAnyShowWindow(PptShowSession session, out int hostPid, out string note)
        {
            note = "";
            IntPtr best = IntPtr.Zero;
            long bestArea = 0;
            int bestPid = 0;

            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h)) return true;
                var cls = new StringBuilder(256);
                GetClassName(h, cls, cls.Capacity);
                if (!string.Equals(cls.ToString(), ShowWindowClass, StringComparison.OrdinalIgnoreCase)) return true;
                if (!GetWindowRect(h, out var r)) return true;

                long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                if (area <= bestArea) return true;

                GetWindowThreadProcessId(h, out uint wpid);
                bestArea = area;
                best = h;
                bestPid = (int)wpid;
                return true;
            }, IntPtr.Zero);

            hostPid = bestPid;
            if (best == IntPtr.Zero) return false;

            GetWindowRect(best, out var rect);
            session.Hwnd = best;
            session.Left = rect.Left;
            session.Top = rect.Top;
            session.Width = Math.Max(1, rect.Right - rect.Left);
            session.Height = Math.Max(1, rect.Bottom - rect.Top);
            note = $"{session.Width}×{session.Height} @ ({session.Left},{session.Top})";
            return true;
        }

        /// <summary>按进程找放映窗口并刷新它的屏幕矩形。找到了返回 true。</summary>
        public static bool TryLocateShowWindow(int pid, PptShowSession session, out string note)
        {
            note = "";
            IntPtr best = IntPtr.Zero;
            long bestArea = 0;
            IntPtr fallback = IntPtr.Zero;
            long fallbackArea = 0;

            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h)) return true;
                GetWindowThreadProcessId(h, out uint wpid);
                if (wpid != pid) return true;
                if (!GetWindowRect(h, out var r)) return true;

                long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                if (area <= 0) return true;

                var cls = new StringBuilder(256);
                GetClassName(h, cls, cls.Capacity);
                if (string.Equals(cls.ToString(), ShowWindowClass, StringComparison.OrdinalIgnoreCase))
                {
                    if (area > bestArea) { bestArea = area; best = h; }
                }
                // 兜底候选：不同 Office 版本 / 多显示器时类名可能变，凡是铺满一大块屏幕的
                // 顶层窗口都算放映窗（放映窗一定接近整屏）。编辑窗有功能区，通常不会铺满。
                if (area > fallbackArea && area > 800L * 600L)
                {
                    fallbackArea = area;
                    fallback = h;
                }
                return true;
            }, IntPtr.Zero);

            if (best == IntPtr.Zero) best = fallback;
            if (best == IntPtr.Zero) return false;

            GetWindowRect(best, out var rect);
            GetWindowThreadProcessId(best, out uint owner);
            session.Hwnd = best;
            session.HostPid = (int)owner;
            session.Left = rect.Left;
            session.Top = rect.Top;
            session.Width = Math.Max(1, rect.Right - rect.Left);
            session.Height = Math.Max(1, rect.Bottom - rect.Top);
            note = $"{session.Width}×{session.Height} @ ({session.Left},{session.Top})";
            return true;
        }

        /// <summary>向指定窗口投递一次按键（按下 + 抬起），不动前台、不改焦点。</summary>
        internal static bool PostKey(IntPtr hWnd, int vk)
        {
            if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return false;
            uint sc = MapVirtualKey((uint)vk, 0);
            IntPtr down = (IntPtr)(1 | (int)(sc << 16));
            IntPtr up = (IntPtr)unchecked((int)0xC0000001 | (int)(sc << 16));
            PostMessage(hWnd, 0x0100, (IntPtr)vk, down);   // WM_KEYDOWN
            PostMessage(hWnd, 0x0101, (IntPtr)vk, up);     // WM_KEYUP
            return true;
        }

        /// <summary>
        /// 退出放映（只在用户删掉这一路输入 / 关程序时调用）。
        /// ⚠ 安全红线：只退放映，绝不乱杀进程。PowerPoint 可能是老板本来开着的、
        ///   里面还有没保存的稿子 —— 所以只有「进程是我们自己起的」才允许 Close/Kill。
        /// </summary>
        public static void Close(PptShowSession? s)
        {
            if (s == null) return;

            // 1) 先投递 Esc 让放映退出（不动前台、不动焦点）
            try { s.ExitShow(); } catch { }

            // 2) 等放映窗消失。PowerPoint 用 /S 放映结束后会自己退，这一步通常就够了
            var deadline = DateTime.UtcNow.AddMilliseconds(4000);
            while (DateTime.UtcNow < deadline)
            {
                if (s.Hwnd == IntPtr.Zero || !IsWindow(s.Hwnd)) return;
                Thread.Sleep(200);
            }

            // 3) 放映窗还在 —— 只有确认这个放映窗是我们自己起的进程持有的，才强退
            bool ours = s.HostPid != 0 && s.HostPid == s.LaunchedPid;
            if (!ours) return;   // 别人（或老板原本开着）的 PowerPoint，到此为止，交给用户自己关

            try
            {
                var p = s.Process;
                if (p != null && !p.HasExited)
                {
                    p.CloseMainWindow();
                    if (!p.WaitForExit(2000)) p.Kill();
                }
            }
            catch { }
        }
    }
}
