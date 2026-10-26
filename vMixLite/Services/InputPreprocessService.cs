using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Cutlance.Models;

namespace Cutlance.Services
{
    /// <summary>
    /// 模块C 方案A：输入预处理层（FFmpeg 虚拟输入）。
    ///
    /// ── 为什么需要这一层 ───────────────────────────────────────────────
    /// VLC 3.0 只在「LibVLC 实例级」认 --video-filter，挂在单个 Media 上的
    /// ":video-filter=…" 会被完全忽略（已用 invert / croppadd 双向实测确认）。
    /// 而实例级滤镜对同一个 LibVLC 实例的所有播放统一生效，做不到「不同输入各带各的裁切」。
    /// 所以改走这条路：给「需要处理的输入」各起一个常驻 ffmpeg，把裁切/旋转/镜像
    /// （后续还会加抠像+背景合成）做进画面，产出一路本地组播 MPEG-TS，VLC 只管播它。
    ///
    /// ── 为什么用组播而不是单播 ─────────────────────────────────────────
    /// 同一个输入常常要「预览框 + 输出图层」同时播 —— 单播端口只能被一个接收方绑定，
    /// 组播天然支持多消费者（已实测：两个消费者同时接收同一路各得完整帧）。
    /// 所以一份预处理产物可以喂给任意多个播放器，不需要自己写转发中继。
    ///
    /// ── 代价（用之前要知道）────────────────────────────────────────────
    ///   · 每个被处理的输入多一个常驻 ffmpeg 进程 + 编码 CPU/GPU 开销；
    ///   · 端到端增加约 400~500ms 延迟（实测 460ms，含探流）；
    ///   · 文件类输入因此变成「直播流」：不能拖动进度条；
    ///   · 被处理的输入与未处理的输入之间不能严格同步（切镜头可能对不上），
    ///     适合固定机位 / 绿幕这类场景，不适合要和其他机位硬切同步的画面。
    ///
    /// ── 失败策略（重要）────────────────────────────────────────────────
    /// 预处理起不来（没有 ffmpeg、参数错、设备被占、NVENC 不可用…）时**不阻断播放**：
    /// 返回 false，由调用方回落到原始画面，并通过 <see cref="Error"/> 在状态栏说清原因。
    /// 绝不允许出现「点了没反应」。
    /// </summary>
    public sealed class InputPreprocessService
    {
        public static InputPreprocessService Instance { get; } = new();

        /// <summary>
        /// 本输入是否需要走预处理层（常驻 ffmpeg）。
        ///
        /// 判定规则：
        ///   · 实时输入（Camera 采集卡 / Network 网络流）：**默认走预处理**。
        ///     采集卡的依据（老板 2026-10-15 拍板链路 A），两条链路实测对比（同源 1080p60）：
        ///       A ffmpeg 预处理→组播 = 25.2% 单核，预览+输出可同看一路，延迟 ≈460ms
        ///       B VLC 直连        = 13.7% 单核，无损，但同输入第二次打开 dshow 必失败（设备独占）
        ///     决定性差异是「独占」：MainWindow 里预览框与输出图层各自 CreateMedia，
        ///     直连时「预览+输出同时看同一路」必然报「设备被占用」，做节目根本用不了。
        ///
        ///     网络流走同一条路的理由是**同类但更强**：
        ///       ① 网络源大多是独占连接 —— RTSP 摄像机普遍只允许 1~2 路并发，预览+输出各自直连
        ///          就是 2 路，很容易把摄像机连爆（表现为第二路黑屏/401）；
        ///          走组播后全程只 1 路连接，喂任意多个播放器。
        ///       ② 断流重连、状态灯、去隔行、低延迟这些基建只有预处理链路才有，
        ///          直连的话网络流就成了一条「断了没人管」的输入。
        ///       ③ ffmpeg 的协议覆盖面比 VLC 宽（RIST 尤其），参数也更可控。
        ///     只有显式选 VlcDirect 才走直连（低延迟/单视图场景）。
        ///   · 其它输入：有裁切/旋转/镜像/抠像就走（模块C A-1 / A-2）。
        /// </summary>
        public static bool NeedsPreprocess(InputSource? src)
        {
            if (src == null) return false;

            // NDI（模块F）：**必须**走预处理层，没有直连这条路。
            // 理由：NDI 帧不是「一个可播放的 URL」，它只能由 NDI 运行时收下来变成像素。
            // 所以链路是「.NET 接收器收帧 → 写进 ffmpeg 的 stdin → 编码成组播 TS → VLC 播」。
            // ⚠ 这条判断必须排在下面的 IsLiveInput 之前 —— NDI 也属于实时输入，
            //   若被 CamLink==VlcDirect 挡掉就整路播不了了（VLC 没有 NDI 插件）。
            if (src.Type == SourceType.Ndi) return true;

            if (src.IsLiveInput)
            {
                // ⚠ 显式选了 VLC 直连就尊重用户选择，不因「检测到隔行」偷偷改链路 ——
                //   改链路会连带把延迟从 ~100ms 抬到 ~460ms，那是用户的取舍。
                //   直连链路下去隔行只能靠 VLC 自己的 :deinterlace（尽力而为，见 VideoEngine），
                //   面板在检测到隔行时会提示「建议改用 ffmpeg 预处理链路」。
                return src.CamLink != CameraLink.VlcDirect;   // Auto / Ffmpeg 都走预处理
            }
            // 文件 / 网络流也允许主动开去隔行（2026-10-24 老板要的 1080i 开关）：
            // 去隔行必须由 ffmpeg 的 yadif 做，所以一旦要开，就必须走预处理链 ——
            // 否则文件输入不进预处理、yadif 根本没机会执行（那正是"勾了没反应"的坑）。
            return src.NeedsVideoProcess || src.NeedDeinterlace;
        }

        /// <summary>预处理层出错（启动失败 / 运行中断）。文案已写成可直接显示的一句话。</summary>
        public event Action<string>? Error;

        /// <summary>
        /// 最近一次「这一路没能走预处理」的原因（起不来时的一句话说明）。
        ///
        /// 为什么要单独留一份：TryGetStreamUrl 返回值只有 true/false，调用方拿到 false
        /// 就回落原始画面了，原因如果只靠 Error 事件推状态栏，很容易被紧接着的其它提示盖掉
        /// （实测踩过：面板关闭后写「已添加 xxx」的提示，把启动失败原因顶没了，
        ///  结果界面上显示着「ffmpeg 预处理」，实际跑的是 VLC 直连 —— 名不副实）。
        /// </summary>
        public string LastError { get; private set; } = "";

        private sealed class Proc
        {
            public Process? P;
            public int Port;
            public string Url = "";
            public bool Nvenc;
            public volatile bool Stopping;
            public string Args = "";
            public readonly StringBuilder ErrTail = new();

            // ---- 断流自动重连（模块E B-3）----
            public int Retries;                 // 已尝试的重连次数
            public volatile bool Abandoned;     // 重试到上限，放弃
            public InputSource? Src;            // 重连时要知道重建谁

            // ---- NDI 输入会话（模块F）----
            // 这一路是 NDI 时才有：接收器负责收帧并写进本进程的 stdin。
            // 它和 ffmpeg 进程同生共死（ffmpeg 退出 → stdin 写失败 → 本会话先被关掉）。
            public NdiInputSession? Ndi;
        }

        private readonly Dictionary<InputSource, Proc> _procs = new();

        /// <summary>
        /// 「这一路曾经起过 ffmpeg」的标记（模块E B-2 状态灯用）。
        ///
        /// 为什么需要它：只看 _procs 里有没有进程，分不清两件完全不同的事 ——
        ///   · 这个输入刚加进来、还没取过画面 → 压根没启动过。报「无信号」是冤枉它；
        ///   · 启动过但已经退出（起不来 / 重试到上限放弃）→ 才是真的「无信号」。
        /// 用引用比较的 HashSet（InputSource 未重写 Equals/GetHashCode）正好按实例判定。
        /// </summary>
        private readonly HashSet<InputSource> _everStarted = new();
        private readonly object _lock = new();
        private int _nextPort = PortBase;
        private volatile bool _shutdown;

        private const int PortBase = 31000;
        private const int PortMax = 31999;

        // ==================== 诊断日志 ====================
        //
        // 为什么要落文件：采集链路的失败几乎都在 ffmpeg 子进程里，而它的 stderr 被程序
        // 收走了 —— 界面显示的内容一旦被后续提示顶掉，现场就没了（实测踩过：预览画面
        // 出得来，但 ffmpeg 根本没起，直接回落 VLC 直连，界面上完全看不出）。
        // 这份日志记「启动命令 / 启动即退 / 运行中退出 / 重连」，排查时一次就能定位。
        // 路径：%TEMP%\cutlance-preprocess.log

        public static string LogPath { get; } =
            Path.Combine(Path.GetTempPath(), "cutlance-preprocess.log");

        private static void Log(string msg)
        {
            try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff}  {msg}\r\n"); }
            catch { }
        }

        /// <summary>预处理流的组播地址。每个输入用同一个地址的不同端口，互不干扰。</summary>
        private const string McastAddr = "239.255.77.1";

        private InputPreprocessService() { }

        /// <summary>当前活跃的预处理进程数（设置界面/状态栏用）。</summary>
        public int ActiveCount
        {
            get
            {
                lock (_lock)
                    return _procs.Values.Count(v => { try { return v.P != null && !v.P.HasExited; } catch { return false; } });
            }
        }

        public bool IsRunning(InputSource src)
        {
            lock (_lock)
            {
                if (!_procs.TryGetValue(src, out var pr) || pr.P == null) return false;
                try { return !pr.P.HasExited; } catch { return false; }
            }
        }

        /// <summary>一路预处理的健康状况（模块E B-2：主窗口输入项上的常显状态灯）。</summary>
        public enum PreprocessState
        {
            /// <summary>这一路不走预处理（文件/桌面的无处理输入，或 VLC 直连），或尚未启动过。</summary>
            None,
            /// <summary>ffmpeg 在跑，画面正常。</summary>
            Running,
            /// <summary>进程已退，看门狗正在按退避重连。</summary>
            Reconnecting,
            /// <summary>已经退出且不在重连（起不来 / 重试到上限放弃）。</summary>
            Down
        }

        /// <summary>
        /// 取一路的健康状况。刷新节奏交给调用方（主窗口 2 秒一次）。
        ///
        /// 判定顺序不能颠倒：先判「在跑」→ 再看「在 _procs 里 = 看门狗正退避重试」→
        /// 最后才靠 _everStarted 区分「起过但死了」（Down）与「从来没启动过」（None）。
        /// </summary>
        public PreprocessState GetState(InputSource? src)
        {
            if (src == null || !NeedsPreprocess(src)) return PreprocessState.None;

            lock (_lock)
            {
                if (_procs.TryGetValue(src, out var pr))
                {
                    try { if (pr.P != null && !pr.P.HasExited) return PreprocessState.Running; }
                    catch { }
                    // ⚠ 还在 _procs 里 = 看门狗持有它，正在 ScheduleReconnect 的退避等待中。
                    //   放弃重连 / 显式 Stop 都会把它从表里摘掉，所以这里可以放心判「重连中」。
                    return PreprocessState.Reconnecting;
                }
                return _everStarted.Contains(src) ? PreprocessState.Down : PreprocessState.None;
            }
        }

        /// <summary>
        /// 提前把「有没有 NVENC」「摄像头设备名」这类一次性探测跑掉（后台线程），
        /// 避免用户第一次播放时卡在探测上。构造完主窗口调一下即可。
        /// </summary>
        public void WarmUp()
        {
            Task.Run(() =>
            {
                try { NvencAvailable(); } catch { }
                try { ProbeDshowDevices(); } catch { }
            });
        }

        // ==================== 对外主入口 ====================

        /// <summary>
        /// 取该输入的预处理流地址；进程没起就起，已在跑就复用。
        /// 返回 false 表示「这一路用不了预处理」——调用方应回落到原始画面，
        /// 并把 <paramref name="error"/> 提示给用户。
        /// </summary>
        public bool TryGetStreamUrl(InputSource src, out string url, out string error)
        {
            url = "";
            error = "";
            LastError = "";

            if (src == null) { error = "输入为空"; LastError = error; return false; }
            if (_shutdown) { error = "程序正在退出"; LastError = error; return false; }
            if (!NeedsPreprocess(src))
            {
                error = "该输入没有需要预处理的画面处理";
                LastError = error;
                Log($"[不走预处理] {src.Name}  |  Type={src.Type}  CamLink={src.CamLink}  "
                    + $"HasGeometry={src.HasGeometry}  → 回落原始画面");
                return false;
            }

            if (!FfmpegOutput.IsAvailable)
            {
                error = "找不到 ffmpeg.exe，无法启用「裁切 / 旋转」（画面按原始显示）";
                LastError = error;
                return false;
            }

            lock (_lock)
            {
                // 已在跑 → 直接复用
                if (_procs.TryGetValue(src, out var exist))
                {
                    try
                    {
                        if (exist.P != null && !exist.P.HasExited) { url = exist.Url; return true; }
                    }
                    catch { }
                    CloseProc(exist, wait: false);      // 已退出/异常 → 清掉重来
                    _procs.Remove(src);
                }

                var pr = new Proc
                {
                    Port = AllocPort(),
                    Nvenc = NvencAvailable()
                };
                pr.Url = $"udp://@{McastAddr}:{pr.Port}";

                if (!Launch(src, pr, out var le))
                {
                    error = le;
                    LastError = le;
                    Log($"[起不来] {src.Name}  |  {le}");
                    Error?.Invoke("⚠ " + le);
                    return false;
                }

                _procs[src] = pr;
                url = pr.Url;
                Log($"[已就绪] {src.Name}  |  {url}  nvenc={pr.Nvenc}");
                return true;
            }
        }

        /// <summary>停掉某输入的预处理进程（几何参数改动、工程载入、退出时调用）。</summary>
        public void Stop(InputSource? src)
        {
            if (src == null) return;
            Proc? pr = null;
            lock (_lock)
            {
                if (_procs.TryGetValue(src, out var v)) { pr = v; _procs.Remove(src); }
            }
            if (pr == null) return;
            pr.Stopping = true;
            CloseProc(pr, wait: false);
        }

        /// <summary>停掉全部（载入新工程前、清空输入时）。</summary>
        public void StopAll()
        {
            List<Proc> all;
            lock (_lock)
            {
                all = _procs.Values.ToList();
                _procs.Clear();
            }
            foreach (var pr in all) { pr.Stopping = true; CloseProc(pr, wait: false); }
        }

        /// <summary>
        /// 关闭程序专用的同步硬停。⚠ ffmpeg 是独立进程，父进程自我终止后它会变成孤儿继续跑，
        /// 所以必须在自我强杀【之前】把整棵进程树杀掉（与 FfmpegOutput.HardStop 同理）。
        /// </summary>
        public void HardStopAll()
        {
            _shutdown = true;
            List<Proc> all;
            lock (_lock)
            {
                all = _procs.Values.ToList();
                _procs.Clear();
            }
            foreach (var pr in all) { pr.Stopping = true; CloseProc(pr, wait: true); }
        }

        // ==================== 进程启动 / 收尾 ====================

        /// <summary>首次启动：分配组播端口、探测编码器，然后起进程。</summary>
        private bool Launch(InputSource src, Proc pr, out string error)
        {
            error = "";
            if (string.IsNullOrEmpty(pr.Url))
            {
                pr.Port = AllocPort();
                pr.Nvenc = NvencAvailable();
                pr.Url = $"udp://@{McastAddr}:{pr.Port}";
            }
            pr.Src = src;
            return StartProcess(src, pr, out error);
        }

        /// <summary>
        /// 起（或重起）一个 ffmpeg 进程。
        /// ⚠ 重连时 pr.Url 保持不变 —— 播放器那边收的还是同一个组播地址，
        ///    所以 ffmpeg 一恢复画面就自己回来了，VLC 端完全不用重建 Media。
        /// </summary>
        private bool StartProcess(InputSource src, Proc pr, out string error)
        {
            error = "";

            // ---- NDI（模块F）：先把源连上、收到第一帧，才知道分辨率 ----
            // 为什么要提前做：ffmpeg 的 rawvideo 输入必须在命令行里**写死 -video_size**，
            // 而 NDI 的分辨率只有收到第一帧才知道。所以顺序是
            // 「探帧 → 拼命令行 → 起 ffmpeg → 把帧从 stdin 灌进去」。
            if (src.Type == SourceType.Ndi)
            {
                try { pr.Ndi?.Dispose(); } catch { }
                pr.Ndi = NdiService.Instance.OpenReceiver(src.Location, out var nerr);
                if (pr.Ndi == null)
                {
                    error = nerr;
                    Log($"[NDI 连不上] {src.Name}  |  {nerr}");
                    return false;
                }
                Log($"[NDI 已连上] {src.Name}  |  {pr.Ndi.Width}x{pr.Ndi.Height}@{pr.Ndi.Fps}");
            }

            pr.Args = BuildArgs(src, pr);
            Log($"[启动] {src.Name}  |  {pr.Args}");
            lock (_lock) _everStarted.Add(src);   // 状态灯：从此不再算「未启动」

            var psi = new ProcessStartInfo(FfmpegOutput.FfmpegPath, pr.Args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                // ffmpeg 往管道写 UTF-8，不指定会按 GBK 解码 —— 报错信息里的中文设备名会变乱码
                StandardErrorEncoding = Encoding.UTF8
            };

            try { pr.P = Process.Start(psi); }
            catch (Exception ex)
            {
                error = "启动预处理进程失败：" + ex.Message;
                Log($"[启动异常] {src.Name}  |  {ex.Message}");
                return false;
            }

            var p = pr.P;
            if (p == null) { error = "启动预处理进程失败"; return false; }

            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (pr.ErrTail)
                {
                    pr.ErrTail.AppendLine(e.Data);
                    if (pr.ErrTail.Length > 4000) pr.ErrTail.Remove(0, pr.ErrTail.Length - 4000);
                }
            };
            try { p.BeginErrorReadLine(); } catch { }
            try { p.OutputDataReceived += (_, _) => { }; p.BeginOutputReadLine(); } catch { }

            // 留 900ms 看它会不会「启动即死」：编码器不可用 / 设备被占 / 滤镜参数非法，
            // 都是这个表现。NVENC 优先策略在这里做回落。
            if (p.WaitForExit(900))
            {
                var tail = TailOf(pr);
                int code0 = -1;
                try { code0 = p.ExitCode; } catch { }
                Log($"[启动即退] {src.Name}  |  code={code0}  nvenc={pr.Nvenc}  |  {tail}");

                if (pr.Nvenc)
                {
                    // NVENC 起不来（显卡不支持 / 会话已满）→ 立刻用软编重来。
                    //
                    // ⚠ 但「启动即退」不等于「编码器的锅」：设备被别的程序占着、USB 抖动、
                    //   信号源刚断电、甚至被外部杀掉，都会在 900ms 内退出。实测踩过 ——
                    //   连着杀掉 ffmpeg 几次，程序就把本机的 NVENC 永久拉黑了（之后全程软编，
                    //   性能白丢一截，而界面上毫无提示）。所以拉黑前先看 stderr 的尾巴：
                    //   只有确实指向编码器才全局禁用它，否则只本次改软编。
                    bool encoderFault = LooksLikeEncoderFault(tail);
                    CloseProc(pr, wait: true);
                    pr.Nvenc = false;
                    if (encoderFault) MarkNvencUnusable();
                    else Log($"[编码器] NVENC 启动即退但不像编码器故障（tail=\"{tail}\"）→ 本次改软编，不拉黑");
                    return Launch(src, pr, out error);
                }

                error = "预处理进程启动即退出" + (tail.Length > 0 ? "：" + tail : "");
                CloseProc(pr, wait: true);
                return false;
            }

            // ---- NDI（模块F）：把 stdin 交给接收器，开始持续灌帧 ----
            if (src.Type == SourceType.Ndi && pr.Ndi != null)
            {
                try
                {
                    var sess = pr.Ndi;
                    // stdin 写不进去 = ffmpeg 已经没了 → 把进程杀掉，交给看门狗按退避重连。
                    sess.StdinBroken += _ =>
                    {
                        try { if (!p.HasExited) p.Kill(true); } catch { }
                    };
                    sess.Attach(p.StandardInput.BaseStream);
                    Log($"[NDI 喂帧中] {src.Name}  |  {sess.Width}x{sess.Height}@{sess.Fps}");
                }
                catch (Exception ex)
                {
                    Log($"[NDI 喂帧失败] {src.Name}  |  {ex.Message}");
                    error = "NDI 画面管道建立失败：" + ex.Message;
                    CloseProc(pr, wait: true);
                    return false;
                }
            }

            AttachWatchdog(src, pr, p);

            // 稳定跑满 15 秒 → 把重连计数清零，之后断流又从第一次重试开始（避免累计到上限）
            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(15000);
                    try { if (p.HasExited) return; } catch { return; }
                    pr.Retries = 0;
                }
                catch { }
            });

            return true;
        }

        /// <summary>
        /// 守护这一路：进程退出后判断「要不要自动重连」。
        ///
        /// 只对采集类输入（摄像头 / 采集卡）重连 —— 它们的画面是"永恒的"，中断一定是故障
        /// （USB 抖动、信号源断电、设备被别的程序抢走又还回来）。
        /// 文件类输入退出码 0 表示「素材播完了」，那是正常结束，重连反而会让它从头再播一遍。
        /// </summary>
        private void AttachWatchdog(InputSource src, Proc pr, Process p)
        {
            Task.Run(() =>
            {
                try { p.WaitForExit(); } catch { return; }

                bool intentional;
                lock (_lock)
                {
                    intentional = pr.Stopping || _shutdown;
                    // ⚠ 这里故意【不】把 pr 从 _procs 摘掉：重连要复用同一个 Proc（URL 不变），
                    // 摘掉会让 ScheduleReconnect 以为用户已经 Stop 了这一路。
                    // 只有「放弃重连」或显式 Stop 时才移除。
                }
                if (intentional)
                {
                    Log($"[主动停止] {src.Name}  |  Stopping={pr.Stopping}  Shutdown={_shutdown}");
                    return;
                }

                int code = -1;
                try { code = p.ExitCode; } catch { }
                Log($"[运行中退出] {src.Name}  |  code={code}  Type={src.Type}  |  {TailOf(pr)}");

                // 退出码 0 且不是实时输入 → 素材播完了，这是正常终点，重连会让它从头再播一遍。
                if (code == 0 && !src.IsLiveInput)
                {
                    Error?.Invoke($"ℹ 输入「{src.Name}」的素材已播完（画面预处理随之结束，"
                                  + "再次播放会重新开始处理）");
                    lock (_lock)
                    {
                        if (_procs.TryGetValue(src, out var cur) && ReferenceEquals(cur, pr))
                            _procs.Remove(src);
                    }
                    return;
                }

                // 实时输入（采集卡 / 网络流）中断 = 故障，一律走退避重连。
                // 网络流即使是「对端正常结束」（退出码 0）也重连 —— 直播源的结束几乎总是暂时性的
                //（推流端切换场景 / HLS 追到分片末尾 / 上游重启），重连能把画面接回来；
                // 真结束了也不过是重试几次后放弃，代价远小于「断了就再也不管」。
                if (src.IsLiveInput)
                {
                    ScheduleReconnect(src, pr, code);
                    return;
                }

                var tail = TailOf(pr);
                Error?.Invoke($"⚠ 输入「{src.Name}」的画面预处理已中断"
                              + (tail.Length > 0 ? "：" + tail : "（退出码 " + code + "）")
                              + "（该输入将按原始画面播出）");
                lock (_lock)
                {
                    if (_procs.TryGetValue(src, out var cur) && ReferenceEquals(cur, pr))
                        _procs.Remove(src);
                }
            });
        }

        // ==================== 断流自动重连（模块E B-3） ====================

        /// <summary>重连退避档位（毫秒）：先快后慢，避免设备没准备好时空转烧 CPU。</summary>
        private static readonly int[] RetryDelays = { 1000, 2000, 4000, 8000, 15000, 30000 };
        private const int MaxRetries = 6;

        /// <summary>通知（重连中 / 已恢复 / 放弃）。文案可直接上状态栏。</summary>
        public event Action<string>? Notify;

        /// <summary>某路是否正处于「等待重连」状态（面板/状态显示用）。</summary>
        public bool IsReconnecting(InputSource? src)
        {
            if (src == null) return false;
            lock (_lock)
            {
                if (!_procs.TryGetValue(src, out var pr)) return false;
                if (pr.Abandoned || pr.Stopping) return false;
                try { return pr.P == null || pr.P.HasExited; }
                catch { return true; }
            }
        }

        /// <summary>
        /// 手动重连一路（放弃过、或用户想立刻重试时用）。
        /// 复位重试计数后重新按首次启动处理。
        /// </summary>
        public bool Reconnect(InputSource src, out string error)
        {
            error = "";
            if (src == null) { error = "输入为空"; return false; }
            if (_shutdown) { error = "程序正在退出"; return false; }

            lock (_lock)
            {
                if (_procs.TryGetValue(src, out var pr))
                {
                    pr.Abandoned = false;
                    pr.Stopping = false;
                    pr.Retries = 0;
                    pr.Nvenc = NvencAvailable();
                    if (!StartProcess(src, pr, out error))
                    {
                        Notify?.Invoke("⚠ 重连失败：" + error);
                        return false;
                    }
                    Notify?.Invoke($"✓ 输入「{src.Name}」已重新连接");
                    return true;
                }
            }
            return TryGetStreamUrl(src, out _, out error);
        }

        private void ScheduleReconnect(InputSource src, Proc pr, int exitCode)
        {
            if (_shutdown) return;

            int attempt;
            int delay;
            lock (_lock)
            {
                if (pr.Stopping || pr.Abandoned) return;
                // 用户已经 Stop / 删除了这一路（_procs 里换成了别的 Proc 或已移除）→ 不重连
                if (!_procs.TryGetValue(src, out var cur) || !ReferenceEquals(cur, pr)) return;

                if (pr.Retries >= MaxRetries)
                {
                    pr.Abandoned = true;
                    _procs.Remove(src);
                    Notify?.Invoke($"⚠ 输入「{src.Name}」的信号中断，已重试 {MaxRetries} 次仍未恢复，"
                                   + "停止自动重连。请检查信号源/接线后手动重连。");
                    return;
                }

                attempt = ++pr.Retries;
                delay = RetryDelays[Math.Min(attempt - 1, RetryDelays.Length - 1)];
            }

            var why = exitCode == 0 ? "信号结束" : "信号中断";
            Notify?.Invoke($"⟳ 输入「{src.Name}」{why}，{delay / 1000.0:0.#} 秒后第 {attempt}/{MaxRetries} 次重连…");

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay);
                    if (_shutdown) return;

                    lock (_lock)
                    {
                        if (pr.Stopping || pr.Abandoned) return;
                        if (!_procs.TryGetValue(src, out var cur) || !ReferenceEquals(cur, pr)) return;
                    }

                    if (StartProcess(src, pr, out var err2))
                    {
                        Notify?.Invoke($"✓ 输入「{src.Name}」信号已恢复（第 {attempt} 次重连成功）");
                        return;
                    }

                    // 还没起来（设备可能仍被占/没插好）→ 继续退避重试
                    ScheduleReconnect(src, pr, -1);
                }
                catch { }
            });
        }

        private static void CloseProc(Proc pr, bool wait)
        {
            // NDI 会话先关（模块F）：它持有 NDI 接收器句柄，并且可能正往 ffmpeg 的 stdin 灌帧。
            // 顺序不能反 —— 先停喂帧，再杀 ffmpeg，避免 kill 的瞬间往一个正在消失的管道里写。
            try { pr.Ndi?.Dispose(); } catch { }
            pr.Ndi = null;

            var p = pr.P;
            pr.P = null;
            if (p == null) return;
            if (wait) { KillProc(p); return; }
            Task.Run(() => KillProc(p));
        }

        private static void KillProc(Process p)
        {
            try { if (!p.HasExited) p.Kill(true); } catch { }   // true = 连子进程树一起
            try { p.Dispose(); } catch { }
        }

        private static string TailOf(Proc pr)
        {
            lock (pr.ErrTail)
            {
                var lines = pr.ErrTail.ToString()
                    .Split('\n')
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0)
                    .ToList();
                if (lines.Count == 0) return "";
                // 取最后 3 行：dshow 失败时关键词往往不在最后一行
                //（末行常是 "Error opening input files: I/O error"，
                //  真正的原因 "Could not run graph (device already in use)" 在它前面）。
                int take = Math.Min(3, lines.Count);
                return string.Join(" / ", lines.Skip(lines.Count - take));
            }
        }

        private int AllocPort()
        {
            for (int i = 0; i < 900; i++)
            {
                int p = _nextPort;
                _nextPort = _nextPort >= PortMax ? PortBase : _nextPort + 1;
                bool used = false;
                foreach (var v in _procs.Values) if (v.Port == p) { used = true; break; }
                if (!used) return p;
            }
            return PortBase;
        }

        // ==================== 命令行构造 ====================

        /// <summary>
        /// 生成这一路预处理的完整 ffmpeg 参数。
        ///
        /// 关键点逐个交代：
        ///   · "-re"（文件源）：按原生帧率读取。不加它会以最快速度冲流，播放器直接追不上。
        ///   · "-g 30 -bsf:v dump_extra=freq=keyframe"：播放器常在 GOP 中途接入
        ///     （用户点预览的瞬间），必须让每个关键帧都带 SPS/PPS，否则播放器报
        ///     "non-existing PPS 0 referenced" 看不出画面。关键帧间隔 1 秒左右，接入最快。
        ///   · "-c:a aac"：音频一并封进 TS。这样 LibVLC 仍能解出音频交给 AudioBus，
        ///     「谁出声 / 进不进推流」的既有逻辑完全不用改。
        ///   · "-muxdelay 0 -muxpreload 0"：压掉 mux 层自带的缓冲延迟。
        /// </summary>
        private string BuildArgs(InputSource src, Proc pr)
        {
            var sb = new StringBuilder(512);
            sb.Append("-hide_banner -loglevel error ");

            // ⚠ NDI（模块F）要从 stdin 读原始帧，绝不能加 -nostdin ——
            //   它会让 ffmpeg 主动关掉 stdin，接收器的每一帧都会写失败（表现为画面永远不出来）。
            bool isNdi = src.Type == SourceType.Ndi;
            if (!isNdi) sb.Append("-nostdin ");

            bool chroma = src.NeedChromaKey;

            // ---------- 输入 ----------
            AppendForegroundInput(sb, src, pr);             // 输入 0：前景（要抠的那一路）
            if (chroma) AppendBackgroundInput(sb, src);     // 输入 1：背景（纯色 / 图片 / 视频）

            // ---------- 画面处理 ----------
            if (chroma)
            {
                // 抠像要双输入 + 合成，只能走 filter_complex。
                // ⚠ 多输入时必须显式 -map：不写的话 ffmpeg 会把背景那一路也当输出源，行为不定。
                //   `0:a?` 的问号 = 「有音频才映射」（纯视频源不该因为没音频而报错）。
                sb.Append("-filter_complex \"").Append(BuildChromaFilter(src)).Append("\" ");
                sb.Append("-map \"[v]\" -map 0:a? ");
            }
            else
            {
                var vf = BuildVideoFilter(src);
                if (vf.Length > 0) sb.Append("-vf \"").Append(vf).Append("\" ");
            }

            // ---------- 编码 ----------
            // 低延迟档把关键帧间隔压到 0.25 秒（60fps 下 15 帧）：播放器接入更快、
            // 断流恢复时等待第一个关键帧的时间更短。标准档 1 秒 —— 码率效率更好。
            int gop = src.CamLowLatency ? 15 : 30;
            if (pr.Nvenc)
            {
                sb.Append("-c:v h264_nvenc -preset p1 -tune ll -rc cbr -b:v 8M -maxrate 8M -bufsize 8M ");
                if (src.CamLowLatency) sb.Append("-rc-lookahead 0 ");
            }
            else
            {
                // ultrafast + zerolatency 本身已无 lookahead / B 帧，无需额外压
                sb.Append("-c:v libx264 -preset ultrafast -tune zerolatency -crf 20 ");
            }

            sb.Append("-g ").Append(gop).Append(" -bf 0 -pix_fmt yuv420p ");

            // ---------- 音频（跟着一起进流，混音链路不用改）----------
            // NDI 这一路当前**只取视频**：NDI 的音频是独立帧流，要合进来得再喂一条 stdin，
            // 复杂度另算。这里显式 -an，免得 ffmpeg 去找一条并不存在的音频流。
            if (isNdi) sb.Append("-an ");
            else sb.Append("-c:a aac -b:a 160k -ar 48000 -ac 2 ");

            // ---------- 输出：本地组播 MPEG-TS ----------
            sb.Append("-f mpegts -muxdelay 0 -muxpreload 0 ");
            sb.Append("-bsf:v dump_extra=freq=keyframe ");
            sb.Append('"').Append(pr.Url)
              .Append("?pkt_size=1316&ttl=1").Append('"');

            return sb.ToString();
        }

        /// <summary>
        /// 前景输入（filter_complex 里的 [0:v]）—— 就是要拿来做抠像的那一路原始素材。
        /// 从原 BuildArgs 里原样搬出来，逻辑一字未改。
        /// </summary>
        private void AppendForegroundInput(StringBuilder sb, InputSource src, Proc? pr = null)
        {
            switch (src.Type)
            {
                case SourceType.File:
                    sb.Append("-re -i \"").Append(src.Location).Append("\" ");
                    break;

                case SourceType.Camera:
                    sb.Append("-f dshow -rtbufsize 512M ");
                    if (src.CamWidth > 0 && src.CamHeight > 0)
                        sb.Append("-video_size ").Append(src.CamWidth).Append('x').Append(src.CamHeight).Append(' ');
                    if (src.CamFps > 0)
                        sb.Append("-framerate ").Append(src.CamFps).Append(' ');
                    var vt = ChromaToInputOption(src.CamChroma);
                    if (vt.Length > 0) sb.Append(vt).Append(' ');
                    {
                        var (vd, ad) = ProbeDshowDevices();
                        var vname = !string.IsNullOrWhiteSpace(src.Location) ? src.Location : vd;
                        sb.Append("-i video=\"").Append(vname ?? "").Append('"');
                        // 音频设备优先用输入自带的（采集卡的「数字音频接口 (GATHER CAMn)」），
                        // 没指定才退回「第一个音频设备」。⚠ ffmpeg 的 dshow 不接受"默认设备"，
                        // 必须点名具体设备名（这点与 VLC 可以留空不同）。
                        var adName = !string.IsNullOrWhiteSpace(src.CamAudioDevice) ? src.CamAudioDevice : ad;
                        if (src.CamAudio && !string.IsNullOrWhiteSpace(adName))
                            sb.Append(":audio=\"").Append(adName).Append('"');
                        sb.Append(' ');
                    }
                    break;

                case SourceType.Desktop:
                    // gdigrab 的 desktop 输入可以带偏移与尺寸：PPT 抓放映窗口时只抓那一块，
                    // 不写明就是整屏（老行为）。偏移必须在 -i 之前给。
                    sb.Append("-f gdigrab -framerate 30 ");
                    if (src.CaptureWidth > 0 && src.CaptureHeight > 0)
                    {
                        sb.Append("-offset_x ").Append(src.CaptureLeft).Append(' ');
                        sb.Append("-offset_y ").Append(src.CaptureTop).Append(' ');
                        sb.Append("-video_size ").Append(src.CaptureWidth).Append('x').Append(src.CaptureHeight).Append(' ');
                    }
                    sb.Append("-i desktop ");
                    break;

                case SourceType.Network:
                    // 网络流（模块D）：输入侧参数（RTSP 承载与超时 / HTTP 自愈重连 / UDP 封装声明）
                    // 全部由 NetworkStreamService 统一生成 —— 播放链路与面板预检两处共用同一份，
                    // 避免「预检能连上、播放连不上」这种参数不一致导致的鬼故事。
                    // ⚠ 地址必须用 BuildUrl 而不是 Location：SRT 的 mode/latency/passphrase
                    //   是写在 URL query 里的（ffmpeg 不接受它们当命令行选项），BuildUrl 负责合并。
                    sb.Append(NetworkStreamService.BuildInputArgs(src));
                    sb.Append("-i \"").Append(NetworkStreamService.BuildUrl(src)).Append("\" ");
                    break;

                case SourceType.Ndi:
                    // NDI（模块F）：输入是接收器从 **stdin** 灌进来的「紧凑 BGRA 原始帧」。
                    // 分辨率与帧率在探帧阶段已经拿到（见 StartProcess），必须在这里写死 ——
                    // rawvideo 没有自描述头，ffmpeg 只能完全按命令行给的尺寸解读。
                    // ⚠ pixel_format 必须与接收器要的 NDI 色彩格式一致（都在 bgra 上）。
                    {
                        int w = pr?.Ndi?.Width ?? 1280;
                        int h = pr?.Ndi?.Height ?? 720;
                        int fps = pr?.Ndi?.Fps ?? 30;
                        sb.Append("-f rawvideo -pixel_format bgra -video_size ")
                          .Append(w).Append('x').Append(h)
                          .Append(" -framerate ").Append(fps).Append(" -i - ");
                    }
                    break;
            }
        }

        /// <summary>
        /// 背景输入（filter_complex 里的 [1:v]）。三种来源的取法各不相同，逐个交代：
        ///   · 纯色：lavfi color 源。必须给确定尺寸（lavfi 没法"跟随前景"），用输入里配的
        ///     BgWidth×BgHeight（默认 1920×1080）。它是**无限**的，不会把画面拖结束。
        ///   · 图片：走 image2 解复用器 + `-loop 1` 无限循环。⚠ 不加 -loop 1 只会输出一帧，
        ///     overlay 立刻没素材可用（实测表现是输出空文件 + libx264 EINVAL，极具误导性）。
        ///   · 视频：`-stream_loop -1` 循环播放。配 `-re` 让它按原生帧率走 ——
        ///     不配的话会把整段视频以最快速度反复冲过去，白白烧 CPU 且时钟错乱。
        /// </summary>
        private static void AppendBackgroundInput(StringBuilder sb, InputSource src)
        {
            int w = EvenSize(src.BgWidth), h = EvenSize(src.BgHeight);
            switch (src.BgType)
            {
                case ChromaBackground.Color:
                    sb.Append("-f lavfi -i \"color=c=").Append(NormalizeHex(src.BgColor))
                      .Append(":s=").Append(w).Append('x').Append(h).Append(":r=30\" ");
                    break;

                case ChromaBackground.Image:
                    sb.Append("-f image2 -loop 1 -framerate 30 -i \"")
                      .Append(src.BgPath).Append("\" ");
                    break;

                default:   // ChromaBackground.Video
                    sb.Append("-stream_loop -1 -re -i \"")
                      .Append(src.BgPath).Append("\" ");
                    break;
            }
        }

        /// <summary>
        /// 抠像 + 背景合成的完整 filter_complex（模块C A-2）。
        ///
        /// ── 为什么不用最直觉的写法 ──────────────────────────────────────────
        /// 直觉写法是「背景当底，前景叠上去」：`[背景][抠像后的前景]overlay`。
        /// 但实测发现 **overlay 的输出帧率跟随"主输入"（第一个输入）** ——
        /// 背景当底时，输出帧率 = 背景帧率。背景若是 25fps 的图/视频，
        /// 50fps 的前景会被**砍掉一半帧**（实测：前景 50 + 背景 25 → 输出 25）。
        /// 直播丢帧是不可接受的，所以必须让**前景当主输入**。
        ///
        /// ── 于是怎么让背景"垫在下面" ────────────────────────────────────────
        /// 把前景的 alpha **反相**，得到一个"只有主体之外为白"的遮罩，
        /// 用它当背景的 alpha → 背景只在主体的空档处显形；
        /// 再把这个"被挖空的背景"叠到前景上（前景为主输入）→ 合成结果正确，且帧率跟着前景走。
        /// 整条链：前景抠像 → 取 alpha → 反相 → 当背景的 alpha → 前景(主) ⊕ 挖空背景。
        ///
        /// ── 三个必须踩对的地方（都是实测踩出来的）────────────────────────────
        ///   ① `scale` 与 `pad` 会**丢掉 alpha 平面**（`alphaextract` 会报
        ///      "Requested planes not available"）→ 所以缩放必须排在 chromakey **之前**。
        ///   ② `alphamerge` 要求两个输入**尺寸完全一致**，否则整个 filter 图重新初始化失败
        ///      （报的是 libx264 EINVAL，极具误导性）→ 前景与背景都要先归一到同一个画布尺寸。
        ///   ③ 画布尺寸必须取偶数 —— yuv420p 的色度是 2×2 下采样，奇数尺寸编码器直接拒绝。
        ///
        /// ── 滤镜顺序 ──────────────────────────────────────────────────────
        ///   去隔行 → 几何(裁切/旋转/镜像) → 缩放到画布 → chromakey → 取 alpha → 合成
        ///   去隔行在最前、几何在缩放前，都是为了在"原始像素"上做处理，避免重采样后再插值。
        /// </summary>
        private static string BuildChromaFilter(InputSource src)
        {
            int w = EvenSize(src.BgWidth), h = EvenSize(src.BgHeight);
            var ci = System.Globalization.CultureInfo.InvariantCulture;

            var fg = new List<string>();
            var geo = BuildVideoFilter(src);          // 去隔行 + 裁切/旋转/镜像
            if (geo.Length > 0) fg.Add(geo);
            fg.Add($"scale={w}:{h}");                 // ① 缩放在前（见上）
            fg.Add($"chromakey=color={NormalizeHex(src.ChromaKeyColor)}"
                 + $":similarity={src.ChromaSimilarity.ToString("0.###", ci)}"
                 + $":blend={src.ChromaBlend.ToString("0.###", ci)}");
            fg.Add("format=yuva420p");
            fg.Add("split[fgMain][fgMask]");

            var sb = new StringBuilder(384);
            sb.Append("[0:v]").Append(string.Join(",", fg)).Append(';');
            sb.Append("[fgMask]alphaextract,negate[inv];");

            // 背景归一到同一画布（② 尺寸必须一致）。
            // 纯色源本身已是指定尺寸，不必缩放；图片/视频用"覆盖式"缩放裁切 ——
            // force_original_aspect_ratio=increase 保证不留黑边，crop 把溢出部分裁掉，比例不变形。
            if (src.BgType == ChromaBackground.Color)
                sb.Append("[1:v]format=yuva420p[bg0];");
            else
                sb.Append($"[1:v]scale={w}:{h}:force_original_aspect_ratio=increase,")
                  .Append($"crop={w}:{h},format=yuva420p[bg0];");

            sb.Append("[bg0][inv]alphamerge[bgcut];");
            sb.Append("[fgMain][bgcut]overlay=shortest=1,format=yuv420p[v]");
            return sb.ToString();
        }

        /// <summary>数值取偶数（yuv420p 的色度 2×2 下采样要求；最小 2）。</summary>
        private static int EvenSize(int v)
        {
            if (v < 2) v = 2;
            return v - (v % 2);
        }

        /// <summary>
        /// 颜色值归一成 ffmpeg 认的 0xRRGGBB 形式。
        /// 面板允许用户手输，常见的三种写法（#RRGGBB / RRGGBB / 0xRRGGBB）都收下，
        /// 不认识的值退回默认，绝不让一个手滑把整条预处理链路搞挂。
        /// </summary>
        private static string NormalizeHex(string? s, string fallback = "0x1E1E1E")
        {
            if (string.IsNullOrWhiteSpace(s)) return fallback;
            var t = s.Trim();
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t.Substring(2);
            else if (t.StartsWith("#", StringComparison.Ordinal)) t = t.Substring(1);
            if (t.Length != 6 || !t.All(c => Uri.IsHexDigit(c))) return fallback;
            return "0x" + t.ToUpperInvariant();
        }

        /// <summary>
        /// 这一路的完整视频滤镜链 = 去隔行 + 「裁切 / 旋转 / 镜像」。
        /// 注意：FFmpeg 滤镜语法与 VLC 的完全不同，不能照搬。
        ///
        /// 顺序（有讲究，不能随便换）：
        ///   ① yadif 去隔行必须排**最前** —— 它要在原始场上做插值。先裁切会让画面边缘
        ///      的场序参考发生错位，先旋转更会让「场」的方向整个错掉（行变成列，无从插值）。
        ///   ② 裁切 → ③ 旋转 → ④ 镜像，与 VLC 链的顺序保持一致。
        ///
        /// 裁切用 iw/ih 表达式表达「按源画面尺寸」，这样不需要事先知道源分辨率；
        /// 且必须把宽高取偶数 —— yuv420p 的色度是 2×2 下采样，奇数宽高会让编码器直接拒绝。
        /// 旋转：transpose=1 顺时针 90°、transpose=2 逆时针 90°（即顺时针 270°）、
        ///       180° 没有专用 transpose 类型，用 hflip + vflip 组合。
        /// </summary>
        public static string BuildVideoFilter(InputSource src)
        {
            var chain = new List<string>();

            // 去隔行（模块E B-2）。只在该输入真的需要时才加 —— 对逐行信号做 yadif
            // 会把垂直细节插值糊掉，反而更差。是否「需要」由 NeedDeinterlace 判定。
            // yadif 默认 mode=send_frame：每帧输出一帧，帧率不变（1080i50 出来仍是 50fps 逐行）。
            if (src.NeedDeinterlace) chain.Add("yadif=mode=send_frame");

            if (src.HasCrop)
            {
                if (src.UsePctCrop)
                {
                    // 百分比裁切（精调面板）：写成 iw/ih 表达式交给 ffmpeg 自己按实际尺寸算，
                    // 于是不需要事先知道源分辨率（文件类输入本来也拿不到）。
                    // ⚠ 小数必须用 InvariantCulture 输出（ffmpeg 只认 '.'），否则中文区域会写成 "0,1" 直接报错。
                    string F(double v) => (v / 100.0).ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);
                    string l = F(src.CropPctLeft), t = F(src.CropPctTop);
                    string r = F(src.CropPctRight), b = F(src.CropPctBottom);
                    chain.Add($"crop=trunc(iw*(1-{l}-{r})/2)*2:" +
                              $"trunc(ih*(1-{t}-{b})/2)*2:" +
                              $"trunc(iw*{l}/2)*2:" +
                              $"trunc(ih*{t}/2)*2");
                }
                else
                {
                    chain.Add($"crop=trunc((iw-{src.CropLeft}-{src.CropRight})/2)*2:" +
                              $"trunc((ih-{src.CropTop}-{src.CropBottom})/2)*2:" +
                              $"{src.CropLeft}:{src.CropTop}");
                }
            }

            switch (src.Rotate)
            {
                case 90: chain.Add("transpose=1"); break;
                case 180: chain.Add("hflip"); chain.Add("vflip"); break;
                case 270: chain.Add("transpose=2"); break;
            }

            if (src.FlipH) chain.Add("hflip");
            if (src.FlipV) chain.Add("vflip");

            return string.Join(",", chain);
        }

        /// <summary>
        /// 摄像头采集色彩格式 → ffmpeg dshow 输入参数（VLC 用 dshow-chroma，ffmpeg 得分开写）。
        /// </summary>
        private static string ChromaToInputOption(string chroma)
        {
            if (string.IsNullOrWhiteSpace(chroma)) return "";
            switch (chroma.Trim().ToUpperInvariant())
            {
                case "MJPG":
                case "MJPEG": return "-vcodec mjpeg";
                case "YUY2": return "-vcodec rawvideo -pixel_format yuyv422";
                case "NV12": return "-vcodec rawvideo -pixel_format nv12";
                case "RGB24": return "-vcodec rawvideo -pixel_format bgr24";
                default: return "";
            }
        }

        // ==================== 一次性探测 ====================

        private static int _nvencState;                       // 0=未探测 1=可用 2=不可用
        private static readonly object _nvencLock = new();

        /// <summary>
        /// 探测本机能否真的用 NVENC 编码。
        /// 只查「ffmpeg 是否编译了 h264_nvenc」是不够的 —— 编译进去了但机器没有可用显卡时
        /// 照样开不了。所以这里真跑一次 256×256 的一帧编码，用退出码定论，结果缓存。
        /// </summary>
        private static bool NvencAvailable()
        {
            if (_nvencState != 0) return _nvencState == 1;
            lock (_nvencLock)
            {
                if (_nvencState != 0) return _nvencState == 1;
                bool ok = false;
                try
                {
                    var psi = new ProcessStartInfo(FfmpegOutput.FfmpegPath,
                        "-hide_banner -loglevel error -f lavfi -i nullsrc=s=256x256:d=0.1 " +
                        "-frames:v 1 -c:v h264_nvenc -f null -")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                        StandardErrorEncoding = Encoding.UTF8
                    };
                    using var p = Process.Start(psi);
                    if (p != null)
                    {
                        try { p.StandardError.ReadToEnd(); } catch { }
                        p.WaitForExit(8000);
                        try { ok = p.HasExited && p.ExitCode == 0; } catch { ok = false; }
                    }
                }
                catch { ok = false; }

                _nvencState = ok ? 1 : 2;
                return ok;
            }
        }

        /// <summary>运行期发现 NVENC 不可用（启动即退）→ 本进程内不再尝试。</summary>
        private static void MarkNvencUnusable() => _nvencState = 2;

        /// <summary>
        /// 「启动即退」的这口锅该不该算在编码器头上。
        ///
        /// 判据：① 完全没有输出（tail 为空）→ 多半是被外部终止，不算；
        ///       ② 输出指向输入侧（打不开设备 / 设备被占 / 实时缓冲溢出）→ 不算；
        ///       ③ 输出里明确出现编码器关键词 → 算。
        /// 宁可少拉黑（最坏就是每次启动多花 900ms 试一次 NVENC），
        /// 也不要错拉黑（后果是整场直播静默降级成软编）。
        /// </summary>
        private static bool LooksLikeEncoderFault(string tail)
        {
            if (string.IsNullOrWhiteSpace(tail)) return false;
            if (Regex.IsMatch(tail,
                    "error opening|I/O error|device already in use|Could not run graph|" +
                    "No such device|real-time buffer|Immediate exit requested",
                    RegexOptions.IgnoreCase))
                return false;
            return Regex.IsMatch(tail,
                "nvenc|encoder|encode|No capable|cuda|OpenEncodeSession",
                RegexOptions.IgnoreCase);
        }

        private static (string? Video, string? Audio) _dshow;
        private static bool _dshowProbed;
        private static readonly object _dshowLock = new();

        /// <summary>
        /// 取「第一个摄像头」与「第一个音频设备」的名字。
        /// 用途：输入源没指定设备名时要给 ffmpeg 一个具体名字（ffmpeg 的 dshow 不接受"默认设备"），
        /// 以及摄像头要采集声音时得点名音频设备。
        /// </summary>
        private static (string? Video, string? Audio) ProbeDshowDevices()
        {
            if (_dshowProbed) return _dshow;
            lock (_dshowLock)
            {
                if (_dshowProbed) return _dshow;
                _dshowProbed = true;
                try
                {
                    var psi = new ProcessStartInfo(FfmpegOutput.FfmpegPath,
                        "-hide_banner -loglevel info -f dshow -list_devices true -i dummy")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                        // ffmpeg 往管道写 UTF-8，不指定编码会按 GBK 解出乱码设备名，
                        // 拼进命令行就是打不开的设备（见 CaptureDeviceService 同款注释）
                        StandardErrorEncoding = Encoding.UTF8
                    };
                    using var p = Process.Start(psi);
                    if (p != null)
                    {
                        var err = "";
                        try { err = p.StandardError.ReadToEnd(); } catch { }
                        try { p.WaitForExit(8000); } catch { }

                        // 形如：[dshow @ 0000] "USB Camera" (video)
                        var ms = Regex.Matches(err, "\"([^\"]+)\"\\s*\\((video|audio)\\)");
                        string? v = null, a = null;
                        foreach (Match m in ms)
                        {
                            var name = m.Groups[1].Value;
                            var kind = m.Groups[2].Value;
                            if (kind == "video" && v == null) v = name;
                            else if (kind == "audio" && a == null) a = name;
                        }
                        _dshow = (v, a);
                    }
                }
                catch { }
                return _dshow;
            }
        }
    }
}
