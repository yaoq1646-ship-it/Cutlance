using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cutlance.Models;
using Cutlance.Services.Audio;

namespace Cutlance.Services
{
    /// <summary>一次输出的完整参数（由 StreamService 组装传入）。</summary>
    public sealed class FfmpegOutputConfig
    {
        public double Left, Top, Width, Height;      // 抓屏区域（屏幕物理像素，即「播出框」）
        public int OutWidth = 1920, OutHeight = 1080; // 输出分辨率（锁定）
        public double Fps = 30;                       // 25 / 29.97 / 30 / 50 / 59.94 / 60
        public string Encoder = "nvenc";              // nvenc | qsv | software
        public int BitrateKbps = 6000;
        public bool EnableStream = true;
        public bool EnableRecord = true;
        public string RtmpUrl = "rtmp://localhost:1935/live/stream";
        public string RecordFolder = "";
        public string Container = "mkv";              // mkv | mp4
        public List<AudioChannel> AudioTracks = new();  // 最多八路（混音后统一进推流+录制）

        /// <summary>
        /// 输出总线压缩器 + 限幅（2026-10-23）。挂在 amix 之后、编码之前：
        /// 八路混完再加图层音量很容易超过 0 dB，不压就是削顶破音。
        /// Level = Off 时 FilterChain 为空串，滤镜链与老版本完全一致。
        /// </summary>
        public AudioCompressor Compressor = new();

        public VideoFx Fx = new();

        /// <summary>
        /// 视频从**合成器喂帧**（stdin rawvideo BGRA），不再抓屏。
        /// 2026-10-20 起的默认路径：成品帧本来就在内存里，输出与窗口是否可见无关。
        /// false = 老 gdigrab 抓「播出框」兜底（合成器起不来时用，语义同以前）。
        /// </summary>
        public bool FeedFromSynth;

        /// <summary>喂帧尺寸（= 合成器成品帧尺寸）。FeedFromSynth 时必填。</summary>
        public int FeedWidth, FeedHeight;

        /// <summary>
        /// 分段录制：每 N 秒自动封包并开下一个文件（0 = 不分段）。
        /// ffmpeg 的 segment 分段器在同一进程里连续编码、只切容器，换段不丢帧不断音。
        /// </summary>
        public double SegmentSeconds;
    }

    /// <summary>
    /// 模块8：FFmpeg 输出引擎（推流 + 录制）。
    ///
    /// 为什么用 FFmpeg：老板要的八路音频混音、磨皮美白、瘦身拉长，LibVLC 的单路 sout 做不到。
    ///
    /// 视频输入有两条路（2026-10-20 起默认走第一条）：
    ///   ① 合成器喂帧：stdin 读 rawvideo BGRA —— 画面与窗口状态无关（最小化/被遮挡都不影响）；
    ///   ② gdigrab 抓「播出框」兜底：合成器起不来时用，语义与老版本完全一致。
    /// 音频始终是 dshow 多路 amix + 程序内混音总线，滤镜链与编码器选择两条路共用。
    /// </summary>
    public sealed class FfmpegOutput
    {
        public static FfmpegOutput Instance { get; } = new();

        /// <summary>ffmpeg.exe 路径（默认自动探测，可在设置里改）。</summary>
        public static string FfmpegPath { get; set; } = DetectFfmpeg();

        public static bool IsAvailable
        {
            get
            {
                try { return !string.IsNullOrWhiteSpace(FfmpegPath) && File.Exists(FfmpegPath); }
                catch { return false; }
            }
        }

        public bool IsRunning { get; private set; }
        public DateTime? StartedAt { get; private set; }

        /// <summary>本次实际使用的编码器（NVENC 失败回退后会变成 software）。</summary>
        public string ActiveEncoder { get; private set; } = "nvenc";

        public string? LastRecordPath { get; private set; }

        public event Action? StatusChanged;
        public event Action<string>? Error;

        private Process? _proc;
        private volatile bool _stopping;
        private DateTime _startedAt;
        private readonly StringBuilder _stderrTail = new();
        private readonly object _errLock = new();
        private FfmpegOutputConfig? _cfg;

        // ===== 合成器喂帧（C 方案）=====
        // 与 POC（Synth/Output/FfmpegEncoder）同一套路，都是踩过坑的：
        //   ① 二进制写必须走 StandardInput.BaseStream（铁律 11）；
        //   ② 帧必须拷进队列（合成器的 front buffer 下一帧就被覆盖）；
        //   ③ 队列满就丢并计数，绝不阻塞合成线程（阻塞合成 = 整机卡死）；
        //   ④ 队列不能太浅：ffmpeg 冷启动要 200~400ms 才吃第一帧。
        private BlockingCollection<byte[]>? _frameQ;
        private readonly ConcurrentBag<byte[]> _framePool = new();
        private Thread? _frameWriter;
        private int _frameBytes;
        private int _feedRowBytes;
        private int _feedRows;        private volatile bool _feedMode;
        private long _framesFed, _framesDropped;

        /// <summary>已送进编码管道的帧数。</summary>
        public long FramesFed => Interlocked.Read(ref _framesFed);

        /// <summary>因编码跟不上被丢弃的帧数（正常应为 0~个位数）。</summary>
        public long FramesDropped => Interlocked.Read(ref _framesDropped);

        /// <summary>本次是否走合成器喂帧（false = 抓屏兜底）。</summary>
        public bool FeedFromSynth => _feedMode;

        /// <summary>本次分段录制段长（秒）；0 = 不分段。</summary>
        public double SegmentSecondsUsed { get; private set; }

        // ===== 段尾碎片清理（2026-10-22）=====
        // 分段录制只能在关键帧边界切段，停止时刻刚好跨过边界时，会留下一个几十毫秒的
        // 第 N+1 段（老板嫌它碍事）。ffmpeg 退出后按 ffprobe 时长判，太短的删掉。
        private string? _segFolder;
        private string? _segStem;
        private string _segExt = "mkv";

        /// <summary>短于这个秒数的段视为碎片（产品默认 1 秒）。</summary>
        private const double TailMinKeepSec = 1.0;

        // ============ 启动 / 停止 ============

        public void Start(FfmpegOutputConfig cfg)
        {
            if (IsRunning) return;
            if (!cfg.EnableStream && !cfg.EnableRecord)
            {
                Error?.Invoke("请至少启用「推流」或「录制」之一");
                return;
            }
            if (cfg.FeedFromSynth)
            {
                // 喂帧模式不看屏幕区域，只看合成器尺寸对不对得上
                if (cfg.FeedWidth <= 1 || cfg.FeedHeight <= 1)
                {
                    Error?.Invoke("合成器尺寸无效（" + cfg.FeedWidth + "×" + cfg.FeedHeight + "），输出无法启动");
                    return;
                }
            }
            else if (cfg.Width <= 1 || cfg.Height <= 1)
            {
                Error?.Invoke("播出框区域无效（宽高须 > 1），请确认播出监视器未被最小化");
                return;
            }
            if (!IsAvailable)
            {
                Error?.Invoke("找不到 ffmpeg.exe，请安装 FFmpeg 或在「输出设置」里指定路径");
                return;
            }
            _cfg = cfg;
            Launch(cfg.Encoder);
        }

        public void Stop()
        {
            _stopping = true;
            var p = _proc;
            _proc = null;
            if (IsRunning || p != null)
            {
                IsRunning = false;
                StartedAt = null;
                StatusChanged?.Invoke();
            }
            if (p == null) return;

            // 优雅退出：
            //   抓屏模式 → 向 stdin 写 'q'（ffmpeg 交互命令，mkv/mp4 都能正常收尾）；
            //   喂帧模式 → stdin 里是二进制视频数据，不能写按键，只能**关掉 stdin 触发 EOF**，
            //              ffmpeg 收到输入结束会自己写完容器索引（POC 已验证）。
            Task.Run(() =>
            {
                if (_feedMode)
                {
                    StopFeed(p);
                }
                else
                {
                    try
                    {
                        if (!p.HasExited)
                        {
                            p.StandardInput.WriteLine("q");
                            p.StandardInput.Flush();
                        }
                    }
                    catch { }
                }
                try { p.WaitForExit(3000); } catch { }
                try { if (!p.HasExited) p.Kill(true); } catch { }
                try { p.Dispose(); } catch { }
            });
        }

        /// <summary>
        /// 关闭程序专用的「同步硬停」：给 ffmpeg 极短的收尾机会（写 'q' 让 mkv 正常写尾部），
        /// 超时立刻杀掉整棵进程树。
        /// ⚠ 必须在父进程自我强杀【之前】调用 —— 否则 ffmpeg 会变成孤儿进程继续推流。
        /// 与 <see cref="Stop"/> 的区别：Stop 是"发完命令就返回、后台慢慢收尾"，这里是"当场办完"。
        /// </summary>
        public void HardStop()
        {
            _stopping = true;
            var p = _proc;
            _proc = null;
            if (p == null) return;

            try
            {
                if (_feedMode)
                {
                    StopFeed(p);
                }
                else if (!p.HasExited)
                {
                    p.StandardInput.WriteLine("q");
                    p.StandardInput.Flush();
                }
            }
            catch { }
            try { p.WaitForExit(600); } catch { }
            try { if (!p.HasExited) p.Kill(true); } catch { }
            try { p.Dispose(); } catch { }
        }

        private void Launch(string encoder)
        {
            var cfg = _cfg!;
            _stopping = false;
            _stderrTail.Clear();
            ActiveEncoder = encoder;

            // 硬编回退会重入本方法（NVENC 起不来 → 换 x264 重开进程），旧的喂帧管道必须先扔掉
            DiscardFeed();

            _feedMode = cfg.FeedFromSynth && cfg.FeedWidth > 0 && cfg.FeedHeight > 0;
            SegmentSecondsUsed = cfg.SegmentSeconds;

            if (cfg.EnableRecord)
            {
                try { Directory.CreateDirectory(cfg.RecordFolder); } catch { }
            }

            var args = BuildArgs(cfg, encoder);
            AudioBus.Log("FFmpeg 启动参数：" + args);
            var psi = new ProcessStartInfo
            {
                FileName = FfmpegPath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                StandardErrorEncoding = Encoding.UTF8
            };

            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.ErrorDataReceived += (_, e) => OnStderr(e.Data);
            p.Exited += (_, _) => OnExited();
            p.Start();
            p.BeginErrorReadLine();
            _proc = p;

            if (_feedMode) StartFeed(p, cfg);

            _startedAt = DateTime.Now;
            IsRunning = true;
            StartedAt = _startedAt;
            StatusChanged?.Invoke();
        }

        // ============ 合成器喂帧管道 ============

        private void StartFeed(Process p, FfmpegOutputConfig cfg)
        {
            _frameBytes = cfg.FeedWidth * cfg.FeedHeight * 4;
            _feedRowBytes = cfg.FeedWidth * 4;
            _feedRows = cfg.FeedHeight;
            Interlocked.Exchange(ref _framesFed, 0);
            Interlocked.Exchange(ref _framesDropped, 0);

            // 队列深度 = 0.35 秒的帧（1080p 一帧 8.3MB → 10 帧约 83MB）。
            // 太浅会被 ffmpeg 冷启动吃掉（实测 3 帧深度在 20 秒里丢 4 帧），太深会推高推流延迟。
            int depth = Math.Clamp((int)Math.Ceiling(Math.Max(1, cfg.Fps) * 0.35), 6, 14);
            var q = new BlockingCollection<byte[]>(depth);
            _frameQ = q;
            for (int i = 0; i < depth; i++) _framePool.Add(new byte[_frameBytes]);

            var stdin = p.StandardInput.BaseStream;
            int bytes = _frameBytes;
            _frameWriter = new Thread(() =>
            {
                try
                {
                    foreach (var buf in q.GetConsumingEnumerable())
                    {
                        stdin.Write(buf, 0, bytes);
                        stdin.Flush();
                        _framePool.Add(buf);
                    }
                }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
                catch (Exception ex)
                {
                    try { lock (_errLock) _stderrTail.Append("[feed] ").Append(ex.Message).Append('\n'); } catch { }
                }
            })
            {
                IsBackground = true,
                Name = "Cutlance-FfmpegFeed",
                Priority = ThreadPriority.AboveNormal
            };
            _frameWriter.Start();

            AudioBus.Log($"[喂帧] 已接入合成器：{cfg.FeedWidth}×{cfg.FeedHeight}@{cfg.Fps}，队列 {depth} 帧");
        }

        /// <summary>
        /// 合成线程调用：把一帧成品像素塞进编码队列。**绝不阻塞** —— 队列满直接丢并记账，
        /// 宁可编码掉一帧，也不能让合成循环等 IO（那会让预览、录制、NDI 一起卡）。
        /// </summary>
        public void FeedFrame(IntPtr src, int stride)
        {
            var q = _frameQ;
            if (q == null || _frameBytes <= 0 || src == IntPtr.Zero) return;

            // ⚠ stride 是**一行**的字节数（1920 宽 = 7680），不是整帧大小（8294400）。
            //   一开始拿它跟整帧字节数比，判据永远不成立 → 帧全被当"尺寸不符"丢掉，
            //   表现是 ffmpeg 一个字节都没收到、录出 0 个文件（2026-10-20 踩过）。
            if (stride < _feedRowBytes)
            {
                if (Interlocked.Increment(ref _framesDropped) == 1)
                    try { ErrorLog.Info($"[喂帧] 行宽不足：stride={stride} 期望={_feedRowBytes}，已丢帧"); } catch { }
                return;
            }

            if (!_framePool.TryTake(out var buf))
            {
                Interlocked.Increment(ref _framesDropped);   // 池空 = 队列已攒满，编码跟不上
                return;
            }

            try
            {
                if (stride == _feedRowBytes && _feedRowBytes * _feedRows == _frameBytes)
                {
                    Marshal.Copy(src, buf, 0, _frameBytes);
                }
                else
                {
                    // 行尾有填充时逐行拷（合成器当前是紧密排列，这里纯防呆）
                    for (int y = 0; y < _feedRows; y++)
                        Marshal.Copy(src + y * stride, buf, y * _feedRowBytes, _feedRowBytes);
                }

                if (q.TryAdd(buf))
                {
                    Interlocked.Increment(ref _framesFed);
                }
                else
                {
                    Interlocked.Increment(ref _framesDropped);
                    _framePool.Add(buf);
                }
            }
            catch
            {
                Interlocked.Increment(ref _framesDropped);
                _framePool.Add(buf);
            }
        }

        /// <summary>
        /// 收尾：先让已排队的帧写完（否则文件尾部会短一截），再关 stdin 触发 EOF，
        /// ffmpeg 收到输入结束会自己写完容器索引。
        /// </summary>
        private void StopFeed(Process? p)
        {
            var q = _frameQ;
            _frameQ = null;                    // 立刻停止收新帧（合成线程可能还在调 FeedFrame）
            try { q?.CompleteAdding(); } catch { }
            try { _frameWriter?.Join(2500); } catch { }
            _frameWriter = null;

            try
            {
                if (p != null && !p.HasExited)
                {
                    p.StandardInput.BaseStream.Flush();
                    p.StandardInput.Close();
                }
            }
            catch { }
            try { q?.Dispose(); } catch { }
        }

        /// <summary>硬编回退重开进程时，把上一轮的管道直接丢弃（旧进程已经退了，没人再读）。</summary>
        private void DiscardFeed()
        {
            var q = _frameQ;
            _frameQ = null;
            _frameBytes = 0;
            try { q?.CompleteAdding(); } catch { }
            try { q?.Dispose(); } catch { }
            _frameWriter = null;
            while (_framePool.TryTake(out _)) { }
        }

        // ============ 命令构建 ============

        private static string Q(string s) => "\"" + (s ?? "").Replace("\"", "") + "\"";

        private static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

        private string BuildArgs(FfmpegOutputConfig c, string encoder)
        {
            var sb = new StringBuilder();
            sb.Append("-hide_banner -loglevel warning -nostats -y ");
            // 喂帧模式下 stdin 里流的是二进制视频，绝不能让 ffmpeg 把它当交互按键去解析
            if (c.FeedFromSynth) sb.Append("-nostdin ");

            // 分段局部形变（模块10）：remap 滤镜需要两张坐标映射图作为额外的视频输入。
            // 映射图按参数缓存（BodyWarp），参数没变就直接复用，不重复生成。
            string? xmap = null, ymap = null;
            if (c.Fx.WarpActive)
            {
                BodyWarp.Ensure(c.Fx, c.OutWidth, c.OutHeight, out xmap, out ymap, out var warpErr);
                if (!string.IsNullOrEmpty(warpErr)) Error?.Invoke(warpErr);
            }
            bool hasWarp = xmap != null && ymap != null;

            // 视频输入①（默认）：合成器成品帧，从 stdin 读 BGRA 原始画面。
            //   画面已经是「节目画面」（含切换、图层、字幕、画中画），且与窗口状态无关。
            // 视频输入②（兜底）：抓「播出框」屏幕区域（物理像素），语义与老版本一致。
            if (c.FeedFromSynth)
            {
                sb.Append("-thread_queue_size 1024 -f rawvideo -pix_fmt bgra")
                  .Append(" -s ").Append(c.FeedWidth).Append('x').Append(c.FeedHeight)
                  .Append(" -r ").Append(F(c.Fps))
                  .Append(" -i pipe:0 ");
            }
            else
            {
                sb.Append("-thread_queue_size 1024 -f gdigrab -framerate ").Append(F(c.Fps))
                  .Append(" -draw_mouse 0")
                  .Append(" -offset_x ").Append((int)Math.Round(c.Left))
                  .Append(" -offset_y ").Append((int)Math.Round(c.Top))
                  .Append(" -video_size ").Append((int)Math.Round(c.Width)).Append("x")
                  .Append((int)Math.Round(c.Height))
                  .Append(" -i desktop ");
            }

            // 映射图输入（静态图用 -loop 1 无限循环，长时推流中映射才会一直生效）
            if (hasWarp)
            {
                sb.Append("-loop 1 -framerate ").Append(F(c.Fps)).Append(" -i ").Append(Q(xmap!)).Append(' ');
                sb.Append("-loop 1 -framerate ").Append(F(c.Fps)).Append(" -i ").Append(Q(ymap!)).Append(' ');
            }
            int audioBase = 1 + (hasWarp ? 2 : 0);   // 音频输入的起始索引（映射图占了 1、2）

            // 音频输入①：最多八路 dshow 采集设备（麦克风 / 线路输入 / 采集卡内嵌音频等硬件采集）
            // 老板 2026-10-20：采集路数由四路扩到八路（混音台也是八路，两处要一致）。
            var tracks = (c.AudioTracks ?? new List<AudioChannel>())
                .Where(t => !string.IsNullOrWhiteSpace(t.DeviceName))
                .Take(8).ToList();
            foreach (var t in tracks)
                sb.Append("-thread_queue_size 1024 -f dshow -i audio=").Append(Q(t.DeviceName)).Append(' ');

            // 音频输入②：程序内混音总线（方案 C）。
            // 图层/文件/摄像头的声音被 LibVLC 接管后由 AudioBus 混好，经命名管道实时喂进来 ——
            // 于是"输出框里该出声的那一路"精确进流，预览框与非音源图层一律不会漏音。
            bool hasBus = AudioBus.Instance.PipeWanted && !string.IsNullOrEmpty(AudioBus.Instance.InputUrl);
            if (hasBus)
                sb.Append("-thread_queue_size 4096 -f s16le -ar ").Append(AudioBus.Rate)
                  .Append(" -ac ").Append(AudioBus.Channels)
                  .Append(" -i ").Append(Q(AudioBus.Instance.InputUrl)).Append(' ');
            int busIdx = audioBase + tracks.Count;

            // 滤镜图：视频滤镜链（含分段形变）+ 各路音量 / 混音
            var vg = c.Fx.BuildVideoGraph(c.OutWidth, c.OutHeight, "[0:v]", "[vout]",
                hasWarp ? "[1:v]" : null, hasWarp ? "[2:v]" : null);
            var graph = new List<string> { vg };
            bool hasAudio = tracks.Count > 0 || hasBus;
            if (hasAudio)
            {
                int ai = 0;
                foreach (var t in tracks)
                {
                    // 角色档（主音 0dB / 次音 -8dB / 背景 -14dB）× 滑杆微调，一次算成线性增益
                    double vol = t.Gain;
                    // 逐路压缩（2026-10-24）：先按这一路自己的档位压 + 限幅，再进 amix。
                    // 顺序不能反 —— 混完再压是"总线压缩"那一道，管不了单路突然凑近话筒。
                    string chComp = t.CompressFilter;
                    if (chComp.Length > 0)
                    {
                        graph.Add($"[{audioBase + ai}:a]volume={F(vol)},{chComp}[a{ai}]");
                        AudioBus.Log($"[压缩] 通道#{t.Index} 逐路压缩挂上（{t.CompressLabel}）：{chComp}");
                    }
                    else
                    {
                        graph.Add($"[{audioBase + ai}:a]volume={F(vol)}[a{ai}]");
                    }
                    ai++;
                }
                if (hasBus)
                {
                    // 总线内部已按各图层音量完成混音，这里原样接入
                    graph.Add($"[{busIdx}:a]volume=1[a{ai}]");
                    ai++;
                }
                if (ai == 1)
                {
                    graph.Add("[a0]anull[amixed]");
                }
                else
                {
                    var ins = string.Concat(Enumerable.Range(0, ai).Select(i => $"[a{i}]"));
                    graph.Add($"{ins}amix=inputs={ai}:duration=longest:normalize=0[amixed]");
                }

                // 总闸：压缩 + 限幅（2026-10-23）。排在 amix 之后 = "混完再顶不顶得住"，
                // 关掉时保持原样（anull 直通），不改变老工程的输出。
                string comp = c.Compressor?.FilterChain ?? "";
                if (comp.Length > 0)
                {
                    graph.Add($"[amixed]{comp}[aout]");
                    AudioBus.Log("[压缩] 输出总线挂上：" + comp);
                }
                else
                {
                    graph.Add("[amixed]anull[aout]");
                }
            }
            // 逐路压缩（2026-10-24）：把最终音频图打一条日志 —— 出问题时能一眼看出
            // 「这一路到底压没压、串进去的是哪串参数」，不用去猜。
            if (hasAudio && tracks.Any(t => t.Compress != CompressorLevel.Off))
            {
                AudioBus.Log("[音频图] " + string.Join(";", graph.Skip(1)));
            }

            sb.Append("-filter_complex \"").Append(string.Join(";", graph)).Append("\" ");            sb.Append("-map \"[vout]\" ");
            if (hasAudio) sb.Append("-map \"[aout]\" ");

            // ⚠ 编码参数必须**每个输出各写一遍**（2026-10-22 修的真 bug）：
            //   ffmpeg 的 -c:v / -c:a 是「输出级」选项，只作用于紧跟其后的那一个输出。
            //   原来只在「录制」前面写了一次 → 同时开推流+录制时，录制是 h264/6000k 正常，
            //   推流却退到 flv 容器默认值（flv1 编码 + mp3 128k，整路实测只有 200kbps），
            //   观众看到的是渣画质。因为以前只测过「只录不推」，这个坑一直没暴露。
            //   代价：推流+录制同开时 ffmpeg 会跑两个编码器实例（NVENC 硬编几乎无感，
            //   软件 x264 会多占一份 CPU）—— 用地道换正确，值得。
            var encPart = new StringBuilder();
            var enc = (encoder ?? "nvenc").Trim().ToLowerInvariant();
            switch (enc)
            {
                case "qsv":
                    encPart.Append("-c:v h264_qsv -preset veryfast -b:v ").Append(c.BitrateKbps).Append("k ");
                    break;
                case "software":
                    // 码率跟「输出设置」里选的一致（以前写死 3000k，选多少都被忽略）
                    encPart.Append("-c:v libx264 -preset veryfast -tune zerolatency -b:v ").Append(c.BitrateKbps).Append("k ");
                    break;
                default:
                    encPart.Append("-c:v h264_nvenc -preset p5 -b:v ").Append(c.BitrateKbps).Append("k ")
                           .Append("-maxrate ").Append(c.BitrateKbps * 4 / 3).Append("k -bufsize ")
                           .Append(c.BitrateKbps * 2).Append("k ");
                    break;
            }
            encPart.Append("-pix_fmt yuv420p -g ").Append(Math.Max(30, (int)Math.Round(c.Fps * 2))).Append(' ');

            if (hasAudio)
                encPart.Append("-c:a aac -b:a 128k -ar 44100 -ac 2 ");
            else
                encPart.Append("-an ");

            // 输出分支：录制 + 推流（各自独立，按需拼装）
            if (c.EnableRecord)
            {
                sb.Append(encPart);
                var ext = c.Container.Equals("mp4", StringComparison.OrdinalIgnoreCase) ? "mp4" : "mkv";
                var fmt = ext == "mp4" ? "mp4" : "matroska";
                var stem = $"record_{DateTime.Now:yyyyMMdd_HHmmss}";
                _segFolder = _segStem = null;   // 不分段时不要残留上一次的段信息

                if (c.SegmentSeconds > 0)
                {
                    // 分段录制（老板 2026-10-20：「达到某个容量或时间自动打包并开始录新文件」）。
                    // 关键：不「停掉 ffmpeg 再起一个」—— 那样换段会丢 0.3~1 秒画面。
                    // 用 ffmpeg 自带的 segment 分段器：同一个编码器连续出帧，只把容器按时间切开，
                    // -reset_timestamps 让每段时间戳从 0 开始（单独播放不会跳到几小时后）。
                    // 实测 25fps 下换段零丢帧、音轨连续。
                    var pat = Path.Combine(c.RecordFolder, stem + "_%03d." + ext);
                    LastRecordPath = Path.Combine(c.RecordFolder, stem + "_000." + ext);
                    // 记下这三样，停止后要靠它们找「本次录制自己的」段文件做段尾清理
                    _segFolder = c.RecordFolder;
                    _segStem = stem;
                    _segExt = ext;
                    sb.Append("-f segment -segment_time ").Append(F(c.SegmentSeconds))
                      .Append(" -reset_timestamps 1 -segment_format ").Append(fmt)
                      .Append(' ').Append(Q(pat)).Append(' ');
                }
                else
                {
                    var rec = Path.Combine(c.RecordFolder, stem + "." + ext);
                    LastRecordPath = rec;
                    sb.Append("-f ").Append(fmt).Append(' ').Append(Q(rec)).Append(' ');
                }
            }
            if (c.EnableStream)
            {
                sb.Append(encPart);   // 推流自己也要一份编码参数（见上面的说明）
                sb.Append("-f flv ").Append(Q(c.RtmpUrl)).Append(' ');
            }

            return sb.ToString().TrimEnd();
        }

        // ============ 进程输出处理 ============

        private void OnStderr(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (_errLock)
            {
                _stderrTail.Append(line).Append('\n');
                if (_stderrTail.Length > 6000) _stderrTail.Remove(0, _stderrTail.Length - 6000);
            }
        }

        private void OnExited()
        {
            var cfg = _cfg;
            bool wasStopping = _stopping;
            string tail;
            lock (_errLock) tail = _stderrTail.ToString();

            try { _proc?.Dispose(); } catch { }
            _proc = null;

            AudioBus.Log("FFmpeg 进程退出（wasStopping=" + wasStopping + "）stderr尾：" +
                         (tail.Length > 900 ? tail.Substring(tail.Length - 900) : tail));

            // 段尾碎片清理：必须等 ffmpeg 退出（容器索引已写完）才读得准时长
            if (cfg != null && cfg.SegmentSeconds > 0) CleanupTailSegment();

            if (wasStopping || cfg == null) return;

            var elapsed = DateTime.Now - _startedAt;
            var usedEnc = ActiveEncoder;

            // 音频采集设备打不开（被其它程序占用 / 两路选了同一设备）：给出明确提示，不自动回退
            if (elapsed.TotalSeconds < 8 && LooksLikeAudioFailure(tail))
            {
                Fail("音频采集设备打开失败（可能被其它程序占用，或两路选了同一个设备）：" +
                     LastErrorLine(tail));
                return;
            }

            // 硬编码起不来（NVENC/QSV 不可用）：自动回退软件 x264 继续输出
            if (usedEnc != "software" && elapsed.TotalSeconds < 8 && LooksLikeEncoderFailure(tail))
            {
                Error?.Invoke($"{EncoderLabel(usedEnc)} 硬编码启动失败，已自动切换「软件 x264」继续输出");
                try { Launch("software"); } catch (Exception ex) { Fail("回退软件编码也失败：" + ex.Message); }
                return;
            }

            Fail("输出已中断：" + LastErrorLine(tail));
        }

        /// <summary>
        /// 删掉本次分段录制的「段尾碎片」：段数 ≥2 且最后一段不足 <see cref="TailMinKeepSec"/> 秒才删。
        ///
        /// 为什么按「段数 ≥2」设闸：只录了几百毫秒就停时，第 1 段本身就是全部内容，
        /// 删掉等于「什么都没录」，比留一个短文件更糟。
        /// 读不出时长（容器没写完 / 进程被硬杀）一律保留，只写日志 —— 宁可留着让人自己删。
        /// </summary>
        private void CleanupTailSegment()
        {
            var folder = _segFolder;
            var stem = _segStem;
            var ext = _segExt;
            _segFolder = null; _segStem = null;
            if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(stem)) return;

            Task.Run(() =>
            {
                try
                {
                    var files = Directory.GetFiles(folder, stem + "_*." + ext)
                                         .OrderBy(f => f, StringComparer.Ordinal)
                                         .ToList();
                    if (files.Count < 2)
                    {
                        ErrorLog.Info($"[段尾] 只有 {files.Count} 段，不清理");
                        return;
                    }

                    var tailFile = files[files.Count - 1];
                    var dur = ProbeDurationSec(tailFile);
                    if (dur < 0)
                    {
                        ErrorLog.Info("[段尾] 读不出时长，保留：" + Path.GetFileName(tailFile));
                        return;
                    }
                    if (dur >= TailMinKeepSec)
                    {
                        ErrorLog.Info($"[段尾] 最后一段 {dur:0.00}s ≥ {TailMinKeepSec}s，保留（共 {files.Count} 段）");
                        return;
                    }

                    var name = Path.GetFileName(tailFile);
                    var size = new FileInfo(tailFile).Length;
                    File.Delete(tailFile);
                    ErrorLog.Info($"[段尾] 已删除碎片 {name}（{dur:0.00}s / {size} 字节），保留 {files.Count - 1} 段");
                }
                catch (Exception ex)
                {
                    ErrorLog.Info("[段尾] 清理失败：" + ex.Message);
                }
            });
        }

        /// <summary>用同目录的 ffprobe 读文件时长（秒）；读不出返回 -1。</summary>
        private static double ProbeDurationSec(string file)
        {
            try
            {
                var dir = Path.GetDirectoryName(FfmpegPath);
                if (string.IsNullOrEmpty(dir)) return -1;
                var probe = Path.Combine(dir, "ffprobe.exe");
                if (!File.Exists(probe)) return -1;

                var psi = new ProcessStartInfo(probe)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                foreach (var a in new[] { "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", file })
                    psi.ArgumentList.Add(a);

                using var p = Process.Start(psi);
                if (p == null) return -1;
                var s = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(4000);
                return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : -1;
            }
            catch { return -1; }
        }

        private void Fail(string msg)
        {
            IsRunning = false;
            StartedAt = null;
            Error?.Invoke(msg);
            StatusChanged?.Invoke();
        }

        /// <summary>判断是否属于「音频采集设备打不开」（dshow 独占被占用等）。</summary>
        private static bool LooksLikeAudioFailure(string stderr)
        {
            var s = stderr.ToLowerInvariant();
            return s.Contains("could not open audio") || s.Contains("audio device") ||
                   s.Contains("dshow") || s.Contains("could not find audio") ||
                   s.Contains("already in use") || s.Contains("error opening input");
        }

        private static bool LooksLikeEncoderFailure(string stderr)
        {
            var s = stderr.ToLowerInvariant();
            return s.Contains("nvenc") || s.Contains("nvcuda") || s.Contains("no capable devices") ||
                   s.Contains("cannot load") || s.Contains("qsv") || s.Contains("mfx") ||
                   s.Contains("unknown encoder") || s.Contains("error while opening encoder");
        }

        private static string EncoderLabel(string enc) => enc switch
        {
            "nvenc" => "NVIDIA NVENC",
            "qsv" => "Intel QuickSync",
            "software" => "软件 x264",
            _ => enc
        };

        private static string LastErrorLine(string stderr)
        {
            var lines = stderr.Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 &&
                            !l.Contains("frame=") && !l.Contains("bitrate=") &&
                            !l.StartsWith("Press [q]"))
                .ToList();
            return lines.Count > 0 ? lines[lines.Count - 1] : "详情见 %TEMP%\\Cutlance_vlc.log";
        }

        // ============ 设备枚举 / 抓帧预览 ============

        /// <summary>枚举系统音频采集设备（dshow），供混音台选择。</summary>
        public static List<string> ListAudioDevices()
        {
            var list = new List<string>();
            if (!IsAvailable) return list;
            try
            {
                var psi = new ProcessStartInfo(FfmpegPath,
                    "-hide_banner -list_devices true -f dshow -i dummy")
                {
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                    StandardErrorEncoding = Encoding.UTF8
                };
                using var p = Process.Start(psi);
                if (p == null) return list;
                var err = p.StandardError.ReadToEnd();
                try { p.WaitForExit(10000); } catch { }

                foreach (var raw in err.Split('\n'))
                {
                    var s = raw.Trim();
                    int i = s.IndexOf("] \"", StringComparison.Ordinal);
                    if (i < 0) continue;
                    s = s.Substring(i + 3);
                    if (!s.EndsWith("(audio)")) continue;
                    s = s.Substring(0, s.Length - "(audio)".Length).Trim().Trim('"').Trim();
                    if (s.Length > 0 && !list.Contains(s)) list.Add(s);
                }
            }
            catch { }
            return list;
        }

        /// <summary>
        /// 抓「播出框」当前一帧并套用同一套滤镜，输出 PNG（给画面效果窗口做效果预览）。
        /// </summary>
        public static bool CaptureStill(double x, double y, double w, double h,
            int outW, int outH, VideoFx fx, string outPng, out string error)
        {
            error = "";
            if (!IsAvailable) { error = "找不到 ffmpeg.exe"; return false; }
            if (w <= 1 || h <= 1) { error = "播出框区域无效"; return false; }
            try
            {
                // 分段局部形变同样要带上（remap 需要把映射图作为额外输入喂进来）
                string? xmap = null, ymap = null;
                if (fx.WarpActive)
                {
                    BodyWarp.Ensure(fx, outW, outH, out xmap, out ymap, out var warpErr);
                    if (!string.IsNullOrEmpty(warpErr)) { error = warpErr; return false; }
                }
                bool hasWarp = xmap != null && ymap != null;

                var graph = fx.BuildVideoGraph(outW, outH, "[0:v]", "[v]",
                    hasWarp ? "[1:v]" : null, hasWarp ? "[2:v]" : null);
                var args = $"-hide_banner -loglevel error -f gdigrab -framerate 30 -draw_mouse 0 " +
                           $"-offset_x {(int)Math.Round(x)} -offset_y {(int)Math.Round(y)} " +
                           $"-video_size {(int)Math.Round(w)}x{(int)Math.Round(h)} -i desktop ";
                if (hasWarp)
                    args += $"-loop 1 -framerate 30 -i {Q(xmap!)} -loop 1 -framerate 30 -i {Q(ymap!)} ";
                args += $"-filter_complex \"{graph}\" -map \"[v]\" -frames:v 1 -update 1 -y {Q(outPng)}";
                var psi = new ProcessStartInfo(FfmpegPath, args)
                {
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                    StandardErrorEncoding = Encoding.UTF8
                };
                using var p = Process.Start(psi);
                if (p == null) { error = "无法启动 ffmpeg"; return false; }
                var err = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(20000)) { try { p.Kill(true); } catch { } error = "抓帧超时"; return false; }
                if (p.ExitCode != 0 || !File.Exists(outPng))
                {
                    error = err.Trim();
                    if (error.Length == 0) error = "ffmpeg 退出码 " + p.ExitCode;
                    return false;
                }
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        /// <summary>
        /// 自动探测 ffmpeg.exe：环境变量 → 常见安装目录 → PATH。
        /// 开源版不内置 ffmpeg 二进制（体积与许可证原因），请自行安装并把 bin 加进 PATH，
        /// 或用环境变量 CUTLANCE_FFMPEG 直接指向 ffmpeg.exe（或它所在目录）。
        /// </summary>
        private static string DetectFfmpeg()
        {
            // ① 显式指定最优先：CUTLANCE_FFMPEG 可以是 exe 路径，也可以是所在目录
            try
            {
                var env = Environment.GetEnvironmentVariable("CUTLANCE_FFMPEG");
                if (!string.IsNullOrWhiteSpace(env))
                {
                    if (File.Exists(env)) return env;
                    var inDir = Path.Combine(env, "ffmpeg.exe");
                    if (File.Exists(inDir)) return inDir;
                }
            }
            catch { }

            var cands = new List<string>
            {
                @"C:\FFMPEG\bin\ffmpeg.exe",
                @"C:\ffmpeg\bin\ffmpeg.exe",
                @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
                @"D:\FFMPEG\bin\ffmpeg.exe",
                @"E:\FFMPEG\bin\ffmpeg.exe"
            };
            try
            {
                var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var dir in pathVar.Split(';'))
                {
                    var d = dir.Trim();
                    if (d.Length == 0) continue;
                    try { cands.Add(Path.Combine(d, "ffmpeg.exe")); } catch { }
                }
            }
            catch { }

            foreach (var c in cands)
            {
                try { if (File.Exists(c)) return c; } catch { }
            }
            return "ffmpeg.exe";   // 兜底：交给 PATH 解析
        }
    }
}
