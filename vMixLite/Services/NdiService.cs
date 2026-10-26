using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using NewTek;
using static NewTek.NDIlib;
using Cutlance.Models;

namespace Cutlance.Services
{
    /// <summary>发现到的一个 NDI 源（局域网里的一个 NDI 发送端）。</summary>
    public sealed class NdiSourceInfo
    {
        /// <summary>完整源名，形如 "PC-20260404UJHW (Camera 1)"。连接与显示都用它。</summary>
        public string Name { get; set; } = "";

        /// <summary>源地址（形如 "192.168.1.5:5960"），仅作显示参考。</summary>
        public string Url { get; set; } = "";

        public override string ToString() => Name;
        public override bool Equals(object? o) => o is NdiSourceInfo s && s.Name == Name;
        public override int GetHashCode() => Name.GetHashCode();
    }

    /// <summary>
    /// NDI 运行时服务（模块F）。
    ///
    /// ── 为什么是「自写 .NET 接收器」而不是给 ffmpeg 打 NDI 补丁 ──────────────
    /// ffmpeg 官方**主动移除**了 NDI（当年 NewTek 分发的 ffmpeg 二进制编入了非自由组件，
    /// 违反 ffmpeg 许可 → 团队把 NDI 从源码里删掉了）。要用就得自己打社区补丁重编译一份，
    /// 而且那份二进制**不能对外分发**。本机实测 VLC 3.0 也没有 NDI 插件。
    /// 所以这里直接调 NDI 官方 SDK（经 .NET 包装层），自己收帧、自己喂给 FFmpeg，
    /// 既避开了「编译并分发 ffmpeg」的坑，也复用了现有的组播链路。
    ///
    /// ── 原生库从哪来（重要）──────────────────────────────────────────────
    /// NuGet 那个包装包**只含托管层**，不含也不授权原生 `Processing.NDI.Lib.x64.dll`。
    /// 本服务在运行时按优先级查找（见 <see cref="ResolveNdi"/>）：
    ///   ① 程序输出目录 → ② 系统目录（装了官方 NDI Runtime 就在这里）
    ///   → ③ NewTek 官方安装路径 → ④ 本机 vMix 自带的副本（开发机兜底）
    /// ⚠ 正式部署请装官方 NDI Runtime（免费，`winget install NDI.NDIRuntime`）；
    ///   第 ④ 条只是开发机「零下载先把功能跑通」的临时手段。
    /// </summary>
    public sealed class NdiService
    {
        public static NdiService Instance { get; } = new();

        private static readonly object _initGate = new();
        private bool _inited;
        private bool _initFailed;

        private IntPtr _finder = IntPtr.Zero;
        private IntPtr _finderIpsPtr = IntPtr.Zero;      // _finder 用的 networks.ips 字符串（非托管，须活到 find_destroy）
        private string _extraDiscoveryIps = "";          // 用户手工追加的发现目标 IP
        private readonly object _scanGate = new();
        private List<NdiSourceInfo> _sources = new();
        private DateTime _lastScanUtc = DateTime.MinValue;

        /// <summary>
        /// 「源名 → 源地址」常驻缓存。**不随 ReleaseFinder 清空** —— 因为面板一关就释放
        /// finder 了，而用户随后点「添加为输入」时还要靠这个地址去连（见 OpenReceiver）。
        /// </summary>
        private readonly Dictionary<string, string> _knownUrls = new(StringComparer.Ordinal);

        /// <summary>
        /// 用户手工追加的「发现目标 IP」（逗号分隔）。
        /// 局域网里另一台机器的 NDI 源，若 mDNS 不通（本机多虚拟网卡/VPN 时很常见），
        /// 把对方 IP 填进来即可走单播发现直接找到。改这个值会重建发现器。
        /// </summary>
        public string ExtraDiscoveryIps
        {
            get => _extraDiscoveryIps;
            set
            {
                var v = value ?? "";
                if (v == _extraDiscoveryIps) return;
                _extraDiscoveryIps = v;
                ReleaseFinder();   // 下次 Scan 会用新列表重建
            }
        }

        /// <summary>
        /// 本次扫描是否走了「单播发现兜底」（mDNS 不通时的救命通道）。
        /// 面板据此提示用户「已启用单播发现」。
        /// </summary>
        public bool UsingUnicastDiscovery { get; private set; }

        /// <summary>最近一次失败的一句话说明（可直接显示）。</summary>
        public string LastError { get; private set; } = "";

        private NdiService() { }

        // ==================== 诊断日志 ====================

        /// <summary>
        /// NDI 专用诊断日志：<c>%TEMP%\cutlance-ndi.log</c>。
        /// 「NDI 用不了」这类问题的线索全在异常的内层（找不到原生库 / 位数不对 / 缺依赖），
        /// 界面上一行字放不下，所以另落一份可回溯的日志。出问题先看它。
        /// </summary>
        public static void Log(string msg)
        {
            try
            {
                var path = Path.Combine(Path.GetTempPath(), "cutlance-ndi.log");
                File.AppendAllText(path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + msg + Environment.NewLine);
            }
            catch { }
        }

        /// <summary>把异常链（含全部 InnerException）拍平成一行，用于日志与界面。</summary>
        private static string Flatten(Exception ex)
        {
            var sb = new System.Text.StringBuilder();
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (sb.Length > 0) sb.Append("　←　");
                sb.Append(e.GetType().Name).Append(": ").Append(e.Message);
            }
            return sb.ToString();
        }

        // ==================== 初始化 ====================

        /// <summary>NDI 运行时是否可用（首次调用会尝试初始化并缓存结果）。</summary>
        public bool IsAvailable => EnsureInit();

        public bool EnsureInit()
        {
            if (_inited) return true;
            if (_initFailed) return false;
            lock (_initGate)
            {
                if (_inited) return true;
                if (_initFailed) return false;
                try
                {
                    EnsureNativeLibrary();
                    if (!NDIlib.initialize())
                    {
                        LastError = "NDI 运行时初始化失败（NDIlib_initialize 返回 false）";
                        _initFailed = true;
                        return false;
                    }
                    _inited = true;
                    LastError = "";
                    Log("EnsureInit 成功：NDI 运行时已就绪");
                    return true;
                }
                catch (Exception ex)
                {
                    // ⚠ 一定要把 InnerException 一路展开：包装层的静态构造抛的是
                    //   TypeInitializationException，真正的失败原因（找不到原生库 / 位数不对 /
                    //   缺依赖 DLL）全在内层。只看外层等于什么都没说。
                    var full = Flatten(ex);
                    LastError = "NDI 运行时加载失败：" + full
                              + "　（装官方 NDI Runtime 可彻底解决：winget install NDI.NDIRuntime）";
                    Log("EnsureInit 失败：" + full);
                    _initFailed = true;
                    return false;
                }
            }
        }

        /// <summary>
        /// 清掉「初始化失败」的缓存，下次调用重新尝试。
        /// 面板的「刷新」会先调这个 —— 用户中途装了 NDI Runtime 就不必重启程序。
        /// </summary>
        public void Reinit()
        {
            lock (_initGate)
            {
                if (_inited) return;
                _initFailed = false;
                LastError = "";
            }
        }

        /// <summary>
        /// 确保原生库 <c>Processing.NDI.Lib.x64.dll</c> 出现在**程序目录**里。
        ///
        /// ── 为什么不能自己注册 DllImport 解析器（本机实测的血坑）────────────────
        /// 包装包 `NDILibDotNetCoreBase` 的 `NewTek.NDIlib` **静态构造里自己会
        /// 调 NativeLibrary.SetDllImportResolver**。这东西是「一次性」的：
        /// 谁先注册谁生效，后注册的直接抛 `InvalidOperationException:
        /// A resolver is already set for the assembly`。
        /// 我们一度抢先注册，结果把它的解析器顶掉 → 它的静态构造抛异常
        /// → 表现为 `TypeInitializationException`，界面上只能说「NDI 加载失败」，
        /// 一点线索都没有（这个内层原因靠 cutlance-ndi.log 才挖出来）。
        /// **正确做法：什么都别抢，把 DLL 摆到它自己会去找的地方 —— 程序目录。**
        ///
        /// 程序目录里没有时，依次尝试从系统目录 / 官方 Runtime 安装路径 / vMix 自带副本
        /// 复制一份过来（vMix 那条只是开发机兜底；正式部署请装官方 NDI Runtime）。
        /// </summary>
        private static void EnsureNativeLibrary()
        {
            const string x64 = "Processing.NDI.Lib.x64.dll";
            var target = Path.Combine(AppContext.BaseDirectory, x64);
            if (File.Exists(target)) return;

            foreach (var src in FallbackNativePaths())
            {
                try
                {
                    if (string.IsNullOrEmpty(src) || !File.Exists(src)) continue;
                    File.Copy(src, target, false);
                    Log("EnsureNativeLibrary: 已从 " + src + " 复制原生库到程序目录");
                    return;
                }
                catch (Exception ex) { Log("EnsureNativeLibrary: 复制失败 " + src + " : " + ex.Message); }
            }
            Log("EnsureNativeLibrary: 程序目录没有原生库，也没找到可复制的来源 → "
              + "请安装官方 NDI Runtime（winget install NDI.NDIRuntime）");
        }

        /// <summary>原生库的「可复制来源」候选（不含程序目录本身）。</summary>
        private static IEnumerable<string> FallbackNativePaths()
        {
            const string x64 = "Processing.NDI.Lib.x64.dll";

            // ⚠ 迭代器方法里不能「在有 catch 的 try 中 yield」，所以先把值取出来
            string sys = "";
            try { sys = Environment.SystemDirectory; } catch { }
            if (!string.IsNullOrEmpty(sys)) yield return Path.Combine(sys, x64);

            yield return @"C:\Program Files\NewTek\NDI 6 Runtime\v6\" + x64;
            yield return @"C:\Program Files\NDI\NDI 6 Runtime\v6\" + x64;
            yield return @"C:\Program Files\NDI\NDI 6 Runtime\v6\x64\" + x64;
            // 开发机兜底：vMix 自带的 NDI 6.x 运行时（文件名少一个 .x64，二进制相同）
            yield return @"C:\Program Files (x86)\vMix\ndi\x64\Processing.NDI.Lib.dll";
        }

        // ==================== 源发现 ====================

        /// <summary>当前已知的源列表快照。</summary>
        public IReadOnlyList<NdiSourceInfo> Sources
        {
            get { lock (_scanGate) return _sources.ToList(); }
        }

        /// <summary>
        /// 拼出要交给 NDI 的「单播发现目标 IP 列表」（= SDK 的 p_extra_ips，等价于
        /// 配置文件里的 <c>networks.ips</c>）。
        ///
        /// ── 为什么必须做这件事（本机实测踩出来的坑）────────────────────────────
        /// NDI 默认靠 mDNS 多播（224.0.0.251）自动发现。本机有多块虚拟网卡 / VPN 适配器
        /// （vgate0、多块 169.254 自动配置地址），NDI 自动选网卡会选错 → **发现列表恒为空**。
        /// 实测：同一台机器上「先起一个发送端、再起一个发现器」也拿不到任何源；
        /// 用 Python 显式指定接口做多播回环测试却是通的 → 说明是 NDI 选错了接口，
        /// 不是系统屏蔽多播。
        /// 解法：给 finder 显式带上本机所有 IPv4 + 127.0.0.1，让 NDI 走**单播**去问
        /// 本机 5960 端口的 NDI 服务。加完之后「发现 → 连接 → 收帧」全链路立刻正常
        /// （实测：1280x720@30 源，连接耗时 27ms）。
        /// 用户还可以用 <see cref="ExtraDiscoveryIps"/> 追加局域网里别的机器 IP。
        /// </summary>
        private string BuildExtraIps()
        {
            var ips = new List<string> { "127.0.0.1" };
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        var s = ua.Address.ToString();
                        if (!ips.Contains(s)) ips.Add(  s);
                    }
                }
            }
            catch { /* 取网卡列表失败 → 至少还有 127.0.0.1 */ }

            foreach (var extra in (_extraDiscoveryIps ?? "").Split(new[] { ',', ';', ' ' },
                                                                    StringSplitOptions.RemoveEmptyEntries))
            {
                var s = extra.Trim();
                if (s.Length > 0 && !ips.Contains(s)) ips.Add(s);
            }

            UsingUnicastDiscovery = ips.Count > 0;
            return string.Join(",", ips) + ",";
        }

        /// <summary>
        /// 刷新源列表。NDI 的发现是异步的（mDNS/单播发现服务），所以首次扫描可能为空，
        /// 需要过一两秒再刷一次 —— 面板上的「刷新」按钮就是干这个的。
        /// </summary>
        public List<NdiSourceInfo> Scan(int waitMs = 1200)
        {
            var list = new List<NdiSourceInfo>();
            if (!EnsureInit()) return list;

            lock (_scanGate)
            {
                try
                {
                    if (_finder == IntPtr.Zero)
                    {
                        // ⚠ 这个字符串必须活到 find_destroy，所以存字段、在 ReleaseFinder 里释放。
                        _finderIpsPtr = Marshal.StringToCoTaskMemUTF8(BuildExtraIps());
                        var fc = new find_create_t
                        {
                            show_local_sources = true,   // 本机自己发出去的源也要能看到（自测/本机回环）
                            p_groups = IntPtr.Zero,      // 默认组 = public
                            p_extra_ips = _finderIpsPtr  // ← 单播发现兜底，见 BuildExtraIps 注释
                        };
                        _finder = NDIlib.find_create_v2(ref fc);
                        if (_finder == IntPtr.Zero)
                        {
                            LastError = "NDI 源发现器创建失败";
                            return list;
                        }
                    }

                    if (waitMs > 0) NDIlib.find_wait_for_sources(_finder, (uint)waitMs);

                    uint n = 0;
                    var ptr = NDIlib.find_get_current_sources(_finder, ref n);
                    int sz = Marshal.SizeOf(typeof(source_t));
                    for (uint i = 0; i < n; i++)
                    {
                        var st = Marshal.PtrToStructure<source_t>(ptr + (int)(i * (uint)sz));
                        var nm = Marshal.PtrToStringUTF8(st.p_ndi_name);
                        if (string.IsNullOrEmpty(nm)) continue;
                        var url = Marshal.PtrToStringUTF8(st.p_url_address) ?? "";
                        list.Add(new NdiSourceInfo { Name = nm, Url = url });
                        if (url.Length > 0) _knownUrls[nm] = url;   // 常驻缓存，供 OpenReceiver 直连
                    }
                    _sources = list;
                    _lastScanUtc = DateTime.UtcNow;
                    LastError = "";
                }
                catch (Exception ex)
                {
                    LastError = "NDI 源扫描失败：" + ex.Message;
                }
            }
            return list;
        }

        /// <summary>距上次扫描过了多久（面板据此提示「结果可能过期」）。</summary>
        public TimeSpan SinceLastScan =>
            _lastScanUtc == DateTime.MinValue ? TimeSpan.MaxValue : DateTime.UtcNow - _lastScanUtc;

        /// <summary>
        /// 释放「源发现器」。关闭 NDI 面板时调用。
        /// ⚠ 只销毁 finder，**不动**已建立的接收/发送会话 —— 那些归各自的输入链路持有。
        /// </summary>
        public void ReleaseFinder()
        {
            lock (_scanGate)
            {
                try { if (_finder != IntPtr.Zero) { NDIlib.find_destroy(_finder); _finder = IntPtr.Zero; } } catch { }
                try { if (_finderIpsPtr != IntPtr.Zero) { Marshal.FreeCoTaskMem(_finderIpsPtr); _finderIpsPtr = IntPtr.Zero; } } catch { }
                _sources = new List<NdiSourceInfo>();
                _lastScanUtc = DateTime.MinValue;
                // ⚠ 注意：_knownUrls 故意不清 —— 见字段注释。
            }
        }

        // ==================== 接收器 ====================

        /// <summary>
        /// 连上一个 NDI 源并开启接收会话（尚未绑定 ffmpeg，先探出分辨率）。
        /// 失败时 <paramref name="error"/> 是一句可直接显示的原因。
        /// </summary>
        public NdiInputSession? OpenReceiver(string sourceName, out string error)
        {
            error = "";
            if (!EnsureInit()) { error = LastError; return null; }
            if (string.IsNullOrWhiteSpace(sourceName)) { error = "NDI 源名为空"; return null; }

            IntPtr namePtr = IntPtr.Zero;
            IntPtr urlPtr = IntPtr.Zero;
            IntPtr recv = IntPtr.Zero;
            try
            {
                // ⚠ 关键：连之前先把「源名 → 源地址(url)」从常驻缓存里捞出来一起交给 NDI。
                //   只给名字的话，NDI 内部还要再做一次名字解析（又是一次 mDNS）——
                //   本机 mDNS 不通，那一步同样是死的。带上 url 就完全绕开解析。
                string url = "";
                lock (_scanGate)
                {
                    if (_knownUrls.TryGetValue(sourceName, out var cached) && !string.IsNullOrEmpty(cached))
                        url = cached;
                }

                namePtr = Marshal.StringToCoTaskMemUTF8(sourceName);
                urlPtr = url.Length > 0 ? Marshal.StringToCoTaskMemUTF8(url) : IntPtr.Zero;
                var rc = new recv_create_v3_t
                {
                    source_to_connect_to = new source_t { p_ndi_name = namePtr, p_url_address = urlPtr },
                    // ⚠ 统一要 BGRA：FFmpeg 的 rawvideo 直接吃 bgra，省一次色彩转换的代码。
                    color_format = recv_color_format_e.recv_color_format_BGRX_BGRA,
                    bandwidth = recv_bandwidth_e.recv_bandwidth_highest,
                    allow_video_fields = false,     // 我们要整帧（不做隔行半场）
                    p_ndi_recv_name = IntPtr.Zero
                };
                recv = NDIlib.recv_create_v3(ref rc);
                if (recv == IntPtr.Zero) { error = "NDI 接收器创建失败（源名：" + sourceName + "）"; return null; }

                var session = new NdiInputSession(sourceName, recv);
                // ⚠ 超时不能太长：这条调用链是同步的（TryGetStreamUrl → StartProcess），
                //   源离线时会把调用方按住这么久。正常在线的源 100ms 内就出第一帧。
                if (!session.ProbeFirstFrame(3000, out var perr))
                {
                    session.Dispose();
                    error = perr;
                    return null;
                }
                return session;
            }
            catch (Exception ex)
            {
                try { if (recv != IntPtr.Zero) NDIlib.recv_destroy(recv); } catch { }
                error = "连接 NDI 源失败：" + ex.Message;
                return null;
            }
            finally
            {
                // ⚠ recv_create_v3 会复制源名/源地址，这边可以立即释放临时字符串。
                if (namePtr != IntPtr.Zero) Marshal.FreeCoTaskMem(namePtr);
                if (urlPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(urlPtr);
            }
        }

        // ==================== 发送器 ====================

        /// <summary>
        /// 起一个 NDI 发送器（把本机画面广播成 NDI 源）。
        /// <paramref name="sourceName"/> 就是别的机器上看到的名字，建议形如 "Cutlance (主机名)"。
        /// </summary>
        public NdiSenderSession? CreateSender(string sourceName, out string error)
        {
            error = "";
            if (!EnsureInit()) { error = LastError; return null; }
            if (string.IsNullOrWhiteSpace(sourceName)) { error = "NDI 输出名不能为空"; return null; }

            try
            {
                var namePtr = Marshal.StringToCoTaskMemUTF8(sourceName);
                var sc = new send_create_t
                {
                    p_ndi_name = namePtr,
                    p_groups = IntPtr.Zero,
                    clock_video = true,    // 由 NDI 自己按帧率配速（我们的送帧节奏由合成器节拍决定）
                    clock_audio = false    // 暂不发送音频
                };
                var send = NDIlib.send_create(ref sc);
                if (send == IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(namePtr);
                    error = "NDI 发送器创建失败（名字被占用？）：" + sourceName;
                    return null;
                }
                // ⚠ namePtr 交给会话持有：sender 生命周期内 NDI 可能仍需读取该名字。
                return new NdiSenderSession(sourceName, send, namePtr);
            }
            catch (Exception ex)
            {
                error = "创建 NDI 发送器失败：" + ex.Message;
                return null;
            }
        }

        /// <summary>程序退出时的同步收尾（销毁 finder）。</summary>
        public void Shutdown()
        {
            lock (_scanGate)
            {
                try { if (_finder != IntPtr.Zero) { NDIlib.find_destroy(_finder); _finder = IntPtr.Zero; } } catch { }
                try { if (_finderIpsPtr != IntPtr.Zero) { Marshal.FreeCoTaskMem(_finderIpsPtr); _finderIpsPtr = IntPtr.Zero; } } catch { }
            }
            try { if (_inited) NDIlib.destroy(); } catch { }
            _inited = false;
        }
    }

    /// <summary>
    /// 一路 NDI 接收会话：连上某个源 → 收 BGRA 帧 → 写进 FFmpeg 的 stdin。
    ///
    /// 为什么要「先探帧再接管」两步走：
    ///   FFmpeg 的 rawvideo 输入必须在命令行里**写死分辨率**（-video_size WxH），
    ///   而 NDI 的分辨率只有收到第一帧才知道。所以先收一帧拿到 W/H/帧率，再起 FFmpeg。
    ///   探测到的那一帧会被缓存，接入 stdin 时先补写出去，不浪费。
    /// </summary>
    public sealed class NdiInputSession : IDisposable
    {
        public string SourceName { get; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int FpsN { get; private set; } = 30;
        public int FpsD { get; private set; } = 1;
        /// <summary>取整后的帧率（给 ffmpeg -framerate 用）。</summary>
        public int Fps => FpsD > 0 ? Math.Max(1, (int)Math.Round((double)FpsN / FpsD)) : 30;

        private readonly IntPtr _recv;
        private Thread? _pump;
        private volatile bool _stop;
        private Stream? _stdin;
        private byte[]? _firstFrame;          // 探测到但还没写出去的首帧（紧凑 BGRA）
        private readonly object _gate = new();
        private volatile bool _stdinBroken;

        /// <summary>stdin 写入失败（ffmpeg 已退出）时触发，供上层做重连。</summary>
        public event Action<NdiInputSession>? StdinBroken;

        internal NdiInputSession(string sourceName, IntPtr recv)
        {
            SourceName = sourceName;
            _recv = recv;
        }

        /// <summary>接收第一帧以确定分辨率/帧率。同时把该帧缓存下来。</summary>
        internal bool ProbeFirstFrame(int timeoutMs, out string error)
        {
            error = "";
            var deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                var vf = new video_frame_v2_t();
                var af = new audio_frame_v3_t();
                var mf = new metadata_frame_t();
                var ft = NDIlib.recv_capture_v3(_recv, ref vf, ref af, ref mf, 1000);

                if (ft == frame_type_e.frame_type_video && vf.p_data != IntPtr.Zero)
                {
                    Width = vf.xres;
                    Height = vf.yres;
                    FpsN = vf.frame_rate_N > 0 ? vf.frame_rate_N : 30;
                    FpsD = vf.frame_rate_D > 0 ? vf.frame_rate_D : 1;
                    _firstFrame = ExtractTightBgra(ref vf);
                    NDIlib.recv_free_video_v2(_recv, ref vf);
                    return true;
                }
                if (ft == frame_type_e.frame_type_audio)
                {
                    NDIlib.recv_free_audio_v3(_recv, ref af);
                    continue;
                }
                // 源存在但暂时没画面（只有音频）→ 继续等
            }
            error = "NDI 源「" + SourceName + "」在 " + timeoutMs + "ms 内没有视频帧"
                  + "（可能只发音频，或源已离线）";
            return false;
        }

        /// <summary>接上 FFmpeg 的 stdin，开始持续喂帧（首帧先补写）。</summary>
        public void Attach(Stream stdin)
        {
            lock (_gate)
            {
                _stdin = stdin;
                _stop = false;
                _stdinBroken = false;
            }

            // 先把手头缓存的首帧写出去
            var first = _firstFrame;
            _firstFrame = null;
            if (first != null)
            {
                try { stdin.Write(first, 0, first.Length); stdin.Flush(); }
                catch { MarkBroken(); return; }
            }

            _pump = new Thread(Pump) { IsBackground = true, Name = "ndi-pump" };
            _pump.Start();
        }

        private void Pump()
        {
            while (!_stop)
            {
                try
                {
                    var vf = new video_frame_v2_t();
                    var af = new audio_frame_v3_t();
                    var mf = new metadata_frame_t();
                    var ft = NDIlib.recv_capture_v3(_recv, ref vf, ref af, ref mf, 500);

                    if (ft == frame_type_e.frame_type_video && vf.p_data != IntPtr.Zero)
                    {
                        var tight = ExtractTightBgra(ref vf);
                        NDIlib.recv_free_video_v2(_recv, ref vf);

                        var s = _stdin;
                        if (s == null) return;
                        s.Write(tight, 0, tight.Length);
                        s.Flush();
                    }
                    else if (ft == frame_type_e.frame_type_audio)
                    {
                        NDIlib.recv_free_audio_v3(_recv, ref af);
                    }
                }
                catch (ObjectDisposedException) { return; }
                catch (IOException) { MarkBroken(); return; }
                catch { /* 单帧异常不致命，继续 */ }
            }
        }

        private void MarkBroken()
        {
            if (_stdinBroken) return;
            _stdinBroken = true;
            try { StdinBroken?.Invoke(this); } catch { }
        }

        /// <summary>
        /// 把一帧 BGRA 抽成「紧凑」字节数组。
        ///
        /// 为什么要这一步：NDI 帧的 line_stride_in_bytes 常带行对齐填充
        /// （实测 640 宽时 stride=2560=640×4 正好紧凑，但 1080p 源常见 1920×4 再补到 4KB 对齐），
        /// 而 FFmpeg 的 rawvideo 输入**假定数据是紧密打包的**。不做紧凑化会得到斜切的画面。
        /// </summary>
        private byte[] ExtractTightBgra(ref video_frame_v2_t vf)
        {
            int rowBytes = vf.xres * 4;
            int h = vf.yres;
            int stride = vf.line_stride_in_bytes > 0 ? vf.line_stride_in_bytes : rowBytes;
            var buf = new byte[rowBytes * h];

            if (stride == rowBytes)
            {
                Marshal.Copy(vf.p_data, buf, 0, buf.Length);
            }
            else
            {
                for (int y = 0; y < h; y++)
                    Marshal.Copy(vf.p_data + y * stride, buf, y * rowBytes, rowBytes);
            }
            return buf;
        }

        public void Stop()
        {
            _stop = true;
            try { _pump?.Join(600); } catch { }
            _pump = null;
            lock (_gate) { _stdin = null; }
        }

        public void Dispose()
        {
            Stop();
            try { NDIlib.recv_destroy(_recv); } catch { }
        }
    }

    /// <summary>
    /// 一路 NDI 发送会话：把 BGRA 帧广播到网络上作为一个 NDI 源。
    /// 实现 <see cref="ILanFrameSink"/>，于是抓屏分发那段代码不必知道底下是 NDI
    /// —— 将来接 OMT 只需换一个实现（见 OutputTransportKind 的说明）。
    /// </summary>
    public sealed class NdiSenderSession : ILanFrameSink
    {
        public string SourceName { get; }

        /// <summary>ILanFrameSink：对外广播的源名。</summary>
        public string Name => SourceName;

        private readonly IntPtr _send;
        private IntPtr _namePtr;
        private IntPtr _frameBuf = IntPtr.Zero;
        private int _bufSize;
        private readonly object _gate = new();
        private long _framesSent;

        internal NdiSenderSession(string sourceName, IntPtr send, IntPtr namePtr)
        {
            SourceName = sourceName;
            _send = send;
            _namePtr = namePtr;
        }

        /// <summary>已发送帧数（状态显示用）。</summary>
        public long FramesSent => Interlocked.Read(ref _framesSent);

        /// <summary>当前连上来的接收端数量（别的机器/程序有没有在看）。</summary>
        public int Connections
        {
            get
            {
                try { return NDIlib.send_get_no_connections(_send, 0u); }
                catch { return 0; }
            }
        }

        /// <summary>发一帧。数据为紧凑 BGRA（宽 × 高 × 4）。</summary>
        public void SendBgra(byte[] bgra, int width, int height, int fpsN, int fpsD)
        {
            if (bgra == null || width <= 0 || height <= 0) return;
            int need = width * height * 4;
            if (bgra.Length < need) return;

            lock (_gate)
            {
                if (_frameBuf == IntPtr.Zero || _bufSize < need)
                {
                    if (_frameBuf != IntPtr.Zero) Marshal.FreeHGlobal(_frameBuf);
                    _frameBuf = Marshal.AllocHGlobal(need);
                    _bufSize = need;
                }
                Marshal.Copy(bgra, 0, _frameBuf, need);

                var vf = new video_frame_v2_t
                {
                    xres = width,
                    yres = height,
                    FourCC = FourCC_type_e.FourCC_type_BGRA,
                    frame_rate_N = fpsN > 0 ? fpsN : 30,
                    frame_rate_D = fpsD > 0 ? fpsD : 1,
                    picture_aspect_ratio = (float)width / height,
                    frame_format_type = frame_format_type_e.frame_format_type_progressive,
                    timecode = NDIlib.send_timecode_synthesize,   // 让 NDI 自己合成时码
                    p_data = _frameBuf,
                    line_stride_in_bytes = width * 4,
                    p_metadata = IntPtr.Zero,
                    timestamp = 0
                };
                try
                {
                    NDIlib.send_send_video_v2(_send, ref vf);
                    Interlocked.Increment(ref _framesSent);
                }
                catch { }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                try { NDIlib.send_destroy(_send); } catch { }
                try { if (_frameBuf != IntPtr.Zero) { Marshal.FreeHGlobal(_frameBuf); _frameBuf = IntPtr.Zero; } } catch { }
                try { if (_namePtr != IntPtr.Zero) { Marshal.FreeCoTaskMem(_namePtr); _namePtr = IntPtr.Zero; } } catch { }
            }
        }
    }
}
