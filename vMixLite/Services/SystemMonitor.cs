using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Cutlance.Services
{
    /// <summary>
    /// 系统资源监控：CPU / 内存 / 显存占用（老板 2026-10-17 要求「在下面加一个系统监控提醒：CPU 内存 显存 报警」）。
    ///
    /// ── 为什么这三项 ──
    ///   · CPU：软编（libx264）或大量预处理时最先吃满，吃满就开始掉帧；
    ///   · 内存：多路 1080p 缓冲区叠加时增长最快，爆了会换页 → 画面卡顿；
    ///   · 显存：NVENC + D3D11 硬解/硬渲的落脚点，显存一爆，硬解直接失败 → 黑画面。
    ///
    /// ── 数据源（均为零第三方依赖，2026-10-17 在本机逐项实测选定）──
    ///   · CPU  —— kernel32!GetSystemTimes 两次采样求差（**全系统**占用，不是本进程）；
    ///             若自己算进程占用，编码跑在 ffmpeg 子进程里，主进程反而看不出来。
    ///   · 内存 —— kernel32!GlobalMemoryStatusEx（物理内存总量 / 可用）。
    ///   · 显存 —— NVML（NVIDIA 显卡驱动自带的 nvml.dll），一条调用同时拿到 total / used。
    ///             实测本机：RTX 5070 Ti Laptop 12227 MiB 总、约 1052 MiB 已用，与 nvidia-smi 一致。
    ///             非 NVIDIA 机器上 nvml.dll 不存在 → GpuAvailable=false，界面显示「显存 --」，
    ///             只是这一项没有数据，绝不抛异常、不影响另外两项。
    ///
    /// ⚠ 设计原则：**只读 + 绝不抛**。这是在直播程序里按秒轮询的代码，
    ///   任何一次异常都可能打断输出，所以对外只暴露 Sample()，内部逐段 try/catch 兜底。
    /// </summary>
    public static class SystemMonitor
    {
        // ============================== 对外结果 ==============================

        /// <summary>一次采样结果。所有百分比已归一化到 0–100。</summary>
        public sealed class Snapshot
        {
            /// <summary>首次采样（还没有基线）时为 false —— 此时 CpuPercent 的 0 是「不知道」而不是「很闲」。</summary>
            public bool CpuValid;
            public double CpuPercent;

            /// <summary>
            /// 本进程 CPU 占**全系统**的百分比（0–100）。
            /// 为什么要单独看它：程序一旦改成 CPU 合成，8 路出帧 + 混合全在本进程里，
            /// 系统 CPU 高到底是不是我们造成的，靠这一项才分得清。
            /// 注意不含 ffmpeg 子进程（编码另有专人负责，见 PerfAdvice 的说明）。
            /// </summary>
            public double OwnCpuPercent;

            public int CpuCores;

            public double MemUsedGb;
            public double MemTotalGb;
            public double MemPercent;

            /// <summary>显存取不到（非 NVIDIA / 驱动未装 NVML）时为 false，其余字段无意义。</summary>
            public bool GpuAvailable;
            public string GpuName = "";
            public double GpuUsedMb;
            public double GpuTotalMb;
            public double GpuPercent;
        }

        // ============================== 本进程 CPU ==============================

        private static System.Diagnostics.Process? _self;
        private static long _prevOwnMs = -1;
        private static long _prevWallTicks;

        /// <summary>CPU 型号，只读一次注册表。取不到就空串（界面不要显示「未知」，直接省掉这行）。</summary>
        public static string CpuName { get; } = ReadCpuName();

        private static string ReadCpuName()
        {
            try
            {
                using var k = Microsoft.Win32.Registry.LocalMachine
                    .OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                var v = k?.GetValue("ProcessorNameString") as string;
                return (v ?? "").Trim();
            }
            catch { return ""; }
        }

        private static double SampleOwnCpu()
        {
            try
            {
                _self ??= System.Diagnostics.Process.GetCurrentProcess();
                _self.Refresh();
                long nowMs = (long)_self.TotalProcessorTime.TotalMilliseconds;
                long wall = DateTime.UtcNow.Ticks;

                if (_prevOwnMs < 0 || _prevWallTicks == 0)
                {
                    _prevOwnMs = nowMs;
                    _prevWallTicks = wall;
                    return 0;
                }

                long dMs = nowMs - _prevOwnMs;
                long dWallMs = (wall - _prevWallTicks) / TimeSpan.TicksPerMillisecond;
                _prevOwnMs = nowMs;
                _prevWallTicks = wall;

                int cores = Math.Max(1, Environment.ProcessorCount);
                if (dWallMs <= 0) return 0;

                // 单核跑满 = 1/核数。乘 100 归一化到「占全系统」，
                // 这样 4 核机 60% 和 32 核机 60% 的含义一致（都代表同一份比例算力）。
                return Math.Clamp(dMs * 100.0 / (dWallMs * (double)cores), 0, 100);
            }
            catch { return 0; }
        }

        // ============================== CPU ==============================

        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME
        {
            public uint Low;
            public uint High;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

        private static long _prevIdle, _prevTotal;
        private static bool _hasCpuBase;

        // ============================== 内存 ==============================

        [StructLayout(LayoutKind.Sequential)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        // ============================== 显存（NVML） ==============================

        [StructLayout(LayoutKind.Sequential)]
        private struct nvmlMemory_t
        {
            public ulong total;
            public ulong free;
            public ulong used;
        }

        [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2", CallingConvention = CallingConvention.Cdecl)]
        private static extern int nvmlInit_v2();

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCount_v2", CallingConvention = CallingConvention.Cdecl)]
        private static extern int nvmlDeviceGetCount_v2(out uint deviceCount);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2", CallingConvention = CallingConvention.Cdecl)]
        private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMemoryInfo", CallingConvention = CallingConvention.Cdecl)]
        private static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out nvmlMemory_t memory);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetName", CallingConvention = CallingConvention.Cdecl)]
        private static extern int nvmlDeviceGetName(IntPtr device, [Out] byte[] name, uint length);

        private static IntPtr _gpu;
        private static string _gpuName = "";
        private static bool _gpuProbed;

        /// <summary>
        /// 惰性初始化 NVML，只做一次。失败（没有 nvml.dll / 没有 N 卡）就永久保持 _gpu = 0。
        /// 注意：nvmlInit 失败时**绝不能**重试 —— 每次轮询都去 LoadLibrary 会拖慢界面。
        /// </summary>
        private static void EnsureGpu()
        {
            if (_gpuProbed) return;
            _gpuProbed = true;
            try
            {
                if (nvmlInit_v2() != 0) return;
                if (nvmlDeviceGetCount_v2(out uint count) != 0 || count == 0) return;
                if (nvmlDeviceGetHandleByIndex_v2(0, out IntPtr dev) != 0) return;
                _gpu = dev;

                var buf = new byte[96];
                if (nvmlDeviceGetName(dev, buf, (uint)buf.Length) == 0)
                {
                    int n = Array.IndexOf(buf, (byte)0);
                    if (n < 0) n = buf.Length;
                    _gpuName = Encoding.ASCII.GetString(buf, 0, n).Trim();
                }
            }
            catch
            {
                _gpu = IntPtr.Zero;
            }
        }

        // ============================== 采样 ==============================

        private static long ToLong(FILETIME f) => ((long)f.High << 32) | f.Low;

        /// <summary>
        /// 采一次样。**不会抛异常**：任何一项读不到就用上一次的值或 0。
        /// 建议 2 秒调一次（与采集状态灯同频），调用成本在微秒级。
        /// </summary>
        public static Snapshot Sample()
        {
            var s = new Snapshot();

            // ---- CPU：两次 GetSystemTimes 的差值 ----
            try
            {
                s.CpuCores = Math.Max(1, Environment.ProcessorCount);

                if (GetSystemTimes(out var idle, out var ker, out var usr))
                {
                    long i = ToLong(idle), t = ToLong(ker) + ToLong(usr);
                    // 第一次调用只打基线不算百分比 —— 否则会拿"刚打基线到此刻"这段近乎为 0 的
                    // 时间差去算，得出的是随机数。第二次（2 秒后）起就是正常值。
                    if (_hasCpuBase)
                    {
                        long di = i - _prevIdle, dt = t - _prevTotal;
                        if (dt > 0)
                        {
                            // ⚠ 必须 Clamp：dt 极小或计数器回绕时会算出负数或 >100
                            s.CpuPercent = Math.Clamp((dt - di) * 100.0 / dt, 0, 100);
                            s.CpuValid = true;
                        }
                    }
                    _prevIdle = i;
                    _prevTotal = t;
                    _hasCpuBase = true;
                }
            }
            catch { }

            // ---- 本进程 CPU（判断「CPU 高到底是不是我们造成的」）----
            try { s.OwnCpuPercent = SampleOwnCpu(); } catch { }

            // ---- 内存 ----
            try
            {
                var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (GlobalMemoryStatusEx(m) && m.ullTotalPhys > 0)
                {
                    const double gb = 1024.0 * 1024 * 1024;
                    s.MemTotalGb = m.ullTotalPhys / gb;
                    s.MemUsedGb = (m.ullTotalPhys - m.ullAvailPhys) / gb;
                    s.MemPercent = Math.Clamp(s.MemUsedGb / s.MemTotalGb * 100.0, 0, 100);
                }
            }
            catch { }

            // ---- 显存 ----
            try
            {
                EnsureGpu();
                if (_gpu != IntPtr.Zero && nvmlDeviceGetMemoryInfo(_gpu, out var mem) == 0 && mem.total > 0)
                {
                    s.GpuAvailable = true;
                    s.GpuName = _gpuName;
                    s.GpuTotalMb = mem.total / 1048576.0;
                    s.GpuUsedMb = mem.used / 1048576.0;
                    s.GpuPercent = Math.Clamp(s.GpuUsedMb / s.GpuTotalMb * 100.0, 0, 100);
                }
            }
            catch { }

            // ---- 测试钩子：注入假的满载读数 ----
            // 只在设了 CUTLANCE_PERF_TEST=cpu / mem 时生效，用来验证「经常性不足 → 建议升级电脑」
            // 这条链路。真跑推流去把 CPU 打满既费时又会把别人的机器搞卡，所以留这个口子。
            if (_testInject == "cpu")
            {
                s.CpuValid = true;
                s.CpuPercent = 96;
                s.OwnCpuPercent = 68;
            }
            else if (_testInject == "mem")
            {
                s.MemPercent = 97;
                s.MemUsedGb = s.MemTotalGb * 0.97;
            }

            return s;
        }

        /// <summary>见 Sample() 末尾的测试钩子。生产环境不设置该变量即完全不生效。</summary>
        private static readonly string _testInject =
            (Environment.GetEnvironmentVariable("CUTLANCE_PERF_TEST") ?? "").Trim().ToLowerInvariant();
    }
}
