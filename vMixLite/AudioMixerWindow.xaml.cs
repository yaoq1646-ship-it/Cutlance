using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Cutlance.Models;
using Cutlance.Services;

namespace Cutlance
{
    /// <summary>
    /// 音频混音台（模块8）：最多八路音频采集，各自选设备 + 角色档 + 音量微调 + 静音；
    /// 八路混音后统一进入「推流 + 录制」。
    /// 老板 2026-10-20：「采集卡从三路增加到八路」—— 采集路数由四路扩到八路，
    /// 现场多机位 / 多支话筒 / 采集卡 HDMI 内嵌音频都能各占一路，不必再靠调音台外部混音。
    /// 角色档解决「采集卡同时送进多路、电平不一致、主次不清」的问题：先定主音/次音/背景拿基准衰减，
    /// 再用滑杆做该路相对微调，最右栏显示最终 dB。
    /// 设备列表通过 ffmpeg -list_devices 实时枚举（真实设备，不是写死的清单）。
    /// </summary>
    public partial class AudioMixerWindow : Window
    {
        private const string Unused = "（未使用）";

        /// <summary>采集通道数（老板 2026-10-20：由四路扩到八路）。改这一个值，UI/枚举/混音全跟着变。</summary>
        private const int ChCount = 8;

        /// <summary>下拉项顺序必须与 AudioRole 枚举值一致（Main=0 / Secondary=1 / Background=2）。</summary>
        private static readonly (AudioRole Role, string Text)[] RoleItems =
        {
            (AudioRole.Main,       "主音 0dB"),
            (AudioRole.Secondary,  "次音 -8dB"),
            (AudioRole.Background, "背景 -14dB")
        };

        private readonly StreamService _svc = StreamService.Instance;
        private readonly List<AudioChannel> _ch;
        private bool _ready;      // 界面初值回填期间，忽略控件事件
        private bool _loading;    // 设备列表加载中
        private bool _syncUi;     // 「关」按钮与「静音」勾选框互相同步时防止事件回环
        private static bool _autoPicked;   // 是否已做过「首次自动预选麦克风」

        /// <summary>当前开着的混音台（同时只会有一个）。主窗口改输出框时靠它刷新「画面声音」。</summary>
        private static AudioMixerWindow? _current;

        /// <summary>主窗口（读 Layers 用）。null = 独立打开，画面声音段只显示提示。</summary>
        private readonly MainWindow? _host;

        /// <summary>「画面声音」整段重排中：此时控件事件一律忽略，避免建行/回填时把路由改一遍。</summary>
        private int _progGuard;

        /// <summary>重排中（用计数而不是 bool：建行时 Sync 会嵌套进来）。</summary>
        private bool ProgBusy => _progGuard > 0;

        /// <summary>上次建行的输入源顺序。与当前一致时只刷数值、不重建控件 —— 否则拖滑杆会把滑块本身换掉，拖一下就断。</summary>
        private List<InputSource> _progKey = new();

        /// <summary>每一行的「从模型刷到界面」委托，由 BuildProgramRow 产出。</summary>
        private readonly List<Action> _progSyncers = new();

        // ── 自检用（CUTLANCE_PROG_AUDIO_TEST=1）：直接驱动真实控件，走跟老板点击完全一样的路径 ──
        internal int TestRowCount => _progSyncers.Count;
        internal void TestSetRole(int row, AudioRole role)
        {
            if (row < 0 || row >= _progUi.Count) return;
            _progUi[row].Role.SelectedIndex = (int)role;
        }
        internal void TestSetVolume(int row, double vol)
        {
            if (row < 0 || row >= _progUi.Count) return;
            _progUi[row].Vol.Value = vol;
        }
        internal void TestSetMuted(int row, bool muted)
        {
            if (row < 0 || row >= _progUi.Count) return;
            _progUi[row].Mut.IsChecked = muted;
        }
        internal string TestRowText(int row)
        {
            if (row < 0 || row >= _progUi.Count) return "-";
            return $"角色={_progUi[row].Role.SelectedIndex} 音量={_progUi[row].Vol.Value:0} 静音={_progUi[row].Mut.IsChecked}";
        }
        private readonly List<(ComboBox Role, Slider Vol, CheckBox Mut)> _progUi = new();

        /// <summary>请求以新参数重启输出（由主窗口处理 stop → start）。</summary>
        public event EventHandler? RestartRequested;

        public AudioMixerWindow() : this(null) { }

        /// <summary>
        /// <paramref name="host"/> 用来读当前输出框的图层（「画面声音」那一段）并回推路由。
        /// 传 null 也能正常打开（只是那一段会提示"输出框里还没有画面"），
        /// 保证任何老调用点、任何设计器预览都不会因为缺它而崩。
        /// </summary>
        public AudioMixerWindow(MainWindow? host)
        {
            _host = host;
            _ch = _svc.AudioChannels;
            InitializeComponent();
            FillRoles();              // 必须在 ApplyToUi 之前：否则给空列表设 SelectedIndex 会抛异常
            FillChannelCompLevels();  // 逐路压缩档（2026-10-24），同样要先有项
            FillCompLevels();         // 同上：压缩档下拉也要先有项
            WorkFrame.Attach(this);   // 统一：置顶 + 抓屏排除 + 底部「保存/关闭」+ Esc/Ctrl+S

            // 回填当前设置（忽略事件）
            _ready = false;
            for (int i = 1; i <= ChCount; i++) ApplyToUi(i);
            ApplyCompToUi();
            _ready = true;
            RefreshHints();

            _current = this;
            RebuildProgramSounds();   // 画面声音（输出框里正在播的几路）
            FitToWorkArea();
            _ = LoadDevicesAsync();
        }

        /// <summary>
        /// 八路排下来整窗约 700 高，小屏笔电（1366×768@125% 可用高只有 614 DIP）放不下。
        /// 内容在 ScrollViewer 里，所以直接把窗口高度夹到可用区内即可，每路都还滚得到。
        /// </summary>
        private void FitToWorkArea()
        {
            try
            {
                var wa = SystemParameters.WorkArea;
                double maxH = wa.Height - 60;
                if (maxH > 320 && Height > maxH) Height = maxH;
                double maxW = wa.Width - 60;
                if (maxW > 480 && Width > maxW) Width = maxW;
            }
            catch { }
        }

        // ================= 设备列表 =================

        private async Task LoadDevicesAsync()
        {
            if (_loading) return;
            _loading = true;
            DeviceHint.Foreground = System.Windows.Media.Brushes.Gray;
            DeviceHint.Text = "正在枚举音频采集设备…";
            List<string> devices = new();
            await Task.Run(() => devices = FfmpegOutput.ListAudioDevices());

            _ready = false;
            for (int i = 1; i <= ChCount; i++)
            {
                var combo = ComboOf(i);
                combo.Items.Clear();
                combo.Items.Add(new ComboBoxItem { Content = Unused, Tag = "" });
                foreach (var d in devices)
                    combo.Items.Add(new ComboBoxItem { Content = d, Tag = d });
            }

            // 首次打开且八路都空：自动为通道 1 预选默认麦克风（保留原来「带麦」的默认行为，避免升级后无声）
            string autoMsg = "";
            if (!_autoPicked && devices.Count > 0 && _ch.TrueForAll(c => string.IsNullOrWhiteSpace(c.DeviceName)))
            {
                _autoPicked = true;
                var def = devices.Find(d => d.Contains("麦克风")) ??
                          devices.Find(d => d.IndexOf("micro", StringComparison.OrdinalIgnoreCase) >= 0) ??
                          devices[0];
                _ch[0].DeviceName = def;
                autoMsg = "已自动为「通道 1」预选默认麦克风：" + def + "（可随时改选或设为未使用）";
            }

            for (int i = 1; i <= ChCount; i++)
            {
                var combo = ComboOf(i);
                var cur = _ch[i - 1].DeviceName;
                int idx = 0;
                for (int k = 0; k < combo.Items.Count; k++)
                    if ((string)((ComboBoxItem)combo.Items[k]).Tag == cur) { idx = k; break; }
                combo.SelectedIndex = idx;
            }
            _ready = true;
            _loading = false;

            // 诊断（仅截图态）：记下"枚举到几个设备、每路模型里的名字、下拉最终选中第几项、显示成什么"
            if (Environment.GetEnvironmentVariable("CUTLANCE_MIXER_SHOT") == "1")
            {
                try
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append($"[设备回填] 枚举 {devices.Count} 个; ");
                    for (int i = 1; i <= ChCount; i++)
                    {
                        var cb = ComboOf(i);
                        string shown = cb.SelectedItem is ComboBoxItem ci ? (ci.Content as string ?? "") : "(null)";
                        sb.Append($"#{i} model=[{_ch[i - 1].DeviceName}] idx={cb.SelectedIndex} shown=[{shown}] items={cb.Items.Count}; ");
                    }
                    ErrorLog.Info(sb.ToString());
                }
                catch { }
            }

            if (!FfmpegOutput.IsAvailable)
            {
                DeviceHint.Foreground = System.Windows.Media.Brushes.OrangeRed;
                DeviceHint.Text = "⚠ 未找到 ffmpeg.exe，无法枚举设备；请在「输出设置」里确认 FFmpeg 可用（默认探测 C:\\FFMPEG\\bin\\ffmpeg.exe）。";
            }
            else if (devices.Count == 0)
            {
                DeviceHint.Foreground = System.Windows.Media.Brushes.OrangeRed;
                DeviceHint.Text = "⚠ 未枚举到音频采集设备（请确认麦克风/线路输入已连接）。";
            }
            else
            {
                DeviceHint.Foreground = System.Windows.Media.Brushes.Gray;
                DeviceHint.Text = $"已发现 {devices.Count} 个音频采集设备（本机含：麦克风阵列、VB-Cable、vMix Audio 等）。";
            }
            if (autoMsg.Length > 0)
            {
                DeviceHint.Foreground = System.Windows.Media.Brushes.LightGreen;
                DeviceHint.Text = autoMsg;
            }
            RefreshHints();
        }

        // 通道号 → 那一排控件（1..ChCount）。用 switch 表达式，加通道只需在末尾补一行。
        private ComboBox ComboOf(int i) => i switch
        {
            1 => Dev1, 2 => Dev2, 3 => Dev3, 4 => Dev4,
            5 => Dev5, 6 => Dev6, 7 => Dev7, _ => Dev8
        };
        private ComboBox RoleOf(int i) => i switch
        {
            1 => Role1, 2 => Role2, 3 => Role3, 4 => Role4,
            5 => Role5, 6 => Role6, 7 => Role7, _ => Role8
        };
        private ToggleButton PwrOf(int i) => i switch
        {
            1 => Pwr1, 2 => Pwr2, 3 => Pwr3, 4 => Pwr4,
            5 => Pwr5, 6 => Pwr6, 7 => Pwr7, _ => Pwr8
        };
        private Slider VolOf(int i) => i switch
        {
            1 => Vol1, 2 => Vol2, 3 => Vol3, 4 => Vol4,
            5 => Vol5, 6 => Vol6, 7 => Vol7, _ => Vol8
        };
        private CheckBox MutOf(int i) => i switch
        {
            1 => Mut1, 2 => Mut2, 3 => Mut3, 4 => Mut4,
            5 => Mut5, 6 => Mut6, 7 => Mut7, _ => Mut8
        };
        private TextBlock VolTextOf(int i) => i switch
        {
            1 => Vol1Text, 2 => Vol2Text, 3 => Vol3Text, 4 => Vol4Text,
            5 => Vol5Text, 6 => Vol6Text, 7 => Vol7Text, _ => Vol8Text
        };

        /// <summary>每路的压缩档下拉（2026-10-24）。</summary>
        private ComboBox CmpOf(int i) => i switch
        {
            1 => Cmp1, 2 => Cmp2, 3 => Cmp3, 4 => Cmp4,
            5 => Cmp5, 6 => Cmp6, 7 => Cmp7, _ => Cmp8
        };

        /// <summary>把「关 / 轻 / 中 / 强」四档灌进八路压缩下拉（与总线压缩下拉同一套档位名）。</summary>
        private void FillChannelCompLevels()
        {
            for (int i = 1; i <= ChCount; i++)
            {
                var cb = CmpOf(i);
                if (cb == null) continue;
                cb.Items.Clear();
                foreach (var lv in new[] { CompressorLevel.Off, CompressorLevel.Light,
                                           CompressorLevel.Medium, CompressorLevel.Strong })
                {
                    cb.Items.Add(new ComboBoxItem
                    {
                        Content = lv switch
                        {
                            CompressorLevel.Light => "轻压",
                            CompressorLevel.Medium => "中压",
                            CompressorLevel.Strong => "强压",
                            _ => "关"
                        },
                        Tag = lv
                    });
                }
            }
        }

        /// <summary>某一路的压缩档被改（2026-10-24）：写进模型 + 刷新提示；改完要**重启输出**才生效。</summary>
        private void Cmp_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready || _syncUi) return;
            if (sender is not ComboBox cb || cb.Tag is not int idx) return;
            if (cb.SelectedItem is not ComboBoxItem item || item.Tag is not CompressorLevel lv) return;
            if (idx < 1 || idx > _ch.Count) return;

            _ch[idx - 1].Compress = lv;
            RefreshHints();
            try
            {
                Services.ErrorLog.Info($"[混音台] 通道#{idx} 压缩档 = {_ch[idx - 1].CompressLabel}");
            }
            catch { }
        }

        /// <summary>把三档角色灌进每个下拉框（顺序即枚举值）。</summary>
        private void FillRoles()
        {
            for (int i = 1; i <= ChCount; i++)
            {
                var cb = RoleOf(i);
                if (cb == null) continue;
                cb.Items.Clear();
                foreach (var (role, text) in RoleItems)
                    cb.Items.Add(new ComboBoxItem { Content = text, Tag = role });
            }
        }

        /// <summary>把某路的当前设置回填到界面控件。</summary>
        private void ApplyToUi(int i)
        {
            var c = _ch[i - 1];
            _syncUi = true;
            var role = RoleOf(i);
            if (role != null && role.Items.Count > 0) role.SelectedIndex = (int)c.Role;
            var pwr = PwrOf(i);
            if (pwr != null) pwr.IsChecked = c.Muted;   // 「关」按钮亮 = 这一路被关掉
            VolOf(i).Value = c.Volume;
            MutOf(i).IsChecked = c.Muted;
            var cmp = CmpOf(i);
            if (cmp != null && cmp.Items.Count > 0)
                cmp.SelectedIndex = (int)c.Compress;
            UpdateGainText(i);
            _syncUi = false;
        }

        /// <summary>刷新某路的最终增益文字（滑杆 × 角色档的结果）。</summary>
        private void UpdateGainText(int i)
        {
            var tb = VolTextOf(i);
            if (tb == null || i < 1 || i > _ch.Count) return;
            var c = _ch[i - 1];
            tb.Text = AudioChannel.FormatDb(c.GainDb);
            tb.ToolTip = $"滑杆 {c.Volume}% × 角色档「{c.RoleLabel}」（{AudioChannel.FormatDb(c.RoleDb)}）";
            // 关掉的那一路滑杆变暗，一眼能看出「这路现在不发声」，但值仍保留、随时可恢复
            var sl = VolOf(i);
            if (sl != null) sl.Opacity = c.Muted ? 0.4 : 1.0;
        }

        // ================= 事件 =================

        private void Dev_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready || sender is not ComboBox cb) return;
            int i = cb == Dev1 ? 1 : cb == Dev2 ? 2 : cb == Dev3 ? 3 : 4;
            if (cb.SelectedItem is ComboBoxItem it)
                _ch[i - 1].DeviceName = (it.Tag as string) ?? "";
            RefreshHints();
        }

        private void Vol_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (sender is not Slider sl) return;
            int.TryParse(sl.Tag?.ToString(), out var i);
            if (i < 1 || i > ChCount) return;
            // ⚠ XAML 解析期滑块初始 Value 也会触发本事件，此时 _ch / 右侧文字可能还没就绪 → 判空返回
            if (_ch == null || i > _ch.Count) return;
            if (VolTextOf(i) == null) return;
            if (!_ready)
            {
                UpdateGainText(i);
                return;
            }
            _ch[i - 1].Volume = (int)sl.Value;
            UpdateGainText(i);
            RefreshHints();
        }

        /// <summary>切换角色档：改的是这一路的基准衰减，滑杆百分比保持不动（继续做微调）。</summary>
        private void Role_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not ComboBox cb) return;
            int.TryParse(cb.Tag?.ToString(), out var i);
            if (i < 1 || i > ChCount || _ch == null || i > _ch.Count) return;
            if (cb.SelectedItem is not ComboBoxItem it || it.Tag is not AudioRole role) return;
            _ch[i - 1].Role = role;
            UpdateGainText(i);
            if (_ready) RefreshHints();
        }

        private void Mut_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncUi || !_ready || sender is not CheckBox ck) return;
            int.TryParse(ck.Tag?.ToString(), out var i);
            if (i < 1 || i > ChCount) return;
            bool off = ck.IsChecked == true;
            _syncUi = true;
            _ch[i - 1].Muted = off;
            var pwr = PwrOf(i);
            if (pwr != null) pwr.IsChecked = off;
            UpdateGainText(i);
            _syncUi = false;
            RefreshHints();
        }

        /// <summary>
        /// 滑杆左端的一键关闭（紧急处理：啸叫、串音、突然爆音）。
        /// 按钮亮起 = 这一路已关；与右侧「静音」勾选框是同一个状态，两处始终同步。
        /// </summary>
        private void Pwr_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncUi || sender is not ToggleButton tb) return;
            int.TryParse(tb.Tag?.ToString(), out var i);
            if (i < 1 || i > ChCount || _ch == null || i > _ch.Count) return;
            bool off = tb.IsChecked == true;
            _syncUi = true;
            _ch[i - 1].Muted = off;
            MutOf(i).IsChecked = off;
            UpdateGainText(i);
            _syncUi = false;
            if (_ready) RefreshHints();
        }

        private void RefreshBtn_Click(object sender, RoutedEventArgs e) => _ = LoadDevicesAsync();

        // ================= 输出总线压缩（2026-10-23） =================

        /// <summary>压缩档下拉项（顺序与 CompressorLevel 枚举一致）。</summary>
        private static readonly (CompressorLevel Level, string Text)[] CompItems =
        {
            (CompressorLevel.Off,    "关闭（不处理）"),
            (CompressorLevel.Light,  "轻压 · 只压尖峰"),
            (CompressorLevel.Medium, "中压 · 日常直播"),
            (CompressorLevel.Strong, "强压 · 现场吵闹")
        };

        private void FillCompLevels()
        {
            var cb = CompLevel;
            if (cb == null) return;
            cb.Items.Clear();
            foreach (var (lv, text) in CompItems)
                cb.Items.Add(new ComboBoxItem { Content = text, Tag = lv });
        }

        /// <summary>把模型里的压缩设置回填到界面（不写回模型）。</summary>
        private void ApplyCompToUi()
        {
            var cmp = _svc.Compressor;
            var cb = CompLevel;
            if (cb != null && cb.Items.Count > 0)
                cb.SelectedIndex = Math.Clamp((int)cmp.Level, 0, cb.Items.Count - 1);

            if (CompThreshold != null) CompThreshold.Value = cmp.ThresholdDb;
            if (CompRatio != null) CompRatio.Value = cmp.Ratio;
            if (CompMakeup != null) CompMakeup.Value = cmp.MakeupDb;

            bool on = cmp.Enabled;
            if (CompThreshold != null) CompThreshold.IsEnabled = on;
            if (CompRatio != null) CompRatio.IsEnabled = on;
            if (CompMakeup != null) CompMakeup.IsEnabled = on;

            if (CompDbText != null) CompDbText.Text = AudioChannel.FormatDb(CompMakeupDbApprox(cmp));
            UpdateCompHint();
        }

        /// <summary>界面右侧那个 dB 只是"补了多少增益"的参考值，不假装是实测电平。</summary>
        private static double CompMakeupDbApprox(AudioCompressor c) => c.Enabled ? c.MakeupDb : 0.0;

        private void UpdateCompHint()
        {
            if (CompHint == null) return;
            var cmp = _svc.Compressor;
            CompHint.Text = !cmp.Enabled
                ? "压缩关闭：混音结果原样进入推流与录制（与旧版本一致）。现场一开嗓就破音时再选「轻压」。"
                : $"压缩 {cmp.Summary}，末尾固定串一级 -1 dB 限幅兜底（实测能把削顶的 0 dB 收在 -1 dB）。"
                  + "　⚠ 这是 ffmpeg 的启动参数：输出进行中改了不会立刻生效，点上方的「重启输出」应用。";
        }

        private void CompLevel_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CompLevel.SelectedItem is not ComboBoxItem it || it.Tag is not CompressorLevel lv) return;
            var cmp = _svc.Compressor;
            if (cmp.Level == lv && _ready) { UpdateCompHint(); return; }
            cmp.Level = lv;
            if (lv != CompressorLevel.Off) cmp.ApplyPresetValues();   // 换档 = 灌出厂参数，之后还能手调

            _ready = false;
            ApplyCompToUi();        // 把新档位的三个参数刷到滑杆上
            _ready = true;
            if (lv != CompressorLevel.Off) UpdateCompHint();
        }

        private void Comp_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // XAML 解析期也会触发（此时 CompHint 还没建好）→ 判空返回，别在这里写模型
            if (!_ready || CompHint == null) return;
            var cmp = _svc.Compressor;
            if (CompThreshold != null) cmp.ThresholdDb = CompThreshold.Value;
            if (CompRatio != null) cmp.Ratio = CompRatio.Value;
            if (CompMakeup != null) cmp.MakeupDb = CompMakeup.Value;
            if (CompDbText != null) CompDbText.Text = AudioChannel.FormatDb(CompMakeupDbApprox(cmp));
            UpdateCompHint();
        }

        private void RestartBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!_svc.IsRunning)
            {
                MessageBox.Show("当前没有正在进行的输出。参数已保存，下次点「开始推流 / 录制」即会生效。",
                    "Cutlance 音频混音台", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (MessageBox.Show("以当前混音设置重启推流与录制？\n（会先停止再重新开始，推流地址不变，录制会新开一个文件）",
                    "Cutlance 音频混音台", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;
            RestartRequested?.Invoke(this, EventArgs.Empty);
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

        /// <summary>
        /// 载入工程（或外部改动模型）后，把每路音频设置同步回界面，不触发写回。
        /// 由主窗口在 ApplyProject 末尾调用。
        /// </summary>
        public void SyncFromModel()
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(SyncFromModel); return; }

            _ready = false;
            for (int i = 1; i <= ChCount; i++)
            {
                ApplyToUi(i);
                var combo = ComboOf(i);
                var cur = _ch[i - 1].DeviceName;
                int idx = 0;
                for (int k = 0; k < combo.Items.Count; k++)
                    if ((string)((ComboBoxItem)combo.Items[k]).Tag == cur) { idx = k; break; }
                combo.SelectedIndex = idx;
            }
            ApplyCompToUi();          // 切布局会改压缩档，窗口开着时要看到新值（2026-10-23）
            _ready = true;
            RefreshHints();
        }

        private void RefreshHints()
        {
            int used = 0, main = 0, sec = 0, bg = 0;
            foreach (var c in _ch)
            {
                if (!c.InUse) continue;
                used++;
                if (c.Role == AudioRole.Main) main++;
                else if (c.Role == AudioRole.Secondary) sec++;
                else bg++;
            }
            // 折叠标题上的「已启用 N 路」（2026-10-23）：八路折起来时也要一眼看出有没有在用，
            // 否则老板会以为"麦克风没了"。
            ChSummary.Text = used == 0 ? "· 未启用" : $"· 已启用 {used} 路";

            if (used == 0)
            {
                TracksHint.Text = "当前没有启用任何音频采集"
                    + (_host != null && _host.Layers.Any(l => l.IsAudioSource && l.Source != null)
                        ? "（输出里的声音来自上面的「画面声音」）"
                        : "（输出将是无声的静音视频）") + "。";
                return;
            }
            var parts = new List<string>();
            if (main > 0) parts.Add($"主音 {main} 路");
            if (sec > 0) parts.Add($"次音 {sec} 路");
            if (bg > 0) parts.Add($"背景 {bg} 路");
            TracksHint.Text = $"当前启用 {used} 路（{string.Join("、", parts)}），混音成一路进入推流与录制。"
                + (main == 0 ? "　⚠ 还没有「主音」——人声容易被音乐压掉。" : "");
        }

        // ══════════════════ 画面声音：输出框里正在播的那几路 ══════════════════

        /// <summary>
        /// 折叠 / 展开「采集通道 1–8」这一块（2026-10-23）。
        /// 默认折叠 —— 打开混音台第一眼要看到的是「画面声音」那几路（老板实际最常调的），
        /// 八路麦克风设定一次就很少再动。
        /// </summary>
        private void ChToggle_Click(object sender, RoutedEventArgs e)
        {
            bool show = ChList.Visibility != Visibility.Visible;
            ChList.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            ChToggleArrow.Text = show ? "▾" : "▸";
        }

        /// <summary>
        /// 主窗口改了输出框（推上/下线、勾音源、拖图层音量、切布局）之后调用。
        /// 开着的混音台跟着重排「画面声音」，关着就什么也不做（零成本）。
        ///
        /// 用 Dispatcher.BeginInvoke 而不是直接调：调用方多半在主窗口的点击/布局流程里，
        /// 异步落地最安全（也不打断老板的操作手感）。
        /// </summary>
        public static void NotifyProgramChanged()
        {
            var w = _current;
            if (w == null) return;
            try { w.Dispatcher.BeginInvoke(new Action(w.RefreshProgramSounds)); } catch { }
        }

        /// <summary>
        /// 算出「当前该有哪些行」：输出框里可见的图层，按输入源去重（保持首次出现顺序）。
        ///
        /// 为什么按**输入源**归组而不是按图层：
        /// 同一个输入在合成器里只有一条解码通路、一份声音（预览与节目共用），
        /// 所以同一文件被两格引用时物理上就是一路声音 —— 列两行会让人以为能分开调，
        /// 实际怎么调都是同一个音量在打架。这里合并成一行，改动写给所有引用它的格。
        /// </summary>
        private List<(InputSource Src, List<Layer> Owners)> GroupProgramSources()
        {
            var result = new List<(InputSource, List<Layer>)>();
            var layers = _host?.Layers?.Where(l => l.Source != null && l.Visible).ToList();
            if (layers == null || layers.Count == 0) return result;

            var map = new Dictionary<InputSource, List<Layer>>(ReferenceEqualityComparer.Instance);
            foreach (var l in layers)
            {
                if (!map.TryGetValue(l.Source!, out var g))
                {
                    g = new List<Layer>();
                    map[l.Source!] = g;
                    result.Add((l.Source!, g));
                }
                g.Add(l);
            }
            return result;
        }

        /// <summary>
        /// 主窗口催刷新时的入口：**源集合没变就只刷数值，变了才重建控件**。
        /// 不这样的话，拖混音台的滑杆 → 推路由 → 主窗口回催 → 整段重建 →
        /// 正在拖的那个 Slider 被换掉 → 鼠标一松就断，手感直接废掉。
        /// </summary>
        private void RefreshProgramSounds()
        {
            if (ProgList == null) return;
            var groups = GroupProgramSources();
            bool same = groups.Count == _progKey.Count;
            if (same)
                for (int i = 0; i < groups.Count; i++)
                    if (!ReferenceEquals(groups[i].Src, _progKey[i])) { same = false; break; }

            if (same && _progSyncers.Count == groups.Count)
            {
                // ⚠ 外层**不能**抬 ProgBusy：Sync 内部第一句就是"忙就整块跳过"，
                //   抬了等于所有行一个都不刷（2026-10-23 实测踩到：改右栏滑杆，混音台纹丝不动）。
                //   每行 Sync 自己会抬/放守卫，设控件值触发的事件也会被它自己挡回去。
                foreach (var s in _progSyncers) s();
                UpdateProgramCounters(groups);
                return;
            }
            RebuildProgramSounds();
        }

        private void UpdateProgramCounters(List<(InputSource Src, List<Layer> Owners)> groups)
        {
            if (groups.Count == 0) { ProgCount.Text = ""; return; }
            int onAir = groups.Count(g => g.Owners.Any(o => o.IsAudioSource));
            ProgCount.Text = $"{groups.Count} 路 · {onAir} 路出声";
        }

        /// <summary>整段重建（源集合变化时才走这里）。</summary>
        private void RebuildProgramSounds()
        {
            if (ProgList == null) return;
            _progGuard++;
            try
            {
                ProgList.Children.Clear();
                _progKey.Clear();
                _progSyncers.Clear();
                _progUi.Clear();

                var groups = GroupProgramSources();
                if (groups.Count == 0)
                {
                    ProgCount.Text = "";
                    ProgHint.Text = "输出框里还没有画面。把左侧输入推到输出后，这里会按「一路输入一行」列出来，"
                                  + "可以像上面八路一样定主次、调音量、单独静音。";
                    return;
                }

                ProgCount.Text = $"{groups.Count} 路 · {groups.Count(g => g.Owners.Any(o => o.IsAudioSource))} 路出声";

                bool noHardware = _ch.TrueForAll(c => !c.InUse);
                ProgHint.Text = groups.Count > 1
                    ? "多路一起播时按主次调：要突出哪一路设「主音」，铺底的设「背景」，再拉滑杆微调。"
                      + "调好的比例会原样混进推流与录制。"
                      + (noHardware ? "　（上面八路采集都没启用，输出里只有这些画面声音。）" : "")
                    : "要突出哪一路设「主音」，铺底的设「背景」。这一行的音量和右侧图层列表的滑杆是同一份设置，改哪边都一样。";

                int idx = 0;
                foreach (var (src, owners) in groups)
                {
                    _progKey.Add(src);
                    _progSyncers.Add(BuildProgramRow(ProgList, ++idx, src, owners));
                }
            }
            catch (Exception ex)
            {
                try { Services.ErrorLog.Info("[混音台] 画面声音列表刷新失败：" + ex.Message); } catch { }
            }
            finally { _progGuard--; }
        }

        /// <summary>造一行并挂进 <paramref name="host"/>，返回「从模型刷到界面」的委托。</summary>
        private Action BuildProgramRow(Panel host, int idx, InputSource src, List<Layer> owners)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            foreach (var w in new[] { 40.0, double.NaN, 110.0, 150.0, 62.0, double.NaN })
                grid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = double.IsNaN(w) ? new GridLength(1, GridUnitType.Star) : new GridLength(w)
                });

            // ① 序号（与上面八路同款方块）
            var numBox = new Border();
            numBox.SetResourceReference(Border.BackgroundProperty, "Brush.Bg.Sunken");
            var num = new TextBlock
            {
                Text = idx.ToString(),
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            num.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Pvw");
            numBox.Child = num;
            Grid.SetColumn(numBox, 0);
            grid.Children.Add(numBox);

            // ② 源名称（同一输入被几格引用时标 ×N，说明它们共用这一份声音）
            string label = string.IsNullOrWhiteSpace(src.Name) ? "（未命名）" : src.Name;
            if (owners.Count > 1) label += $"  ×{owners.Count}";
            var name = new TextBlock
            {
                Text = label,
                Margin = new Thickness(8, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = owners.Count > 1
                    ? $"{src.Name}\n（被 {owners.Count} 格引用 —— 同一个输入只有一份声音，调这一行几格一起变）"
                    : src.Name
            };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Primary");
            Grid.SetColumn(name, 1);
            grid.Children.Add(name);

            // ③ 角色档
            var roleBox = new ComboBox { Margin = new Thickness(6, 0, 4, 0) };
            roleBox.SetResourceReference(StyleProperty, "RoleBox");
            foreach (var (r, t) in RoleItems)
                roleBox.Items.Add(new ComboBoxItem { Content = t, Tag = (int)r });
            roleBox.SelectedIndex = (int)owners[0].AudioRole;
            Grid.SetColumn(roleBox, 2);
            grid.Children.Add(roleBox);

            // ④ 音量（上限 150，与图层滑杆一致；更大增益走八路角色档）
            var slider = new Slider
            {
                Minimum = 0,
                Maximum = 150,
                Value = owners.Max(o => o.Volume),
                Margin = new Thickness(4, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "这一路在混合里的音量（100% = 素材原音量）。角色档先定基准，这里做相对微调。"
            };
            Grid.SetColumn(slider, 3);
            grid.Children.Add(slider);

            // ⑤ 最终 dB
            var db = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            db.SetResourceReference(StyleProperty, "Lbl");
            Grid.SetColumn(db, 4);
            grid.Children.Add(db);

            // ⑥ 静音
            var mut = new CheckBox
            {
                Content = "静音",
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "只让这一路不出声（画面照常）。要连画面一起下，就在输出框里把它关掉。"
            };
            mut.SetResourceReference(ForegroundProperty, "Brush.Text.Secondary");
            Grid.SetColumn(mut, 5);
            grid.Children.Add(mut);

            // —— 显示与真值同步（同一份设置，双向都靠这个刷）——
            void Sync()
            {
                int vol = owners.Max(o => o.Volume);
                if (!ProgBusy)
                {
                    _progGuard++;
                    try
                    {
                        if (Math.Abs(slider.Value - vol) > 0.01) slider.Value = vol;
                        bool silent = !owners.Any(o => o.IsAudioSource);
                        if (mut.IsChecked != silent) mut.IsChecked = silent;
                        roleBox.SelectedIndex = (int)owners[0].AudioRole;
                    }
                    finally { _progGuard--; }
                }
                db.Text = FormatDb(vol, owners[0].AudioRole);
                db.Opacity = mut.IsChecked == true ? 0.4 : 1.0;
            }

            void SetAudible(bool on)
            {
                foreach (var o in owners) o.IsAudioSource = on;
                ApplyProgramRouting();
                Sync();
            }

            slider.ValueChanged += (_, _) =>
            {
                if (ProgBusy) return;
                int v = (int)Math.Round(slider.Value);
                foreach (var o in owners) o.Volume = v;   // 同一源一份声音 → 几格一起改，避免取值打架
                ApplyProgramRouting();
                Sync();
            };

            roleBox.SelectionChanged += (_, _) =>
            {
                if (ProgBusy) return;
                var r = (AudioRole)roleBox.SelectedIndex;
                foreach (var o in owners) o.AudioRole = r;
                ApplyProgramRouting();
                Sync();
            };

            mut.Checked += (_, _) => { if (!ProgBusy) SetAudible(false); };
            mut.Unchecked += (_, _) => { if (!ProgBusy) SetAudible(true); };

            Sync();
            host.Children.Add(grid);
            if (ReferenceEquals(host, ProgList)) _progUi.Add((roleBox, slider, mut));
            return Sync;
        }

        /// <summary>把改动推回主窗口 → 合成器重算哪几路出声、各自多大声。</summary>
        private void ApplyProgramRouting()
        {
            try { _host?.ApplyAudioRoutingFromMixer(); } catch { }
        }

        /// <summary>最终电平：角色档基准 × 滑杆微调（与合成器里算的是同一套）。</summary>
        private static string FormatDb(int volume, AudioRole role)
        {
            double lin = Math.Max(0, volume) / 100.0 * Layer.GainOf(role);
            if (lin <= 0.0001) return "-∞ dB";
            return (20.0 * Math.Log10(lin)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " dB";
        }

        protected override void OnClosed(EventArgs e)
        {
            if (ReferenceEquals(_current, this)) _current = null;
            base.OnClosed(e);
        }
    }
}
