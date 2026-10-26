using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Cutlance.Services;

namespace Cutlance
{
    /// <summary>PPT 导入方式（老板 2026-10-19 21:06 第 2 条）。</summary>
    public enum PptImportMode
    {
        /// <summary>① 每页导出成图片 → 图片序列。不挡屏、PPT 可关；没有动画 / 转场 / 声音。</summary>
        ImageSequence,

        /// <summary>② 全屏放映 + 抓放映窗口 → 视频采集。动画 / 转场 / 视频都能播；放映窗必须一直可见。</summary>
        Capture,
    }

    /// <summary>
    /// PPT 导入方式选择窗。
    ///
    /// 为什么必须让人选：两种路线的能力边界是互补的，程序没法替人判断 ——
    ///   · 有动画 / 要声音 → 只能视频采集（图片序列把动画丢了，等于毁掉直播内容）；
    ///   · 没动画 / 想要稳定不挡屏 → 图片序列更稳（PPT 关掉也能播、可自动播）。
    ///
    /// 「声音」这一栏只在视频采集下可用：图片序列是静态图，本来就没有声音；
    /// 而抓屏（screen://）只出画面，声音得另接一路回环录音设备（VB-Audio Cable / 立体声混音）——
    /// 这条限制必须写在界面上，不能让人以为选了②就自动有声音。
    /// </summary>
    public partial class PptImportWindow : Window
    {
        public PptImportMode Mode { get; private set; } = PptImportMode.Capture;

        /// <summary>选中的 dshow 录音设备名；空字符串 = 不采集声音。</summary>
        public string AudioDevice { get; private set; } = "";

        private sealed class AudioChoice
        {
            public string Label { get; init; } = "";
            public string Device { get; init; } = "";
            public override string ToString() => Label;
        }

        public PptImportWindow()
        {
            InitializeComponent();

            // 最高权限（老板 2026-10-21 第 1 条）：置顶 + 不透明 + 抓屏排除 + Esc 关闭。
            // 这个窗口以前漏装 —— 播出框是全屏置顶的，一开就被压在下面，根本选不了导入方式。
            WorkFrame.Pin(this);

            // ⚠ XAML 里 RbCapture 的 IsChecked="True" 在解析期就会触发 Checked，
            //   那一刻另一个卡片可能还没建出来 → ApplyCardStyles() 内部会判空自保，
            //   这里再补一刀确保初始观感正确。
            ApplyCardStyles();

            CmbAudio.ItemsSource = new List<AudioChoice>
            {
                new() { Label = "不采集声音（只有画面）", Device = "" }
            };
            CmbAudio.SelectedIndex = 0;
            UpdateAudioEnabled();

            Loaded += async (_, _) => await FillAudioDevicesAsync();
        }

        private async Task FillAudioDevicesAsync()
        {
            List<string> devs;
            try { devs = await Task.Run(() => FfmpegOutput.ListAudioDevices()); }
            catch { return; }

            if (devs.Count == 0) return;
            if (!IsLoaded) return;

            var keep = (CmbAudio.SelectedItem as AudioChoice)?.Device ?? "";
            var list = new List<AudioChoice> { new() { Label = "不采集声音（只有画面）", Device = "" } };
            foreach (var d in devs) list.Add(new AudioChoice { Label = d, Device = d });

            CmbAudio.ItemsSource = list;
            int idx = list.FindIndex(x => x.Device == keep);
            CmbAudio.SelectedIndex = idx >= 0 ? idx : 0;
        }

        private void RbMode_Checked(object sender, RoutedEventArgs e)
        {
            // ⚠ 解析期会真实触发：控件可能还没建 → 判空退出（铁律：XAML 解析期会真跑事件）
            if (CardImage == null || CardCapture == null) return;
            ApplyCardStyles();
            UpdateAudioEnabled();
        }

        private void RbMode_Unchecked(object sender, RoutedEventArgs e)
        {
            if (CardImage == null || CardCapture == null) return;
            ApplyCardStyles();
        }

        private void ApplyCardStyles()
        {
            if (CardImage == null || CardCapture == null || RbImage == null || RbCapture == null) return;

            var accent = (Brush)FindResource("Brush.Accent");
            var border = (Brush)FindResource("Brush.Border");
            var surface = (Brush)FindResource("Brush.Bg.Surface");
            var raised = (Brush)FindResource("Brush.Bg.Raised");

            bool img = RbImage.IsChecked == true;
            CardImage.BorderBrush = img ? accent : border;
            CardImage.Background = img ? raised : surface;
            CardCapture.BorderBrush = img ? border : accent;
            CardCapture.Background = img ? surface : raised;
        }

        /// <summary>图片序列没有声音可采 —— 选①时把声音那一栏整体置灰，避免误导。</summary>
        private void UpdateAudioEnabled()
        {
            if (CmbAudio == null) return;
            CmbAudio.IsEnabled = RbCapture.IsChecked == true;
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            Mode = RbImage.IsChecked == true ? PptImportMode.ImageSequence : PptImportMode.Capture;
            AudioDevice = Mode == PptImportMode.Capture
                ? ((CmbAudio.SelectedItem as AudioChoice)?.Device ?? "")
                : "";
            DialogResult = true;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
