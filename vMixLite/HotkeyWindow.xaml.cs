using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Cutlance.Models;
using Cutlance.Services;

namespace Cutlance
{
    /// <summary>
    /// 「导播键盘 / 快捷键」设置窗口。
    ///
    /// 只做两件事：把动作目录列出来、把用户按下的键记下来交给 <see cref="HotkeyMap"/>。
    /// 真正的触发在 MainWindow（全局键盘钩子 + 键盘事件），本窗口不参与运行期触发。
    ///
    /// 窗口是**非模态**的：导播可以把它开着，切回主界面边试边调。
    /// </summary>
    public partial class HotkeyWindow : Window
    {
        /// <summary>列表行（一个动作一行）。</summary>
        public sealed class HotkeyRow : INotifyPropertyChanged
        {
            public string Id { get; init; } = "";
            public string Group { get; init; } = "";
            public string Title { get; init; } = "";
            public string Hint { get; init; } = "";

            private string _combo = "";
            private bool _capturing;

            public string Combo
            {
                get => _combo;
                set { _combo = value ?? ""; Notify(nameof(Combo)); Notify(nameof(ComboDisplay)); Notify(nameof(ComboBrush)); }
            }

            /// <summary>该行正在等用户按键。</summary>
            public bool Capturing
            {
                get => _capturing;
                set { _capturing = value; Notify(nameof(Capturing)); Notify(nameof(ComboDisplay)); Notify(nameof(ComboBrush)); }
            }

            public string ComboDisplay => Capturing ? "请按外设上的键…" : HotkeyMap.Display(Combo);

            public Brush? ComboBrush => Capturing ? BrushCapturing : (string.IsNullOrEmpty(Combo) ? BrushMuted : BrushNormal);

            // 三个画刷在窗口构造时从主题层解析一次（本项目没有运行时换主题）
            internal static Brush? BrushNormal, BrushMuted, BrushCapturing;

            public event PropertyChangedEventHandler? PropertyChanged;
            private void Notify(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        }

        private readonly List<HotkeyRow> _rows = new();
        private HotkeyRow? _capturing;

        public HotkeyWindow()
        {
            InitializeComponent();
            WorkFrame.Attach(this);   // 统一：置顶 + 抓屏排除 + 底部「保存/关闭」+ Esc/Ctrl+S
            WindowFit.FitToWorkArea(this);   // 小屏（1366x768@125%）自动收敛到可用区

            HotkeyRow.BrushNormal = TryBrush("Brush.Text");
            HotkeyRow.BrushMuted = TryBrush("Brush.Text.Muted");
            HotkeyRow.BrushCapturing = TryBrush("Brush.Gold");

            BuildRows();

            ChkGlobal.IsChecked = HotkeyMap.GlobalEnabled;
            UpdateGlobalHint();

            // 关窗即解除「捕获模式」，并兜底保存一次
            Closed += (_, _) =>
            {
                HotkeyMap.CaptureMode = false;
                _capturing = null;
                Save();
            };
            // 捕获模式**只在真的在录键时为 true**（_capturing != null），不是"本窗口有焦点"就挂起。
            // 理由：窗口停在副屏是人家的正常用法，那会儿外设按键必须照常生效；
            // 真正需要挂起的只有"等用户按键"这一小段时间（否则钩子会把要录的键抢走）。
            Loaded += (_, _) => { SyncCaptureMode(); HotkeyList.Focus(); };
        }

        private Brush? TryBrush(string key)
        {
            try { return FindResource(key) as Brush; } catch { return null; }
        }

        private void SyncCaptureMode() => HotkeyMap.CaptureMode = _capturing != null;

        private void BuildRows()
        {
            foreach (var a in HotkeyMap.Actions)
            {
                _rows.Add(new HotkeyRow
                {
                    Id = a.Id, Group = a.Group, Title = a.Title, Hint = a.Hint,
                    Combo = HotkeyMap.Get(a.Id)
                });
            }

            var view = CollectionViewSource.GetDefaultView(_rows);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(HotkeyRow.Group)));
            HotkeyList.ItemsSource = view;
        }

        // ================= 设定 / 清除 =================

        private void SetBtn_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is not HotkeyRow row) return;

            EndCapture();                    // 先结束上一行的捕获
            _capturing = row;
            row.Capturing = true;
            SyncCaptureMode();

            StatusLine.Text = $"正在录制按键：{row.Title}";
            // 焦点抓回本窗口，否则 PreviewKeyDown 收不到（按钮点击后焦点在按钮上，仍在窗口内，但显式更稳）
            Keyboard.Focus(this);
        }

        private void ClearBtn_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is not HotkeyRow row) return;
            EndCapture();
            row.Combo = "";
            HotkeyMap.Set(row.Id, "");
            Save();
            StatusLine.Text = $"已清除：{row.Title}";
        }

        private void BtnResetAll_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("把所有动作恢复成内置默认快捷键？\n（你自己加的绑定会全部丢掉）",
                    "Cutlance · 导播键盘", MessageBoxButton.OKCancel, MessageBoxImage.Question)
                != MessageBoxResult.OK) return;

            EndCapture();
            HotkeyMap.ResetToDefaults();
            RefreshAllRows();
            Save();
            StatusLine.Text = "已全部恢复默认";
        }

        private void BtnClearAll_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("清空所有按键绑定？\n（包括 Ctrl+S / F5 这些内置的，清完就一个快捷键都没有了）",
                    "Cutlance · 导播键盘", MessageBoxButton.OKCancel, MessageBoxImage.Question)
                != MessageBoxResult.OK) return;

            EndCapture();
            // 先全部置空，再显式覆盖默认 —— Set 空串写在配置里就是"用户清掉了"，不会退回默认
            foreach (var a in HotkeyMap.Actions) HotkeyMap.Set(a.Id, "");
            RefreshAllRows();
            Save();
            StatusLine.Text = "已清空全部绑定";
        }

        private void ChkGlobal_Click(object sender, RoutedEventArgs e)
        {
            HotkeyMap.GlobalEnabled = ChkGlobal.IsChecked == true;
            UpdateGlobalHint();
            Save();
            StatusLine.Text = HotkeyMap.GlobalEnabled ? "已开启全局生效" : "已关闭全局生效（只在主窗前台时响应）";
        }

        private void UpdateGlobalHint() =>
            GlobalHint.Opacity = ChkGlobal.IsChecked == true ? 1.0 : 0.45;

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        // ================= 按键捕获 =================

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_capturing == null)
            {
                // 没在录键：Esc 关窗（顺手给个顺手的退出口）
                if (e.Key == Key.Escape) { e.Handled = true; Close(); }
                return;
            }

            e.Handled = true;   // 录键期间吃掉一切按键，别让它们去翻列表 / 触发按钮

            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (key == Key.Escape) { EndCapture(); StatusLine.Text = "已取消"; return; }
            if (key == Key.None || HotkeyMap.IsModifierKey(key)) return;   // 只按了 Ctrl/Shift → 继续等真正的键

            var combo = HotkeyMap.Combo(key, Keyboard.Modifiers);
            if (string.IsNullOrEmpty(combo)) return;

            var row = _capturing;

            // 冲突：这个键已经给了别的动作 → 问一声再改绑
            var owner = HotkeyMap.OwnerOfCombo(combo, row.Id);
            if (owner != null)
            {
                var ok = MessageBox.Show(
                    $"「{HotkeyMap.Display(combo)}」已经绑给「{HotkeyMap.TitleOf(owner)}」。\n\n要改绑到「{row.Title}」上吗？\n（原来那个动作会变成未绑定）",
                    "Cutlance · 导播键盘", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (ok != MessageBoxResult.OK) { EndCapture(); StatusLine.Text = "已取消（该键已被占用）"; return; }
                HotkeyMap.Set(owner, "");
                var other = _rows.FirstOrDefault(r => r.Id == owner);
                if (other != null) other.Combo = "";
            }

            row.Combo = combo;
            HotkeyMap.Set(row.Id, combo);
            EndCapture();
            Save();
            StatusLine.Text = $"已绑定：{row.Title} → {HotkeyMap.Display(combo)}";
        }

        private void EndCapture()
        {
            if (_capturing != null) _capturing.Capturing = false;
            _capturing = null;
            SyncCaptureMode();
        }

        // ================= 小工具 =================

        private void RefreshAllRows()
        {
            foreach (var r in _rows) r.Combo = HotkeyMap.Get(r.Id);
        }

        private void Save()
        {
            if (!HotkeyMap.Save(out var err) && !string.IsNullOrEmpty(err))
                StatusLine.Text = "⚠ 保存失败：" + err;
        }
    }
}
