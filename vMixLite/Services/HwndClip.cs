using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Cutlance.Services
{
    /// <summary>
    /// 用 Win32 窗口区域（SetWindowRgn）把 HwndHost 里的视频子窗裁到指定矩形内。
    ///
    /// 为什么只能这么干（2026-10-18 实测结论，别再回头找 WPF 的办法）：
    ///   · 视频是 HwndHost 包出来的 Win32 子窗口，按 Windows 规则永远画在 WPF 内容之上（airspace）；
    ///   · **WPF 的 ClipToBounds 对 HwndHost 子窗完全无效** —— 预览框一直带着 ClipToBounds="True"，
    ///     把画面拖出框外照样糊到隔壁「输出」框上（截图铁证：红色块直接压在输出框里）；
    ///   · 给 LayersCanvas 加 ClipToBounds 还会让进程几秒后崩溃（见 MainWindow.xaml 里的告诫）。
    /// 所以「取景框」语义只能落到窗口区域这一层。
    /// </summary>
    public static class HwndClip
    {
        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);

        /// <summary>成功时系统接管 hRgn 所有权（不要再 DeleteObject）。hRgn = 0 表示取消区域。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

        /// <summary>
        /// 缓存键。⚠ 必须带上**元素尺寸 (W,H)**，这是 2026-10-18 抓到的一个真 bug：
        /// 缓存键只比「可见矩形 local + 子窗签名」时，从 100% 放大到 400% 会**整刀被跳过** ——
        /// 因为 local 始终是「元素左上角算起的整个取景框」(0,0,画布宽,画布高)，
        /// 元素尺寸变了但 local 一个数都不变，于是「上次已裁」的判断成立，区域永远不重设。
        /// 表现出来就是：手输 400% 后画面直接糊到制作条/右栏/精调面板上，界面上再也改不回来。
        /// </summary>
        private sealed class State
        {
            public Rect Last;
            public bool Has;
            public long Sig;
            public double W, H;
        }

        // 按元素记上次施加的区域：拖动时 RenderPreview/RenderLayer 会跑满帧，
        // 不比较就去调 SetWindowRgn 的话每秒几十次重设窗口区域，画面会抖。
        private static readonly ConditionalWeakTable<FrameworkElement, State> States = new();

        private static readonly PropertyInfo? HandleProp = typeof(HwndHost).GetProperty(
            "Handle", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        /// <summary>
        /// 「已知要裁的元素 + 它当前的可见矩形」。RetryAll 靠这张表补漏。
        ///
        /// 为什么必须有这张表（2026-10-18 定位到的真 bug）：
        ///   Apply() 第一次被调用时，元素刚 Add 进画布、WPF 还没布局，HwndHost 的 Handle 还是 0
        ///   → 只能 return，而**没有任何人去调第二次**。于是"加载工程 / 新建图层"这种
        ///   一次成型的场景，裁剪从来没生效过；只有手动拖一下图层才会碰巧补上。
        ///   这就是"超出输出框的部分还看得见"的主因。
        /// </summary>
        private sealed class Entry
        {
            public readonly WeakReference<FrameworkElement> Host;
            public Rect Visible;               // 画布坐标系下的可见矩形（完整取景框）
            public Entry(FrameworkElement host, Rect visible)
            {
                Host = new WeakReference<FrameworkElement>(host);
                Visible = visible;
            }
        }
        private static readonly List<Entry> Registry = new();

        /// <summary>
        /// 把宿主裁到 visibleInCanvas（**画布坐标系**下的可见矩形）。
        /// visibleInCanvas 覆盖整个元素时等同"不裁"（会把窗口区域清掉）。
        /// </summary>
        public static void Apply(FrameworkElement? host, Rect visibleInCanvas)
        {
            if (host == null) return;

            // 登记下来，等原生子窗建好之后由 RetryAll 补这一刀
            bool found = false;
            for (int i = 0; i < Registry.Count; i++)
            {
                if (Registry[i].Host.TryGetTarget(out var t) && ReferenceEquals(t, host))
                {
                    Registry[i].Visible = visibleInCanvas;
                    found = true;
                    break;
                }
            }
            if (!found) Registry.Add(new Entry(host, visibleInCanvas));

            ApplyCore(host, visibleInCanvas);
        }

        /// <summary>元素尺寸/位置重建后强制重设一次（缓存会挡住重复调用）。</summary>
        public static void Invalidate(FrameworkElement? host)
        {
            if (host == null) return;
            if (States.TryGetValue(host, out var st)) st.Has = false;
        }

        /// <summary>
        /// 把所有登记过的元素重放一遍。**由看守轮询（700ms）调**，兜住三类必然漏掉的情况：
        ///   ① 元素刚建好时 Handle 还没生出来（加载工程、新建图层）；
        ///   ② VLC 换媒体/改分辨率会**重建** vout 子窗，新窗口没有区域；
        ///   ③ 画布尺寸变化后位置漂移。
        /// 有 State 缓存挡着，矩形没变的元素不会真的去调 SetWindowRgn，开销可忽略。
        /// </summary>
        public static void RetryAll()
        {
            if (Registry.Count == 0) return;
            for (int i = Registry.Count - 1; i >= 0; i--)
            {
                var e = Registry[i];
                if (!e.Host.TryGetTarget(out var fe)) { Registry.RemoveAt(i); continue; }
                // 已经从可视树摘掉（图层被删除）→ 不再管它
                if (fe.Parent == null) { Registry.RemoveAt(i); continue; }
                ApplyCore(fe, e.Visible);
            }
        }

        /// <summary>把某个元素从登记表里移除（图层/预览槽销毁时调）。</summary>
        public static void Forget(FrameworkElement? host)
        {
            if (host == null) return;
            for (int i = Registry.Count - 1; i >= 0; i--)
            {
                if (!Registry[i].Host.TryGetTarget(out var t) || ReferenceEquals(t, host))
                    Registry.RemoveAt(i);
            }
        }

        private static void ApplyCore(FrameworkElement host, Rect visibleInCanvas)
        {
            var hwnd = FindHwnd(host);
            if (hwnd == IntPtr.Zero) return;          // 原生子窗还没建出来，下次再试（不写缓存）

            // 先收掉 VideoView 那个「前景窗」—— 见 HideForegroundOverlay 的注释
            HideForegroundOverlay(hwnd);

            double ex = Canvas.GetLeft(host), ey = Canvas.GetTop(host);
            if (double.IsNaN(ex)) ex = 0;
            if (double.IsNaN(ey)) ey = 0;

            // 显式 Width/Height 优先：拖动中刚改完尺寸，布局还没跑，ActualWidth 会慢一帧
            double w = !double.IsNaN(host.Width) ? host.Width : host.ActualWidth;
            double h = !double.IsNaN(host.Height) ? host.Height : host.ActualHeight;
            if (w <= 0.5 || h <= 0.5) return;

            // 画布坐标 → 宿主自己的客户区坐标（宿主左上角 = 元素左上角）
            var local = new Rect(visibleInCanvas.X - ex, visibleInCanvas.Y - ey,
                                 visibleInCanvas.Width, visibleInCanvas.Height);
            local.Intersect(new Rect(0, 0, w, h));

            var tree = Tree(hwnd);

            // 缓存键 = 可见矩形 + **子窗集合签名**。
            // 只比矩形是不够的：VLC 换媒体/改分辨率会重建 vout 子窗（新窗口没有区域），
            // 而元素矩形一点没变 —— 只看矩形就会把这一刀永久跳过，框外又开始溢出。
            long sig = 17;
            unchecked
            {
                foreach (var (ch, _, _) in tree) sig = sig * 31 + ch.ToInt64();
            }

            var st = States.GetOrCreateValue(host);
            // 尺寸也必须参与比较（见 State 的注释：只比 local 会让「放大」这一步永久漏裁）
            bool sizeChanged = !st.Has || Math.Abs(st.W - w) > 0.5 || Math.Abs(st.H - h) > 0.5;
            if (st.Has && st.Sig == sig && Same(st.Last, local) && !sizeChanged) return;

            if (!ApplyRegion(hwnd, local, w, h, tree)) return;
            st.Last = local;
            st.Sig = sig;
            st.W = w;
            st.H = h;
            st.Has = true;

            // ⚠ 改尺寸后必须再补一次**延迟**重绘（2026-10-18 实测）：
            //   这一帧里 HwndHost 的原生子窗还没跟着 WPF 布局改大小，SetWindowRgn 之后 VLC 才把窗口拉大、
            //   并把自己的空白底（#F0F0F0）铺满整块 —— 于是刚才那次重绘白做，屏幕上留下一大片白。
            //   等布局跑完（Loaded 优先级）再擦一刀就干净了：实测擦完 3 秒后仍是正常深色，不会被重画回来。
            if (sizeChanged)
            {
                var target = hwnd;
                try
                {
                    host.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        RepaintParent(target);
                        EraseLetterbox(target);
                    }), System.Windows.Threading.DispatcherPriority.Loaded);
                }
                catch { }
            }
        }

        private static bool ApplyRegion(IntPtr hostHwnd, Rect local, double w, double h,
                                        List<(IntPtr Hwnd, int Dx, int Dy)> tree)
        {
            // 区域坐标要的是**物理像素**，而 WPF 布局是 DIP（本机 150% → 差 1.5 倍）
            var dpi = GetDpi(hostHwnd);
            int l = (int)Math.Round(local.Left * dpi.X);
            int t = (int)Math.Round(local.Top * dpi.Y);
            int r = (int)Math.Round(local.Right * dpi.X);
            int b = (int)Math.Round(local.Bottom * dpi.Y);

            bool full = l <= 0 && t <= 0 && r >= w * dpi.X - 1 && b >= h * dpi.Y - 1;
            // 完全在取景框外 → 给空区域（窗口彻底不可见）。
            // ⚠ 不能像早期那样直接 return：那样会保留上一刀的旧区域，画面就"冻"在最后可见的位置。
            bool outside = !full && (r <= l || b <= t);

            bool any = false;

            // ⚠ 必须**整棵子树一起裁**（宿主 Static + 里面的 VLC video main + VLC video output）。
            //   只裁一层会漏，而且以前"不碰画面窗"那条结论是错的（2026-10-18 实测更正）：
            //   **子窗口不受父窗口 SetWindowRgn 的约束** —— 只裁 Static 时，3306×1860 的
            //   "VLC video output" 照旧把它的空白底（#F0F0F0）铺满整个工作区，就是老板看到的白块。
            //   实测给画面窗也设同样的矩形区域后，Direct3D11 画面照常播放（无花屏/无掉帧），
            //   所以这里三层全裁。区域坐标要按各自相对宿主的偏移平移（Tree 已算好 dx/dy）。
            foreach (var (hwnd, dx, dy) in tree)
            {
                IntPtr rgn;
                if (full) rgn = IntPtr.Zero;                       // 0 = 恢复默认区域（整窗可见）
                else if (outside) rgn = CreateRectRgn(0, 0, 0, 0); // 空区域 = 不可见
                // ⚠ 偏移要**减**不是加（2026-10-18 抓到的真 bug，就是"放大后画面消失"的根因）：
                //   Tree() 给的 dx/dy 是「子窗原点 − 宿主原点」，而 l/t/r/b 是**宿主局部坐标**。
                //   宿主局部 → 子窗局部 的换算是 `减`；写成 `加` 会把区域整体推到窗口外
                //   （实测 2× 时 VLC video output 偏移 791px，区域被加到了 1368..3099，
                //    而那个窗口只有 726 高 → 整窗变空区域 → 画面彻底不见，只剩黑边）。
                else rgn = CreateRectRgn(l - dx, t - dy, r - dx, b - dy);
                if (!full && rgn == IntPtr.Zero) continue;
                // 画面窗不重绘（避免惊动 D3D），其余让它立刻重画擦掉框外残影
                if (SetWindowRgn(hwnd, rgn, true) != 0) any = true;
            }

            // 区域一变，**父窗口被"新露出来"的那一块不会自己重绘** —— 那儿留着上一帧的画面，
            // 表现出来就是"框外还有一条残影/白块"。主动 invalidate 父窗口这一块，让 WPF 重画背景。
            // ⚠ 只 invalidate 不带 RDW_ALLCHILDREN：带上会把子窗口（D3D 画面）一起打回去重绘。
            RepaintParent(hostHwnd);
            return any;
        }

        /// <summary>让父窗口重绘「宿主所占的那一整块」—— 被裁掉的部分就该由它来擦。</summary>
        private static void RepaintParent(IntPtr hostHwnd)
        {
            try
            {
                var parent = GetParent(hostHwnd);
                if (parent == IntPtr.Zero) return;
                if (!GetWindowRect(hostHwnd, out RECT hr)) return;
                var pt = new POINT { X = hr.Left, Y = hr.Top };
                if (!ScreenToClient(parent, ref pt)) return;
                var rc = new RECT
                {
                    Left = pt.X,
                    Top = pt.Y,
                    Right = pt.X + (hr.Right - hr.Left),
                    Bottom = pt.Y + (hr.Bottom - hr.Top)
                };
                // ⚠ 必须带 RDW_UPDATENOW（2026-10-18 实测）：
                //   只发 INVALIDATE 时 WPF 会把这条 WM_PAINT 当成"我没什么要画的"（渲染树没变）而跳过，
                //   于是被裁掉那块的**旧像素就留在屏幕上** —— 表现为一整片 #F0F0F0 白色方块盖住制作条/右栏/精调面板，
                //   而且会一直留着（等用户点一下别处才可能被擦掉）。带上 UPDATENOW 才会真的擦干净。
                //   代价：只在"可见矩形真的变了"时才走到这里（有 State 缓存挡着），不是每帧都刷。
                RedrawWindow(parent, ref rc, IntPtr.Zero,
                    RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_UPDATENOW);
            }
            catch { /* 重绘失败不影响裁剪本身 */ }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        private const uint RDW_INVALIDATE = 0x0001;
        private const uint RDW_ERASE = 0x0004;
        private const uint RDW_FRAME = 0x0400;
        private const uint RDW_UPDATENOW = 0x0100;

        [DllImport("user32.dll")]
        private static extern bool RedrawWindow(IntPtr hWnd, ref RECT lprcUpdate, IntPtr hrgnUpdate, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

        /// <summary>
        /// 重画「留白区」（信箱边）。
        ///
        /// 画面比例 ≠ 画框比例时（典型：16:9 信号进竖屏 9:16 槽），视频只占中间一条，
        /// 上下留白由 <c>VLC video main</c> 那层补底 —— 它的类背景刷已被 VideoBackdrop 换成黑色。
        /// 但 VLC 的窗口类没有 CS_HREDRAW/CS_VREDRAW：**改尺寸后不会自动重画整块**，
        /// 新露出来的那截留白就留着旧像素（实测是 #F0F0F0 白块，竖屏切过去特别刺眼）。
        /// 所以尺寸一变，就在这里把 VLC video main 整窗擦一遍（erase=true 才会用黑刷子填）。
        /// ⚠ 只擦 "VLC video main"；"VLC video output" 是真画面窗，动它会打乱 D3D（VideoBackdrop 的告诫）。
        /// </summary>
        private static void EraseLetterbox(IntPtr hostHwnd)
        {
            try
            {
                EnumChildWindows(hostHwnd, (h, _) =>
                {
                    var c = new System.Text.StringBuilder(256);
                    GetClassNameW(h, c, 256);
                    if (c.ToString().StartsWith("VLC video main", StringComparison.Ordinal))
                        InvalidateRect(h, IntPtr.Zero, true);
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
        }

        [DllImport("user32.dll")]
        private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

        /// <summary>宿主 hwnd → 它的 VideoView 前景窗 hwnd（避免每帧全桌面 EnumWindows）。</summary>
        private static readonly Dictionary<IntPtr, IntPtr> Overlays = new();

        /// <summary>
        /// 收掉 LibVLCSharp.WPF 的「前景窗」（顶层窗口，标题就是 <c>LibVLCSharp.WPF</c>）。
        ///
        /// 为什么必须处理它（2026-10-18 实测，这是「放大后白块盖住整个界面」的真凶）：
        ///   · <c>VideoView</c> 的模板里除了视频子窗，还有一个 **ForegroundWindow** ——
        ///     它是**顶层窗口**（owner = 主窗），不在宿主子窗树里，所以早先按子窗裁剪时根本碰不到它；
        ///   · 它建出来就带 WPF 默认的 #F0F0F0 底色：元素是 400% 时，这块白就把制作条、右栏、
        ///     连精调面板一起糊住，用户连「改回去」的按钮都点不到（导播软件里这是事故级）。
        ///   · 它的扩展样式是 **WS_EX_LAYERED**，`SetWindowRgn` 对分层窗无效（实测设了返回 1 但不生效）。
        /// 我们的预览槽/图层**从不使用 VideoView.Content**（画面上不叠任何 WPF 内容），
        /// 所以这个前景窗纯属负担 —— 直接 ShowWindow(SW_HIDE) 收掉，画面反而更干净。
        /// 换媒体/改分辨率时 VLC 可能重建窗口，所以 RetryAll 的轮询会再来收一遍。
        /// </summary>
        private static void HideForegroundOverlay(IntPtr hostHwnd)
        {
            if (hostHwnd == IntPtr.Zero) return;

            if (Overlays.TryGetValue(hostHwnd, out var known))
            {
                if (IsWindow(known))
                {
                    if (IsWindowVisible(known)) ShowWindow(known, SW_HIDE);
                    return;
                }
                Overlays.Remove(hostHwnd);      // 窗口重建了，重新找
            }

            if (!GetWindowRect(hostHwnd, out RECT hr)) return;
            GetWindowThreadProcessId(hostHwnd, out uint pid);

            IntPtr found = IntPtr.Zero;
            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out uint p);
                if (p != pid || !IsWindowVisible(h)) return true;
                var title = new System.Text.StringBuilder(64);
                GetWindowTextW(h, title, 64);
                if (title.ToString() != "LibVLCSharp.WPF") return true;
                if (!GetWindowRect(h, out RECT r)) return true;
                // 同一个 VideoView 的前景窗与视频宿主左上角完全重合
                if (Math.Abs(r.Left - hr.Left) > 2 || Math.Abs(r.Top - hr.Top) > 2) return true;
                found = h;
                return false;
            }, IntPtr.Zero);

            if (found == IntPtr.Zero) return;
            Overlays[hostHwnd] = found;
            ShowWindow(found, SW_HIDE);
        }

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr hwnd, System.Text.StringBuilder buf, int max);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hwnd, int cmd);

        private const int SW_HIDE = 0;

        /// <summary>宿主窗口 + 全部子窗口，附带各自相对宿主的像素偏移。</summary>
        private static List<(IntPtr Hwnd, int Dx, int Dy)> Tree(IntPtr host)
        {
            var list = new List<(IntPtr, int, int)>();
            if (!GetWindowRect(host, out RECT hr)) return list;

            void Take(IntPtr h)
            {
                if (GetWindowRect(h, out RECT rr))
                    list.Add((h, rr.Left - hr.Left, rr.Top - hr.Top));
            }

            Take(host);
            EnumChildWindows(host, (h, _) => { Take(h); return true; }, IntPtr.Zero);
            return list;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr hwnd, System.Text.StringBuilder buf, int max);

        /// <summary>取宿主窗口所在显示器的 DPI 缩放（窗口级 API，比全局 DPI 稳）。</summary>
        private static (double X, double Y) GetDpi(IntPtr hwnd)
        {
            try
            {
                uint dpi = GetDpiForWindow(hwnd);
                if (dpi > 0) return (dpi / 96.0, dpi / 96.0);
            }
            catch { }
            return (1.0, 1.0);
        }

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        /// <summary>取元素里那个视频原生子窗（找不到返回 0）。供 Z 序同步等外部逻辑用。</summary>
        public static IntPtr HandleOf(FrameworkElement? host) => host == null ? IntPtr.Zero : FindHwnd(host);

        private static IntPtr FindHwnd(FrameworkElement root)
        {
            var hh = FindHwndHost(root);
            if (hh == null || HandleProp == null) return IntPtr.Zero;
            try
            {
                object? v = HandleProp.GetValue(hh);
                return v is IntPtr p ? p : IntPtr.Zero;
            }
            catch { return IntPtr.Zero; }
        }

        /// <summary>在可视树里找 HwndHost —— LibVLCSharp 的 VideoView 是 ContentControl，真正建窗的是里面的 VideoHwndHost。</summary>
        private static HwndHost? FindHwndHost(DependencyObject root)
        {
            if (root is HwndHost h) return h;
            int n;
            try { n = VisualTreeHelper.GetChildrenCount(root); } catch { return null; }
            for (int i = 0; i < n; i++)
            {
                var c = FindHwndHost(VisualTreeHelper.GetChild(root, i));
                if (c != null) return c;
            }
            return null;
        }

        private static bool Same(Rect a, Rect b)
            => Math.Abs(a.X - b.X) < 0.5 && Math.Abs(a.Y - b.Y) < 0.5
            && Math.Abs(a.Width - b.Width) < 0.5 && Math.Abs(a.Height - b.Height) < 0.5;
    }
}
