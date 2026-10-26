using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using System.Diagnostics;

namespace Cutlance.Models
{
    /// <summary>
    /// 右栏「倒计时牌」（2026-10-24 老板要求）。
    ///
    /// 与模块 B 的 <see cref="CountdownConfig"/> / CountdownService 的区别：
    ///   · 那个是「到点触发动作」（切输入 / 推流 / 弹提醒），归零就停，归零后要做别的事；
    ///   · 这个是**给观众看的倒计时牌**：归零后继续往下走负数，直到老板点停止，
    ///     用来表示「已经超时多久了」，不触发任何动作。
    /// 所以不复用同一个实例：两者语义相反（一个必须归零即停，一个必须归零继续），
    /// 塞进同一个状态机会让「到点动作」在负数阶段反复触发。
    ///
    /// 计时口径：Stopwatch 算 elapsed（不用累加 Timer 次数，界面卡顿/系统卡一下也不漂）；
    /// 剩余 = 设定值 − elapsed，**不夹到 0**，所以天然能走负数。
    /// </summary>
    public sealed class CountdownClock
    {
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
        private readonly Stopwatch _sw = new();

        /// <summary>设定时长（秒）。「恢复」就是回到这个值。</summary>
        public double SetSeconds { get; private set; } = 3600;

        /// <summary>剩余秒数，可为负（负数 = 超时多久）。</summary>
        public double Remaining { get; private set; } = 3600;

        public bool IsRunning { get; private set; }

        /// <summary>是否已经走过 0（显示变红、带负号）。</summary>
        public bool IsNegative => Remaining < -0.0001;

        /// <summary>大字显示文本（h:mm:ss / mm:ss，负数前面带 -）。</summary>
        public string Text => Format(Remaining);

        /// <summary>设定值的同款文本，用于「设定」对话框的预填。</summary>
        public string SetText => Format(SetSeconds);

        /// <summary>每 100ms 一拍（界面刷新用）。</summary>
        public event Action? Changed;

        public CountdownClock()
        {
            _timer.Tick += (_, _) =>
            {
                if (!IsRunning) return;
                Remaining = SetSeconds - _sw.Elapsed.TotalSeconds;   // 不夹 0：负数照走
                Changed?.Invoke();
            };
        }

        /// <summary>设定时长（秒）并停止计时，大字立刻显示新值。</summary>
        public void Set(double seconds)
        {
            SetSeconds = Math.Max(0.1, seconds);
            Remaining = SetSeconds;
            IsRunning = false;
            _sw.Reset();
            _timer.Stop();
            Changed?.Invoke();
        }

        /// <summary>开始（已停表就从头走；已在走则重新计时 —— 两次点「开始」不会叠加两条计时）。</summary>
        public void Start()
        {
            Remaining = SetSeconds;
            _sw.Restart();
            IsRunning = true;
            _timer.Start();
            Changed?.Invoke();
        }

        /// <summary>停止：大字保留当前值（可能是负数），不再变化。</summary>
        public void Stop()
        {
            _sw.Stop();
            IsRunning = false;
            _timer.Stop();
            Changed?.Invoke();
        }

        /// <summary>恢复成设定的那个时间（顺带停表，等于「回到起点」）。</summary>
        public void Restore()
        {
            Stop();
            Remaining = SetSeconds;
            Changed?.Invoke();
        }

        /// <summary>退出程序时收尾。</summary>
        public void Shutdown()
        {
            try { _timer.Stop(); } catch { }
            _sw.Stop();
            IsRunning = false;
        }

        /// <summary>
        /// 剩余时间的显示：≥1 小时 h:mm:ss，其余 mm:ss。
        /// 正负都用进一（2026-10-24 老板口径）：
        ///   · 还剩 0.4 秒显示 00:01（不是 00:00，符合"倒计时"直觉）；
        ///   · 超时 0.4 秒显示 -00:01（不是 -00:00，后者看着像没动过）。
        /// </summary>
        public static string Format(double seconds)
        {
            bool neg = seconds < -0.0001;
            double a = Math.Abs(seconds);
            int total = (int)Math.Ceiling(a);

            string body;
            if (total >= 3600)
                body = $"{total / 3600}:{total % 3600 / 60:00}:{total % 60:00}";
            else
                body = $"{total / 60:00}:{total % 60:00}";
            return neg ? "-" + body : body;
        }

        // ══════════════ 设定对话框的自由输入 ══════════════
        // 认这几种写法（老板可以随手敲）：
        //   1:00:00 / 01:00:00   时分秒
        //   30:00                分:秒
        //   90                   纯数字 = **分钟**（界面上有提示文案）
        //   1h30m / 90m / 45s / 1小时30分钟 / 45秒
        // 解析失败返回 false，调用方弹提示、不改原值（不静默改成 0）。
        private static readonly Regex RxPiece = new(@"(\d+(?:\.\d+)?)([hms]?)", RegexOptions.Compiled);

        public static bool TryParseDuration(string? text, out double seconds)
        {
            seconds = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;

            string s = text.Trim().Replace("：", ":")
                            .Replace("小时", "h").Replace("分钟", "m").Replace("秒", "s")
                            .Replace(" ", "").Replace("　", "");
            if (s.Length == 0) return false;

            // ① 冒号写法：按 60 进制从右往左进位
            if (s.Contains(':'))
            {
                var parts = s.Split(':');
                if (parts.Length < 2 || parts.Length > 3) return false;
                double total = 0;
                foreach (var p in parts)
                {
                    if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0)
                        return false;
                    total = total * 60 + v;
                }
                seconds = total;
            }
            else
            {
                // ② 带单位的写法：逐段累加；没有单位的段按分钟算
                var ms = RxPiece.Matches(s);
                if (ms.Count == 0) return false;
                if (string.Concat(ms.Select(m => m.Value)).Length != s.Length) return false;   // 有杂字符 → 判非法

                double total = 0;
                foreach (Match m in ms)
                {
                    double v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    total += m.Groups[2].Value switch
                    {
                        "h" => v * 3600,
                        "m" => v * 60,
                        "s" => v,
                        _ => v * 60          // 裸数字 = 分钟
                    };
                }
                seconds = total;
            }

            if (seconds <= 0) return false;
            if (seconds > 100 * 3600) seconds = 100 * 3600;   // 上限 100 小时，防止手滑多打几个 0
            return true;
        }
    }
}
