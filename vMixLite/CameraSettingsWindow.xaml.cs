using Cutlance.Services;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace Cutlance
{
    /// <summary>
    /// 摄像头采集设置对话框（模块6，对标 OBS「属性」/ vMix「Input Settings」）。
    /// 负责选择设备与采集参数（分辨率/帧率/格式/音频），由 MainWindow 读取结果后
    /// 生成带参数的摄像头输入源（dshow 选项落到 VideoEngine.CreateCameraMedia）。
    /// </summary>
    public partial class CameraSettingsWindow : Window
    {
        public string SelectedDevice { get; private set; } = "";
        public int CamWidth { get; private set; }
        public int CamHeight { get; private set; }
        public int CamFps { get; private set; }
        public string Chroma { get; private set; } = "";
        public bool Audio { get; private set; } = true;

        public CameraSettingsWindow(IEnumerable<string> devices, string? preselect = null,
                                    int w = 0, int h = 0, int fps = 0, string? chroma = null, bool audio = true)
        {
            InitializeComponent();
            WorkFrame.Attach(this);   // 统一：置顶 + 抓屏排除 + 底部「保存/关闭」+ Esc/Ctrl+S

            foreach (var d in devices)
                DeviceCombo.Items.Add(new ComboBoxItem { Content = d, Tag = d });
            if (DeviceCombo.Items.Count > 0)
                DeviceCombo.SelectedIndex = 0;
            if (!string.IsNullOrWhiteSpace(preselect))
                foreach (ComboBoxItem it in DeviceCombo.Items)
                    if ((it.Tag as string) == preselect) { DeviceCombo.SelectedItem = it; break; }

            // 分辨率预设（Tag = "WxH"，空 = 设备默认）
            AddRes("默认（设备自选）", "");
            AddRes("1920 × 1080 (Full HD)", "1920x1080");
            AddRes("1280 × 720 (HD)", "1280x720");
            AddRes("960 × 540", "960x540");
            AddRes("848 × 480", "848x480");
            AddRes("640 × 480 (VGA)", "640x480");
            AddRes("640 × 360", "640x360");
            AddRes("320 × 240 (QVGA)", "320x240");
            SelectRes(w, h);

            // 帧率预设（Tag = 数字字符串，空 = 默认）
            AddItem(FpsCombo, "默认", "");
            foreach (var f in new[] { 60, 50, 30, 25, 20, 15 })
                AddItem(FpsCombo, f + " fps", f.ToString());
            SelectByTag(FpsCombo, fps > 0 ? fps.ToString() : "");

            // 格式预设
            AddItem(FmtCombo, "默认（设备自选）", "");
            AddItem(FmtCombo, "MJPG", "MJPG");
            AddItem(FmtCombo, "YUY2", "YUY2");
            AddItem(FmtCombo, "NV12", "NV12");
            SelectByTag(FmtCombo, chroma ?? "");

            AudioChk.IsChecked = audio;
        }

        private static void AddItem(ComboBox box, string content, string tag) =>
            box.Items.Add(new ComboBoxItem { Content = content, Tag = tag });

        private void AddRes(string content, string tag) => AddItem(ResCombo, content, tag);

        private void SelectRes(int w, int h)
        {
            var tag = (w > 0 && h > 0) ? $"{w}x{h}" : "";
            SelectByTag(ResCombo, tag);
        }

        private static void SelectByTag(ComboBox box, string tag)
        {
            foreach (ComboBoxItem it in box.Items)
                if ((it.Tag as string) == tag) { box.SelectedItem = it; return; }
            if (box.Items.Count > 0) box.SelectedIndex = 0;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            SelectedDevice = (DeviceCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

            var res = (ResCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
            if (!string.IsNullOrWhiteSpace(res))
            {
                var parts = res.Split('x');
                if (parts.Length == 2
                    && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
                { CamWidth = w; CamHeight = h; }
            }

            var fpsTag = (FpsCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
            if (int.TryParse(fpsTag, out var fps)) CamFps = fps;

            Chroma = (FmtCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
            Audio = AudioChk.IsChecked == true;

            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
