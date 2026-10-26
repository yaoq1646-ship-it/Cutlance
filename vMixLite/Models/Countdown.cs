using System;

namespace Cutlance.Models
{
    /// <summary>
    /// 倒计时到点后执行的动作（对标 vMix 的 Triggers / OnCountdownTime）。
    /// </summary>
    public enum CountdownAction
    {
        /// <summary>只倒计时，不做任何事。</summary>
        None = 0,
        /// <summary>把第 N 路输入切到输出（N = TargetIndex，1 基）。</summary>
        SwitchToInput = 1,
        /// <summary>打开第 N 号叠加通道。</summary>
        OverlayOn = 2,
        /// <summary>关闭第 N 号叠加通道。</summary>
        OverlayOff = 3,
        /// <summary>切换第 N 号叠加通道的显隐。</summary>
        OverlayToggle = 4,
        /// <summary>开始推流 / 录制。</summary>
        StartOutput = 5,
        /// <summary>停止推流 / 录制。</summary>
        StopOutput = 6,
        /// <summary>淡黑（FTB）开 / 关切换。</summary>
        FtbToggle = 7,
        /// <summary>跳到播放列表第 N 项。</summary>
        PlayListItem = 8,
        /// <summary>
        /// 弹框提醒（可自定义文字，如「请录制推流」）。
        /// 提醒框是系统标准 MessageBox 且**置顶**，点「是」＝立即开始推流 / 录制，点「否」＝只关掉提醒。
        /// </summary>
        Remind = 9
    }

    /// <summary>
    /// 倒计时的计时方式。
    /// </summary>
    public enum CountdownMode
    {
        /// <summary>按时长倒数（默认）。</summary>
        Duration = 0,
        /// <summary>倒数到「电脑时间」的某个时刻（可只填时间，也可带日期）。</summary>
        UntilTime = 1
    }

    /// <summary>
    /// 倒计时配置（模块 B）。
    /// 一个倒计时 = 时长 + 到点动作 + 可选循环 + 可选的「把剩余时间显示到画面上」。
    /// </summary>
    public sealed class CountdownConfig
    {
        /// <summary>计时方式：按时长 / 到指定时刻。</summary>
        public CountdownMode Mode { get; set; } = CountdownMode.Duration;

        /// <summary>
        /// 到指定时刻模式的目标时刻文本（如 21:00 / 21:00:00 / 10-20 21:00 / 2026-10-20 21:00:00）。
        /// 只填时间部分时，按「今天，已过则明天」解释。
        /// </summary>
        public string TargetTimeText { get; set; } = "21:00";

        /// <summary>倒计时时长（秒）。</summary>
        public double DurationSec { get; set; } = 60;

        /// <summary>到点执行的动作。</summary>
        public CountdownAction Action { get; set; } = CountdownAction.None;

        /// <summary>动作目标：输入源序号 / 叠加通道号 / 播放列表项序号（均 1 基）。</summary>
        public int TargetIndex { get; set; } = 1;

        /// <summary>
        /// 弹框提醒的正文（仅 <see cref="CountdownAction.Remind"/> 用）。
        /// 到点后原样显示在系统提醒框里，所以写成人能直接看懂的一句话，例如「请录制推流」。
        /// </summary>
        public string RemindText { get; set; } = "请录制推流";

        /// <summary>到点后是否自动重新开始（循环倒计时）。</summary>
        public bool Repeat { get; set; }

        /// <summary>把剩余时间写进第 N 号叠加通道（0 = 不显示到画面）。</summary>
        public int DisplayOverlayChannel { get; set; }

        /// <summary>
        /// 叠加通道上的显示格式。{t} 会被替换成 mm:ss 或 ss.s。
        /// </summary>
        public string DisplayFormat { get; set; } = "倒计时 {t}";
    }
}
