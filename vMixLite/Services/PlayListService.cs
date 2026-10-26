using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Threading;
using Cutlance.Models;

namespace Cutlance.Services
{
    /// <summary>
    /// 播放列表推进器（对标 vMix 的 PlayList 自动顺播）。
    ///
    /// 职责边界：**只决定「什么时候该切到哪一项」，不碰任何画面**。
    /// 该切项时抛出 <see cref="ItemActivated"/>，由 MainWindow 执行真正的切源动作
    /// （把该项的输入源放到输出图层上）。这样画面逻辑仍然集中在主窗口，服务本身可独立测试。
    ///
    /// 推进方式两种：
    ///   · 定时模式（DurationSec &gt; 0）—— 用 Stopwatch 计时，到点自动下一项，支持暂停续播；
    ///   · 播完模式（DurationSec &lt;= 0）—— 由 MainWindow 在媒体自然结束时调 <see cref="NotifyMediaEnded"/> 驱动。
    /// Stopwatch 而非累加计数：暂停/恢复后剩余时间依然准确。
    /// </summary>
    public sealed class PlayListService
    {
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
        private readonly Stopwatch _sw = new();

        private List<PlayListItem> _items = new();

        /// <summary>当前项是否靠「媒体播完」推进（定时模式下该标志为 false，忽略 EndReached）。</summary>
        private bool _waitingMediaEnd;

        public int CurrentIndex { get; private set; } = -1;
        public bool IsRunning { get; private set; }
        public bool IsPaused { get; private set; }

        /// <summary>循环播放（播完最后一项后回到第 1 项）。</summary>
        public bool Loop { get; set; }

        public int Count => _items.Count;
        public PlayListItem? Current =>
            CurrentIndex >= 0 && CurrentIndex < _items.Count ? _items[CurrentIndex] : null;

        /// <summary>请求播放第 index 项（MainWindow 据此切源）。参数：项、下标。</summary>
        public event Action<PlayListItem, int>? ItemActivated;
        /// <summary>整轮播放结束（非循环模式下播完最后一项）。</summary>
        public event Action? Finished;
        /// <summary>进度刷新：参数为（本项已播秒数、本项剩余秒数；「播完」模式剩余恒为 0）。</summary>
        public event Action<double, double>? ProgressTick;
        /// <summary>运行状态变化（开始/暂停/恢复/停止），供界面刷新按钮与文案。</summary>
        public event Action? StateChanged;

        public PlayListService()
        {
            _timer.Tick += OnTick;
        }

        /// <summary>开始播放（items 为当前列表快照，运行期改动列表需重新开始）。</summary>
        public void Start(List<PlayListItem> items, int startIndex = 0)
        {
            if (items == null || items.Count == 0) return;
            _items = items;
            IsRunning = true;
            IsPaused = false;
            Activate(Math.Clamp(startIndex, 0, items.Count - 1));
            _timer.Start();
            StateChanged?.Invoke();
        }

        /// <summary>停止并复位（不清除列表，只中止播放）。</summary>
        public void Stop()
        {
            _timer.Stop();
            _sw.Reset();
            IsRunning = false;
            IsPaused = false;
            _waitingMediaEnd = false;
            foreach (var it in _items) it.IsCurrent = false;
            CurrentIndex = -1;
            StateChanged?.Invoke();
        }

        /// <summary>暂停 / 恢复。暂停期间计时冻结，恢复后从原处继续。</summary>
        public void TogglePause()
        {
            if (!IsRunning) return;
            IsPaused = !IsPaused;
            if (IsPaused) _sw.Stop();
            else _sw.Start();
            StateChanged?.Invoke();
        }

        /// <summary>跳到下一项（到末尾：循环则回第 1 项，否则结束整轮）。</summary>
        public void Next()
        {
            if (!IsRunning || _items.Count == 0) return;
            int n = CurrentIndex + 1;
            if (n >= _items.Count)
            {
                if (Loop) n = 0;
                else { Finish(); return; }
            }
            Activate(n);
        }

        /// <summary>跳到上一项（到开头：循环则去末项，否则停在第一项）。</summary>
        public void Prev()
        {
            if (!IsRunning || _items.Count == 0) return;
            int n = CurrentIndex - 1;
            if (n < 0) n = Loop ? _items.Count - 1 : 0;
            Activate(n);
        }

        /// <summary>
        /// 媒体自然播完时由 MainWindow 调用。
        /// ⚠ 只在「播完」模式下推进；定时模式下媒体提前结束不触发切项（继续等满时长）。
        /// </summary>
        public void NotifyMediaEnded()
        {
            if (!IsRunning || IsPaused) return;
            if (!_waitingMediaEnd) return;
            Next();
        }

        /// <summary>切到指定下标（供界面双击列表项直接跳播）。</summary>
        public void JumpTo(int index)
        {
            if (!IsRunning || index < 0 || index >= _items.Count) return;
            Activate(index);
        }

        private void Activate(int index)
        {
            if (index < 0 || index >= _items.Count) return;
            CurrentIndex = index;
            for (int i = 0; i < _items.Count; i++) _items[i].IsCurrent = (i == index);

            var item = _items[index];
            _waitingMediaEnd = item.DurationSec <= 0;
            _sw.Restart();                         // 每项单独计时
            ItemActivated?.Invoke(item, index);
            StateChanged?.Invoke();
        }

        private void OnTick(object? sender, EventArgs e)
        {
            if (!IsRunning || IsPaused) return;
            var item = Current;
            if (item == null) return;

            double elapsed = _sw.Elapsed.TotalSeconds;
            if (item.DurationSec > 0)
            {
                double remain = Math.Max(0, item.DurationSec - elapsed);
                ProgressTick?.Invoke(elapsed, remain);
                if (elapsed >= item.DurationSec) Next();
            }
            else
            {
                // 播完模式：实际推进交给 NotifyMediaEnded，这里只刷新已播时长
                ProgressTick?.Invoke(elapsed, 0);
            }
        }

        private void Finish()
        {
            Stop();
            Finished?.Invoke();
        }
    }
}
