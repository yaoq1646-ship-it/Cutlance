using System;
using System.ComponentModel;

namespace Cutlance.Models
{
    /// <summary>
    /// 音频通道的「角色档」：决定这一路在混音里是主角、配角还是垫底。
    /// 采集卡同时送进多路信号时，各路电平天然不一样（话筒近讲 vs 现场环境 vs 播放器伴奏），
    /// 光靠滑杆一路一路比着听，很难快速定出主次 —— 所以先选角色定基准衰减，再用滑杆微调。
    /// </summary>
    public enum AudioRole
    {
        /// <summary>主音：说话/主唱，不衰减（0 dB）。</summary>
        Main = 0,
        /// <summary>次音：陪衬、提问、副麦，压 8 dB 让位给主音。</summary>
        Secondary = 1,
        /// <summary>背景：音乐、环境声，压 14 dB 垫在底下。</summary>
        Background = 2
    }

    /// <summary>
    /// 音频混音台的一路输入（最多四路）。
    /// 每路选一个系统采集设备（dshow 音频设备），带独立音量与静音；
    /// 四路在 FFmpeg 里用 volume 滤镜各自调音量、再 amix 混成一路，
    /// 统一进入「推流 + 录制」。
    /// 命名用 AudioChannel 而非 AudioTrack：LibVLC 自带 LibVLCSharp.Shared.AudioTrack，避免重名歧义。
    /// </summary>
    public sealed class AudioChannel : INotifyPropertyChanged
    {
        /// <summary>通道序号（1 起），仅用于界面显示。</summary>
        public int Index { get; set; }

        private string _deviceName = "";

        /// <summary>dshow 采集设备名；空字符串表示该路未启用。</summary>
        public string DeviceName
        {
            get => _deviceName;
            set { _deviceName = value ?? ""; Raise(nameof(DeviceName)); Raise(nameof(InUse)); Raise(nameof(Summary)); }
        }

        private int _volume = 100;

        /// <summary>该路音量百分比（0–200，100 = 角色档基准值），作为角色档之上的微调。</summary>
        public int Volume
        {
            get => _volume;
            set
            {
                _volume = Math.Clamp(value, 0, 200);
                Raise(nameof(Volume)); Raise(nameof(Gain)); Raise(nameof(GainDb)); Raise(nameof(Summary));
            }
        }

        private bool _muted;

        /// <summary>静音（保留设备选择，便于临时切换）。</summary>
        public bool Muted
        {
            get => _muted;
            set
            {
                _muted = value;
                Raise(nameof(Muted)); Raise(nameof(InUse)); Raise(nameof(Gain)); Raise(nameof(GainDb)); Raise(nameof(Summary));
            }
        }

        private AudioRole _role = AudioRole.Main;
        /// <summary>角色档：主音 / 次音 / 背景。只改基准衰减，不动滑杆值。</summary>
        public AudioRole Role
        {
            get => _role;
            set
            {
                if (_role == value) return;
                _role = value;
                Raise(nameof(Role)); Raise(nameof(RoleLabel)); Raise(nameof(RoleDb));
                Raise(nameof(Gain)); Raise(nameof(GainDb)); Raise(nameof(Summary));
            }
        }

        private CompressorLevel _compress = CompressorLevel.Off;

        /// <summary>
        /// 这一路自己的压缩档（2026-10-24 老板要「逐路压缩」）。
        /// 与「输出总线压缩」分工：这里管**单路忽大忽小**（一支麦突然凑近），
        /// 总线管「混完是不是顶到 0dB」。逐路先压过，总线就不用那么用力。
        /// 只存档位不存三个参数：八路各带三根滑杆会把混音台撑爆，档位够用。
        /// </summary>
        public CompressorLevel Compress
        {
            get => _compress;
            set
            {
                if (_compress == value) return;
                _compress = value;
                Raise(nameof(Compress));
                Raise(nameof(CompressLabel));
                Raise(nameof(Summary));
            }
        }

        /// <summary>压缩档的中文名（界面 / 日志用）。</summary>
        public string CompressLabel => _compress switch
        {
            CompressorLevel.Light => "轻",
            CompressorLevel.Medium => "中",
            CompressorLevel.Strong => "强",
            _ => "关"
        };

        /// <summary>这一路的压缩滤镜串（空 = 不压）。参数用 AudioCompressor 的同一套预设，两处口径一致。</summary>
        public string CompressFilter
        {
            get
            {
                if (_compress == CompressorLevel.Off) return "";
                var c = new AudioCompressor { Level = _compress };
                c.ApplyPresetValues();
                return c.FilterChain;
            }
        }

        /// <summary>角色档的基准衰减（dB）：主音 0、次音 -8、背景 -14。</summary>
        public double RoleDb => Role switch
        {
            AudioRole.Secondary => -8.0,
            AudioRole.Background => -14.0,
            _ => 0.0
        };

        /// <summary>角色档中文名，供界面与提示文字使用。</summary>
        public string RoleLabel => Role switch
        {
            AudioRole.Secondary => "次音",
            AudioRole.Background => "背景",
            _ => "主音"
        };

        /// <summary>
        /// 最终送给 FFmpeg volume 滤镜的线性增益 = 滑杆百分比 × 角色档衰减。
        /// 静音时为 0（仍按时长参与混音，避免 amix 提前收尾）。
        /// </summary>
        public double Gain
        {
            get
            {
                if (_muted) return 0.0;
                double slider = Math.Clamp(_volume, 0, 200) / 100.0;
                return slider * Math.Pow(10.0, RoleDb / 20.0);
            }
        }

        /// <summary>最终增益的 dB 表示（静音时按 -inf 记作 -96），供界面显示 0.0 dB / -8.0 dB。</summary>
        public double GainDb => Gain <= 0.0001 ? -96.0 : 20.0 * Math.Log10(Gain);

        /// <summary>该路是否真的会出声（已选设备、未静音、音量 &gt; 0）。</summary>
        public bool InUse => !string.IsNullOrWhiteSpace(_deviceName) && !_muted && _volume > 0;

        /// <summary>供界面显示的摘要文字。</summary>
        public string Summary => string.IsNullOrWhiteSpace(_deviceName)
            ? "未使用"
            : $"{_deviceName} · {RoleLabel} · {(Muted ? "静音" : FormatDb(GainDb))}";

        /// <summary>把 dB 值格式化成界面文字：静音显示「静音」，否则「-8.0 dB」。</summary>
        public static string FormatDb(double db)
            => db <= -90 ? "静音" : (Math.Abs(db) < 0.05 ? "0.0 dB" : db.ToString("0.0") + " dB");

        public event PropertyChangedEventHandler? PropertyChanged;
        public void Raise(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }
}
