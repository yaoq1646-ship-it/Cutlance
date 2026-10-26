using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Cutlance.Models;

namespace Cutlance.Services
{
    /// <summary>
    /// 输出布局预设（P3）的采集、匹配与落盘（2026-10-22）。
    ///
    /// 文件：%APPDATA%\Cutlance\program-layouts.json
    /// 为什么放这儿：直播版面是**跨工程复用**的（"主讲+右下PPT"每期都用），
    /// 塞进 .cutlance 就变成每个新工程都要重存一遍。
    ///
    /// ⚠ 任何读写失败都不抛给调用方——导播软件不能因为一个预设文件坏了起不来。
    /// </summary>
    public static class ProgramLayoutStore
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static string Dir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cutlance");

        public static string FilePath => Path.Combine(Dir, "program-layouts.json");

        /// <summary>导出文件的扩展名（单套布局，可与别人交换）。</summary>
        public const string ExportExtension = ".clayout";

        /// <summary>导出文件里 App 字段的合法值（与工程文件同一口径）。</summary>
        public const string AppTag = "Cutlance";

        /// <summary>
        /// 转场名 → 枚举。认不出（旧文件没有这个字段 / 手改坏了）一律当硬切，
        /// 绝不让"读一个不认识的转场名"把整个切换流程卡住。
        /// </summary>
        public static TransitionType ParseTransition(string? name)
        {
            if (!string.IsNullOrWhiteSpace(name) &&
                Enum.TryParse<TransitionType>(name, true, out var t) &&
                Enum.IsDefined(typeof(TransitionType), t))
                return t;
            return TransitionType.Cut;
        }

        /// <summary>转场时长夹到界面滑杆的同一区间，防止手改文件写出 0.0001s 或 300s。</summary>
        public static double ClampSeconds(double s) => s < 0.1 ? 1.0 : Math.Clamp(s, 0.3, 3.0);

        // ==================== 落盘 ====================

        public static List<ProgramLayoutPreset> LoadAll()
        {
            try
            {
                if (!File.Exists(FilePath)) return new List<ProgramLayoutPreset>();
                var json = File.ReadAllText(FilePath, Encoding.UTF8);
                var list = JsonSerializer.Deserialize<List<ProgramLayoutPreset>>(json, Options);
                return (list ?? new List<ProgramLayoutPreset>())
                       .Where(p => p != null && !string.IsNullOrWhiteSpace(p.Name))
                       .ToList();
            }
            catch
            {
                return new List<ProgramLayoutPreset>();   // 文件坏了当作没有，下次保存整个重写
            }
        }

        public static bool SaveAll(List<ProgramLayoutPreset> list, out string err)
        {
            err = "";
            try
            {
                Directory.CreateDirectory(Dir);
                // 先写临时文件再替换：写到一半断电不会把原文件写坏
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(list, Options), new UTF8Encoding(false));
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);
                return true;
            }
            catch (Exception ex)
            {
                err = ex.Message;
                return false;
            }
        }

        public static bool Upsert(ProgramLayoutPreset preset, out string err)
        {
            var all = LoadAll();
            int i = all.FindIndex(p => string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) all[i] = preset; else all.Add(preset);
            return SaveAll(all, out err);
        }

        public static bool Delete(string name, out string err)
        {
            var all = LoadAll();
            int i = all.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (i < 0) { err = "没有这个布局"; return false; }
            all.RemoveAt(i);
            return SaveAll(all, out err);
        }

        // ==================== 导出 / 导入（单套 .clayout 文件） ====================

        /// <summary>
        /// 把一套布局导成独立文件，用来发给别人 / 拷到另一台机器 / 存进素材盘。
        /// 只导这一套，不带其它预设；文件里带 App 与 Version 头，导入时先验。
        /// </summary>
        public static bool ExportFile(ProgramLayoutPreset preset, string path, out string err)
        {
            err = "";
            try
            {
                if (preset == null || preset.Cells == null || preset.Cells.Count == 0)
                {
                    err = "这套布局里没有格子，没东西可导";
                    return false;
                }
                preset.App = AppTag;
                preset.Version = 1;
                preset.Transition = ParseTransition(preset.Transition).ToString();
                preset.TransitionSeconds = ClampSeconds(preset.TransitionSeconds);

                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, JsonSerializer.Serialize(preset, Options), new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                err = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 读一套 .clayout。认不出 App / 版本过新 / 没有格子都当失败并给原因，
        /// 不把半个布局塞进列表（切到半个布局比不切更糟）。
        /// </summary>
        public static ProgramLayoutPreset? ImportFile(string path, out string err)
        {
            err = "";
            try
            {
                if (!File.Exists(path)) { err = "文件不存在"; return null; }
                var json = File.ReadAllText(path, Encoding.UTF8);
                var p = JsonSerializer.Deserialize<ProgramLayoutPreset>(json, Options);
                if (p == null) { err = "文件内容不是一套布局"; return null; }

                if (!string.IsNullOrWhiteSpace(p.App) &&
                    !string.Equals(p.App, AppTag, StringComparison.OrdinalIgnoreCase))
                {
                    err = $"这是「{p.App}」的布局文件，不是 Cutlance 的";
                    return null;
                }
                if (p.Version > 99) { err = $"文件版本 {p.Version} 太新，当前程序读不了"; return null; }

                var cells = p.Cells ?? new List<ProgramPresetCell>();
                p.Cells = cells.Where(c => c != null).ToList();
                if (p.Cells.Count == 0) { err = "这套布局里没有格子"; return null; }

                p.Transition = ParseTransition(p.Transition).ToString();
                p.TransitionSeconds = ClampSeconds(p.TransitionSeconds);
                if (string.IsNullOrWhiteSpace(p.Name)) p.Name = Path.GetFileNameWithoutExtension(path);
                return p;
            }
            catch (Exception ex)
            {
                err = "读不了这个文件：" + ex.Message;
                return null;
            }
        }

        // ==================== 采集 ====================

        /// <summary>
        /// 把输出框当前这一整套画面拍成预设。
        /// 只拍「看得见」的图层：已经下线（Visible=false）的图层不属于当前版面，
        /// 把它们也拍进去会导致下次切回来凭空多出几块黑。
        /// </summary>
        public static ProgramLayoutPreset Capture(string name, IReadOnlyList<Layer> layers, DateTime now,
                                                 TransitionType transition = TransitionType.Cut,
                                                 double seconds = 1.0,
                                                 IReadOnlyList<AudioChannel>? mixer = null,
                                                 AudioCompressor? compressor = null,
                                                 ProgramPresetClock? clock = null)
        {
            var p = new ProgramLayoutPreset
            {
                Name = name,
                SavedAt = now.ToString("yyyy-MM-dd HH:mm:ss"),
                App = AppTag,
                Version = 1,
                Transition = transition.ToString(),
                TransitionSeconds = ClampSeconds(seconds)
            };
            foreach (var l in layers.Where(x => x != null && x.Visible).OrderBy(x => x.Z))
            {
                p.Cells.Add(new ProgramPresetCell
                {
                    SourceNumber = l.Source?.Number ?? 0,
                    SourceName = l.Source?.Name ?? "",
                    SourcePath = l.Source?.Location ?? "",
                    X = l.X,
                    Y = l.Y,
                    ScaleX = l.ScaleX,
                    ScaleY = l.ScaleY,
                    Z = l.Z,
                    AspectLock = l.AspectLock,
                    BorderThickness = l.BorderThickness,
                    BorderRadius = l.BorderRadius,
                    BorderColor = l.BorderColor,
                    Opacity = l.Opacity,          // 透明度随布局（2026-10-24）
                    Shadow = l.Shadow,            // 阴影随布局（2026-10-25）
                    ShadowOpacity = l.ShadowOpacity,
                    FitMode = l.FitMode,          // 填充方式随布局（异形屏）
                    IsAudioSource = l.IsAudioSource,
                    Volume = l.Volume,
                    AudioRole = l.AudioRole.ToString()
                });
            }
            CaptureMixer(p, mixer, compressor);
            p.Clock = clock;      // 倒计时牌样式（2026-10-24）：null = 不动
            return p;
        }

        /// <summary>
        /// 把混音台当前的角色档 / 音量 / 静音，以及输出总线压缩档，快照进这套布局（2026-10-23）。
        /// 只拍**选了设备**的那几路：空通道拍进去没意义，切回来只会多几行噪声。
        /// </summary>
        public static void CaptureMixer(ProgramLayoutPreset p, IReadOnlyList<AudioChannel>? mixer,
                                        AudioCompressor? compressor)
        {
            if (p == null) return;

            if (mixer != null)
            {
                var list = new List<ProgramPresetMixerChannel>();
                foreach (var c in mixer)
                {
                    if (c == null || string.IsNullOrWhiteSpace(c.DeviceName)) continue;
                    list.Add(new ProgramPresetMixerChannel
                    {
                        DeviceName = c.DeviceName,
                        Role = c.Role.ToString().ToLowerInvariant(),
                        Volume = c.Volume,
                        Muted = c.Muted,
                        Compress = c.Compress.ToString()   // 逐路压缩档（2026-10-24）
                    });
                }
                p.Mixer = list;
            }

            if (compressor != null)
            {
                p.Compressor = new ProgramPresetCompressor
                {
                    Level = compressor.Level.ToString().ToLowerInvariant(),
                    ThresholdDb = compressor.ThresholdDb,
                    Ratio = compressor.Ratio,
                    MakeupDb = compressor.MakeupDb
                };
            }
        }

        // ==================== 混音套用（2026-10-23） ====================

        /// <summary>套用结果，供状态栏报数（不抛异常，缺设备只是跳过）。</summary>
        public readonly struct MixerApplyResult
        {
            /// <summary>这套布局里到底有没有混音快照（老布局文件 = false）。</summary>
            public bool LayoutHadMixer { get; init; }
            /// <summary>真套上了几路。</summary>
            public int Applied { get; init; }
            /// <summary>预设里有、本机当前没有这个设备的路数。</summary>
            public int Missing { get; init; }
            /// <summary>压缩档是否被改动（用于提示"要重启输出才生效"）。</summary>
            public bool CompressorChanged { get; init; }
            /// <summary>套用后的压缩档文字（"关闭" / "中压（…）"）。</summary>
            public string CompressorText { get; init; }
        }

        /// <summary>角色档字符串 → 枚举；认不出按主音（0 dB），绝不因为一个坏值把声音弄哑。</summary>
        public static AudioRole ParseRole(string? s)
            => Enum.TryParse<AudioRole>(s, true, out var r) && Enum.IsDefined(typeof(AudioRole), r)
               ? r : AudioRole.Main;

        /// <summary>压缩档字符串 → 枚举；认不出按关闭（宁可没压，也不要莫名其妙被压扁）。</summary>
        public static CompressorLevel ParseCompressorLevel(string? s)
            => Enum.TryParse<CompressorLevel>(s, true, out var l) && Enum.IsDefined(typeof(CompressorLevel), l)
               ? l : CompressorLevel.Off;

        /// <summary>
        /// 把布局里的混音快照套到当前混音台上。
        /// · 按**设备名**认领（大小写不敏感）：认不到的跳过并计数，不动那一路。
        /// · 老布局没有 Mixer / Compressor 段（null）→ 整块不动，返回 LayoutHadMixer=false。
        /// ⚠ 只是改模型值；ffmpeg 的参数是启动时拼的，所以输出进行中要「重启输出」才生效。
        /// </summary>
        public static MixerApplyResult ApplyMixer(ProgramLayoutPreset preset,
                                                  IReadOnlyList<AudioChannel> channels,
                                                  AudioCompressor? compressor)
        {
            if (preset == null || channels == null)
                return new MixerApplyResult { CompressorText = compressor?.Summary ?? "" };

            bool had = preset.Mixer != null || preset.Compressor != null;
            int applied = 0, missing = 0;

            if (preset.Mixer != null)
            {
                foreach (var m in preset.Mixer)
                {
                    if (m == null || string.IsNullOrWhiteSpace(m.DeviceName)) continue;
                    var target = channels.FirstOrDefault(c => c != null && string.Equals(
                        c.DeviceName, m.DeviceName, StringComparison.OrdinalIgnoreCase));
                    if (target == null) { missing++; continue; }   // 这台机器上没这个设备
                    target.Role = ParseRole(m.Role);
                    target.Volume = Math.Clamp(m.Volume, 0, 200);
                    target.Muted = m.Muted;
                    // 逐路压缩档（2026-10-24）：老布局空串 → 关闭，不改变这一路的声音处理
                    target.Compress = ParseCompressorLevel(m.Compress);
                    applied++;
                }
            }

            bool compChanged = false;
            if (preset.Compressor != null && compressor != null)
            {
                var lv = ParseCompressorLevel(preset.Compressor.Level);
                compChanged = lv != compressor.Level
                              || Math.Abs(preset.Compressor.ThresholdDb - compressor.ThresholdDb) > 0.01
                              || Math.Abs(preset.Compressor.Ratio - compressor.Ratio) > 0.01
                              || Math.Abs(preset.Compressor.MakeupDb - compressor.MakeupDb) > 0.01;
                compressor.Level = lv;
                compressor.ThresholdDb = preset.Compressor.ThresholdDb;
                compressor.Ratio = preset.Compressor.Ratio;
                compressor.MakeupDb = preset.Compressor.MakeupDb;
            }

            return new MixerApplyResult
            {
                LayoutHadMixer = had,
                Applied = applied,
                Missing = missing,
                CompressorChanged = compChanged,
                CompressorText = compressor?.Summary ?? ""
            };
        }

        // ==================== 匹配 ====================

        /// <summary>
        /// 按「路径 → 编号+名字 → 名字」三级，把预设里的一格认到当前的某个输入上。
        /// 认不到返回 null（那一格留空，几何保留）。
        /// </summary>
        public static InputSource? MatchSource(ProgramPresetCell cell, IReadOnlyList<InputSource> inputs)
        {
            if (cell == null || inputs == null || inputs.Count == 0) return null;

            // ① 路径全等（采集设备没有路径，会落到第 ② 级）
            if (!string.IsNullOrWhiteSpace(cell.SourcePath))
            {
                var byPath = inputs.FirstOrDefault(s =>
                    string.Equals(s.Location, cell.SourcePath, StringComparison.OrdinalIgnoreCase));
                if (byPath != null) return byPath;
            }

            // ② 编号 + 名字都对上（同一期节目里最可靠）
            if (cell.SourceNumber > 0 && !string.IsNullOrWhiteSpace(cell.SourceName))
            {
                var byBoth = inputs.FirstOrDefault(s =>
                    s.Number == cell.SourceNumber &&
                    string.Equals(s.Name, cell.SourceName, StringComparison.OrdinalIgnoreCase));
                if (byBoth != null) return byBoth;
            }

            // ③ 只按名字（换了工程、编号全乱了，但素材名没变）
            if (!string.IsNullOrWhiteSpace(cell.SourceName))
            {
                var byName = inputs.FirstOrDefault(s =>
                    string.Equals(s.Name, cell.SourceName, StringComparison.OrdinalIgnoreCase));
                if (byName != null) return byName;
            }

            return null;
        }

        /// <summary>把一整份预设认到当前输入列表上，返回与 Cells 一一对应的结果（null = 没认到）。</summary>
        public static List<InputSource?> ResolveAll(ProgramLayoutPreset preset, IReadOnlyList<InputSource> inputs)
        {
            var list = new List<InputSource?>();
            if (preset?.Cells == null) return list;
            foreach (var c in preset.Cells) list.Add(MatchSource(c, inputs));
            return list;
        }

        /// <summary>认不出来的格数（用于状态栏报数）。</summary>
        public static int CountMissing(IEnumerable<InputSource?> resolved)
            => resolved.Count(s => s == null);
    }
}
