using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Cutlance.Models;

namespace Cutlance.Services
{
    /// <summary>
    /// 采集设备与信号格式枚举（模块E：采集卡 / 摄像机信号）。
    ///
    /// 为什么走 ffmpeg 而不是 WMI / DirectShow COM：
    ///   1) ffmpeg 的 dshow 后端就是 DirectShow 本身，枚举结果权威、与采集链路（VLC dshow://）一致；
    ///   2) 只有它能一并给出「设备真实支持的信号格式」（分辨率 × 像素格式 × 帧率），
    ///      这是替掉硬编码预设、让采集卡 1080p60 / 4K 等能力如实呈现的关键；
    ///   3) 项目已依赖 ffmpeg（FfmpegOutput.FfmpegPath），不引入新依赖。
    ///
    /// ⚠ ffmpeg 的 -list_devices / -list_options 输出全部走 **stderr**，且退出码非 0
    ///   （因为 dummy 输入必然失败）—— 所以只读 stderr，不能用退出码判断成败。
    /// </summary>
    public static class CaptureDeviceService
    {
        // "HD Webcam" (video)   /   "CABLE-B Output (VB-Audio Cable B)" (audio)
        // 设备名可能自带括号，故引号内用贪婪匹配，再靠末尾 (video|audio|none) 收口。
        private static readonly Regex DeviceRe = new Regex(
            "^\\[.*?\\]\\s+\"(.+)\"\\s+\\((video|audio|none)\\)\\s*$", RegexOptions.Compiled);

        // Alternative name "@device_pnp_\\?\usb#vid_..."
        private static readonly Regex AltRe = new Regex(
            "^\\[.*?\\]\\s+Alternative name \"(.+)\"\\s*$", RegexOptions.Compiled);

        // pixel_format=yuyv422  min s=640x480 fps=15 max s=640x480 fps=30
        private static readonly Regex PixelRe = new Regex(
            "pixel_format=(\\w+)\\s+min s=(\\d+)x(\\d+)\\s+fps=([\\d.]+)\\s+max s=(\\d+)x(\\d+)\\s+fps=([\\d.]+)",
            RegexOptions.Compiled);

        // 少数设备（尤其专业采集卡）用 vcodec= 而非 pixel_format=
        private static readonly Regex VcodecRe = new Regex(
            "vcodec=(\\w+)\\s+min s=(\\d+)x(\\d+)\\s+fps=([\\d.]+)\\s+max s=(\\d+)x(\\d+)\\s+fps=([\\d.]+)",
            RegexOptions.Compiled);

        // 实际打开设备后 ffmpeg 报出的视频流：
        //   Stream #0:0: Video: rawvideo (YUY2 / 0x32595559), yuyv422(tv, ...), 1920x1080, 60 fps, 60 tbr
        private static readonly Regex VideoStreamRe = new Regex(
            "Stream #\\d+:\\d+\\s*(?:\\(\\w+\\))?:\\s*Video:\\s*\\w+\\s*\\(([^)/]+)[^)]*\\)\\s*,\\s*" +
            "([a-z0-9_]+)\\([^)]*\\),\\s*(\\d+)x(\\d+)(?:[^,]*),\\s*([\\d.]+)\\s*fps",
            RegexOptions.Compiled);

        /// <summary>最近一次操作的错误说明（界面如实展示，不吞错）。</summary>
        public static string LastError { get; private set; } = "";

        /// <summary>最近一次 ffmpeg 的原始输出（诊断用，界面可展开查看）。</summary>
        public static string LastRawLog { get; private set; } = "";

        // ==================== 对外接口 ====================

        /// <summary>枚举本机全部 DirectShow 采集设备（视频 + 音频），已按「采集卡→摄像头→虚拟设备→音频」排序。</summary>
        public static Task<CaptureEnumeration> EnumerateDevicesAsync() => Task.Run(() =>
        {
            var result = new CaptureEnumeration();
            var log = RunFfmpeg("-hide_banner -list_devices true -f dshow -i dummy", 12000);
            result.RawLog = log;
            LastRawLog = log;

            if (string.IsNullOrWhiteSpace(log))
            {
                result.Error = string.IsNullOrWhiteSpace(LastError)
                    ? "未取得 ffmpeg 输出（可能 ffmpeg 不可用）"
                    : LastError;
                return result;
            }

            CaptureDevice? pending = null;   // 等待紧随其后的 Alternative name 行
            foreach (var raw in log.Split('\n'))
            {
                var line = raw.TrimEnd('\r');

                var alt = AltRe.Match(line);
                if (alt.Success && pending != null)
                {
                    pending.AltName = alt.Groups[1].Value;
                    pending = null;
                    continue;
                }

                var m = DeviceRe.Match(line);
                if (!m.Success) continue;

                var name = m.Groups[1].Value;
                var type = m.Groups[2].Value;
                bool isVideo = type == "video";

                // "(none)" 是仅有音频能力、无视频输出的设备（如纯虚拟音频）——跳过，
                // 它出现在采集面板里只会干扰；音频设备另有 (audio) 条目。
                if (type == "none") continue;
                if (string.IsNullOrWhiteSpace(name)) continue;

                var dev = new CaptureDevice
                {
                    Name = name,
                    Kind = ClassifyDevice(name, isVideo),
                    IsVideo = isVideo
                };
                result.Devices.Add(dev);
                if (isVideo) pending = dev;   // 视频设备后面才跟 Alternative name
            }

            // 品牌名没命中的多路采集卡（GATHER CAM1~4 这类）靠「同名多路」兜底识别
            DetectMultiChannelCards(result.Devices);

            // 排序：采集卡 → 摄像头 → 虚拟设备 → 音频；同类按名称
            result.Devices.Sort((a, b) =>
            {
                int k = ((int)a.Kind).CompareTo((int)b.Kind);
                return k != 0 ? k : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            if (result.Devices.Count == 0)
                result.Error = "未检测到任何采集设备（HDMI 采集卡请确认已插好、驱动已装）";

            LastError = result.Error;
            return result;
        });

        /// <summary>
        /// 枚举某视频设备真实支持的信号格式（分辨率 × 像素格式 × 帧率）。
        /// 结果已按「分辨率降序 → 像素格式 → 帧率降序」排好，界面直接展示即可。
        /// </summary>
        public static Task<List<CaptureFormat>> EnumerateFormatsAsync(string videoDevice) => Task.Run(() =>
        {
            var list = new List<CaptureFormat>();
            if (string.IsNullOrWhiteSpace(videoDevice)) { LastError = "未指定设备"; return list; }

            var log = RunFfmpeg(
                $"-hide_banner -list_options true -f dshow -i video={Quote(videoDevice)}", 15000);
            LastRawLog = log;

            if (string.IsNullOrWhiteSpace(log)) return list;

            // 同一格式可能在不同 Pin 上重复出现，用 Key 去重
            var seen = new HashSet<string>();
            foreach (var raw in log.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                var m = PixelRe.Match(line);
                if (!m.Success) m = VcodecRe.Match(line);
                if (!m.Success) continue;

                var f = new CaptureFormat
                {
                    PixelFormat = m.Groups[1].Value,
                    Width = int.Parse(m.Groups[2].Value),
                    Height = int.Parse(m.Groups[3].Value),
                    FpsMin = ParseFps(m.Groups[4].Value),
                    FpsMax = ParseFps(m.Groups[7].Value)
                };
                // 设备默认（0x0）与明显无效项不列
                if (f.Width <= 0 || f.Height <= 0) continue;
                if (f.FpsMax <= 0 && f.FpsMin <= 0) continue;

                if (f.FpsMax < f.FpsMin) (f.FpsMin, f.FpsMax) = (f.FpsMax, f.FpsMin);

                if (seen.Add(f.Key)) list.Add(f);
            }

            list.Sort((a, b) =>
            {
                int p = (b.Width * b.Height).CompareTo(a.Width * a.Height);   // 分辨率降序
                if (p != 0) return p;
                p = string.Compare(a.PixelFormat, b.PixelFormat, StringComparison.OrdinalIgnoreCase);
                if (p != 0) return p;
                return b.FpsMax.CompareTo(a.FpsMax);                          // 帧率降序
            });

            return list;
        });

        // ==================== 内部实现 ====================

        /// <summary>
        /// 探测「现在这根线正送进来什么」——真的把设备打开采样几帧，读出实际信号，
        /// 并**实测场序**（隔行 / 逐行 / 判不出来）。
        ///
        /// 用途：面板上那一行「当前输入信号：1920 × 1080 · YUY2 · 60 fps · 逐行」。
        /// 为什么不直接照能力表显示：能力表是「设备支持哪些」（一张菜单），
        /// 实际信号是「现在端上来的是哪道」——本机实测两者就不一致
        /// （能力表最高 2560×1600@30，实际进来的是 1920×1080@60）。
        ///
        /// ⚠ DirectShow 采集设备是**独占**的：若设备正被 vMix / OBS 占用，这里会失败，
        ///   此时如实把原因报出来（"被其他程序占用"），不假装没信号。
        /// </summary>
        public static Task<CaptureSignalInfo> ProbeLiveSignalAsync(string videoDevice) => Task.Run(() =>
        {
            var info = new CaptureSignalInfo();
            if (string.IsNullOrWhiteSpace(videoDevice)) { info.Error = "未指定设备"; return info; }

            // ⚠ 一次调用同时取两样东西：
            //     stderr → 实际信号的格式（宽高 / 像素格式 / 帧率）
            //     stdout → 若干原始**灰度**帧，用来做隔行行结构分析
            //   **绝不能加 -vf scale 缩小**：任何垂直方向的插值（bicubic/bilinear）
            //   都会当场抹平隔行行结构，判据直接失效。宁可多传几 MB 原始像素。
            const int grabFrames = 3;
            var frames = RunFfmpegBinary(
                $"-hide_banner -loglevel info -nostats -f dshow -rtbufsize 64M " +
                $"-i video={Quote(videoDevice)} -frames:v {grabFrames} -pix_fmt gray -f rawvideo -",
                9000, out var log);
            LastRawLog = log;

            if (string.IsNullOrWhiteSpace(log))
            {
                info.Error = string.IsNullOrWhiteSpace(LastError)
                    ? "设备无响应" : LastError;
                return info;
            }

            // 形如：Stream #0:0: Video: rawvideo (YUY2 / 0x32595559),
            //        yuyv422(tv, bt470bg/bt709/unknown), 1920x1080, 60 fps, 60 tbr
            // ⚠ Match 取的是**第一个**匹配；Input #0 必然排在 Output #0 之前，所以拿到的是输入行。
            var m = VideoStreamRe.Match(log);
            if (!m.Success)
            {
                info.Error = OccupiedHint(log);
                Log($"探测「{videoDevice}」失败：{info.Error}");
                return info;
            }

            info.HasSignal = true;
            info.FourCc = m.Groups[1].Value.Trim();
            info.PixelFormat = m.Groups[2].Value.Trim();
            info.Width = int.Parse(m.Groups[3].Value);
            info.Height = int.Parse(m.Groups[4].Value);
            info.Fps = ParseFps(m.Groups[5].Value);
            info.RawLine = m.Value.Trim();

            // ── 场序实测 ──
            // 见 CaptureSignalInfo.Interlaced 的注释：为什么不能从 ffmpeg 日志 grep 关键字。
            int frameBytes = info.Width * info.Height;
            if (frameBytes > 0 && frames.Length >= frameBytes)
            {
                // 用**最后一帧**：设备刚打开时的头几帧可能还没稳定。
                var last = new byte[frameBytes];
                Buffer.BlockCopy(frames, frames.Length - frameBytes, last, 0, frameBytes);
                info.InterlaceRatio = MeasureInterlaceRatio(last, info.Width, info.Height);
                if (info.InterlaceRatio > 0) info.InterlaceFrames = 1;
            }
            info.Interlaced = JudgeInterlace(info.InterlaceRatio);

            var verdict = info.Interlaced switch { true => "隔行", false => "逐行", _ => "未能判定" };
            Log($"探测「{videoDevice}」：{info.Width}x{info.Height} {info.PixelFormat} {info.Fps:0.##}fps"
                + $"　→　R={info.InterlaceRatio:0.000}　场序={verdict}"
                + (info.InterlaceRatio <= 0 ? "（画面无内容，无从判断）" : ""));
            return info;
        });

        /// <summary>
        /// 隔行行结构分析：真隔行帧的奇偶行来自不同时刻的两个场，所以
        /// 「跨场相邻行」的像素差应显著大于「同场相邻行（隔一行）」的像素差。
        /// 返回 R = d_cross / d_same；0 表示像素不足或画面无内容（纯色），无从判断。
        ///
        /// 为什么用几何判据而不是 ffmpeg 的 idet：实测证明 idet 在高频纹理上会大量误报 ——
        /// 拿 100% 确认逐行的合成图喂给它，它判出 TFF 83 / BFF 62 / Progressive 仅 36。
        /// </summary>
        private static double MeasureInterlaceRatio(byte[] gray, int w, int h)
        {
            if (gray == null || w <= 0 || h < 32) return 0;
            if ((long)gray.Length < (long)w * h) return 0;

            // 只取中间 60% 宽度，避开左右边缘可能存在的黑边 / 边框
            int x0 = w * 20 / 100, x1 = w * 80 / 100;
            if (x1 - x0 < 16) { x0 = 0; x1 = w; }

            long crossSum = 0, sameSum = 0, n = 0;
            for (int y = 0; y + 2 < h; y += 2)
            {
                int o0 = y * w, o1 = (y + 1) * w, o2 = (y + 2) * w;
                for (int x = x0; x < x1; x++)
                {
                    crossSum += Math.Abs(gray[o0 + x] - gray[o1 + x]);   // 跨场（相邻两行）
                    sameSum += Math.Abs(gray[o0 + x] - gray[o2 + x]);   // 同场（隔一行）
                    n++;
                }
            }
            if (n == 0) return 0;

            double dc = (double)crossSum / n;
            double ds = (double)sameSum / n;
            // 画面是纯色（无内容 / 未接信号）时两个差都趋近 0，比值无意义 → 返回 0 = 判不出
            if (ds < 0.01 || dc < 0.01) return 0;
            return dc / ds;
        }

        /// <summary>
        /// 由 R 值给出**三态**判定。阈值来自 2026-10-16 真机标定：
        ///   已知逐行样本 R = 0.80 ／ 本机实拍逐行 R = 0.53 ／ 合成隔行样本 R = 1.33 ~ 1.36
        /// 取 0.95 / 1.15 为分界，两侧各留约 0.2 的余量；
        /// **落在中间就如实返回 null（判不了），不硬猜** —— 硬猜的代价是真接上 1080i 时静默拉丝。
        /// </summary>
        private static bool? JudgeInterlace(double ratio)
        {
            if (ratio <= 0) return null;        // 画面无内容 / 像素不足
            if (ratio <= 0.95) return false;    // 逐行
            if (ratio >= 1.15) return true;     // 隔行
            return null;                        // 灰区：判不出来
        }

        /// <summary>设备打不开时，从 ffmpeg 的错误文本里给一句人话。</summary>
        private static string OccupiedHint(string log)
        {
            if (Regex.IsMatch(log, "Could not find|I/O error|Cannot open|Error opening|0x80070005",
                    RegexOptions.IgnoreCase))
                return "未检测到信号（设备打不开 —— 可能被 vMix / OBS 等程序独占，或信号源未开机）";
            return "未检测到信号";
        }

        /// <summary>按设备名称启发式分类（后续可用 PnP 的 PNPClass / Service 交叉校验提高准确度）。</summary>
        private static CaptureDeviceKind ClassifyDevice(string name, bool isVideo)
        {
            if (!isVideo) return CaptureDeviceKind.Audio;
            var n = name.ToLowerInvariant();

            // 软件虚拟设备：它们不是物理信号源，标出来避免与真实采集卡混淆
            string[] virtualHints =
            {
                "virtual", "obs", "vmix", "webcast", "ndi", "manycam",
                "snap camera", "xsplit", "droidcam", "iriun"
            };
            foreach (var h in virtualHints)
                if (n.Contains(h)) return CaptureDeviceKind.VirtualDevice;

            // 采集卡：常见品牌 / 型号 / 通用词
            //   ⚠ 本机实测踩过：GATHER CAM1~4（四路 USB3 采集卡）品牌名不在表里，
            //     名字里的 "cam" 反而像摄像头 → 被误判成摄像头，默认链路也跟着选错。
            //     所以这里补上 "gather"，并且下面还有一道「多路同名」兜底规则。
            string[] cardHints =
            {
                "cam link", "capture", "grabber", "hdmi", "elgato", "avermedia",
                "magewell", "blackmagic", "ultra studio", "decklink", "uvc",
                "sdi", "hd60", "gc5", "gc3", "tc-", "digilive", "gather",
                "采集", "天创", "圆刚", "美乐威", "品尼高"
            };
            foreach (var h in cardHints)
                if (n.Contains(h)) return CaptureDeviceKind.CaptureCard;

            return CaptureDeviceKind.Camera;
        }

        /// <summary>名称去掉末尾数字后的「基名」（`GATHER CAM3` → `GATHER CAM`）。无末尾数字返回 null。</summary>
        private static string? BaseName(string name)
        {
            var m = Regex.Match(name.Trim(), @"^(.*?)(\d+)$");
            if (!m.Success) return null;
            var b = m.Groups[1].Value.TrimEnd(' ', '-', '_');
            return b.Length == 0 ? null : b;
        }

        /// <summary>
        /// 兜底分类：一趟里出现「≥2 个基名相同的视频设备」（`GATHER CAM1` / `CAM2` / `CAM3` / `CAM4`）
        /// 基本可以断定是同一块多路采集卡的不同通道 —— 普通电脑不会插四只同名摄像头。
        /// 这条规则不依赖品牌名，专治「没见过的国产采集卡」。
        /// </summary>
        private static void DetectMultiChannelCards(List<CaptureDevice> devices)
        {
            var groups = new Dictionary<string, List<CaptureDevice>>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in devices)
            {
                if (!d.IsVideo) continue;
                if (d.Kind == CaptureDeviceKind.VirtualDevice) continue;
                var b = BaseName(d.Name);
                if (b == null) continue;                     // 名字没带序号 → 不参与（避免「两只同名摄像头」被误判）
                if (!groups.TryGetValue(b, out var list)) groups[b] = list = new List<CaptureDevice>();
                list.Add(d);
            }

            foreach (var kv in groups)
                if (kv.Value.Count >= 2)
                    foreach (var d in kv.Value)
                        d.Kind = CaptureDeviceKind.CaptureCard;
        }

        private static double ParseFps(string s) =>
            double.TryParse(s, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;

        /// <summary>dshow 设备名可能含空格/括号，必须加引号（内部引号去掉以防破坏参数）。</summary>
        private static string Quote(string s) => "\"" + s.Replace("\"", "") + "\"";

        /// <summary>
        /// 同步跑一次 ffmpeg 并取回输出。
        /// ⚠ ffmpeg 的 -list_* 全部写 stderr，且进程必然以非 0 退出（dummy 输入失败）——只取文本。
        /// </summary>
        private static string RunFfmpeg(string args, int timeoutMs)
        {
            LastError = "";
            if (!FfmpegOutput.IsAvailable)
            {
                LastError = "找不到 ffmpeg.exe（采集设备枚举依赖它）";
                return "";
            }

            try
            {
                var psi = new ProcessStartInfo(FfmpegOutput.FfmpegPath, args)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    // ⚠ 必须显式 UTF-8：ffmpeg 的 Windows 版往管道里写的是 UTF-8 字节，
                    // 不指定时 .NET 按控制台代码页（中文系统 = GBK）解码，
                    // 「数字音频接口」会变成乱码「鏁板瓧闊抽鎺ュ彛」——
                    // 乱码名再拼进采集命令行就成了打不开的设备（I/O error，实测踩过）。
                    StandardErrorEncoding = Encoding.UTF8,
                    StandardOutputEncoding = Encoding.UTF8
                };

                using var p = Process.Start(psi);
                if (p == null) { LastError = "无法启动 ffmpeg 进程"; return ""; }

                // 起异步读取，避免设备无响应时把读取线程一起卡死
                var errTask = p.StandardError.ReadToEndAsync();
                var outTask = p.StandardOutput.ReadToEndAsync();

                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    LastError = $"设备响应超时（{timeoutMs / 1000} 秒）——可能被其他程序占用";
                }

                var sb = new StringBuilder();
                try { if (errTask.Wait(3000)) sb.Append(errTask.Result); } catch { }
                try { if (outTask.Wait(1000)) sb.Append('\n').Append(outTask.Result); } catch { }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return "";
            }
        }

        /// <summary>
        /// 跑一次 ffmpeg，同时取回 stderr 文本与 **stdout 原始字节**。
        /// 场序判定要拿未解码的原始灰度帧，所以 stdout 必须走二进制通道 ——
        /// ⚠ 不能设 StandardOutputEncoding（那样 .NET 会按文本解码，帧数据全毁）。
        /// </summary>
        private static byte[] RunFfmpegBinary(string args, int timeoutMs, out string log)
        {
            log = "";
            LastError = "";
            if (!FfmpegOutput.IsAvailable)
            {
                LastError = "找不到 ffmpeg.exe（采集设备枚举依赖它）";
                return Array.Empty<byte>();
            }

            try
            {
                var psi = new ProcessStartInfo(FfmpegOutput.FfmpegPath, args)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using var p = Process.Start(psi);
                if (p == null) { LastError = "无法启动 ffmpeg 进程"; return Array.Empty<byte>(); }

                var errTask = p.StandardError.ReadToEndAsync();

                // ⚠ stdout 必须在**后台**读：管道写满（几 MB 灰度帧）后 ffmpeg 会阻塞，
                //   而此时若先 WaitForExit 就成了互相等死（经典管道死锁）。
                //   封顶 16 MB，防止异常输入把内存灌爆。
                var buf = new System.IO.MemoryStream();
                var copyTask = Task.Run(() =>
                {
                    try
                    {
                        var src = p.StandardOutput.BaseStream;
                        var chunk = new byte[64 * 1024];
                        int n;
                        while (buf.Length < 16L * 1024 * 1024 &&
                               (n = src.Read(chunk, 0, chunk.Length)) > 0)
                            buf.Write(chunk, 0, n);
                    }
                    catch { /* 进程被 kill 时读会抛，忽略 */ }
                });

                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    LastError = $"设备响应超时（{timeoutMs / 1000} 秒）——可能被其他程序占用";
                }

                try { copyTask.Wait(2000); } catch { }
                try { if (errTask.Wait(3000)) log = errTask.Result; } catch { }
                return buf.ToArray();
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return Array.Empty<byte>();
            }
        }

        private static readonly object _logGate = new();

        /// <summary>
        /// 采集探测诊断日志 → <c>%TEMP%\cutlance-capture-probe.log</c>
        ///
        /// 为什么要留这份日志：场序判定是个**看不见的算法**，R 值是它唯一的可观测指标。
        /// 万一判错（把 1080i 当逐行播出去，画面拉丝），只有这份日志能一眼看出
        /// R 到底是多少、离阈值还差多远 —— 否则又是「界面上一切正常」的静默故障。
        /// （同类先例：预处理链路 %TEMP%\cutlance-preprocess.log、NDI %TEMP%\cutlance-ndi.log）
        /// </summary>
        private static void Log(string msg)
        {
            try
            {
                lock (_logGate)
                {
                    var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                                      "cutlance-capture-probe.log");
                    var fi = new System.IO.FileInfo(path);
                    if (fi.Exists && fi.Length > 512 * 1024)
                        System.IO.File.Delete(path);      // 超 512 KB 就重开，别无限长
                    System.IO.File.AppendAllText(path,
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}{Environment.NewLine}");
                }
            }
            catch { /* 日志失败绝不能影响探测本身 */ }
        }
    }
}
