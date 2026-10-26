using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using Cutlance.Models;

namespace Cutlance.Services
{
    /// <summary>
    /// 倒计时服务（模块 B，对标 vMix 的 Countdown + OnCountdownTime 触发器）。
    ///
    /// 两种计时方式：
    ///   · 按时长（默认）：Stopwatch + 100ms 心跳，暂停/恢复后剩余依然准确，不会因为定时器抖动漂移。
    ///   · 到指定时刻：记住一个「挂钟目标时刻」，每次心跳用**电脑当前时间**重算剩余 ——
    ///     这样即使界面卡顿、系统休眠再回来，剩余时间依然与真实时间一致（挂钟才是权威）。
    ///
    /// 职责边界：只负责「计时」和「到点喊一声」——到点时抛 <see cref="Fired"/>，
    /// 具体要切哪路输入 / 开哪个叠加通道 / 启停推流，由 MainWindow 按 Config.Action 执行。
    /// 这样服务本身不依赖任何界面元素，配置窗口与主窗口可以共享同一个实例。
    /// </summary>
    public sealed class CountdownService
    {
        public static CountdownService Instance { get; } = new();

        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
        private readonly Stopwatch _sw = new();

        /// <summary>运行时挂钟锚点（到指定时刻模式）；null = 按时长计时。</summary>
        private DateTime? _deadline;

        /// <summary>暂停瞬间的剩余秒数，恢复时用它续接（挂钟模式会把目标时刻顺延同样多）。</summary>
        private double _pausedRemain;

        /// <summary>当前倒计时配置（方式 / 时长 / 目标时刻 / 到点动作 / 循环 / 是否显示到画面）。</summary>
        public CountdownConfig Config { get; } = new();

        public bool IsRunning { get; private set; }
        public bool IsPaused { get; private set; }

        /// <summary>当前是否处于「到指定时刻」计时（供界面显示目标时刻）。</summary>
        public bool IsUntilTime => _deadline.HasValue;

        /// <summary>本次倒计时的总时长（秒）。</summary>
        public double Total { get; private set; }

        /// <summary>剩余秒数。</summary>
        public double Remaining { get; private set; }

        /// <summary>心跳（约 100ms 一次），供界面刷新剩余时间。</summary>
        public event Action? Tick;
        /// <summary>倒计时归零，该执行动作了。</summary>
        public event Action? Fired;
        /// <summary>运行状态变化（开始 / 暂停 / 恢复 / 停止 / 到点结束）。</summary>
        public event Action? StateChanged;

        private CountdownService()
        {
            _timer.Tick += OnTick;
        }

        /// <summary>
        /// 剩余时间的显示文本：≥1 小时给 h:mm:ss，≥1 分钟给 mm:ss，不足 1 分钟给 ss.s（一位小数）。
        /// 「到指定时刻」模式下可能有好几小时，所以补了小时档。
        /// </summary>
        public string RemainingText
        {
            get
            {
                double r = Math.Max(0, Remaining);
                int total = (int)Math.Ceiling(r);
                if (total >= 3600)
                {
                    int h = total / 3600, m = total % 3600 / 60, s = total % 60;
                    return $"{h}:{m:00}:{s:00}";
                }
                if (r >= 60)
                {
                    int m = (int)(r / 60);
                    int s = (int)Math.Ceiling(r - m * 60);
                    if (s >= 60) { m++; s = 0; }     // 59.6 秒会被进成 60，归位一下
                    return $"{m:00}:{s:00}";
                }
                return r.ToString("0.0");
            }
        }

        /// <summary>
        /// 开始倒计时。按 Config.Mode 自动选计时方式：
        ///   · Duration  → 用 durationSec（或配置里的时长）倒数；
        ///   · UntilTime → 解析 Config.TargetTimeText，倒数到那个时刻（解析失败则退回时长方式，不静默失败）。
        /// </summary>
        public void Start(double? durationSec = null)
        {
            if (durationSec == null && Config.Mode == CountdownMode.UntilTime
                && TryParseTarget(Config.TargetTimeText, out var dl))
            {
                StartUntil(dl);
                return;
            }

            _deadline = null;
            Total = Math.Max(0.1, durationSec ?? Config.DurationSec);
            Remaining = Total;
            IsRunning = true;
            IsPaused = false;
            _sw.Restart();
            _timer.Start();
            StateChanged?.Invoke();
            Tick?.Invoke();
        }

        /// <summary>倒数到某个绝对时刻（已过去的话自动顺延到下一个有效时刻）。</summary>
        public void StartUntil(DateTime deadline)
        {
            _deadline = NextOccurrence(deadline);
            Total = Math.Max(0.1, (_deadline.Value - DateTime.Now).TotalSeconds);
            Remaining = Total;
            IsRunning = true;
            IsPaused = false;
            _sw.Restart();
            _timer.Start();
            StateChanged?.Invoke();
            Tick?.Invoke();
        }

        /// <summary>暂停 / 恢复。恢复时把剩余量续接上，挂钟模式会把目标时刻顺延「暂停掉的那段时间」。</summary>
        public void PauseResume()
        {
            if (!IsRunning) return;

            if (!IsPaused)
            {
                _pausedRemain = ComputeRemaining();
                IsPaused = true;
                _sw.Stop();
            }
            else
            {
                IsPaused = false;
                if (_deadline != null)
                {
                    // 挂钟模式：目标时刻顺延，保证"剩余量"连续，而不是暂停期间白等
                    _deadline = DateTime.Now.AddSeconds(_pausedRemain);
                }
                else
                {
                    Total = Math.Max(0, _pausedRemain);
                    _sw.Restart();       // elapsed 归零 → 剩余从 _pausedRemain 接着走
                }
                _sw.Start();
            }
            StateChanged?.Invoke();
        }

        /// <summary>复位到初始值并停止（下次点开始重新来）。</summary>
        public void Reset()
        {
            _timer.Stop();
            _sw.Reset();
            IsRunning = false;
            IsPaused = false;
            _deadline = null;

            // 到指定时刻模式下，复位后大字先显示"现在离目标时刻还有多久"，方便校对
            if (Config.Mode == CountdownMode.UntilTime && TryParseTarget(Config.TargetTimeText, out var dl))
                Total = Math.Max(0, (dl - DateTime.Now).TotalSeconds);
            else
                Total = Math.Max(0.1, Config.DurationSec);

            Remaining = Total;
            StateChanged?.Invoke();
            Tick?.Invoke();
        }

        /// <summary>停止（保留当前剩余值显示）。</summary>
        public void Stop()
        {
            _timer.Stop();
            _sw.Reset();
            IsRunning = false;
            IsPaused = false;
            _deadline = null;
            StateChanged?.Invoke();
        }

        private void OnTick(object? sender, EventArgs e)
        {
            if (!IsRunning || IsPaused) return;

            Remaining = ComputeRemaining();
            Tick?.Invoke();

            if (Remaining > 0) return;

            // —— 到点 ——
            // ⚠ 顺序是硬要求：必须**先把状态收干净 / 把下一轮排好，再抛 Fired**。
            //   「弹框提醒」的处理器会弹一个模态 MessageBox，它在当前线程跑**嵌套消息泵**，
            //   而 DispatcherTimer 在嵌套消息泵里照常 tick —— 若此刻 IsRunning 仍为 true、Remaining 还停在 0，
            //   每一拍都会再抛一次 Fired，一口气叠出一堆提醒框（点了下面那个又冒出新的）。
            _sw.Stop();

            // ⚠ 「循环」只在**按时长倒计时**下有效：界面上的循环勾选只存在于时长面板，
            //   「到指定时刻」面板已移除该选项。这里必须一并限定 —— 否则从旧工程载入
            //   Repeat=true 会变成界面上**看不见、也关不掉**的「每天定点」隐形循环。
            //   （时长分支在 Start() 里已把 _deadline 清空，所以下面只可能走 _sw 续接。）
            if (Config.Repeat && Config.Mode == CountdownMode.Duration)
            {
                _sw.Restart();
                Remaining = Total;
                Fired?.Invoke();
                return;
            }

            IsRunning = false;
            _timer.Stop();
            Fired?.Invoke();
            StateChanged?.Invoke();
        }

        private double ComputeRemaining()
        {
            if (_deadline is DateTime dl && !IsPaused)
                return Math.Max(0, (dl - DateTime.Now).TotalSeconds);
            return Math.Max(0, Total - _sw.Elapsed.TotalSeconds);
        }

        /// <summary>把时刻推进到"将来"的第一次出现（长时间休眠/顺延时用）。</summary>
        private static DateTime NextOccurrence(DateTime d)
        {
            var now = DateTime.Now;
            while (d <= now) d = d.AddDays(1);
            return d;
        }

        // ==================== 目标时刻解析 ====================
        // 接受：18:30 / 18:30:00 / 10-20 18:30 / 2026-10-20 18:30:00（分隔符 - . / 与全角冒号都认）
        private static readonly Regex RxFullDate = new(
            @"^\s*(\d{4})[-/.年](\d{1,2})[-/.月](\d{1,2})日?\s+(\d{1,2})[:：](\d{1,2})(?:[:：](\d{1,2}))?\s*$",
            RegexOptions.Compiled);
        private static readonly Regex RxMonthDay = new(
            @"^\s*(\d{1,2})[-/.月](\d{1,2})日?\s+(\d{1,2})[:：](\d{1,2})(?:[:：](\d{1,2}))?\s*$",
            RegexOptions.Compiled);
        private static readonly Regex RxTimeOnly = new(
            @"^\s*(\d{1,2})[:：](\d{1,2})(?:[:：](\d{1,2}))?\s*$",
            RegexOptions.Compiled);

        /// <summary>
        /// 解析用户填的目标时刻。只填时间（如 21:00）＝「今天该时刻，已过则明天」；
        /// 只填月日＝「今年该日期，已过则明年」；填了完整年月日＝绝对时刻。
        /// </summary>
        public static bool TryParseTarget(string? text, out DateTime target)
        {
            target = default;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = text.Trim();
            var now = DateTime.Now;

            var mf = RxFullDate.Match(s);
            if (mf.Success)
                return TryBuild(Int(mf, 1), Int(mf, 2), Int(mf, 3), Int(mf, 4), Int(mf, 5), Opt(mf, 6), out target);

            var md = RxMonthDay.Match(s);
            if (md.Success)
            {
                int mo = Int(md, 1), d = Int(md, 2), h = Int(md, 3), mi = Int(md, 4), sec = Opt(md, 5);
                for (int y = now.Year; y <= now.Year + 4; y++)      // 2-29 这种要跳到闰年，所以多试几年
                    if (TryBuild(y, mo, d, h, mi, sec, out var t) && t > now) { target = t; return true; }
                return false;
            }

            var mt = RxTimeOnly.Match(s);
            if (mt.Success)
            {
                if (!TryBuild(now.Year, now.Month, now.Day, Int(mt, 1), Int(mt, 2), Opt(mt, 3), out var t))
                    return false;
                if (t <= now) t = t.AddDays(1);                     // 今天的这个点已经过了 → 明天
                target = t;
                return true;
            }
            return false;
        }

        /// <summary>把目标时刻写成给人看的短句，如「今天 21:00:00」「明天 08:30:00」「2026-10-20 21:00:00」。</summary>
        public static string DescribeTarget(DateTime target)
        {
            var today = DateTime.Now.Date;
            string day = target.Date == today ? "今天"
                       : target.Date == today.AddDays(1) ? "明天"
                       : target.ToString("yyyy-MM-dd");
            return $"{day} {target:HH:mm:ss}";
        }

        private static int Int(Match m, int i) => int.Parse(m.Groups[i].Value);
        private static int Opt(Match m, int i) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value) : 0;

        private static bool TryBuild(int y, int mo, int d, int h, int mi, int s, out DateTime t)
        {
            t = default;
            try { t = new DateTime(y, mo, d, h, mi, s); return true; }
            catch { return false; }      // 越界的月/日/时直接判为非法输入
        }
    }
}
