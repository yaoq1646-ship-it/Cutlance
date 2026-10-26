using Cutlance.Services;
using System;
using System.Windows;
using System.Windows.Controls;
using Cutlance.Models;

namespace Cutlance
{
    /// <summary>
    /// 输入级裁切 / 旋转 / 镜像配置窗口（模块C）。
    ///
    /// 非模态：改一个数值就立刻应用到该输入，主界面的预览框与输出画面即时可见。
    /// 窗口本身不碰任何渲染细节 —— 它只改 <see cref="InputSource"/> 上的几何属性。
    /// 真正的画面处理由「输入预处理层」完成：主窗口会把这个输入的常驻 ffmpeg 停掉，
    /// 下次播放时按新参数重新起一个（见 <see cref="Services.InputPreprocessService"/>），
    /// 因此「同一个输入在任何地方播都带同样的裁切」，这才叫输入级。
    /// </summary>
    public partial class InputGeometryWindow : Window
    {
        private readonly InputSource _src;
        private readonly Action<InputSource> _onApply;
        private bool _loading = true;

        public InputGeometryWindow(InputSource src, string srcInfo, Action<InputSource> onApply)
        {
            InitializeComponent();
            WorkFrame.Attach(this);   // 统一：置顶 + 抓屏排除 + 底部「保存/关闭」+ Esc/Ctrl+S

            _src = src;
            _onApply = onApply;

            Title = "输入裁切 / 旋转 —— " + src.Name;
            TitleLine.Text = src.Name + "（" + src.TypeShort + "）";
            if (!string.IsNullOrWhiteSpace(srcInfo)) SrcInfo.Text = srcInfo;

            // 载入现有设置（这一段会触发 Field_Changed，靠 _loading 抑制自我应用）
            TxtTop.Text = _src.CropTop.ToString();
            TxtBottom.Text = _src.CropBottom.ToString();
            TxtLeft.Text = _src.CropLeft.ToString();
            TxtRight.Text = _src.CropRight.ToString();
            ChkFlipH.IsChecked = _src.FlipH;
            ChkFlipV.IsChecked = _src.FlipV;

            foreach (ComboBoxItem it in RotateCombo.Items)
                if (it.Tag is string t && int.TryParse(t, out var deg) && deg == _src.Rotate)
                { RotateCombo.SelectedItem = it; break; }
            if (RotateCombo.SelectedItem == null) RotateCombo.SelectedIndex = 0;

            _loading = false;
            StatusLine.Text = "当前：" + _src.GeometrySummary;
        }

        private void Field_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            Apply();
        }

        private void Apply()
        {
            // 负数无意义，直接按 0 处理；上界交由 VLC 自行按源尺寸钳制
            _src.CropTop = ReadInt(TxtTop);
            _src.CropBottom = ReadInt(TxtBottom);
            _src.CropLeft = ReadInt(TxtLeft);
            _src.CropRight = ReadInt(TxtRight);

            int rot = 0;
            if (RotateCombo.SelectedItem is ComboBoxItem it && it.Tag is string t)
                int.TryParse(t, out rot);
            _src.Rotate = rot;

            _src.FlipH = ChkFlipH.IsChecked == true;
            _src.FlipV = ChkFlipV.IsChecked == true;

            // 这个窗口用的是**像素**裁切，与精调面板的**百分比**裁切是同一件事的两种表述。
            // 在这里一改就要把百分比那套关掉，否则两套同时"有效"会打成平手看不出谁起作用。
            _src.UsePctCrop = false;
            _src.CropPctLeft = _src.CropPctTop = _src.CropPctRight = _src.CropPctBottom = 0;

            // 交给主窗口：重建该输入在所有播放位置（预览 / 图层 / 播放列表）的媒体
            _onApply(_src);
            StatusLine.Text = "已应用：" + _src.GeometrySummary;
        }

        private static int ReadInt(TextBox tb)
        {
            if (tb == null) return 0;
            return int.TryParse((tb.Text ?? "").Trim(), out var v) ? Math.Max(0, v) : 0;
        }

        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            _loading = true;
            TxtTop.Text = "0"; TxtBottom.Text = "0";
            TxtLeft.Text = "0"; TxtRight.Text = "0";
            ChkFlipH.IsChecked = false; ChkFlipV.IsChecked = false;
            RotateCombo.SelectedIndex = 0;
            _loading = false;
            Apply();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
