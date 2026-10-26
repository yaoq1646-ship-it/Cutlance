using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Cutlance.Models;
using Cutlance.Services;

namespace Cutlance
{
    /// <summary>
    /// 倒计时 / 触发器配置窗口（模块 B）。
    ///
    /// 与 CountdownService 共享同一个单例：本窗口只负责编辑配置与开始/暂停/复位，
    /// 真正的「到点做什么」由 MainWindow 订阅 CountdownService.Fired 后执行
    /// （因为切输入、开叠加通道、启停推流这些能力都在主窗口手上）。
    /// 窗口以非模态方式打开，导播可以一边开着它一边操作主界面。
    ///
    /// 两种计时方式：按时长 / 到指定时刻（按电脑时间）——后者的解析与计时都在服务层，
    /// 本窗口只做输入与提示。
    /// </summary>
    public partial class CountdownWindow : Window
    {
        private readonly CountdownService _svc = CountdownService.Instance;

        /// <summary>
        /// 载入配置期间抑制「控件变化 → 写回配置」，避免自我触发。
        ///
        /// ⚠ 初值必须是 <c>true</c>：<c>InitializeComponent()</c> 解析 XAML 时，控件上写的
        ///   <c>Text="…"</c> 会**真实触发** TextChanged。若此时允许写回，就会拿 XAML 里的默认值
        ///   去覆盖用户刚存好的配置（比如把已设的提醒文字冲回「请录制推流」），
        ///   紧接着 LoadFromConfig 再读回被冲掉的默认值 —— 用户会觉得"设置没保存住"。
        ///   LoadFromConfig 的末尾会把它复位为 false。
        /// </summary>
        private bool _loading = true;

        private static readonly Color IdleColor = Color.FromRgb(0x4F, 0xC3, 0xF7);   // 蓝
        private static readonly Color WarnColor = Color.FromRgb(0xFF, 0x52, 0x52);   // 红（最后 10 秒）

        public CountdownWindow()
        {
            InitializeComponent();
            WindowFit.FitToWorkArea(this);   // 小屏自动收敛到桌面可用区（1366x768@125% 下装不下）
            // 工作框统一行为（老板 2026-10-18 第 ④⑤ 条）：置顶 + 抓屏排除 + 不透明
            // + Esc 关闭 / Ctrl+S 保存 + 底部「保存」按钮（自动插到底部「关闭」左边）。
            // 倒计时配置本来就随工程存（ProjectFile.Countdown），所以这里的保存是实打实落盘的。
            WorkFrame.Attach(this);
            BuildActionCombo();
            LoadFromConfig();

            _svc.Tick += OnTick;
            _svc.StateChanged += OnState;
            Closed += (_, _) =>
            {
                // 必须解绑：否则窗口关掉后单例仍持有它的回调引用
                _svc.Tick -= OnTick;
                _svc.StateChanged -= OnState;
            };

            // 打开时先把大字刷成「配置好的时长 / 离目标时刻的剩余」，否则会停在服务的初始值 0.0
            // （正在跑的时候不许 Reset，那会把用户的倒计时打断）
            if (!_svc.IsRunning) _svc.Reset();

            OnTick();
            OnState();
        }

        // —— 初始化 ——

        private void BuildActionCombo()
        {
            AddAction(CountdownAction.None, "无（只倒计时，不做动作）");
            AddAction(CountdownAction.SwitchToInput, "切换到指定输入（目标 = 输入源序号）");
            AddAction(CountdownAction.OverlayOn, "打开指定叠加通道（目标 = 通道号 1-8）");
            AddAction(CountdownAction.OverlayOff, "关闭指定叠加通道（目标 = 通道号 1-8）");
            AddAction(CountdownAction.OverlayToggle, "切换叠加通道显隐（目标 = 通道号 1-8）");
            AddAction(CountdownAction.StartOutput, "开始推流 / 录制");
            AddAction(CountdownAction.StopOutput, "停止推流 / 录制");
            AddAction(CountdownAction.FtbToggle, "淡黑 FTB 开 / 关");
            AddAction(CountdownAction.PlayListItem, "跳到播放列表第 N 项（目标 = 列表项序号）");
            AddAction(CountdownAction.Remind, "弹框提醒（显示自定义文字，可一键开始推流 / 录制）");
        }

        private void AddAction(CountdownAction a, string text) =>
            ActionCombo.Items.Add(new ComboBoxItem { Content = text, Tag = a });

        private void LoadFromConfig()
        {
            _loading = true;
            SelectMode(_svc.Config.Mode);
            TxtTargetTime.Text = string.IsNullOrWhiteSpace(_svc.Config.TargetTimeText) ? "21:00" : _svc.Config.TargetTimeText;
            TxtDuration.Text = _svc.Config.DurationSec.ToString("0.#");
            ChkRepeat.IsChecked = _svc.Config.Repeat;
            SelectAction(_svc.Config.Action);
            TxtTarget.Text = _svc.Config.TargetIndex.ToString();
            TxtRemind.Text = string.IsNullOrWhiteSpace(_svc.Config.RemindText) ? "请录制推流" : _svc.Config.RemindText;
            TxtOvChannel.Text = _svc.Config.DisplayOverlayChannel.ToString();
            TxtFormat.Text = _svc.Config.DisplayFormat;
            _loading = false;
            ApplyModeUi();
            ApplyActionUi();
            UpdateTargetHint();
        }

        private void SelectAction(CountdownAction a)
        {
            foreach (ComboBoxItem it in ActionCombo.Items)
                if (it.Tag is CountdownAction t && t == a) { ActionCombo.SelectedItem = it; return; }
            ActionCombo.SelectedIndex = 0;
        }

        private CountdownAction CurrentAction =>
            ActionCombo.SelectedItem is ComboBoxItem it && it.Tag is CountdownAction a
                ? a : CountdownAction.None;

        private CountdownMode CurrentMode =>
            ModeCombo.SelectedItem is ComboBoxItem it && (it.Tag as string) == "until"
                ? CountdownMode.UntilTime : CountdownMode.Duration;

        private void SelectMode(CountdownMode m)
        {
            string want = m == CountdownMode.UntilTime ? "until" : "duration";
            foreach (ComboBoxItem it in ModeCombo.Items)
                if ((it.Tag as string) == want) { ModeCombo.SelectedItem = it; return; }
            ModeCombo.SelectedIndex = 0;
        }

        /// <summary>按当前模式切换「时长区 / 时刻区」的显隐，并刷新时刻提示。</summary>
        private void ApplyModeUi()
        {
            bool until = CurrentMode == CountdownMode.UntilTime;
            PanelDuration.Visibility = until ? Visibility.Collapsed : Visibility.Visible;
            PanelUntil.Visibility = until ? Visibility.Visible : Visibility.Collapsed;
            UpdateUntilHint();
        }

        /// <summary>把「目标时刻」解析成人话显示出来（解析不了就明确报格式，不静默）。</summary>
        private void UpdateUntilHint()
        {
            // ⚠ XAML 解析期给 TxtTargetTime.Text 赋值就会触发 TextChanged → 走到这里，
            //   而此时声明在它后面的 TxtUntilHint 还没创建 → 直接返回，别拿 null 去赋值。
            //   （和 OutputWindow 里 EngineCombo 的 SelectionChanged 是同一个坑：XAML 解析会真实触发事件）
            if (TxtUntilHint == null) return;

            if (CountdownService.TryParseTarget(TxtTargetTime.Text, out var t))
            {
                var left = t - DateTime.Now;
                string leftText = left.TotalHours >= 1
                    ? $"{left.TotalHours:0.#} 小时"
                    : $"{Math.Max(1, left.TotalMinutes):0.#} 分钟";
                TxtUntilHint.Text = $"→ {CountdownService.DescribeTarget(t)}，现在起还有约 {leftText}（按电脑当前时间计算）";
            }
            else
            {
                TxtUntilHint.Text = "⚠ 时刻认不出来。可填：21:00 ／ 21:00:30 ／ 10-20 21:00 ／ 2026-10-20 21:00:00";
            }
        }

        // —— 刷新 ——

        /// <summary>
        /// 按当前动作切换「动作参数区」：一般动作填目标序号，「弹框提醒」改填提醒文字。
        /// 两者互斥显示 —— 同时摆出来会白白占掉两行高，小屏上容易撑出滚动条。
        /// </summary>
        private void ApplyActionUi()
        {
            bool remind = CurrentAction == CountdownAction.Remind;
            PanelTarget.Visibility = remind ? Visibility.Collapsed : Visibility.Visible;
            PanelRemind.Visibility = remind ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnTick()
        {
            BigTime.Text = _svc.RemainingText;
            BigTime.Foreground = new SolidColorBrush(
                _svc.IsRunning && !_svc.IsPaused && _svc.Remaining <= 10 ? WarnColor : IdleColor);
        }

        private void OnState()
        {
            // ⚠ 只换「图标 + 文字」，不能整体覆盖 Content，否则图标那层 StackPanel 会被顶掉
            TxtPause.Text = _svc.IsPaused ? "继续" : "暂停";
            IcoPause.Data = (Geometry)FindResource(_svc.IsPaused ? "Icon.Play" : "Icon.Pause");

            string target = "";
            if (_svc.Config.Mode == CountdownMode.UntilTime
                && CountdownService.TryParseTarget(_svc.Config.TargetTimeText, out var t))
                target = " · 目标 " + CountdownService.DescribeTarget(t);

            StatusLine.Text = (_svc.IsRunning
                ? (_svc.IsPaused ? "已暂停" : "倒计时进行中…")
                : "就绪") + target;
        }

        private void UpdateTargetHint()
        {
            TargetHint.Text = CurrentAction switch
            {
                CountdownAction.SwitchToInput => "输入源序号（左栏 INPUTS 里的编号）",
                CountdownAction.OverlayOn or CountdownAction.OverlayOff or CountdownAction.OverlayToggle
                    => "叠加通道号 1–8",
                CountdownAction.PlayListItem => "播放列表里的第几项",
                CountdownAction.Remind => "提醒不需要目标序号，改填上面的提醒文字",
                _ => "该动作不需要目标序号"
            };
        }

        /// <summary>循环开关（只属于「按时长倒计时」面板；「到指定时刻」面板已不再提供循环）。</summary>
        private void SyncRepeat(bool v)
        {
            if (_loading) return;
            _svc.Config.Repeat = v;
            _loading = true;
            ChkRepeat.IsChecked = v;
            _loading = false;
        }

        // —— 控件事件 ——

        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentMode == CountdownMode.UntilTime)
            {
                // 先在界面上挡住非法输入：这里 return 不会触发 StateChanged，提示不会被覆盖
                if (!CountdownService.TryParseTarget(TxtTargetTime.Text, out _))
                {
                    StatusLine.Text = "⚠ 目标时刻格式不对，请改成 21:00 或 2026-10-20 21:00:00 这类写法";
                    return;
                }
                _svc.Config.TargetTimeText = TxtTargetTime.Text.Trim();
            }
            else if (double.TryParse(TxtDuration.Text.Trim(), out double d) && d > 0)
            {
                // 时长模式下把界面上的时长先落到配置里，再开跑
                _svc.Config.DurationSec = d;
            }
            _svc.Start();
        }

        private void BtnPause_Click(object sender, RoutedEventArgs e) => _svc.PauseResume();

        private void BtnReset_Click(object sender, RoutedEventArgs e) => _svc.Reset();

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyModeUi();
            if (_loading) return;
            _svc.Config.Mode = CurrentMode;
            _svc.Reset();     // 换模式后让大字立刻预览新方式下的剩余
        }

        private void TxtTargetTime_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateUntilHint();
            // 解析期（XAML 里写了 Text="21:00"）与程序化载入期间都不要回写配置、不要碰计时器
            if (_loading || !IsLoaded) return;
            _svc.Config.TargetTimeText = TxtTargetTime.Text.Trim();
            if (!_svc.IsRunning) _svc.Reset();     // 没在跑时预览"离该时刻还有多久"
        }

        /// <summary>快捷时间：+1 分钟 / +5 分钟 / 下个整点。跨天时自动写成「月-日 时:分」，避免被当成今天。</summary>
        private void BtnQuickTime_Click(object sender, RoutedEventArgs e)
        {
            string? tag = (sender as Button)?.Tag as string;
            DateTime t;
            if (tag == "hour")
            {
                var n = DateTime.Now.AddHours(1);
                t = new DateTime(n.Year, n.Month, n.Day, n.Hour, 0, 0);
                if (t <= DateTime.Now) t = t.AddHours(1);
            }
            else
            {
                t = DateTime.Now.AddMinutes(int.TryParse(tag, out int m) ? m : 1);
            }
            TxtTargetTime.Text = t.Date == DateTime.Now.Date
                ? t.ToString("HH:mm:ss")
                : t.ToString("MM-dd HH:mm:ss");
        }

        private void TxtDuration_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            if (double.TryParse(TxtDuration.Text.Trim(), out double d) && d > 0)
                _svc.Config.DurationSec = d;
            if (!_svc.IsRunning) OnTick();   // 没在跑时让大字跟着预览一下新时长
        }

        private void ChkRepeat_Click(object sender, RoutedEventArgs e) =>
            SyncRepeat(ChkRepeat.IsChecked == true);

        private void ActionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyActionUi();      // 面板显隐是纯界面行为，_loading 期间也要跟着切换
            if (_loading) return;
            _svc.Config.Action = CurrentAction;
            UpdateTargetHint();
        }

        private void TxtRemind_TextChanged(object sender, TextChangedEventArgs e)
        {
            // 解析期（XAML 里写了 Text="请录制推流"）与载入配置期间都不回写，否则会冲掉用户存好的文字
            if (_loading) return;
            _svc.Config.RemindText = TxtRemind.Text;
        }

        private void TxtTarget_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            if (int.TryParse(TxtTarget.Text.Trim(), out int n))
                _svc.Config.TargetIndex = n;
        }

        private void TxtOvChannel_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            if (int.TryParse(TxtOvChannel.Text.Trim(), out int n))
                _svc.Config.DisplayOverlayChannel = Math.Clamp(n, 0, 8);
        }

        private void TxtFormat_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            _svc.Config.DisplayFormat = TxtFormat.Text;
        }
    }
}
