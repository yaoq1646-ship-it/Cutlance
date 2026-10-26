using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using LibVLCSharp.Shared;
using Cutlance.Models;
using Cutlance.Services.Audio;

namespace Cutlance.Services
{
    /// <summary>
    /// 模块4：RTMP 推流 + 本地录制（对标 vMix 输出总线）。
    ///
    /// ── 视频源（2026-10-20 改）─────────────────────────────────────────────
    ///   以前：叠加层画在 WPF 画布上、视频是 VLC 的原生子窗口，内存里根本没有「成品画面」，
    ///         所以只能抓「输出监视器」那块屏幕区域 —— 窗口一挡就断流。
    ///   现在：<see cref="ProgramFrames"/>（合成器）直接供帧，推流/录制/NDI 的像素与
    ///         屏幕上看到的完全同源，且跟窗口是否可见无关。
    ///         合成器起不来时才回落抓屏（参数 left/top/width/height 就是那时的屏幕矩形）。
    ///
    /// 音频：八路 dshow 采集 + 程序内混音总线（AudioBus，TCP 回环喂给 ffmpeg），两条路一致。
    /// </summary>
    public sealed class StreamService
    {
        public static StreamService Instance { get; } = new();
        private StreamService()
        {
            // FFmpeg 引擎的状态/错误转发给界面（与 LibVLC 路径同一套事件）
            FfmpegOutput.Instance.StatusChanged += () => StatusChanged?.Invoke();
            FfmpegOutput.Instance.Error += m => Error?.Invoke(m);
        }

        private readonly LibVLC _libvlc = VideoEngine.Instance.LibVLC;
        private MediaPlayer? _player;
        private Media? _media;

        // —— 配置（默认值按老板拍板：默认占位 RTMP、mkv 录制、带麦克风）——
        public string RtmpUrl { get; set; } = "rtmp://localhost:1935/live/stream";
        public string RecordFolder { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Cutlance");
        public bool EnableStream { get; set; } = true;
        public bool EnableRecord { get; set; } = true;
        public bool IncludeMic { get; set; } = true;

        // —— 模块F：NDI 输出 ——
        // 和推流/录制的关系：**同一个源（播出框），三条独立的输出**。
        // 可以只开 NDI、也可以 RTMP + 录制 + NDI 一起开（NDI 走自己的抓屏进程，互不挤占）。
        /// <summary>是否把节目画面同时广播成一个 NDI 源（供局域网里别的软件/机器取用）。</summary>
        public bool EnableNdi { get; set; }

        /// <summary>NDI 输出源名（空 = 默认 "Cutlance Output (主机名)"）。</summary>
        public string NdiSourceName { get; set; } = "";

        /// <summary>NDI 输出帧率（0 = 跟随 Fps）。</summary>
        public int NdiFps { get; set; }

        /// <summary>
        /// 局域网输出协议（模块F）。
        /// 当前只有 NDI 有实现；OMT 是**预留档** —— 见 Models/OutputTransportKind 的说明
        /// （MIT 开源、vMix 29 原生支持的 NDI 替代，官方参考实现就是 C#）。
        /// 选到 Omt 时 NdiOutputService 会明确报「尚未实现」，不会静默什么都不做。
        /// </summary>
        public OutputTransportKind OutputTransport { get; set; } = OutputTransportKind.Ndi;

        // —— 输出引擎（模块8）——
        //   "ffmpeg"：默认。支持八路音频混音、调色/美颜/美体、NVENC 硬编、硬编失败自动回退。
        //   "vlc"   ：兼容模式，沿用 LibVLC 抓屏 + sout（单路麦克风，无混音/美颜）。
        public string OutputEngine { get; set; } = "ffmpeg";

        /// <summary>录制容器：mkv（默认，中断不易坏文件）或 mp4。</summary>
        public string Container { get; set; } = "mkv";

        /// <summary>
        /// 分段录制：每 N 秒自动封包并开始录下一个文件（0 = 不分段，一直录到一个文件）。
        ///
        /// 老板 2026-10-20 的原话是「达到某个容量或时间自动打包并开始录制新文件」。
        /// 注意语义是「**换新文件继续录**」，不是「到点停」—— 长会/长课时单文件几十 GB，
        /// 拷贝、上传、断电全都不友好，切成小时段才可管理。
        /// 实现用 ffmpeg 的 segment 分段器（同进程连续编码），换段不丢帧不断音。
        ///
        /// 为什么只给时间档、不按容量：ffmpeg 8.x 已移除 segment 的按大小切分
        /// （只有 segment_size 是 MPEG-TS 专用且不适用于 mp4/mkv），按容量只能拿码率折算，
        /// 实测误差 ±10%，写成 UI 反而误导人。时间档是精确的。
        /// </summary>
        public double RecordSegmentSeconds { get; set; }

        /// <summary>
        /// 节目成品帧源（合成器）。非空时推流/录制/NDI 直接从它取帧，不再抓屏；
        /// 为空才退回老 gdigrab 抓「播出框」（合成器起不来时的兜底）。
        /// </summary>
        public IProgramFrames? ProgramFrames { get; set; }

        // 挂到帧源上的委托必须缓存：每次取方法组都会造新委托，
        // 摘的时候如果现取现传，就可能因为不是同一个对象而摘不掉（导致停止后还往死管道塞帧）。
        private Action<IntPtr, int>? _frameSink;

        /// <summary>
        /// 输出帧率 —— 合成器按这个出帧，ffmpeg 按这个打时间戳（<c>-r</c>）。
        /// ⚠ 这两处**必须是同一个值**：以前合成器读它、编码却写死 30，
        /// 设 25 时 300 帧被按 30fps 打时间戳 → 12 秒录成 10 秒、音画差 2 秒（2026-10-21 实测）。
        /// double 是为了 29.97 / 59.94 这两档 PAL/NTSC 值。
        /// </summary>
        public double Fps { get; set; } = 30;

        /// <summary>
        /// 切换台（节目合成器）的帧率；0 = 跟随 <see cref="Fps"/>。
        /// 为什么单独存一份：<see cref="Fps"/> 是 int（喂 ffmpeg 的 -r），装不下 29.97 / 59.94，
        /// 而切换台下拉里有这两个 PAL/NTSC 档。以前它只改窗口内局部字段，
        /// 关窗再开就变回 30 —— 老板 2026-10-21 要求「写回工程」。
        /// </summary>
        public double SynthFps { get; set; }

        /// <summary>视频码率（kbps；NVENC 默认 6000，软件 x264 固定 3000）。</summary>
        public int BitrateKbps { get; set; } = 6000;

        /// <summary>
        /// 八路音频混音通道（统一进推流 + 录制）。
        /// 2026-10-16 老板要求由三路增加到四路；2026-10-20 又要求「采集卡从三路增加到八路」，
        /// 于是扩到八路 —— 采集卡多机位、多支话筒、HDMI 内嵌音频都能各占一路。
        /// ⚠ 改这个列表长度时，FfmpegOutput 里的 dshow 输入上限（现在也是 8）要一起看。
        /// </summary>
        public List<AudioChannel> AudioChannels { get; } = new()
        {
            new AudioChannel { Index = 1 },
            new AudioChannel { Index = 2 },
            new AudioChannel { Index = 3 },
            new AudioChannel { Index = 4 },
            new AudioChannel { Index = 5 },
            new AudioChannel { Index = 6 },
            new AudioChannel { Index = 7 },
            new AudioChannel { Index = 8 }
        };

        /// <summary>画面效果：调色 / 美白 / 磨皮 / 瘦身 / 拉长（仅编码器 = NVENC 时启用）。</summary>
        public VideoFx Fx { get; } = new();

        /// <summary>
        /// 输出总线压缩器 + 限幅（2026-10-23）。
        /// 放在这里而不是混音台窗口：命令行拼装、工程存取、布局预设都读同一个对象，
        /// 免得"窗口关了设置就丢"（老代码里混音参数就是靠服务对象活着的）。
        /// ⚠ 它是 ffmpeg 启动参数的一部分 → 改了要「重启输出」才生效。
        /// </summary>
        public AudioCompressor Compressor { get; } = new();


        private bool _useFfmpeg;                  // 本次输出走哪条引擎
        private bool _vlcRunning;
        private DateTime? _vlcStartedAt;
        private string? _lastRecordPath;

        public bool IsRunning => (_useFfmpeg ? FfmpegOutput.Instance.IsRunning : _vlcRunning)
                               || NdiOutputService.Instance.IsRunning;
        public DateTime? StartedAt => _useFfmpeg ? FfmpegOutput.Instance.StartedAt : _vlcStartedAt;
        public string? LastRecordPath => _useFfmpeg ? FfmpegOutput.Instance.LastRecordPath : _lastRecordPath;

        /// <summary>本次实际使用的编码器（硬编失败回退后为 software）。</summary>
        public string ActiveEncoder => _useFfmpeg ? FfmpegOutput.Instance.ActiveEncoder : Encoder;

        /// <summary>当前是否走 FFmpeg 引擎。</summary>
        public bool UsingFfmpeg => _useFfmpeg;

        // —— 输出分辨率设定（模块7：1920x1080 横屏 / 1080x1920 竖屏）——
        // 预览框/播出框按该宽高比约束显示，推流/录制转码锁定到该分辨率。
        private int _outW = 1920, _outH = 1080;
        public int OutputWidth
        {
            get => _outW;
            set { if (_outW != value) { _outW = value; OutputGeometryChanged?.Invoke(); } }
        }
        public int OutputHeight
        {
            get => _outH;
            set { if (_outH != value) { _outH = value; OutputGeometryChanged?.Invoke(); } }
        }

        /// <summary>输出分辨率变化通知（UI 据此重排预览/播出框比例）。</summary>
        public event Action? OutputGeometryChanged;

        /// <summary>
        /// 推流/录制用的视频编码器（默认走英伟达显卡硬编码 NVENC）。
        ///   "nvenc"    → NVIDIA NVENC 显卡硬编码（默认，大幅降 CPU）
        ///   "qsv"      → Intel QuickSync 核显硬编码
        ///   "software" → libx264 纯软件（最兼容，无 N 卡时用）
        /// </summary>
        public string Encoder { get; set; } = "nvenc";

        public event Action? StatusChanged;
        public event Action<string>? Error;

        /// <summary>
        /// 开始推流/录制。left/top/width/height 为「播出框 / 输出监视器」在屏幕上的物理像素区域。
        /// 默认走 FFmpeg 引擎（四路混音 + 画效 + NVENC）；OutputEngine="vlc" 或找不到 ffmpeg 时退回兼容模式。
        /// </summary>
        public void Start(double left, double top, double width, double height, double fps = 0)
        {
            if (IsRunning) return;

            // ── 两条引擎共用的前置检查（2026-10-22 统一）──────────────────────
            // 以前只有 VLC 那条分支会校验"至少开一项"，ffmpeg 分支漏了：
            // 推流录制都没勾却点了开始，会真的拉起一个空管道，状态栏显示"正在输出"、
            // 实际什么都没出去。检查提到入口，两条路行为一致。
            if (!EnableStream && !EnableRecord && !EnableNdi)
            {
                Error?.Invoke("请至少启用「推流」「录制」「NDI 输出」之一");
                return;
            }

            // 录制目录也统一在这里建/校验：两边原来各建一次，报错文案还不一样。
            if (EnableRecord)
            {
                if (string.IsNullOrWhiteSpace(RecordFolder))
                {
                    Error?.Invoke("录制目录为空：请先在输出设置里选一个存放位置");
                    return;
                }
                try { Directory.CreateDirectory(RecordFolder); }
                catch (Exception ex)
                {
                    Error?.Invoke("录制目录不可写（" + RecordFolder + "）：" + ex.Message);
                    return;
                }
            }

            double useFps = fps > 0 ? fps : Fps;

            // ---- NDI 输出（模块F）----
            // 与推流/录制**同源**（同一份成品帧），但各自独立成一路：互不挤占、互不拖累。
            // 放在前面是为了支持「只开 NDI、不推流不录制」这种用法。
            if (EnableNdi)
            {
                NdiOutputService.Instance.Start(new NdiOutputConfig
                {
                    Left = left, Top = top, Width = width, Height = height,
                    // NDI 走整数帧率（发送器只认整档），29.97 这类就近取整
                    Fps = NdiFps > 0 ? NdiFps : (int)Math.Round(useFps),
                    SourceName = NdiSourceName,
                    Transport = OutputTransport
                }, ProgramFrames, out var nerr);
                if (!string.IsNullOrEmpty(nerr)) Error?.Invoke("NDI 输出未开启：" + nerr);
                StatusChanged?.Invoke();

                // 只勾了 NDI → 到此为止（不需要 RTMP / 录制）
                if (!EnableStream && !EnableRecord) return;
            }

            bool wantFfmpeg = !OutputEngine.Equals("vlc", StringComparison.OrdinalIgnoreCase);
            if (wantFfmpeg && !FfmpegOutput.IsAvailable)
            {
                Error?.Invoke("未找到 ffmpeg.exe，已退回 LibVLC 兼容模式（不支持四路混音与美颜美体）");
                wantFfmpeg = false;
            }

            if (wantFfmpeg)
            {
                _useFfmpeg = true;
                // 方案 C：先把音频总线的 TCP 回环准备好，FFmpeg 启动时才能接上程序内混音
                AudioBus.Instance.StartPipe();

                var frames = ProgramFrames;
                bool feed = frames != null && frames.Width > 1 && frames.Height > 1;

                FfmpegOutput.Instance.Start(new FfmpegOutputConfig
                {
                    Left = left, Top = top, Width = width, Height = height,
                    OutWidth = _outW, OutHeight = _outH,
                    Fps = fps > 0 ? fps : Fps,
                    Encoder = Encoder,
                    BitrateKbps = BitrateKbps,
                    EnableStream = EnableStream,
                    EnableRecord = EnableRecord,
                    RtmpUrl = RtmpUrl,
                    RecordFolder = RecordFolder,
                    Container = Container,
                    AudioTracks = AudioChannels,
                    Compressor = Compressor,
                    Fx = Fx,
                    FeedFromSynth = feed,
                    FeedWidth = feed ? frames!.Width : 0,
                    FeedHeight = feed ? frames!.Height : 0,
                    SegmentSeconds = RecordSegmentSeconds
                });

                // 起来之后再挂帧源；启动失败就别挂（否则一直往不存在的管道拷帧）
                if (feed && FfmpegOutput.Instance.IsRunning)
                {
                    _frameSink ??= FfmpegOutput.Instance.FeedFrame;
                    frames!.AddSink(_frameSink);
                }
                return;
            }

            _useFfmpeg = false;
            StartVlc(left, top, width, height, fps > 0 ? fps : Fps);
        }

        /// <summary>LibVLC 兼容模式：抓屏 + sout 转码推流/录制（单路麦克风，无混音与美颜美体）。</summary>
        /// <summary>
        /// LibVLC 兼容模式：抓屏 + sout 转码推流/录制。
        ///
        /// ── 定位（2026-10-22 明确）──────────────────────────────────────────
        /// 这条分支**不是历史包袱**，而是「这台机器上没有 ffmpeg.exe」时的唯一出路：
        /// ffmpeg 有 227MB，没有打进安装包，所以必须留着这条兜底。
        /// 与主流（ffmpeg + 合成器供帧）的差距是写死的，不要假装能对齐：
        ///   · 抓的是播出框那片屏幕 → 主窗被盖住 / 最小化时画面会不对
        ///   · 单路麦克风，没有四路混音
        ///   · 没有美颜美体（画效在合成器里，这条路拿不到）
        ///   · 容器固定 mkv，不支持分段录制
        /// 用户能看出"降级了"，所以启动时会在状态栏明确提示走的是兼容模式。
        /// </summary>
        private void StartVlc(double left, double top, double width, double height, double fps)
        {
            // 参数校验已提到 Start() 入口统一做（两条引擎一致），这里只管"区域有没有效"
            if (width <= 0 || height <= 0)
            {
                Error?.Invoke("输出监视器区域无效（宽高须 > 0）");
                return;
            }

            var media = new Media(_libvlc, "screen://", FromType.FromLocation);
            media.AddOption($":screen-left={left:0}");
            media.AddOption($":screen-top={top:0}");
            media.AddOption($":screen-width={width:0}");
            media.AddOption($":screen-height={height:0}");
            media.AddOption($":screen-fps={fps}");
            media.AddOption(":screen-caching=100");

            // 屏幕源为原始帧，必须转码成 H264（+AAC）才能推流/封装 mkv
            var acodec = IncludeMic ? "aac" : "none";
            var audioOpts = IncludeMic ? ",ab=128,channels=2,samplerate=44100" : "";

            // 视频编码器：默认软件 x264；N 卡用 NVENC、Intel 用 QuickSync 走显卡硬编码
            var enc = Encoder.Trim().ToLowerInvariant();
            var venc = enc switch
            {
                "nvenc" => "venc=nvenc,",
                "qsv" => "venc=qsv,",
                _ => ""   // 软件编码（libx264）
            };
            // 码率跟「输出设置」里选的一致（以前写死 4000/2500，选什么都被忽略）
            var vb = BitrateKbps;
            // 锁定输出分辨率为设定值（1920x1080 / 1080x1920）；源区域已按同比例约束，缩放不失真
            var transcode = $"#transcode{{vcodec=h264,{venc}vb={vb},width={_outW},height={_outH},acodec={acodec}{audioOpts}}}";

            // 输出分支（推流 / 录制，按需拼装）
            var dsts = new StringBuilder();
            if (EnableStream)
            {
                // 占位默认地址即可；RTMP URL 内含 :// ，放在 rtmp{ dst=... } 内由 VLC 解析
                var url = RtmpUrl;
                dsts.Append($"dst=rtmp{{dst={url}}},");
            }
            if (EnableRecord)
            {
                var rec = Path.Combine(RecordFolder, $"record_{DateTime.Now:yyyyMMdd_HHmmss}.mkv");
                // Windows 路径反斜杠在 sout 里易被判为转义，统一转正斜杠
                rec = rec.Replace('\\', '/');
                _lastRecordPath = rec;
                dsts.Append($"dst=std{{access=file,mux=mkv,dst={rec}}},");
            }

            var sout = transcode + ":duplicate{" + dsts.ToString().TrimEnd(',') + "}";
            media.AddOption($":sout={sout}");

            // 麦克风：屏幕源本身无音频，用 dshow 从默认音频设备补声（仅启用时）
            if (IncludeMic)
            {
                media.AddOption(":input-slave=dshow://");
                media.AddOption(":dshow-vdev=none");   // 只要声音，不要视频设备
                media.AddOption(":dshow-adev=");       // 空 = 默认音频设备
            }

            _media = media;
            _player = new MediaPlayer(_libvlc);
            _player.EncounteredError += OnPlayerError;
            _player.Play(media);

            _vlcRunning = true;
            _vlcStartedAt = DateTime.Now;
            StatusChanged?.Invoke();
        }

        /// <summary>停止输出（按本次实际使用的引擎分别处理）。</summary>
        public void Stop()
        {
            // NDI 输出是自己那个进程，无论本次走哪条引擎都要一起收掉
            try { NdiOutputService.Instance.Stop(); } catch { }

            if (_useFfmpeg)
            {
                // 顺序要紧：先把帧源摘掉，再停 ffmpeg —— 否则停止过程中合成线程还在往
                // 即将关闭的 stdin 塞 8MB 帧，写线程会抛异常、编码器收尾也会被拖慢。
                if (_frameSink != null && ProgramFrames != null)
                {
                    try { ProgramFrames.RemoveSink(_frameSink); } catch { }
                }
                FfmpegOutput.Instance.Stop();
                AudioBus.Instance.StopPipe();
                return;
            }
            StopVlc();
        }

        private void StopVlc()
        {
            if (!_vlcRunning) return;

            // 先通知 UI 状态切换（早于释放，避免竞态与高亮残留）
            _vlcRunning = false;
            _vlcStartedAt = null;
            StatusChanged?.Invoke();

            // 解绑事件并清空引用；真正的释放放到后台线程，规避 UI 线程上
            // 非托管释放竞态（屏幕捕获 + transcode + 推流时 Stop/Dispose 可能抛 AV）
            var p = _player;
            var m = _media;
            _player = null;
            _media = null;
            if (p != null) p.EncounteredError -= OnPlayerError;

            _ = Task.Run(() =>
            {
                try { p?.Stop(); } catch { }
                try { m?.Dispose(); } catch { }
                try { p?.Dispose(); } catch { }
            });
        }

        private void OnPlayerError(object? sender, EventArgs e)
        {
            Error?.Invoke("推流/录制出错，详见 %TEMP%\\Cutlance_vlc.log");
        }
    }
}
