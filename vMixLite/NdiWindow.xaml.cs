using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Cutlance.Services;

namespace Cutlance
{
    /// <summary>
    /// NDI 输入面板（模块F）。
    ///
    /// 交互与「+采集卡」面板同思路：**先看清信号规格，再决定要不要加进来**。
    /// NDI 的源名在局域网里是唯一的（"主机名 (源名)"），列表点一下就能选定。
    ///
    /// ⚠ 发现是异步的：NDI 靠 mDNS / 发现服务广播，刚打开面板时列表常常是空的，
    ///   一两秒后才冒出来 —— 所以这里连扫两轮，并且明确告诉用户「没有」时要检查什么，
    ///   而不是丢一个空列表让人对着发呆。
    /// </summary>
    public partial class NdiWindow : Window
    {
        /// <summary>用户确认添加的 NDI 源名（DialogResult=true 时有效）。</summary>
        public string SelectedSourceName { get; private set; } = "";

        private bool _busy;

        public NdiWindow()
        {
            InitializeComponent();
            WorkFrame.Attach(this);   // 统一：置顶 + 抓屏排除 + 底部「保存/关闭」+ Esc/Ctrl+S
            WindowFit.FitToWorkArea(this);   // 小屏自动收敛到桌面可用区（1080p@150% / 1366x768@125% 都装不下 700 DIP）
            TxtExtraIps.Text = NdiService.Instance.ExtraDiscoveryIps ?? "";
            Loaded += (_, _) => RefreshSources();
        }

        // ==================== ② 额外发现 IP ====================

        /// <summary>
        /// 应用「额外发现 IP」。NDI 默认靠 mDNS 自动发现；本机若装了 VPN / 多块虚拟网卡，
        /// mDNS 常会选错网卡导致**一个源都刷不出来**。填对方的 IP 后改走单播发现即可绕开。
        /// </summary>
        private void BtnApplyIps_Click(object sender, RoutedEventArgs e)
        {
            var ips = (TxtExtraIps.Text ?? "").Trim();
            NdiService.Instance.ExtraDiscoveryIps = ips;   // 赋值会重建发现器
            TxtInfo.Text = ips.Length == 0
                ? "已清空额外发现 IP（只用本机地址 + mDNS）。"
                : "已应用额外发现 IP：" + ips + "　正在重新扫描…";
            RefreshSources();
        }

        // ==================== ① 源发现 ====================

        private void RefreshSources()
        {
            if (_busy) return;
            _busy = true;
            BtnRefresh.IsEnabled = false;
            TxtStatus.Text = "正在扫描…（已启用单播发现兜底；NDI 发现是异步的，首次通常要一两秒）";
            LstSources.ItemsSource = null;

            Task.Run(() =>
            {
                var svc = NdiService.Instance;
                svc.Reinit();       // 上次若初始化失败，这里重新试一次（用户可能刚装上 NDI Runtime）
                if (!svc.IsAvailable)
                    return (List: new List<NdiSourceInfo>(), Err: svc.LastError);

                // 扫两轮：第一轮把发现流程踢起来，第二轮基本就齐了。
                svc.Scan(1200);
                var list = svc.Scan(800);
                return (List: list, Err: svc.LastError);
            }).ContinueWith(t =>
            {
                _busy = false;
                BtnRefresh.IsEnabled = true;
                var list = t.Result.List;
                var err = t.Result.Err;

                if (!string.IsNullOrEmpty(err))
                {
                    TxtStatus.Text = "⚠ " + err;
                    return;
                }

                LstSources.ItemsSource = list;
                TxtStatus.Text = list.Count == 0
                    ? "没有发现 NDI 源。请确认：① 发送端已开启 NDI 输出；② 与本机在同一局域网；"
                      + "③ 防火墙未拦截；④ 对方在别的机器上时，把它的 IP 填进「② 额外发现 IP」再刷新。"
                    : $"发现 {list.Count} 个 NDI 源，选中后点「检测」可先看规格。";
                if (list.Count > 0) LstSources.SelectedIndex = 0;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e) => RefreshSources();

        private void LstSources_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LstSources.SelectedItem is not NdiSourceInfo sel) return;
            TxtInfo.Text = "已选中：" + sel.Name
                         + (string.IsNullOrEmpty(sel.Url) ? "" : "　（" + sel.Url + "）");
        }

        // ==================== ③ 检测 / 手动源名 ====================

        private void BtnTest_Click(object sender, RoutedEventArgs e) => DoTest(CurrentName());

        private void DoTest(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                TxtInfo.Text = "请先在列表里选一个源，或在下面手动填源名。";
                return;
            }
            if (_busy) return;

            _busy = true;
            BtnTest.IsEnabled = false;
            TxtInfo.Text = "正在连接 " + name + " …";

            Task.Run(() =>
            {
                var sess = NdiService.Instance.OpenReceiver(name, out var err);
                if (sess == null) return (Info: "", Err: err);
                var info = $"{sess.Width}×{sess.Height} @ {sess.Fps} fps · BGRA（NDI 直收）";
                sess.Dispose();
                return (Info: info, Err: "");
            }).ContinueWith(t =>
            {
                _busy = false;
                BtnTest.IsEnabled = true;
                if (!string.IsNullOrEmpty(t.Result.Err))
                    TxtInfo.Text = "✗ " + t.Result.Err;
                else
                    TxtInfo.Text = "✓ 可以连接：" + t.Result.Info;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>当前要用的源名：手输框优先（用户主动填了就听他的），否则取列表选中项。</summary>
        private string CurrentName()
        {
            var manual = (TxtManual.Text ?? "").Trim();
            if (manual.Length > 0) return manual;
            return LstSources.SelectedItem is NdiSourceInfo s ? s.Name : "";
        }

        // ==================== ④ 添加 / 关闭 ====================

        private void BtnAdd_Click(object sender, RoutedEventArgs e)
        {
            var name = CurrentName();
            if (string.IsNullOrWhiteSpace(name))
            {
                TxtNote.Text = "请先选中一个 NDI 源，或手动填源名。";
                return;
            }
            SelectedSourceName = name;
            DialogResult = true;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            // 只销毁「列出源」用的 finder；接收会话由输入链路自己持有，不能在这里一起收掉。
            NdiService.Instance.ReleaseFinder();
        }
    }
}
