using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Cutlance.Models;
using Cutlance.Services.Audio;

namespace Cutlance.Services
{
    /// <summary>NDI 输出的启动参数。</summary>
    public sealed class NdiOutputConfig
    {
        /// <summary>抓屏区域（屏幕物理像素，就是「播出框」——与 FfmpegOutputConfig 同口径）。</summary>
        public double Left, Top, Width, Height;

        /// <summary>广播帧率。NDI 是逐帧直传、不做码率压缩，帧率越高网络占用越大。</summary>
        public int Fps = 30;

        /// <summary>NDI 源名（别的机器上看到的就是它）。空 = 用默认名。</summary>
        public string SourceName = "";

        /// <summary>
        /// 局域网输出协议（模块F）。Ndi = 已实现；Omt = 预留（会明确报未实现，
        /// 而不是静默什么都不做）。见 Models/OutputTransportKind。
        /// </summary>
        public OutputTransportKind Transport = OutputTransportKind.Ndi;
    }

    /// <summary>
    /// 模块F：NDI 输出 —— 把节目画面同时广播成一个 NDI 源。
    ///
    /// ── 两条链路（2026-10-20 起以第一条为默认）───────────────────────────
    ///   ① 合成器喂帧：合成线程每出一帧，直接拷进本服务的缓冲交给 NDI 发送器。
    ///      画面与推流/录制**同一份像素**，窗口被遮挡/最小化都不影响（C 方案）。
    ///   ② gdigrab 抓屏兜底：合成器起不来时，起一个轻量 ffmpeg 抓「播出框」出 rawvideo。
    ///
    /// ── 为什么不再需要「为了对齐语义而抓屏」──────────────────────────────
    ///   老版本靠「大家都抓同一块屏幕」来保证输出一致，代价是窗口一挡就断流。
    ///   现在成品帧本来就在内存里，一致性天然成立，抓屏反而成了唯一的薄弱环节。
    ///
    /// ── 为什么 NDI 不用编码 ──────────────────────────────────────────────
    ///   NDI 自己用 SpeedHQ 做帧内压缩，我们只负责把 BGRA 交过去。
    ///   所以 ① 链路里连 ffmpeg 都不起，省一个进程、省一次拷贝。
    /// </summary>
    public sealed class NdiOutputService
    {
        public static NdiOutputService Instance { get; } = new();

        private Process? _proc;
        // ⚠ 依赖抽象而不是具体类型：将来接 OMT（见 OutputTransportKind）只需换一个 sink 实现，
        //   抓屏与分发这段逻辑一行都不用动。
        private ILanFrameSink? _sender;
        private Thread? _reader;
        private volatile bool _stop;
        // 后台 CreateSender 进行中：防重入。此刻 IsRunning 仍为 false（_sender 还没设上），
        // 没有这个标记的话连点两下「开始」会同时起两个发送器。
        private bool _starting;
        private readonly object _gate = new();

        // 合成器喂帧：sink 委托必须缓存（现取现传可能摘不掉），缓冲按帧复用，避免每帧 new 8MB
        private IProgramFrames? _feedSource;
        private Action<IntPtr, int>? _frameSink;
        private byte[]? _frameBuf;

        private int _w, _h, _fps;
        private long _frames;
        private string _sourceName = "";
        private DateTime _startedAt;

        public event Action<string>? Error;
        public event Action<string>? Notify;

        public string LastError { get; private set; } = "";

        /// <summary>
        /// 后台正在创建发送器（Start 已返回 true、IsRunning 还是 false 的那十几秒）。
        /// 界面靠它显示「正在启动…」，不要用 IsRunning 判断"是不是已经开起来了"。
        /// </summary>
        public bool IsStarting { get { lock (_gate) return _starting; } }

        private NdiOutputService() { }

        public bool IsRunning
        {
            get
            {
                lock (_gate)
                {
                    // 喂帧模式没有 ffmpeg 进程，运行与否只看发送器在不在
                    if (_sender == null) return false;
                    if (_proc == null) return true;
                    try { return !_proc.HasExited; }
                    catch { return false; }
                }
            }
        }

        public string SourceName { get { lock (_gate) return _sourceName; } }
        public int Width { get { lock (_gate) return _w; } }
        public int Height { get { lock (_gate) return _h; } }
        public int Fps { get { lock (_gate) return _fps; } }
        public long FramesSent => Interlocked.Read(ref _frames);
        public TimeSpan Uptime => _startedAt == default ? TimeSpan.Zero : DateTime.Now - _startedAt;

        /// <summary>当前有没有接收端在收看（用于状态显示：「有没有人真的在看」）。</summary>
        public int Connections
        {
            get
            {
                lock (_gate)
                {
                    try { return _sender?.Connections ?? 0; } catch { return 0; }
                }
            }
        }

        /// <summary>默认源名：Cutlance (主机名) —— NDI 会在前面自动补主机名，所以这里只给短名。</summary>
        public static string DefaultSourceName
        {
            get
            {
                try { return "Cutlance Output (" + Environment.MachineName + ")"; }
                catch { return "Cutlance Output"; }
            }
        }

        // ==================== 启动 / 停止 ====================

        /// <summary>
        /// 启动 NDI 输出：**同步只做轻量校验，真正的启动丢到后台线程**。
        /// 为什么：首次 CreateSender 在部分网络环境下要 12~60 秒（NDI 内部要做 mDNS 注册 + 网卡枚举），
        /// 同步调用等于点一下「开始推流 / 录制」界面卡死半分钟（MEMORY 铁律 11）。
        /// 返回 true 只代表「已排上队」，真正成败走 Error / Notify 事件
        /// （MainWindow 已把这两个事件接到状态栏 —— 否则异步之后失败会被静默吞掉）。
        /// </summary>
        public bool Start(NdiOutputConfig cfg, IProgramFrames? frames, out string error)
        {
            error = "";

            // 下面这两项判断不碰 NDI 运行时，同步判掉 —— 用户能立刻看到「为什么起不来」，
            // 不用等后台线程再回报。
            if (cfg.Transport == OutputTransportKind.Omt)
            {
                error = "OMT 输出尚未实现（接口已预留，见 Models/OutputTransport.cs）。"
                      + "当前请使用 NDI 输出。";
                LastError = error;
                return false;
            }

            bool feedChk = frames != null && frames.Width > 1 && frames.Height > 1;
            if (!feedChk && !FfmpegOutput.IsAvailable)
            {
                error = "找不到 ffmpeg.exe，NDI 输出无法采集画面";
                LastError = error;
                return false;
            }

            lock (_gate)
            {
                if (_starting || IsRunning) { error = "NDI 输出已在运行或正在启动"; return false; }
                _starting = true;
                _stop = false;   // 清掉上一次 Stop 留下的取消标记，否则这一次会被当成"已取消"
            }

            // 界面已经不再卡了，但用户需要知道「它在忙」—— 首次建发送器可能十几秒才有结果。
            Notify?.Invoke("NDI 输出正在启动…（首次可能要十几秒，稍等）");

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    if (!StartCore(cfg, frames, out var err) && !string.IsNullOrWhiteSpace(err))
                        Error?.Invoke(err);
                }
                catch (Exception ex)
                {
                    LastError = "NDI 输出启动失败：" + ex.Message;
                    Error?.Invoke(LastError);
                }
                finally
                {
                    lock (_gate) _starting = false;
                }
            });
            return true;
        }

        /// <summary>后台线程执行：阻塞的 CreateSender + 两条链路装配。失败一律经 Error 事件外抛。</summary>
private bool StartCore(NdiOutputConfig cfg, IProgramFrames? frames, out string error)
        {
            error = "";
            bool feed;
            int w, h;
            string name;

            lock (_gate)
            {
                if (IsRunning) { error = "NDI 输出已在运行"; return false; }

                // 预留档：选了 OMT 要**明确说清楚**没实现。
                // 静默不做事是最坏的（用户以为在广播，接收端什么都没有）。
                if (cfg.Transport == OutputTransportKind.Omt)
                {
                    error = "OMT 输出尚未实现（接口已预留，见 Models/OutputTransport.cs）。"
                          + "当前请使用 NDI 输出。";
                    LastError = error;
                    return false;
                }

                // 有合成器就不用 ffmpeg 抓屏了；没有才检查 ffmpeg 在不在
                feed = frames != null && frames.Width > 1 && frames.Height > 1;

                if (!feed && !FfmpegOutput.IsAvailable)
                {
                    error = "找不到 ffmpeg.exe，NDI 输出无法采集画面";
                    LastError = error;
                    return false;
                }

                w = EvenSize(feed ? frames!.Width : (int)Math.Round(cfg.Width));
                h = EvenSize(feed ? frames!.Height : (int)Math.Round(cfg.Height));
                if (w < 16 || h < 16)
                {
                    error = feed
                        ? $"合成器画面尺寸无效（{w}×{h}），请重启程序后再开 NDI"
                        : "播出框区域无效（宽高须 > 16），请确认播出监视器未被最小化";
                    LastError = error;
                    return false;
                }

                name = string.IsNullOrWhiteSpace(cfg.SourceName) ? DefaultSourceName : cfg.SourceName.Trim();
            }

            // ⚠ 下面这段**必须在锁外**：首次 CreateSender 要 12~60 秒（mDNS 注册 + 网卡枚举）。
            //   原来整个方法都在 lock(_gate) 里，后台线程虽然不卡 UI 了，
            //   但 UI 线程一读 IsRunning / FramesSent（都要 _gate）就会跟着冻住 —— 等于没异步。
            var fps = cfg.Fps > 0 ? cfg.Fps : 30;
            var swSend = Stopwatch.StartNew();
            var sender = NdiService.Instance.CreateSender(name, out var serr);
            AudioBus.Log($"[NDI] CreateSender 耗时 {swSend.ElapsedMilliseconds}ms（源名 {name}）");
            if (sender == null)
            {
                error = serr;
                LastError = error;
                return false;
            }

            // 建发送器这十几秒里用户可能已经点了「停」—— Stop() 会置 _stop。
            // 不拦的话会出现「界面显示已停、NDI 其实还在广播」这种最坏的情况。
            if (_stop)
            {
                try { sender.Dispose(); } catch { }
                error = "";   // 用户主动取消，不是故障，不必弹警告
                return false;
            }

            // ===== 链路①：合成器直供（不起 ffmpeg）=====
            if (feed)
            {
                var buf = new byte[w * h * 4];
                var s = sender;
                int fw = w, fh = h;
                // 委托必须存字段：每次取方法组都造新对象，现取现传就摘不掉
                Action<IntPtr, int> sink = (ptr, stride) => SendFrame(ptr, stride, buf, s, fw, fh, fps);

                lock (_gate)
                {
                    _sender = sender;
                    _w = w; _h = h; _fps = fps;
                    _sourceName = name;
                    _frames = 0;
                    _stop = false;
                    _feedSource = frames;
                    _frameBuf = buf;
                    _frameSink = sink;
                    frames!.AddSink(sink);
                    _startedAt = DateTime.Now;
                    LastError = "";
                }

                Notify?.Invoke($"● NDI 输出已开启：{name}（{w}×{h}@{fps}，合成器直供）");
                return true;
            }

            // ===== 链路②：gdigrab 抓屏兜底 =====
            var args = BuildArgs(cfg, w, h);
            AudioBus.Log("NDI 输出启动参数：" + args);

            var psi = new ProcessStartInfo(FfmpegOutput.FfmpegPath, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // ffmpeg 往管道写 UTF-8；不指定会按 GBK 解出乱码（本项目踩过的老坑）
                StandardErrorEncoding = Encoding.UTF8
            };

            Process? p;
            try { p = Process.Start(psi); }
            catch (Exception ex)
            {
                error = "启动 NDI 输出进程失败：" + ex.Message;
                LastError = error;
                return false;
            }

            if (p == null)
            {
                error = "启动 NDI 输出进程失败（进程对象为空）";
                LastError = error;
                return false;
            }

            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                LastError = e.Data;
                AudioBus.Log("[NDI 输出] " + e.Data);
            };
            try { p.BeginErrorReadLine(); } catch { }

            // 启动即退（gdigrab 区域非法 / 屏幕缩放变化导致尺寸不符）→ 立刻报错，别让界面骗人
            if (p.WaitForExit(800))
            {
                int code = -1;
                try { code = p.ExitCode; } catch { }
                var msg = "NDI 输出进程启动即退出（退出码 " + code + "）"
                        + (string.IsNullOrWhiteSpace(LastError) ? "" : "：" + LastError);
                try { if (!p.HasExited) p.Kill(true); } catch { }
                try { p.Dispose(); } catch { }
                error = msg;
                LastError = msg;
                return false;
            }

            lock (_gate)
            {
                _sender = sender;
                _proc = p;
                _w = w; _h = h; _fps = fps;
                _sourceName = name;
                _frames = 0;
                _stop = false;
                _startedAt = DateTime.Now;
                LastError = "";
                _reader = new Thread(() => ReadLoop(p)) { IsBackground = true, Name = "ndi-out" };
                _reader.Start();
            }

            Notify?.Invoke($"● NDI 输出已开启：{name}（{w}×{h}@{fps}）");
            return true;
        }


        public void Stop()
        {
            Thread? rd;
            Process? p;
            IProgramFrames? frames;
            Action<IntPtr, int>? sink;
            lock (_gate)
            {
                _stop = true;
                rd = _reader; _reader = null;
                p = _proc; _proc = null;
                frames = _feedSource; _feedSource = null;
                sink = _frameSink; _frameSink = null;
            }

            // 先摘帧源再释放发送器：否则合成线程可能正握着即将释放的 sender
            if (frames != null && sink != null)
            {
                try { frames.RemoveSink(sink); } catch { }
            }

            try { rd?.Join(800); } catch { }
            try { if (p != null && !p.HasExited) p.Kill(true); } catch { }
            try { p?.Dispose(); } catch { }

            lock (_gate) CleanupLocked();
        }

        /// <summary>关闭程序前的同步硬停（与 InputPreprocessService.HardStopAll 同理）。</summary>
        public void HardStop() => Stop();

        private void CleanupLocked()
        {
            if (_feedSource != null && _frameSink != null)
            {
                try { _feedSource.RemoveSink(_frameSink); } catch { }
            }
            _feedSource = null;
            _frameSink = null;
            _frameBuf = null;

            try { _sender?.Dispose(); } catch { }
            _sender = null;
            _sourceName = "";
        }

        /// <summary>
        /// 合成线程回调：把成品帧拷进自有缓冲交给 NDI。
        /// 这里必须自己拷一份 —— 合成器的 front buffer 下一帧就被覆盖，
        /// 而 NDI 发送器虽然会拷贝，但拷贝发生在调用期间，指针必须在那之前有效。
        /// </summary>
        private void SendFrame(IntPtr src, int stride, byte[] buf,
                               ILanFrameSink sender, int w, int h, int fps)
        {
            try
            {
                int row = w * 4;
                if (stride == row) Marshal.Copy(src, buf, 0, buf.Length);
                else
                {
                    // stride 带行尾填充时逐行拷（合成器当前是紧密排列，这里纯防呆）
                    for (int y = 0; y < h; y++)
                        Marshal.Copy(src + y * stride, buf, y * row, row);
                }
                sender.SendBgra(buf, w, h, fps, 1);
                Interlocked.Increment(ref _frames);
            }
            catch { }
        }

        private static int EvenSize(int v)
        {
            if (v < 2) v = 2;
            return v - (v % 2);
        }

        /// <summary>
        /// 只抓屏、不编码：输出 BGRA 原始帧到 stdout。
        /// ⚠ -pix_fmt bgra 必须和 NDI 发送器声明的 FourCC(BGRA) 严格一致，
        ///   不一致会出现「红蓝互换」这种一眼看着像显卡坏了的现象。
        /// </summary>
        private string BuildArgs(NdiOutputConfig c, int w, int h)
        {
            var sb = new StringBuilder();
            sb.Append("-hide_banner -loglevel error -nostats ");
            sb.Append("-thread_queue_size 1024 ");
            sb.Append("-f gdigrab -framerate ").Append(c.Fps > 0 ? c.Fps : 30).Append(' ');
            sb.Append("-draw_mouse 0 ");
            sb.Append("-offset_x ").Append((int)Math.Round(c.Left)).Append(' ');
            sb.Append("-offset_y ").Append((int)Math.Round(c.Top)).Append(' ');
            sb.Append("-video_size ").Append(w).Append('x').Append(h).Append(' ');
            sb.Append("-i desktop ");
            sb.Append("-pix_fmt bgra ");
            sb.Append("-f rawvideo -");
            return sb.ToString();
        }

        // ==================== 读帧 → NDI ====================

        private void ReadLoop(Process p)
        {
            int w, h, fps;
            ILanFrameSink? sender;
            lock (_gate) { w = _w; h = _h; fps = _fps; sender = _sender; }
            if (sender == null) return;

            int frameSize = w * h * 4;
            var buf = new byte[frameSize];
            int off = 0;

            try
            {
                var stdout = p.StandardOutput.BaseStream;
                while (!_stop)
                {
                    int n = stdout.Read(buf, off, frameSize - off);
                    if (n <= 0) break;                 // ffmpeg 结束/管道关闭
                    off += n;
                    if (off < frameSize) continue;     // 还没凑满一帧

                    sender.SendBgra(buf, w, h, fps, 1);
                    Interlocked.Increment(ref _frames);
                    off = 0;
                }
            }
            catch { }
            finally
            {
                // 走到这里说明抓屏断了 —— 要么用户 Stop，要么 ffmpeg 自己退了
                if (!_stop)
                {
                    var msg = "⚠ NDI 输出已中断（抓屏进程退出）" +
                              (string.IsNullOrWhiteSpace(LastError) ? "" : "：" + LastError);
                    try { Error?.Invoke(msg); } catch { }
                }
            }
        }
    }
}
