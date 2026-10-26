using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace Cutlance.Services
{
    /// <summary>
    /// 播出保护（**抓屏时代的过渡补丁**，合成器落地后只在兜底路径生效）。
    ///
    /// ── 为什么需要它 ──────────────────────────────────────────────────────
    /// 抓屏链路（gdigrab 抓「播出框」）抓的是**屏幕像素**而不是"我们的画面"。
    /// 只要那块屏幕被别的窗口盖住，或者主窗被最小化，送出去的画面就变成遮挡物本身。
    ///
    /// ── 现状（2026-10-20）────────────────────────────────────────────────
    /// 合成器已经落地：推流/录制/NDI 默认直接吃内存里的成品帧，跟屏幕无关
    /// （实测主窗最小化后连录 14 秒、420 帧零丢）。所以 MainWindow 挂上来的
    /// active 判据带了「合成器是否为空」—— 合成器在跑时本模块整体不介入，
    /// 免得拿「你被遮挡了」这种已经不成立的理由去闪任务栏、拦最小化。
    /// 只有合成器起不来、退回抓屏兜底时，下面两件事才重新有意义：
    ///   ① 输出期间禁止最小化（避免一次误触把整场直播变成桌面）；
    ///   ② 每 0.5 秒采样播出框，一旦被外部窗口盖住就立刻报警。
    ///
    /// ── 为什么不做"自动置顶" ─────────────────────────────────────────────
    /// 那会变成和老板抢桌面：他拖个窗口过来查资料，我们把它顶下去，再拖再顶。
    /// 而且 SetWindowPos 改 Z 序会和别的程序打架。报警比抢窗口更诚实。
    /// </summary>
    public sealed class BroadcastGuard
    {
        public static BroadcastGuard Instance { get; } = new();

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT p);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder sb, int max);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr hWnd, StringBuilder sb, int max);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        private readonly DispatcherTimer _timer;
        private readonly uint _myPid = (uint)Environment.ProcessId;

        private Func<(double left, double top, double width, double height)>? _rectProvider;
        private Func<bool>? _isLive;

        private bool _live;
        private int _badStreak;
        private string? _blocker;

        // 测试用：设了 CUTLANCE_GUARD_PROBE=x,y,w,h 就改探这块屏幕区域，并强制进入保护态，
        // 这样不用真的开推流也能验证遮挡判定（否则只能等出事才知道它不work）。
        private (double, double, double, double)? _probeRect;

        /// <summary>输出正在跑（推流 / 录制 / NDI 任一）。</summary>
        public bool IsLive => _live;

        /// <summary>输出期间禁止主窗最小化。可在设置里关掉。</summary>
        public bool ProtectMinimize { get; set; } = true;

        /// <summary>是否启用遮挡监控。关掉只剩禁最小化。</summary>
        public bool MonitorOcclusion { get; set; } = true;

        /// <summary>播出框被外部窗口盖住。参数是遮挡者描述（窗口标题 / 进程名）。</summary>
        public event Action<string>? Blocked;

        /// <summary>遮挡消失或输出停止。</summary>
        public event Action? Recovered;

        private BroadcastGuard()
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _timer.Tick += (_, _) => Tick();
        }

        /// <summary>挂上主窗。rectProvider 返回播出框的**屏幕物理像素**矩形。</summary>
        public void Attach(
            Func<(double left, double top, double width, double height)> rectProvider,
            Func<bool> isLive)
        {
            _rectProvider = rectProvider;
            _isLive = isLive;

            var env = Environment.GetEnvironmentVariable("CUTLANCE_GUARD_PROBE");
            if (!string.IsNullOrWhiteSpace(env))
            {
                var parts = env!.Split(',');
                if (parts.Length == 4
                    && double.TryParse(parts[0], out var x)
                    && double.TryParse(parts[1], out var y)
                    && double.TryParse(parts[2], out var w)
                    && double.TryParse(parts[3], out var h))
                {
                    _probeRect = (x, y, w, h);
                }
            }

            if (!_timer.IsEnabled) _timer.Start();
        }

        public void Detach()
        {
            _timer.Stop();
            _rectProvider = null;
            _isLive = null;
            _live = false;
            _badStreak = 0;
            _blocker = null;
        }

        private void Tick()
        {
            bool live;
            if (_probeRect != null)
            {
                live = true;   // 探针模式：强制视为"正在输出"
            }
            else
            {
                try { live = _isLive?.Invoke() ?? false; }
                catch { live = false; }
            }

            if (live != _live)
            {
                _live = live;
                _badStreak = 0;
                if (!live)
                {
                    bool had = _blocker != null;
                    _blocker = null;
                    if (had) Recovered?.Invoke();
                }
            }

            if (!live || !MonitorOcclusion) return;

            var rect = _probeRect;
            if (rect == null)
            {
                try { rect = _rectProvider?.Invoke(); } catch { rect = null; }
            }
            if (rect == null) return;

            var (left, top, width, height) = rect.Value;
            if (width < 32 || height < 32) return;

            // 播出框整块在屏幕外（--offscreen 测试）→ 采样点没有意义，直接跳过，免得刷假警报
            if (left + width < 0 || top + height < 0) return;

            string? who = FindBlocker(left, top, width, height);

            if (who == null)
            {
                _badStreak = 0;
                if (_blocker != null)
                {
                    _blocker = null;
                    Recovered?.Invoke();
                }
                return;
            }

            _badStreak++;
            // 连续两次（约 1 秒）才报：拖窗口闪过、Alt+Tab 预览层这类瞬时覆盖不算
            if (_badStreak >= 2 && _blocker != who)
            {
                _blocker = who;
                Blocked?.Invoke(who);
            }
        }

        /// <summary>3×3 采样：任一点落在别的进程窗口上就算被盖住。</summary>
        private string? FindBlocker(double left, double top, double width, double height)
        {
            double[] fx = { 1.0 / 6, 0.5, 5.0 / 6 };
            double[] fy = { 1.0 / 6, 0.5, 5.0 / 6 };

            foreach (var ry in fy)
            {
                foreach (var rx in fx)
                {
                    var pt = new POINT
                    {
                        X = (int)Math.Round(left + width * rx),
                        Y = (int)Math.Round(top + height * ry)
                    };
                    var hwnd = WindowFromPoint(pt);
                    if (hwnd == IntPtr.Zero) continue;

                    GetWindowThreadProcessId(hwnd, out uint pid);
                    if (pid == _myPid) continue;   // 自家窗口（含 VLC 子窗、叠加层浮窗）不算遮挡

                    return Describe(hwnd, pid);
                }
            }
            return null;
        }

        private static string Describe(IntPtr hwnd, uint pid)
        {
            var cb = new StringBuilder(128);
            GetClassNameW(hwnd, cb, cb.Capacity);
            string cls = cb.ToString();

            if (cls == "Progman" || cls == "WorkerW") return "桌面（主窗可能被最小化了）";
            if (cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd"
                || cls.Contains("TaskSw") || cls.Contains("Tray")) return "任务栏";

            var sb = new StringBuilder(300);
            GetWindowTextW(hwnd, sb, sb.Capacity);
            string title = sb.ToString().Trim();

            if (string.IsNullOrWhiteSpace(title))
            {
                try
                {
                    using var p = Process.GetProcessById((int)pid);
                    return p.ProcessName;
                }
                catch { return "另一个程序"; }
            }

            if (title.Length > 26) title = title.Substring(0, 26) + "…";
            return title;
        }
    }
}
