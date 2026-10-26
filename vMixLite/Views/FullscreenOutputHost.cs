using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Cutlance.Models;
using Cutlance.Services;

namespace Cutlance.Views
{
    /// <summary>
    /// HDMI 全屏输出的**独立线程宿主**（2026-10-24 老板要求拆线程）。
    ///
    /// ── 为什么拆 ──────────────────────────────────────────────────────────
    ///   老实现把全屏窗放在主 UI 线程渲染：主界面一卡（拖图层、开对话框、载入工程），
    ///   副屏 HDMI 就跟着停一下。推流/录制在别的线程和进程里，只有全屏受影响。
    ///
    /// ── 怎么拆 ─────────────────────────────────────────────────────────────
    ///   起一条 **STA 线程 + 自己的 Dispatcher**，窗口与 WriteableBitmap 都在这条线程上创建；
    ///   画面由合成器 sink（`IProgramFrames.AddSink`）在**合成线程**推过来，回调里只做：
    ///     ① 把这一帧拷进自己缓冲（源指针下一帧就被覆盖，必须立刻拷）；
    ///     ② 给窗口线程排一次 BeginInvoke（用 pending 标志合并，渲染追不上就丢帧，绝不排队）。
    ///
    /// ⚠ 代价（写在 MEMORY 里）：每帧多拷一份全帧（1080p BGRA ≈ 8MB/帧，30fps ≈ 240MB/s）。
    ///   换来的是：主界面再卡，副屏也能拿到最新成品帧。
    /// ⚠ 绝不能在 sink 里碰 UI、不能阻塞 —— 那会把合成线程（四条输出线共同的命）拖死。
    /// </summary>
    internal sealed class FullscreenOutputHost : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly Action<IntPtr, int> _sink;      // 保存委托实例，防止被 GC 回收
        private readonly IProgramFrames? _frames;
        private readonly int _w, _h;

        private Dispatcher? _disp;
        private FullscreenOutputWindow? _win;
        private IntPtr _buf;
        private int _bufBytes;
        private int _pending;
        private int _drawErrors;
        private int _stride;
        private volatile bool _closed;

        /// <summary>窗口被关掉（用户右键/系统关闭/我们主动 Close 都会走这里）。</summary>
        public event Action? Closed;

        /// <summary>全屏窗的 HWND（屏外截图/置顶检查用）。</summary>
        public IntPtr Handle { get; private set; }

        /// <summary>右键菜单项数（自检用，创建完成后才有值）。</summary>
        public int RefreshMenuItemCount { get; private set; }

        /// <summary>已经贴上去多少帧（自检用：>0 说明合成器真的把帧推过来了）。</summary>
        public long FramesDrawn;

        public bool IsOpen => !_closed && _win != null;

        public FullscreenOutputHost(MonitorInfo? monitor, IProgramFrames? frames, int width, int height,
                                    bool offscreen = false)
        {
            _frames = frames;
            _w = width > 16 ? width : 1920;
            _h = height > 16 ? height : 1080;
            _sink = OnFrame;

            _thread = new Thread(() => Run(monitor, offscreen))
            {
                IsBackground = true,
                Name = "cutlance-fullscreen-out"
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();

            // 等窗口真正建好（含 HWND）再挂 sink：否则头几帧会写进还没准备好的缓冲
            _ready.Wait(5000);
            if (!_closed) frames?.AddSink(_sink);
        }

        private void Run(MonitorInfo? monitor, bool offscreen)
        {
            try
            {
                _disp = Dispatcher.CurrentDispatcher;

                var win = new FullscreenOutputWindow(monitor);
                _win = win;

                win.Closed += (_, _) =>
                {
                    _closed = true;
                    try { Closed?.Invoke(); } catch { }
                    try { Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } catch { }
                };

                win.Show();     // 内部已在 SourceInitialized/Loaded 里摆到目标显示器
                Handle = new WindowInteropHelper(win).Handle;
                RefreshMenuItemCount = win.RefreshMenuItemCount;

                // 自检用：扔到屏幕外，绝不闪在老板眼前
                if (offscreen)
                {
                    win.WindowStartupLocation = WindowStartupLocation.Manual;
                    win.Left = -12000; win.Top = -12000;
                    win.Left = -12000; win.Top = -12000;
                }

                _ready.Set();
                Dispatcher.Run();       // 这条线程专门跑这个窗口的消息循环
            }
            catch (Exception ex)
            {
                try { ErrorLog.Error("[全屏输出] 独立线程起窗失败", ex); } catch { }
                _closed = true;
                _ready.Set();
            }
        }

        /// <summary>
        /// 合成线程回调：拷贝一帧 + 排一次渲染。
        /// ⚠ 这里只能用无锁/短锁操作；绝对不能 Wait 窗口线程。
        /// </summary>
        private unsafe void OnFrame(IntPtr src, int stride)
        {
            if (_closed || src == IntPtr.Zero || stride <= 0) return;

            int need = stride * _h;
            if (_buf == IntPtr.Zero || _bufBytes < need)
            {
                if (_buf != IntPtr.Zero) { Marshal.FreeHGlobal(_buf); _buf = IntPtr.Zero; }
                _buf = Marshal.AllocHGlobal(need);
                _bufBytes = need;
            }

            // 源指针在锁内、下一帧就被覆盖 → 必须立刻拷走
            Buffer.MemoryCopy((void*)src, (void*)_buf, _bufBytes, need);
            _stride = stride;

            // 渲染追不上就丢帧（宁可少画一帧，也不让 BeginInvoke 队列无限涨）
            if (Interlocked.Exchange(ref _pending, 1) != 0) return;

            var d = _disp;
            if (d == null) { Interlocked.Exchange(ref _pending, 0); return; }
            try
            {
                d.BeginInvoke(DispatcherPriority.Render, new Action(DrawFrame));
            }
            catch
            {
                Interlocked.Exchange(ref _pending, 0);
            }
        }

        /// <summary>窗口线程：把缓冲贴到 WriteableBitmap。</summary>
        private void DrawFrame()
        {
            Interlocked.Exchange(ref _pending, 0);
            if (_closed || _win == null || _buf == IntPtr.Zero) return;
            try
            {
                _win.ShowFrame(_buf, _stride, _w, _h);
                Interlocked.Increment(ref FramesDrawn);
            }
            catch (Exception ex)
            {
                // 只在前 3 次记日志：贴图失败会每帧触发，刷屏会把真问题埋掉
                if (Interlocked.Increment(ref _drawErrors) <= 3)
                {
                    try { ErrorLog.Info("[全屏输出] 贴图失败：" + ex.Message); } catch { }
                }
            }
        }

        /// <summary>右键「刷新画面」的等价操作（跨线程调用）。</summary>
        public void Refresh()
        {
            try { _disp?.Invoke(new Action(() => _win?.RefreshNow())); } catch { }
        }

        /// <summary>把窗口重新摆到目标显示器（热插拔 / 改分辨率后）。</summary>
        public void Reposition(MonitorInfo? monitor)
        {
            try { _disp?.Invoke(new Action(() => _win?.RepositionTo(monitor))); } catch { }
        }

        public void Close()
        {
            _closed = true;
            try { _frames?.RemoveSink(_sink); } catch { }

            // ⚠ 必须确认那条线程的 Dispatcher 真的在跑：起窗失败时 _disp 有值但没人跑消息泵，
            //   这时 Invoke 会**永久阻塞**（把主 UI 线程一起拖死，而且不会抛异常，最难查）。
            var d = _disp;
            try
            {
                if (d != null && !d.HasShutdownStarted && _thread.IsAlive)
                    d.BeginInvoke(new Action(() => { try { _win?.Close(); } catch { } }));
            }
            catch { }

            try { _thread.Join(1500); } catch { }
            try
            {
                if (_buf != IntPtr.Zero) { Marshal.FreeHGlobal(_buf); _buf = IntPtr.Zero; _bufBytes = 0; }
            }
            catch { }
        }

        public void Dispose() => Close();
    }
}
