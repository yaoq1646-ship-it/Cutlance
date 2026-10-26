using System;
using System.Globalization;

namespace Cutlance.Models
{
    /// <summary>
    /// 输出总线的压缩档位（2026-10-23 新增）。
    /// 为什么要有：八路采集各自电平天然不一样，混完再加图层音量，很容易一开嗓就削顶（0 dB 平头 = 破音）。
    /// 档位是"给不会调压缩器的人用的"，选完还能用下面的三个数自己微调。
    /// </summary>
    public enum CompressorLevel
    {
        /// <summary>关闭：混完直接输出，行为与老版本完全一致。</summary>
        Off = 0,
        /// <summary>轻压：只压最尖的峰值，几乎听不出变化（访谈、单人说话）。</summary>
        Light = 1,
        /// <summary>中压：日常直播的稳妥档（人声为主的节目）。</summary>
        Medium = 2,
        /// <summary>强压：现场吵闹 / 多人抢话，宁可扁一点也别破音。</summary>
        Strong = 3
    }

    /// <summary>
    /// 输出总线的压缩器 + 限幅器（做在 ffmpeg 音频滤镜链的最后一环：amix 之后 → 编码之前）。
    ///
    /// ── 为什么放在总线上而不是每一路 ──
    /// 削顶是"混完之后超过 0 dB"造成的，逐路压完再混一样会顶。所以只在总线上串一级，
    /// 每路的角色档（主音/次音/背景）继续管"谁大声"，压缩器只管"顶不顶"。
    ///
    /// ── 参数口径（ffmpeg 8.1.1 实测）──
    /// · acompressor 的 threshold / makeup 是**线性倍数**，不是 dB —— 本类内部存 dB，拼串时换算。
    /// · 限幅器固定 -1 dB（linear 0.891），留 1 dB 余量，防止 AAC 编码器自己的峰值过冲再削一次。
    /// · level=disabled：alimiter 默认会把输出自动拉满，那样"小声的段落会被抬起来"，直播里很危险。
    /// 实测：不做处理时音量放大 40 倍 max_volume = 0.0 dB（削顶）；
    ///       挂上本链后 max_volume = **-1.0 dB**，mean 从 -0.4 → -7.9 dB。
    /// </summary>
    public sealed class AudioCompressor
    {
        /// <summary>限幅上限（线性）：0.891 ≈ -1 dB。改这里等于改兜底安全线，别随手调高。</summary>
        public const double LimiterLinear = 0.891;

        private CompressorLevel _level = CompressorLevel.Off;
        private double _thresholdDb = -18;
        private double _ratio = 3;
        private double _makeupDb = 2;

        /// <summary>压缩档位（Off = 整条链不参与，输出与老版本一致）。</summary>
        public CompressorLevel Level
        {
            get => _level;
            set { _level = value; }
        }

        /// <summary>阈值（dB，-40…-3）：超过这条线才开始压。</summary>
        public double ThresholdDb
        {
            get => _thresholdDb;
            set => _thresholdDb = Math.Clamp(value, -40, -3);
        }

        /// <summary>压缩比（1…20）：3 ≈ 超出 3 dB 只放 1 dB 出去。</summary>
        public double Ratio
        {
            get => _ratio;
            set => _ratio = Math.Clamp(value, 1, 20);
        }

        /// <summary>增益补偿（dB，0…12）：压完整体变小了，这里补回来一点。</summary>
        public double MakeupDb
        {
            get => _makeupDb;
            set => _makeupDb = Math.Clamp(value, 0, 12);
        }

        /// <summary>是否真的挂到滤镜链上。</summary>
        public bool Enabled => _level != CompressorLevel.Off;

        /// <summary>档位中文名（界面与状态栏用）。</summary>
        public static string LevelLabel(CompressorLevel l) => l switch
        {
            CompressorLevel.Light => "轻压",
            CompressorLevel.Medium => "中压",
            CompressorLevel.Strong => "强压",
            _ => "关闭"
        };
        public string LevelText => LevelLabel(_level);

        /// <summary>档位 → 三个参量的出厂值。改档只动这三个数，不影响其它设置。</summary>
        public static (double ThresholdDb, double Ratio, double MakeupDb) PresetOf(CompressorLevel l) => l switch
        {
            CompressorLevel.Light => (-24.0, 2.0, 0.0),
            CompressorLevel.Medium => (-18.0, 3.0, 2.0),
            CompressorLevel.Strong => (-14.0, 6.0, 4.0),
            _ => (-18.0, 3.0, 2.0)          // Off 用不到，给个合理值方便切回来
        };

        /// <summary>把档位对应的出厂参数灌进来（换档时调用；载入存盘值时不要调，否则会覆盖手调的数值）。</summary>
        public void ApplyPresetValues()
        {
            var (t, r, m) = PresetOf(_level);
            ThresholdDb = t;
            Ratio = r;
            MakeupDb = m;
        }

        /// <summary>
        /// 拼成 ffmpeg 滤镜串；关闭时返回空串（调用方据此决定"不插这一级"）。
        /// 只在这一处拼串 —— 命令行与自检日志用的是同一份，避免两处写歪。
        /// </summary>
        public string FilterChain
        {
            get
            {
                if (!Enabled) return "";
                string th = Num(Math.Pow(10.0, _thresholdDb / 20.0));
                string mk = Num(Math.Pow(10.0, _makeupDb / 20.0));
                return $"acompressor=threshold={th}:ratio={Num(_ratio)}:attack=20:release=250:makeup={mk}"
                     + $",alimiter=limit={Num(LimiterLinear)}:level=disabled";
            }
        }

        /// <summary>给状态栏/手册看的一句话。</summary>
        public string Summary => Enabled
            ? $"{LevelText}（阈值 {_thresholdDb:0} dB / {_ratio:0.#}:1 / +{_makeupDb:0.#} dB）"
            : "关闭";

        /// <summary>复制一份（工程保存 / 布局快照用，避免后续改到同一对象）。</summary>
        public AudioCompressor Clone() => new()
        {
            _level = _level,
            _thresholdDb = _thresholdDb,
            _ratio = _ratio,
            _makeupDb = _makeupDb
        };

        /// <summary>数值写进 ffmpeg 必须用点号小数（中文系统区域设置会输出逗号，ffmpeg 直接报错）。</summary>
        private static string Num(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
