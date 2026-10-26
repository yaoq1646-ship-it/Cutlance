using System;
using System.Collections.Generic;
using System.Text;

namespace Cutlance.Services
{
    /// <summary>
    /// 性能是否够用的长期判定（老板 2026-10-20 要求：
    /// 「使用 CPU 要注意一点，监控系统参数，经常性出现 CPU 报警，告诉用户建议升级您的电脑」）。
    ///
    /// ── 为什么要和状态栏的即时报警分开 ──
    ///   状态栏那套是「连续 6 秒越线就闪红字」，用来抓**瞬时**峰值（打开一张大表格、
    ///   突然多路上墙）。但这种短报警给的建议只能是「换个设置试试」。
    ///   真正救不了的情况是：这台机器的算力**长期**不够 —— 报警天天响、响一晚上。
    ///   那时候再劝人调参数就是耽误事，该说的是「建议升级您的电脑」。
    ///   所以这里用 10 分钟滑动窗口统计「越线时间占比」，占比过半才升级成硬件结论。
    ///
    /// ── 为什么要单独看「本进程占用」──
    ///   程序改成 CPU 合成之后，8 路出帧 + 混合全在本进程内。系统 CPU 90% 到底是我们
    ///   造成的还是别的软件在抢，直接决定该建议「少开几路」还是「换台机器」。
    ///   注意本进程占用**不含 ffmpeg 子进程**：编码那部分算在系统总量里，
    ///   所以 OwnCpu 低而系统高时，结论会落到「别的程序在抢 CPU」而不是「本程序太吃」。
    ///
    /// 线程约定：只在 UI 线程调用（RefreshSystemMonitor 每 2 秒一次）。
    /// </summary>
    public static class PerfAdvice
    {
        // ============================== 阈值（与状态栏告警同口径） ==============================

        public const double CpuHotPct = 85.0;   // 系统 CPU 越线
        public const double OwnHotPct = 60.0;   // 本进程吃掉全系统的 60%
        public const double MemHotPct = 88.0;   // 物理内存占用

        /// <summary>窗口容量：2 秒一轮 → 300 轮 = 10 分钟。</summary>
        private const int WindowSamples = 300;

        /// <summary>样本不足不下结论：刚启动 1 分钟就喊「升级电脑」太草率。</summary>
        private const int MinSamples = 45;      // 90 秒

        /// <summary>「经常性」的界线：窗口内一半以上时间越线。</summary>
        private const double HotRate = 0.5;

        /// <summary>同一台机器的提示冷却：半小时内只打扰一次。</summary>
        private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(30);

        // ============================== 状态 ==============================

        private struct Row
        {
            public bool CpuHot, OwnHot, MemHot;
            public double Cpu, Own, Mem;
        }

        private static readonly Queue<Row> _win = new();
        private static DateTime _lastPrompt = DateTime.MinValue;

        /// <summary>测试钩子：设了就把门槛降到几轮，用来验证提示链路（生产环境不设=不生效）。</summary>
        private static readonly bool _testMode =
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CUTLANCE_PERF_TEST"));

        /// <summary>最近一次判定结果（界面随时可读）。</summary>
        public static Verdict Latest { get; private set; } = new Verdict();

        // ============================== 判定 ==============================

        /// <summary>喂一轮采样，返回当前判定。不抛异常。</summary>
        public static Verdict Update(SystemMonitor.Snapshot s)
        {
            try
            {
                _win.Enqueue(new Row
                {
                    CpuHot = s.CpuValid && s.CpuPercent >= CpuHotPct,
                    OwnHot = s.OwnCpuPercent >= OwnHotPct,
                    MemHot = s.MemPercent >= MemHotPct,
                    Cpu = s.CpuPercent,
                    Own = s.OwnCpuPercent,
                    Mem = s.MemPercent,
                });

                int cap = _testMode ? 4 : WindowSamples;
                while (_win.Count > cap) _win.Dequeue();

                int n = _win.Count;
                int need = _testMode ? 2 : MinSamples;

                var v = new Verdict
                {
                    Samples = n,
                    WindowMinutes = Math.Max(1, (int)Math.Round(n * 2 / 60.0)),
                    CpuPercent = s.CpuPercent,
                    OwnCpuPercent = s.OwnCpuPercent,
                    MemPercent = s.MemPercent,
                    MemUsedGb = s.MemUsedGb,
                    MemTotalGb = s.MemTotalGb,
                    CpuCores = s.CpuCores,
                    CpuName = SystemMonitor.CpuName,
                };

                if (n >= need)
                {
                    int cpuHot = 0, ownHot = 0, memHot = 0;
                    double ownSum = 0, ownMax = 0, cpuSum = 0;
                    foreach (var r in _win)
                    {
                        if (r.CpuHot) cpuHot++;
                        if (r.OwnHot) ownHot++;
                        if (r.MemHot) memHot++;
                        ownSum += r.Own;
                        cpuSum += r.Cpu;
                        if (r.Own > ownMax) ownMax = r.Own;
                    }

                    v.CpuHotRate = cpuHot * 100.0 / n;
                    v.OwnHotRate = ownHot * 100.0 / n;
                    v.MemHotRate = memHot * 100.0 / n;
                    v.OwnAvg = ownSum / n;
                    v.OwnMax = ownMax;
                    v.CpuAvg = cpuSum / n;

                    // 「经常性」才算数：占比过半。
                    v.CpuOften = v.CpuHotRate >= HotRate * 100;
                    v.OwnOften = v.OwnHotRate >= HotRate * 100;
                    v.MemOften = v.MemHotRate >= HotRate * 100;
                    v.Stressed = v.CpuOften || v.OwnOften || v.MemOften;

                    if (v.Stressed) v.Cause = v.OwnOften ? "own" : v.CpuOften ? "cpu" : "mem";
                }

                Latest = v;
                return v;
            }
            catch
            {
                // 判定本身出问题不许影响直播；返回空的「不紧张」结论即可
                return new Verdict();
            }
        }

        /// <summary>是否该打扰用户：判定成立 + 过了冷却期。</summary>
        public static bool ShouldPrompt(Verdict v)
        {
            if (v == null || !v.Stressed) return false;
            if (DateTime.Now - _lastPrompt < Cooldown) return false;
            _lastPrompt = DateTime.Now;
            return true;
        }

        /// <summary>用户点了「本次运行不再提示」。</summary>
        public static bool Suppressed { get; private set; }

        /// <summary>本次运行内不再提示（关掉程序即恢复）。</summary>
        public static void SuppressForSession() => Suppressed = true;

        /// <summary>把状态栏那句「建议」按判定结果改口径。短暂峰值仍给设置建议，长期不足给硬件结论。</summary>
        /// <remarks>
        /// 必须短：状态栏还要放推流 / 录制指示灯，写成一整段论证会把它们挤出屏幕。
        /// 完整论证在「查看详情」的报告里。
        /// </remarks>
        public static string Headline(Verdict v)
        {
            if (v == null || !v.Stressed) return "";
            return v.Cause == "mem"
                ? $"内存长期 {v.MemPercent:F0}% —— 建议加内存，或升级您的电脑"
                : $"CPU 长期 {v.CpuAvg:F0}% —— 建议升级您的电脑";
        }

        // ============================== 报告 ==============================

        /// <summary>生成给人看的完整报告（「查看详情」弹窗用）。</summary>
        public static string BuildReport(Verdict v)
        {
            v ??= Latest;
            var sb = new StringBuilder();

            sb.AppendLine("【结论】");
            if (!v.Stressed)
            {
                sb.AppendLine("  当前设置在这台电脑上**跑得动**，没有发现长期资源不足。");
                sb.AppendLine();
            }
            else if (v.Cause == "mem")
            {
                sb.AppendLine("  这台电脑的内存不够用 —— 建议加内存条，或升级您的电脑。");
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("  这台电脑的运算能力跟不上当前的导播设置 —— 建议升级您的电脑。");
                sb.AppendLine();
            }

            sb.AppendLine($"【依据】最近 {v.WindowMinutes} 分钟 · 共 {v.Samples} 次采样（每 2 秒一次）");
            sb.AppendLine($"  CPU 占用     平均 {v.CpuAvg:F0}%　近半时间 ≥ {CpuHotPct:F0}% 的占比：{v.CpuHotRate:F0}%");
            sb.AppendLine($"  本程序占用   平均 {v.OwnAvg:F0}%　峰值 {v.OwnMax:F0}%（合成 + 界面，不含编码子进程）");
            sb.AppendLine($"  内存占用     {v.MemUsedGb:F1} / {v.MemTotalGb:F1} GB（{v.MemPercent:F0}%）" +
                          $"　≥ {MemHotPct:F0}% 的占比：{v.MemHotRate:F0}%");
            sb.AppendLine();

            sb.AppendLine("【这台电脑】");
            if (!string.IsNullOrWhiteSpace(v.CpuName))
                sb.AppendLine($"  CPU    {v.CpuName}（{v.CpuCores} 线程）");
            else
                sb.AppendLine($"  CPU    {v.CpuCores} 线程");
            sb.AppendLine($"  内存   {v.MemTotalGb:F1} GB");
            sb.AppendLine($"  系统   {Environment.OSVersion.VersionString}（{(Environment.Is64BitProcess ? "64" : "32")} 位程序）");
            sb.AppendLine();

            sb.AppendLine("【会出什么问题】");
            sb.AppendLine("  · CPU 跟不上，预览和输出就开始掉帧，画面一顿一顿");
            sb.AppendLine("  · 编码抢不到 CPU，推流码率掉下来，观众那边看到的是卡顿和马赛克");
            sb.AppendLine("  · 到极限时编码进程会断流，直播直接中断");
            sb.AppendLine("  · 这些都不是「设置没调好」，是机器的算力真的不够");
            sb.AppendLine();

            sb.AppendLine("【先这么顶一下（治不了根，但能少出事）】");
            sb.AppendLine("  1. 少上几路：同时进节目输出的输入控制在 4 路以内，不用的图层关掉");
            sb.AppendLine("  2. 推流改用显卡编码（NVENC / QSV），把 CPU 让给合成");
            sb.AppendLine("  3. 输出分辨率降到 720p，或者把帧率从 60 降到 30");
            sb.AppendLine("  4. 导播期间别再同时开浏览器、剪辑软件这类吃 CPU 的程序");
            sb.AppendLine();

            sb.AppendLine("【真正的解法】");
            if (v.Cause == "mem")
            {
                sb.AppendLine("  加内存条到 32 GB 以上。如果机器本身太老（DDR3 / 无法扩内存），那就换一台。");
            }
            else
            {
                sb.AppendLine("  换一台算力够的电脑。导播场景的参考配置：");
                sb.AppendLine("    · CPU    8 核 16 线程以上（近两代的 i7 / i9 或 R7 / R9）");
                sb.AppendLine("    · 内存   32 GB 以上");
                sb.AppendLine("    · 显卡   带 NVENC 的 NVIDIA 卡（编码交给它，CPU 只管合成）");
                sb.AppendLine("    · 硬盘   SSD，录制盘单独一块");
            }
            sb.AppendLine();
            sb.AppendLine($"（这份报告也会记进日志：{ErrorLog.Directory}）");

            return sb.ToString();
        }

        /// <summary>把一个快照直接写成一行日志，方便事后对照时间线。</summary>
        public static void LogVerdict(Verdict v)
        {
            if (v == null || !v.Stressed) return;
            ErrorLog.Info($"性能不足：{v.Cause} / CPU 均 {v.CpuAvg:F0}% 越线率 {v.CpuHotRate:F0}% / " +
                          $"本程序 均 {v.OwnAvg:F0}% 峰 {v.OwnMax:F0}% / 内存 {v.MemPercent:F0}%（{v.MemUsedGb:F1}/{v.MemTotalGb:F1}GB）" +
                          $" / {v.CpuName} {v.CpuCores} 线程");
        }
    }

    /// <summary>一次长期判定的结果。全部字段都是「给人看」的口径，不再做二次换算。</summary>
    public sealed class Verdict
    {
        public int Samples;
        public int WindowMinutes;

        public double CpuPercent;
        public double OwnCpuPercent;
        public double MemPercent;
        public double MemUsedGb;
        public double MemTotalGb;

        public int CpuCores;
        public string CpuName = "";

        // 窗口统计
        public double CpuAvg, OwnAvg, OwnMax;
        public double CpuHotRate, OwnHotRate, MemHotRate;

        public bool CpuOften, OwnOften, MemOften;

        /// <summary>长期资源不足（判定成立）。</summary>
        public bool Stressed;

        /// <summary>主因：cpu / own / mem。</summary>
        public string Cause = "";
    }
}
