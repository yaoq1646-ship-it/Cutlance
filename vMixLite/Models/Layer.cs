using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using LibVLCSharp.Shared;
using Cutlance.Models;

namespace Cutlance.Models
{
    /// <summary>
    /// 输出图层（模块5：完整多图层画中画/分屏）。
    /// 每个图层绑定一个输入源，拥有独立 MediaPlayer 与屏幕上可拖拽/缩放的容器。
    /// 输出画面 = 所有图层按 Z 序合成；仅 IsAudioSource=true 的图层出声（其余静音）。
    /// </summary>
    public sealed class Layer : INotifyPropertyChanged, IFrameItem
    {
        private static int _seq;
        public int Id { get; } = ++_seq;

        public InputSource? Source { get; set; }
        public double X { get; set; } = 0;        // 归一化 0–1（左上角相对画布宽）
        public double Y { get; set; } = 0;        // 归一化 0–1（左上角相对画布高）

        /// <summary>
        /// 宽度缩放（1 = 原始大小；图层基准尺寸是画布的一半，故 2 = 正好铺满）。
        /// 2026-10-18 精调面板：宽高改成可以分别设，于是拆成 ScaleX / ScaleY，
        /// 而 <see cref="Scale"/> 保留成「读=宽、写=宽高一起」的别名，老代码不用动。
        /// </summary>
        public double ScaleX { get; set; } = 1;
        public double ScaleY { get; set; } = 1;

        public double Scale
        {
            get => ScaleX;
            set { ScaleX = value; ScaleY = value; }
        }

        // ==================== 几何换算（2026-10-18 起，同 PreviewSlot）====================
        // 「窗口最多铺满画布，>100% 的放大交给 VLC」。
        // ⚠ 算元素矩形/命中区一律用 ViewScale*，别直接用 ScaleX。
        /// <summary>元素实际宽度（图层口径：2 = 铺满画布）。</summary>
        public double ViewScaleX => Math.Min(ScaleX, 2.0);

        /// <summary>元素实际高度（2 = 铺满画布）。</summary>
        public double ViewScaleY => Math.Min(ScaleY, 2.0);

        /// <summary>交给 VLC 的画面放大倍数（1 = 不放大）。</summary>
        public double Zoom => Math.Max(1.0, Math.Max(ScaleX, ScaleY) / 2.0);

        public int Z { get; set; } = 1;           // 层级，越大越靠上

        private int _opacity = 100;

        /// <summary>
        /// 不透明度 0–100（100 = 完全不透明）。2026-10-24 老板要求。
        ///
        /// 落点在**合成器**：C 方案下图层只是透明 Grid，WPF 的 Opacity 对成品帧毫无影响，
        /// 所以这个值最终交给 <c>Compositor.Layer.Alpha</c>，由 Blit 按
        /// 直通 alpha（out = s×a + d×(1−a)）混进去 —— 推流 / 录制 / NDI / 全屏一起生效、
        /// 切到半透明也能看到底下的画面。
        /// </summary>
        public int Opacity
        {
            get => _opacity;
            set
            {
                int v = Math.Clamp(value, 0, 100);
                if (_opacity != v)
                {
                    _opacity = v;
                    Raise(nameof(Opacity));
                    Raise(nameof(OpacityFrac));
                    Raise(nameof(OpacityText));
                }
            }
        }

        /// <summary>滑杆文本（0 直接写"全透明"，比"0%"更说明问题）。</summary>
        public string OpacityText => _opacity <= 0 ? "全透明" : _opacity + "%";

        /// <summary>交给合成器的 alpha（0–255）。</summary>
        public int OpacityAlpha => (int)Math.Round(_opacity * 255 / 100.0);

        // —— IFrameItem：精调面板用归一化口径（1 = 不透明） ——
        public double OpacityFrac
        {
            get => _opacity / 100.0;
            set => Opacity = (int)Math.Round(Math.Clamp(value, 0, 1) * 100);
        }

        public bool SupportsOpacity => true;

        /// <summary>
        /// 所属「画面组」（2026-10-22 新增）。
        ///
        /// 一次「全部上输出」得到的 N 个图层属于同一组，代表「这一套布局」。
        /// 整组切换时旧组整组下线、新组整组上线 —— 这是修掉
        /// 「全部上输出后再切画面，输出框画面错乱」的关键：以前只隐藏满框层，
        /// 分屏小窗永远不下线，新画面不满框时旧画面就从缝里露出来。
        ///
        /// 0 = 老工程 / 无组归属，整组切时按「随便一组」处理（同样会被下线）。
        /// </summary>
        public int GroupId { get; set; }

        private bool _visible = true;

        /// <summary>
        /// 是否显示。2026-10-18 改为带变更通知：切换新画面时会把旧的满框层隐藏，
        /// 右侧图层列表里的勾选框必须跟着变，否则界面显示「还开着」、画面却已经没了。
        /// </summary>
        public bool Visible
        {
            get => _visible;
            set { if (_visible != value) { _visible = value; Raise(nameof(Visible)); } }
        }
        public bool IsAudioSource { get; set; }   // 是否该层提供输出音频

        private int _volume = 100;

        /// <summary>
        /// 这一格的声音大小（0–150，100 = 原音量）。2026-10-22 老板要求「每格音量」。
        ///
        /// 与 <see cref="IsAudioSource"/> 的分工：开关决定"出不出声"，音量决定"多大声"。
        /// 为什么上限放在 150 而不是 200：图层音量是给"这一格比别的大一点/小一点"用的微调，
        /// 拉到 300% 那种增益交给混音台的角色档（那里有压缩器与限幅），这里再放开只会削波。
        /// </summary>
        public int Volume
        {
            get => _volume;
            set
            {
                int v = Math.Clamp(value, 0, 150);
                if (v == _volume) return;
                _volume = v;
                Raise(nameof(Volume));
                Raise(nameof(VolumeText));
                Raise(nameof(VolumePercent));
                Raise(nameof(VolumeDetail));
                Raise(nameof(RoleVolumeText));
            }
        }

        /// <summary>滑杆右边那个百分比数字（0 直接写"静音"，比"0%"看得懂）。</summary>
        public string VolumeText => _volume <= 0 ? "静音" : _volume + "%";

        /// <summary>
        /// 右栏图层行里那一行摘要（2026-10-23 老板「把单独的声音大小控制合并进混音台」）：
        /// 右栏不再放滑杆，只显示「主音 · 100%」这样的结果，要调就点旁边的「调音」进混音台。
        /// 两处都能改同一个值太容易让人以为"改了没生效"，统一成混音台一个入口。
        /// </summary>
        public string RoleVolumeText => $"{RoleLabel} · {VolumeText}";

        /// <summary>给合成器用的 0…1.5 系数。</summary>
        public float VolumePercent => _volume / 100f;

        /// <summary>
        /// 滑杆的悬停提示（2026-10-23）。**为什么必须有它**：
        /// 加了角色档之后，右栏滑杆上的"130%"是「相对角色档」的值，不再是绝对电平 ——
        /// 次音档下 130% 实际只有 −5.7 dB，比主音 100% 还小。
        /// 不把这个换算写出来，老板会觉得"我拖到 130% 怎么反而更小声，是不是坏了"。
        /// </summary>
        public string VolumeDetail
        {
            get
            {
                double lin = VolumePercent * RoleGain;
                string db = lin <= 0.0001
                    ? "-∞ dB"
                    : (20.0 * Math.Log10(lin)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " dB";
                return $"音量 {_volume}%（相对角色档）\n"
                     + $"角色档：{RoleLabel}（{(RoleDb > 0 ? "+" : "")}{RoleDb:0} dB）\n"
                     + $"最终电平：{db}";
            }
        }

        private AudioRole _audioRole = AudioRole.Main;

        /// <summary>
        /// 这一格在混音里演什么角色（2026-10-23 老板要求「按主次调」）：
        /// 主音 0dB / 次音 -8dB / 背景 -14dB。
        ///
        /// 与混音台硬件通道 (AudioChannel) 用**同一套枚举、同一套衰减值** ——
        /// 两个界面里的"主音"必须是一个意思，否则按主次调出来的比例跟耳朵听到的对不上。
        /// 老工程 / 老布局没有这个字段 → 缺省 Main（不衰减），升级后音量与以前完全一致。
        /// </summary>
        public AudioRole AudioRole
        {
            get => _audioRole;
            set
            {
                if (_audioRole == value) return;
                _audioRole = value;
                Raise(nameof(AudioRole));
                Raise(nameof(RoleGain));
                Raise(nameof(RoleLabel));
                Raise(nameof(RoleDb));
                Raise(nameof(VolumeDetail));
                Raise(nameof(RoleVolumeText));
            }
        }

        /// <summary>角色档折算成的线性增益（0dB=1、-8dB≈0.398、-14dB≈0.199）。</summary>
        public float RoleGain => GainOf(_audioRole);

        /// <summary>
        /// 角色档 → 线性增益。**混音台与图层共用这一处换算**，
        /// 避免两边各写一份、以后改 dB 值时漏改一边导致「界面写主音、实际却降了 8dB」。
        /// </summary>
        public static float GainOf(AudioRole role) => role switch
        {
            AudioRole.Secondary => 0.39810717f,   // -8 dB
            AudioRole.Background => 0.19952623f,  // -14 dB
            _ => 1f                               // 主音 0 dB
        };

        public string RoleLabel => _audioRole switch
        {
            AudioRole.Secondary => "次音",
            AudioRole.Background => "背景",
            _ => "主音"
        };

        /// <summary>角色档的 dB 衰减（主音 0 / 次音 −8 / 背景 −14）。</summary>
        public double RoleDb => _audioRole switch
        {
            AudioRole.Secondary => -8.0,
            AudioRole.Background => -14.0,
            _ => 0.0
        };

        // —— 精调面板（2026-10-18）——
        public bool AspectLock { get; set; } = true;
        public double BorderThickness { get; set; }
        public double BorderRadius { get; set; }
        public string BorderColor { get; set; } = "#FF33C6F5";

        // —— 阴影（2026-10-25）：跟描边一起画进成品帧 ——
        private double _shadow;
        private int _shadowOpacity = 60;
        /// <summary>阴影向外扩散的粗细（DIP，0 = 无阴影）。四周均匀，分屏贴边也不会一边深一边浅。</summary>
        public double Shadow
        {
            get => _shadow;
            set
            {
                double v = Math.Clamp(value, 0, 60);
                if (Math.Abs(v - _shadow) < 1e-6) return;
                _shadow = v;
                Raise(nameof(Shadow));
                Raise(nameof(ShadowText));
            }
        }

        /// <summary>阴影浓淡 0–100（越大越黑）。只有 <see cref="Shadow"/> &gt; 0 时才看得见。</summary>
        public int ShadowOpacity
        {
            get => _shadowOpacity;
            set
            {
                int v = Math.Clamp(value, 0, 100);
                if (v == _shadowOpacity) return;
                _shadowOpacity = v;
                Raise(nameof(ShadowOpacity));
                Raise(nameof(ShadowText));
            }
        }

        public string ShadowText => _shadow <= 0 ? "无" : $"{_shadow:0}px · {_shadowOpacity}%";
        /// <summary>阴影最深处 alpha（0–255），合成器只用这个。</summary>
        public int ShadowAlpha => (int)Math.Round(_shadowOpacity * 255 / 100.0);

        // —— 填充方式（2026-10-25 异形屏）——
        private int _fitMode;

        /// <summary>
        /// 画面怎么填进这一格：
        ///   0 = 等比内接（留黑边，出厂默认，跟升级前完全一致）
        ///   1 = 铺满裁切（填满格子且不变形，多出来的从源中心裁掉）
        ///   2 = 拉伸铺满（会变形，异形屏做 1:1 像素映射时用）
        /// 只改合成器几何，不重开媒体 —— 直播中切换画面不会闪。
        /// </summary>
        public int FitMode
        {
            get => _fitMode;
            set
            {
                int v = Math.Clamp(value, 0, 2);
                if (v == _fitMode) return;
                _fitMode = v;
                Raise(nameof(FitMode));
                Raise(nameof(FitLabel));
            }
        }

        public string FitLabel => _fitMode switch
        {
            1 => "铺满裁切",
            2 => "拉伸铺满",
            _ => "等比内接"
        };

        /// <summary>
        /// 描边颜色转合成器口径（0xAARRGGBB）。合成器的 FillSpan 按这个布局取通道，
        /// 正好和 "#AARRGGBB" 的十六进制同序，所以直接解析就能用。
        /// </summary>
        public uint BorderColorValue
        {
            get
            {
                var s = (BorderColor ?? "").Trim().TrimStart('#');
                if (s.Length == 8 && uint.TryParse(s, System.Globalization.NumberStyles.HexNumber,
                                                  System.Globalization.CultureInfo.InvariantCulture, out uint v))
                    return v;
                if (s.Length == 6 && uint.TryParse(s, System.Globalization.NumberStyles.HexNumber,
                                                  System.Globalization.CultureInfo.InvariantCulture, out uint v6))
                    return 0xFF000000u | v6;
                return 0xFF33C6F5u;
            }
        }

        public MediaPlayer? Player { get; set; }  // 专属播放器（由 VideoEngine 创建）
        public UIElement? View { get; set; }       // 对应的屏幕容器（Grid：VideoView + 透明命中层，用于拖动/缩放/高亮）

        public string DisplayName => Source?.Name ?? "空图层";
        public string TypeShort => Source?.TypeShort ?? "-";

        public event PropertyChangedEventHandler? PropertyChanged;
        public void Raise(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

        // ==================== IFrameItem ====================
        public string PanelTitle => $"图层 #{Id} · {DisplayName}";
        // 图层宽 = 画布宽 × 0.5 × ScaleX，所以占框比例 = 0.5 × ScaleX
        public double WidthFrac
        {
            get => 0.5 * ScaleX;
            set => ScaleX = value * 2.0;
        }
        public double HeightFrac
        {
            get => 0.5 * ScaleY;
            set => ScaleY = value * 2.0;
        }
        public bool IsPreviewItem => false;
        public double MinFrac => 0.1;
        public double MaxFrac => 4.0;
    }
}
