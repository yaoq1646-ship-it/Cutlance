using System;
using System.IO;
using System.Linq;
using LibVLCSharp.Shared;
using Cutlance.Models;
using Cutlance.Services.Audio;

namespace Cutlance.Services
{
    /// <summary>
    /// LibVLC 核心引擎（模块1 + 模块2）。
    /// 三路播放器：
    ///   - MediaPlayer     ：PROGRAM 总线（直播输出）
    ///   - PreviewPlayer   ：PREVIEW 总线（导播待切预览）
    ///   - TransitionPlayer：转场覆盖层（AlphaFade 交叉溶解专用）
    /// 后续模块（多路预览/推流/录制）在基础上扩展。
    /// </summary>
    public sealed class VideoEngine : IDisposable
    {
        private static VideoEngine? _instance;
        public static VideoEngine Instance => _instance ??= new VideoEngine();

        private static readonly string VlcLogPath =
            Path.Combine(Path.GetTempPath(), "Cutlance_vlc.log");

        public LibVLC LibVLC { get; }
        public MediaPlayer MediaPlayer { get; }        // PROGRAM
        public MediaPlayer PreviewPlayer { get; }      // PREVIEW
        public MediaPlayer TransitionPlayer { get; }   // 转场覆盖层

        /// <summary>输出图层上限（画中画/分屏同时活跃的图层数）。</summary>
        public int MaxLayers { get; } = 9;

        /// <summary>
        /// 硬件解码（显卡解码）总开关。默认 true，让所有播放器走 DXVA/D3D11 等
        /// GPU 解码路径，降低 CPU 占用。个别机器硬解会黑屏，可在「输出设置」里关掉。
        /// 设为 true/false 会即时作用于所有已注册播放器（下次播放/重载生效）。
        /// </summary>
        public bool HardwareDecoding { get; private set; } = true;

        private static readonly System.Collections.Generic.List<MediaPlayer> _allPlayers = new();
        private static readonly object _playersLock = new();

        /// <summary>
        /// 为单个输出图层创建专属 MediaPlayer（沿用全局硬件解码策略）。
        /// trackKey：该图层在音频总线里的轨道名（形如 "layer:3"）；创建即接管其音频，
        /// 是否出声由 AudioBus 决定（默认静音，被选为音源时才打开）。
        /// </summary>
        public MediaPlayer CreateLayerPlayer(string trackKey)
        {
            var p = new MediaPlayer(LibVLC);
            p.EnableHardwareDecoding = HardwareDecoding;
            RegisterPlayer(p);
            var t = AudioBus.Instance.EnsureTrack(trackKey);
            t.Mute = true; t.ToSoundCard = false; t.ToStream = false;   // 默认不出声
            VlcAudioTap.Attach(p, t);
            return p;
        }


        private static void RegisterPlayer(MediaPlayer p)
        {
            lock (_playersLock) _allPlayers.Add(p);
        }

        /// <summary>图层销毁时反注册，避免列表里残留已释放的播放器。</summary>
        public static void UnregisterPlayer(MediaPlayer p)
        {
            lock (_playersLock) _allPlayers.Remove(p);
        }

        /// <summary>切换硬件解码并即时应用到全部播放器（下次播放/重载生效）。</summary>
        public void SetHardwareDecoding(bool on)
        {
            HardwareDecoding = on;
            lock (_playersLock)
            {
                foreach (var p in _allPlayers.ToList())
                {
                    try { p.EnableHardwareDecoding = on; }
                    catch { _allPlayers.Remove(p); }
                }
            }
        }

        private VideoEngine()
        {
            Core.Initialize();

            LibVLC = new LibVLC(enableDebugLogs: false, "--no-video-title-show");
            LibVLC.Log += (s, e) =>
            {
                if (e.Level == LogLevel.Error || e.Level == LogLevel.Warning)
                {
                    try { File.AppendAllText(VlcLogPath, $"[{e.Level}] {e.Message}\n"); } catch { }
                }
            };

            MediaPlayer = new MediaPlayer(LibVLC);
            PreviewPlayer = new MediaPlayer(LibVLC);
            TransitionPlayer = new MediaPlayer(LibVLC);
            // 默认开启硬件解码（显卡解码，降 CPU）；个别机器若黑屏可在设置里关闭
            foreach (var p in new[] { MediaPlayer, PreviewPlayer, TransitionPlayer })
            {
                p.EnableHardwareDecoding = HardwareDecoding;
                RegisterPlayer(p);
            }

            // ===== 方案 C：所有播放器的音频统一交给 AudioBus，彻底取消 LibVLC 直出 =====
            // 以前 LibVLC 会把解码音频直接送声卡，程序管不住 → 出现「预览框也出声」
            // 「非音源图层也出声」。装上 audio callbacks 后，音频全部落进总线，
            // 谁出声、进不进声卡、进不进推流，完全由程序决定。
            AudioBus.Instance.EnsureStarted();
            var programTrack = AudioBus.Instance.EnsureTrack("program");
            var transitionTrack = AudioBus.Instance.EnsureTrack("transition");
            var previewTrack = AudioBus.Instance.EnsureTrack("preview");
            // 预览轨永远不出声（预览框只做画面监看）
            previewTrack.Mute = true; previewTrack.ToSoundCard = false; previewTrack.ToStream = false;
            programTrack.Mute = true; programTrack.ToSoundCard = false; programTrack.ToStream = false;
            transitionTrack.Mute = true; transitionTrack.ToSoundCard = false; transitionTrack.ToStream = false;

            VlcAudioTap.Attach(MediaPlayer, programTrack);
            VlcAudioTap.Attach(PreviewPlayer, previewTrack);
            VlcAudioTap.Attach(TransitionPlayer, transitionTrack);
        }

        /// <summary>
        /// 按输入源类型构造一路媒体（文件 / 摄像头 / 桌面 / 网络流）。
        ///
        /// 模块C（输入级裁切 / 旋转 / 镜像 / 后续的抠像）走这里分流：
        /// 需要画面处理的输入，改播「预处理层（常驻 ffmpeg）」产出的本地组播流，
        /// 处理结果因此对「所有播放该输入的地方」——预览框、输出图层、播放列表——统一生效，
        /// 这就是「输入级」的含义。
        ///
        /// 预处理起不来时**不阻断播放**：回落到原始画面，由预处理层通过 Error 事件说明原因。
        /// </summary>
        public Media CreateMedia(InputSource src)
        {
            if (InputPreprocessService.NeedsPreprocess(src)
                && InputPreprocessService.Instance.TryGetStreamUrl(src, out var url, out _))
            {
                var live = new Media(LibVLC, url, FromType.FromLocation);
                ApplyLiveCaching(live, src.CamLowLatency);
                return live;
            }

            return src.Type switch
            {
                SourceType.File => CreateFileMedia(src),
                SourceType.Camera => CreateCameraMedia(src),
                SourceType.Desktop => CreateDesktopMedia(src),
                SourceType.Network => CreateNetworkMedia(src),
                _ => throw new InvalidOperationException("未知输入源类型：" + src.Type)
            };
        }

        /// <summary>静态图片扩展名（不含 gif：动图要靠 input-repeat 循环，不能用 image-duration 处理）。</summary>
        private static readonly string[] StillImageExts =
            { ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".tif", ".tiff" };

        /// <summary>
        /// 文件输入的媒体（视频 / 音频 / 静态图片）。
        ///
        /// ⚠ 静态图片必须显式设 `:image-duration=-1`：VLC 对图片的默认显示时长是 **10 秒**，
        ///   到点就当"播完"停下（触发 EndReached）—— 导播时拿图片当背景板 / 挂机画面，
        ///   会毫无征兆地黑掉；老板实测用美女图做美体测试时正是这个坑。
        ///   -1 = 一直显示，与 vMix 的图片输入行为一致。
        ///   放在这里（CreateMedia 的唯一分叉点）所以预览框、输出图层、播放列表统一生效。
        /// </summary>
        private static Media CreateFileMedia(InputSource src)
        {
            var media = new Media(VideoEngine.Instance.LibVLC, src.Location, FromType.FromPath);
            try
            {
                var ext = System.IO.Path.GetExtension(src.Location ?? "")?.ToLowerInvariant() ?? "";
                if (Array.IndexOf(StillImageExts, ext) >= 0)
                    media.AddOption(":image-duration=-1");
            }
            catch { /* 取扩展名失败就当普通文件，不影响播放 */ }
            return media;
        }

        /// <summary>
        /// 网络流的 VLC 直连媒体（模块D）。
        ///
        /// 只在用户显式选「VLC 直连」链路时走到这儿 —— 默认链路是预处理（见 InputPreprocessService），
        /// 走那条路时播的是本地组播，媒体构造在上面的 TryGetStreamUrl 分支里就完成了。
        ///
        /// 地址必须用 NetworkStreamService.BuildUrl：SRT 的 mode/latency/passphrase 是 URL query，
        /// 直接拿 Location 会把这些设置丢掉（表现为「设了 latency 却毫无变化」）。
        /// </summary>
        private static Media CreateNetworkMedia(InputSource src)
        {
            var media = new Media(VideoEngine.Instance.LibVLC,
                                  NetworkStreamService.BuildUrl(src), FromType.FromLocation);
            NetworkStreamService.ApplyVlcOptions(media, src);
            // 这是**真网络**（不是本机回环组播），缓冲要给得比回环宽一些才扛得住抖动
            ApplyLiveCaching(media, src.CamLowLatency, isRealNetwork: true);
            // 去隔行直连时只能寄望 VLC 自己（与采集卡直连同一个坑，见 CreateCameraMedia 注释）
            if (src.NeedDeinterlace) media.AddOption(":deinterlace=1");
            return media;
        }

        /// <summary>
        /// 直播流缓冲档（模块E B-2 低延迟档，模块D 网络流复用）。
        ///
        /// 默认档（标准）：本机回环组播给 120ms 缓冲就够 —— 给大了切换镜头会明显滞后。
        ///   ⚠ 但**真网络**（网络流直连）不能照搬 120ms：那是跨网段的真实链路，
        ///   抖动与乱序比回环大一个量级，给 120ms 会表现为频繁花屏。所以真网络默认给 300ms。
        /// 低延迟档：压到 60ms，并把 VLC 的「时钟抖动补偿」和「时钟同步」都关掉。
        ///   这两个才是直播延迟的大头：VLC 默认会为每路流预留最长 5 秒的抖动窗口来等
        ///   网络乱序包，音频/视频时钟还要互相收敛；回环组播根本不需要这些。
        ///   代价：真出现抖动时没有余量，会直接表现为花屏/丢帧。
        /// </summary>
        private static void ApplyLiveCaching(Media m, bool lowLatency, bool isRealNetwork = false)
        {
            if (lowLatency)
            {
                m.AddOption(":network-caching=60");
                m.AddOption(":live-caching=60");
                m.AddOption(":clock-jitter=0");
                m.AddOption(":clock-synchro=0");
            }
            else
            {
                var ms = isRealNetwork ? 300 : 120;
                m.AddOption($":network-caching={ms}");
                m.AddOption($":live-caching={ms}");
            }
        }

        /// <summary>按「摄像头采集设置」构造 dshow 媒体（分辨率/帧率/格式/音频均可指定）。</summary>
        private static Media CreateCameraMedia(InputSource src)
        {
            var media = new Media(VideoEngine.Instance.LibVLC, "dshow://", FromType.FromLocation);
            if (!string.IsNullOrWhiteSpace(src.Location))
                media.AddOption($":dshow-vdev={src.Location}");
            if (src.CamWidth > 0 && src.CamHeight > 0)
                media.AddOption($":dshow-size={src.CamWidth}x{src.CamHeight}");
            if (src.CamFps > 0)
                media.AddOption($":dshow-fps={src.CamFps}");
            if (!string.IsNullOrWhiteSpace(src.CamChroma))
                media.AddOption($":dshow-chroma={src.CamChroma}");
            // 音频：① 关闭 → 显式 none；② 指定了设备名 → 点名设备
            //       （采集卡的 HDMI 内嵌音频必须点名，否则会抓到系统默认麦克风）；
            //       ③ 都没指定 → 留空 = 系统默认音频设备（与旧行为一致）。
            if (!src.CamAudio)
                media.AddOption(":dshow-adev=none");
            else if (!string.IsNullOrWhiteSpace(src.CamAudioDevice))
                media.AddOption($":dshow-adev={src.CamAudioDevice}");
            else
                media.AddOption(":dshow-adev=");
            // 缓冲档：直连没有采集侧缓冲可调（VLC 的 dshow 模块不暴露 rtbufsize），
            // 只能调输入缓存。标准 300ms（够吸收 USB 抖动），低延迟 60ms。
            media.AddOption(src.CamLowLatency ? ":live-caching=60" : ":live-caching=300");

            // 去隔行（模块E B-2）：只对直连链路写在这里。
            // ⚠ 这是**尽力而为**：VLC 的去隔行滤镜在 3.0 上偏实例级，媒体级选项未必生效
            //   （项目里已实测过 --video-filter 只认实例级）。真正的去隔行靠 ffmpeg 预处理链路，
            //   面板在检测到隔行信号时会提示优先用那条链路。
            if (src.NeedDeinterlace) media.AddOption(":deinterlace=1");
            return media;
        }

        private static Media CreateDesktopMedia(InputSource src)
        {
            var media = new Media(VideoEngine.Instance.LibVLC, "screen://", FromType.FromLocation);
            media.AddOption(":screen-fps=30");
            // 只抓屏幕上一块区域（PPT 抓放映窗口用）：放映是全屏的，抓那块屏幕 = 抓到放映画面。
            // 四个值都 > 0 才生效；否则维持"抓整屏"的老行为。
            if (src != null && src.CaptureWidth > 0 && src.CaptureHeight > 0)
            {
                media.AddOption(":screen-left=" + src.CaptureLeft);
                media.AddOption(":screen-top=" + src.CaptureTop);
                media.AddOption(":screen-width=" + src.CaptureWidth);
                media.AddOption(":screen-height=" + src.CaptureHeight);
                media.AddOption(":screen-follow-mouse=0");
            }

            // 声音（PPT 视频采集用）：抓屏只有画面，要声音得挂一路从属媒体。
            // 用 VLC 的 input-slave 挂一个只带音频的 dshow 源：
            //   :dshow-vdev=none   = 不要视频设备
            //   :dshow-adev=<名字>  = 点名录音设备（回环设备：VB-Audio Cable / 立体声混音）
            // ⚠ 不点名时 VLC 会抓「系统默认录音设备」（通常是麦克风），不是 PPT 的声音 ——
            //   所以这里只在明确选了设备时才挂，绝不默认挂。
            if (src != null && !string.IsNullOrWhiteSpace(src.CaptureAudioDevice))
            {
                media.AddOption(":input-slave=dshow://");
                media.AddOption(":dshow-vdev=none");
                media.AddOption(":dshow-adev=" + src.CaptureAudioDevice);
            }
            return media;
        }

        public void Dispose()
        {
            MediaPlayer.Dispose();
            PreviewPlayer.Dispose();
            TransitionPlayer.Dispose();
            LibVLC.Dispose();
            _instance = null;
        }
    }
}
