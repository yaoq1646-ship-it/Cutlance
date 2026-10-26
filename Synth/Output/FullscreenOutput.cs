using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SynthPoc.Output
{
    /// <summary>
    /// P2 全屏输出：把成品帧铺满指定显示器，等价于 vMix 的"全屏输出到第二屏"。
    ///
    /// 为什么单独开一个线程：
    ///   主界面（预览窗）跟这个全屏窗如果共用一个 UI 线程，
    ///   主界面一卡（拖窗口、开菜单、.NET 进 GC），全屏输出就跟着卡 —— 那是事故。
    ///   所以这里起独立的 STA 线程 + 独立 Dispatcher，主界面怎么折腾都不影响它出画。
    ///
    /// 帧的传递：
    ///   合成线程 memcpy 到自己的缓冲（不占合成器的锁），再 BeginInvoke 通知窗口线程上传。
    ///   全屏窗跟不上就丢这一帧（丢显示帧不影响推流/录制，那两条走各自的队列）。
    /// </summary>
    internal sealed class FullscreenOutput : IDisposable
    {
        private readonly int _w, _h;
        private readonly int _monitorIndex;
        private readonly bool _topmost;
        private readonly int[]? _overrideRect;   // 自动化测试用：x,y,w,h（放到屏幕外，不干扰老板）
        private readonly int _bytes;

        private Thread? _thread;
        private Dispatcher? _disp;
        private Window? _win;
        private WriteableBitmap? _bmp;

        // 双缓冲：合成线程写 free，窗口线程读 ready。用 2 块就够，多了白占内存。
        private readonly ConcurrentBag<byte[]> _free = new();
        private byte[]? _ready;
        private readonly object _gate = new();
        private int _pending;
        private bool _disposed;

        private long _shown, _skipped;

        public string MonitorName { get; private set; } = string.Empty;
        public string MonitorRect { get; private set; } = string.Empty;
        public long Shown => Interlocked.Read(ref _shown);
        public long Skipped => Interlocked.Read(ref _skipped);
        public bool IsOpen => _win != null;

        public FullscreenOutput(int w, int h, int monitorIndex = 1, bool topmost = true, int[]? overrideRect = null)
        {
            _w = w; _h = h; _monitorIndex = monitorIndex; _topmost = topmost;
            _overrideRect = (overrideRect != null && overrideRect.Length == 4) ? overrideRect : null;
            _bytes = w * h * 4;
            for (int i = 0; i < 2; i++) _free.Add(new byte[_bytes]);
        }

        /// <summary>在独立线程上建窗并显示。返回 false = 线程没起来（极少见）。</summary>
        public bool Open(int timeoutMs = 4000)
        {
            if (_thread != null) return true;

            var ready = new ManualResetEventSlim(false);
            var th = new Thread(() => ThreadMain(ready))
            {
                IsBackground = true,
                Name = "FullscreenOut"
            };
            th.SetApartmentState(ApartmentState.STA);
            _thread = th;
            _thread.Start();

            ready.Wait(timeoutMs);
            return _bmp != null;
        }

        private void ThreadMain(ManualResetEventSlim ready)
        {
            try
            {
                _disp = Dispatcher.CurrentDispatcher;

                var screens = ScreenInfo.All();
                var scr = (screens.Count == 0)
                    ? new ScreenInfo { X = 0, Y = 0, W = 1920, H = 1080, Name = "主屏" }
                    : screens[Math.Clamp(_monitorIndex, 0, screens.Count - 1)];

                if (_overrideRect != null)
                {
                    scr = new ScreenInfo
                    {
                        X = _overrideRect[0], Y = _overrideRect[1],
                        W = _overrideRect[2], H = _overrideRect[3],
                        Name = "测试定位（屏幕外）"
                    };
                }

                MonitorName = scr.Name;
                MonitorRect = $"{scr.X},{scr.Y} {scr.W}x{scr.H}";

                _bmp = new WriteableBitmap(_w, _h, 96, 96, PixelFormats.Bgr32, null);

                var img = new Image
                {
                    Source = _bmp,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.Linear);

                var host = new Grid { Background = Brushes.Black };
                host.Children.Add(img);

                _win = new Window
                {
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    Background = Brushes.Black,
                    Content = host,
                    Title = "SynthPoc 全屏输出",
                    Topmost = _topmost
                };

                _win.Show();

                // WPF 的 Left/Width 是 DIP，跨 DPI 的显示器上一换算就偏；
                // 直接按物理像素 SetWindowPos，并把 NOACTIVATE 带上，绝不抢老板焦点。
                var hwnd = new WindowInteropHelper(_win).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    SetWindowPos(hwnd, _topmost ? HWND_TOPMOST : HWND_NOTOPMOST,
                                 scr.X, scr.Y, scr.W, scr.H,
                                 SWP_NOACTIVATE | SWP_SHOWWINDOW);
                }

                ready.Set();
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                Trace.Log("全屏输出线程异常: " + ex);
                ready.Set();
            }
        }

        /// <summary>合成线程调用：把当前成品帧交给全屏窗。绝不允许阻塞合成线程。</summary>
        public void Present(IntPtr src, int stride)
        {
            if (_disposed || _bmp == null || _disp == null) return;

            if (!_free.TryTake(out var buf))
            {
                Interlocked.Increment(ref _skipped);
                return;
            }

            try
            {
                Marshal.Copy(src, buf, 0, _bytes);
            }
            catch
            {
                _free.Add(buf);
                return;
            }

            // 上一帧还没被消费掉就覆盖它：全屏显示只要最新帧，旧帧没有保留价值
            lock (_gate) _ready = buf;

            if (Interlocked.CompareExchange(ref _pending, 1, 0) == 0)
            {
                try
                {
                    _disp.BeginInvoke(DispatcherPriority.Render, new Action(Upload));
                }
                catch
                {
                    Volatile.Write(ref _pending, 0);
                }
            }
        }

        private void Upload()
        {
            byte[]? buf = null;
            try
            {
                lock (_gate) { buf = _ready; _ready = null; }
                if (buf == null) return;

                var bmp = _bmp;
                if (bmp == null) return;

                // 注意重载：byte[] 版的签名是 (rect, buffer, stride, offset)，
                // 不是 (rect, buffer, bufferSize, stride)。传错会拿 8294400 当 stride 去算 length，直接溢出。
                bmp.WritePixels(new Int32Rect(0, 0, _w, _h), buf, _w * 4, 0);
                Interlocked.Increment(ref _shown);
            }
            catch (Exception ex)
            {
                Trace.Log("全屏输出上传异常: " + ex.Message);
            }
            finally
            {
                if (buf != null) _free.Add(buf);
                Volatile.Write(ref _pending, 0);
            }
        }

        public void Close()
        {
            var disp = _disp;
            if (disp == null) return;
            try
            {
                disp.BeginInvoke(new Action(() =>
                {
                    try { _win?.Close(); } catch { }
                    disp.BeginInvokeShutdown(DispatcherPriority.Background);
                }));
            }
            catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Close();
            try { _thread?.Join(2500); } catch { }
            _bmp = null;
            _win = null;
        }

        // ── Win32 ────────────────────────────────────────────────────────────
        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new(-2);
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    }

    /// <summary>显示器矩形（物理像素）。用 EnumDisplayMonitors 而不是 WinForms 的 Screen，避免多引一个程序集。</summary>
    internal sealed class ScreenInfo
    {
        public int X, Y, W, H;
        public string Name = string.Empty;
        public bool Primary;

        public static List<ScreenInfo> All()
        {
            var list = new List<ScreenInfo>();
            try
            {
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data) =>
                {
                    var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                    string name = "显示器";
                    if (GetMonitorInfo(hMon, ref mi))
                    {
                        rc = mi.rcMonitor;
                        name = mi.szDevice;
                        if ((mi.dwFlags & 1) != 0) name += "（主）";
                    }
                    list.Add(new ScreenInfo
                    {
                        X = rc.Left,
                        Y = rc.Top,
                        W = rc.Right - rc.Left,
                        H = rc.Bottom - rc.Top,
                        Name = name,
                        Primary = (mi.dwFlags & 1) != 0
                    });
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                Trace.Log("枚举显示器失败: " + ex.Message);
            }
            return list;
        }

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFOEX info);
    }
}
