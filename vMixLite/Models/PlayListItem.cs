using System;
using System.ComponentModel;

namespace Cutlance.Models
{
    /// <summary>
    /// 播放列表项（对标 vMix 的 PlayList / 播放列表）。
    ///
    /// 每一项指向一个**已添加的输入源**（Inputs 里的某一项），并携带该项自己的播放时长。
    /// 时长语义：
    ///   DurationSec &lt;= 0  → 「播完」模式：等媒体自然结束（MediaPlayer.EndReached）再走下一项；
    ///                        直播源（摄像头/桌面）永远不会结束，此时需手动点「下一项」。
    ///   DurationSec &gt; 0   → 「定时」模式：播满这么多秒强制切下一项，到点即走。
    ///
    /// 该模型只描述「要播什么、播多久」，不含播放器与画面逻辑（由 PlayListService + MainWindow 负责）。
    /// </summary>
    public sealed class PlayListItem : INotifyPropertyChanged
    {
        private double _durationSec;
        private bool _isCurrent;
        private int _order;

        /// <summary>关联的输入源（必填）。</summary>
        public InputSource Source { get; set; } = null!;

        /// <summary>列表中的序号（1 基，重排/删除后由 RefreshOrders 统一刷新）。</summary>
        public int Order
        {
            get => _order;
            set { if (_order != value) { _order = value; Raise(nameof(Order)); } }
        }

        /// <summary>本项播放时长（秒）。&lt;= 0 表示「播完自动下一项」。</summary>
        public double DurationSec
        {
            get => _durationSec;
            set
            {
                if (Math.Abs(_durationSec - value) > 0.0001)
                {
                    _durationSec = value;
                    Raise(nameof(DurationSec));
                    Raise(nameof(DurationText));
                    Raise(nameof(IsTimed));
                }
            }
        }

        /// <summary>是否为正在播放的那一项（列表高亮用）。</summary>
        public bool IsCurrent
        {
            get => _isCurrent;
            set { if (_isCurrent != value) { _isCurrent = value; Raise(nameof(IsCurrent)); } }
        }

        /// <summary>是否定时模式（用于界面标注）。</summary>
        public bool IsTimed => _durationSec > 0;

        public string DisplayName => Source?.Name ?? "(无效项)";
        public string TypeShort => Source?.TypeShort ?? "-";

        /// <summary>时长列的显示文本：「播完」或秒数。</summary>
        public string DurationText =>
            _durationSec <= 0
                ? "播完"
                : (_durationSec % 1 == 0 ? ((int)_durationSec).ToString() : _durationSec.ToString("0.#"));

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }
}
