using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cutlance.Models;
using SynthPoc.Engine;
using ProdAudio = Cutlance.Services.Audio;

namespace Cutlance.Views
{
    /// <summary>
    /// 主界面监视器桥（A 方案：中间「预览框 + 输出框」换成 PGM/PVW 合成监看）。
    ///
    /// ── 为什么要有这层 ──
    ///   老界面把视频交给 VLC 的 HwndHost 原生子窗口显示，画面是 DWM 在屏幕上拼的，
    ///   内存里并不存在成品帧 —— 于是输出只能抓屏，被遮挡/最小化就断流（MEMORY 铁律 2）。
    ///   现在改成：每个输入只解码一次（vmem 出帧）→ CPU 合成 → 两个 WriteableBitmap。
    ///   屏幕上看到的和推出去的是**同一份像素**，跟窗口状态彻底脱钩。
    ///
    /// ── 一个输入只解一次 ──
    ///   老链路「预览一套播放器 + 图层一套播放器」，同一个文件会被解两遍。
    ///   这里按 <see cref="InputSource"/> 引用缓存，预览和节目共用同一路帧。
    ///
    /// ── 线程 ──
    ///   合成跑在独立后台线程（铁律 47：界面卡死不能拖垮输出）；
    ///   只有「上传到 WriteableBitmap」这一步回 UI 线程，且做了丢帧保护
    ///   （上一帧还没传完就跳过，宁可监视器掉帧也不堵 UI）。
    /// </summary>
    internal sealed class MonitorBridge : IDisposable, Services.IProgramFrames
    {
        /// <summary>
        /// 节目成品帧尺寸。
        /// ⚠ 构造时定死：输出分辨率改了要重建 MonitorBridge（MainWindow 里按 StreamService 的设定传进来），
        ///   否则竖屏工程会拿横屏画布去缩放，画面被拉伸。
        /// </summary>
        public int OutW { get; }
        public int OutH { get; }

        /// <summary>预监画面尺寸。屏幕上看得到的最多几百 DIP，960×540 足够且省 CPU。</summary>
        public int PvwW { get; }
        public int PvwH { get; }

        /// <summary>
        /// 节目画布的逻辑宽度（DIP）。描边 / 圆角 / 阴影在界面上都是 DIP，
        /// 要换成成品帧像素就得知道「画布在界面上有多宽」。
        /// MainWindow 排版完就更新一次；默认 1280（1080p@150% 的基准）。
        /// </summary>
        public double CanvasWidthDip { get; set; } = 1280;

        // ── 快照：UI 线程写、合成线程读 ─────────────────────────────
        private struct GeomItem
        {
            public VmemInput Feed;
            public double X, Y, SX, SY;   // 归一化左上角 + 相对缩放
            public int Z;
            public bool Visible;

            /// <summary>整块不透明度 0–255（2026-10-24）。两条路径都必须显式填，别依赖 0 默认值。</summary>
            public int Alpha;

            // ── 描边 / 阴影（2026-10-25）──────────────────────────────
            // 都用「相对画布宽度」的归一化比例传，合成层再乘成品宽 ——
            // 界面上的描边是 DIP，成品帧是 1080p 像素，同一套 DIP 值在横竖屏差别很大，
            // 传比例才能保证「界面上看着多粗，播出去就多粗」。
            public double BorderNorm;
            public double RadiusNorm;
            public double ShadowNorm;
            public int ShadowAlpha;
            public uint BorderColor;

            /// <summary>填充方式（2026-10-25 异形屏）：0 等比内接 / 1 铺满裁切 / 2 拉伸铺满。</summary>
            public int Fit;
        }

        private sealed class Feed
        {
            public VmemInput Vmem = null!;
            public ProdAudio.AudioBus.Track? Track;
            public bool Bad;              // 建失败过就别每 500ms 重试一遍
        }

        private readonly Dictionary<InputSource, Feed> _feeds =
            new(ReferenceEqualityComparer.Instance);
        private readonly object _feedGate = new();

        private readonly Compositor _pgm;
        private readonly Compositor _pvw;
        private readonly SynthOverlayRenderer _overlay;

        /// <summary>倒计时牌（2026-10-24）：挂在 _pgm.Overlays 上，所以四条输出线都会带上它。</summary>
        private readonly CountdownOverlay _countdown;

        private readonly object _snapGate = new();
        private List<GeomItem> _pgmItems = new();
        private List<GeomItem> _pvwItems = new();

        private readonly object _composeGate = new();   // 叠加层 Sync 与 Compose 互斥

        private Thread? _thread;
        private volatile bool _running;
        private volatile bool _paused;
        private double _fps = 30;
        private long _frames;

        // ── 整帧淡出（真转场，2026-10-22）────────────────────────────────────
        // 为什么不是给图层元素做 Opacity 动画：C 方案下图层只是透明 Grid、像素由这里合成，
        // 对元素做动画在播出画面上看不出来（渲染的是合成器成品帧，不是 WPF 视觉树）。
        private IntPtr _fadeBuf;
        private int _fadeCapBytes;
        private long _fadeStart;
        private double _fadeDur;
        private volatile bool _fadeActive;

        // ── 两种转场的区别（2026-10-22 老板要求分开）────────────────────────────
        // false = AlphaFade 交叉溶解：旧帧一路淡下去，新画面在底下同步透出来（vMix AlphaFade）。
        // true  = Fade 淡到黑再淡出：前半段旧帧压住不动、整幅渐渐变黑；到半程全黑，
        //         后半段黑场退去、露出底下已经换好的新画面（vMix Fade）。
        // 以前两种都走同一个 alpha 递减，所以看起来完全一样 —— 那是没区分，不是设计。
        private volatile bool _fadeTwoStage;

        // ── FTB 淡黑（节目总闸，2026-10-22）───────────────────────────────────
        // 老实现只在界面浮层上盖一块黑矩形：推流/录制/NDI/全屏吃的是合成器成品帧，
        // 界面怎么黑都传不出去。现在改成按时间把 FtbAlpha 推上去，由合成器压黑成品帧。
        private long _ftbStart;
        private double _ftbDur;
        private int _ftbFrom;
        private int _ftbTo;
        private volatile bool _ftbActive;

        /// <summary>当前淡黑程度 0…255（读给自检/状态用）。</summary>
        public int FtbAlpha => _pgm.FtbAlpha;

        /// <summary>当前转场淡出的旧帧 alpha 0…255（读给自检用；0 = 没有转场在演）。</summary>
        public int FadeAlphaNow => _pgm.FadeAlpha;

        private int _uploadPending;

        public WriteableBitmap PgmBitmap { get; }
        public WriteableBitmap PvwBitmap { get; }

        /// <summary>合成线程每出一帧（且已排好上传）触发一次，UI 侧用来刷状态。</summary>
        public event Action? Tick;

        public bool Running => _running;
        public long Frames => Interlocked.Read(ref _frames);
        public double LastComposeMs => _pgm.LastComposeMs;
        public int FeedCount { get { lock (_feedGate) return _feeds.Count; } }
        public long DroppedFrames { get { lock (_feedGate) { long n = 0; foreach (var f in _feeds.Values) if (!f.Bad) n += f.Vmem.Dropped; return n; } } }

        public double Fps
        {
            get => _fps;
            set { if (value > 0) _fps = value; }
        }

        /// <summary>暂停合成（例如切回老的编辑视图时省 CPU）。</summary>
        public bool Paused
        {
            get => _paused;
            set => _paused = value;
        }

        public MonitorBridge() : this(1920, 1080) { }

        public MonitorBridge(int outW, int outH)
        {
            OutW = outW > 0 ? outW : 1920;
            OutH = outH > 0 ? outH : 1080;

            // 预监按节目分辨率等比缩到 960 宽（竖屏时 540×960，不要写死 960×540）
            if (Math.Abs((double)OutW / OutH - 16.0 / 9) < 0.01) { PvwW = 960; PvwH = 540; }
            else
            {
                double s = 960.0 / Math.Max(OutW, OutH);
                PvwW = Math.Max(2, (int)Math.Round(OutW * s));
                PvwH = Math.Max(2, (int)Math.Round(OutH * s));
            }

            _pgm = new Compositor(OutW, OutH);
            _pvw = new Compositor(PvwW, PvwH);
            _overlay = new SynthOverlayRenderer(OutW, OutH);
            _countdown = new CountdownOverlay(OutW, OutH, _pgm);

            PgmBitmap = new WriteableBitmap(OutW, OutH, 96, 96, PixelFormats.Bgr32, null);
            PvwBitmap = new WriteableBitmap(PvwW, PvwH, 96, 96, PixelFormats.Bgr32, null);
        }

        // ══════════════════════ 输出侧取帧（IProgramFrames） ══════════════════════

        // 挂上来的消费者（推流/录制、NDI、全屏…）。合成线程每帧读一次快照，增删只在 UI 线程发生。
        private Action<IntPtr, int>[] _sinks = Array.Empty<Action<IntPtr, int>>();

        public int Width => OutW;
        public int Height => OutH;

        public void AddSink(Action<IntPtr, int> sink)
        {
            if (sink == null) return;
            lock (_sinkGate)
            {
                var list = new List<Action<IntPtr, int>>(_sinks);
                if (list.Contains(sink)) return;
                list.Add(sink);
                _sinks = list.ToArray();
            }
        }

        public void RemoveSink(Action<IntPtr, int> sink)
        {
            if (sink == null) return;
            lock (_sinkGate)
            {
                var list = new List<Action<IntPtr, int>>(_sinks);
                if (!list.Remove(sink)) return;
                _sinks = list.ToArray();
            }
        }

        private readonly object _sinkGate = new();

        /// <summary>
        /// 在 swap 锁内把成品帧分发给所有输出通路。
        /// ⚠ 每路各自 try/catch：某一路（例如推流断了）抛异常绝不能拖垮合成循环。
        /// </summary>
        private void DispatchFrame()
        {
            var sinks = _sinks;
            if (sinks.Length == 0) return;

            try
            {
                _pgm.WithFront((ptr, stride) =>
                {
                    for (int i = 0; i < sinks.Length; i++)
                    {
                        try { sinks[i](ptr, stride); }
                        catch (Exception ex)
                        {
                            try { Services.ErrorLog.Info("[监视器] 帧分发失败：" + ex.Message); } catch { }
                        }
                    }
                });
            }
            catch { }
        }

        // ══════════════════════ 输入池 ══════════════════════

        /// <summary>
        /// 取（必要时新建）某输入源的解码通路。
        /// 媒体构造走 <c>VideoEngine.CreateMedia</c>（铁律 3：唯一注入点），
        /// 不在这里自己拼 MRL，否则文件/摄像头/桌面/网络流四套参数必然跟主程序分叉。
        /// </summary>
        /// <summary>
        /// 丢掉这一路的解码通路（2026-10-24）：换了解码参数（去隔行 / 旋转 / 裁切改了）时用。
        /// 下一次 <see cref="FeedFor"/> 会按新的媒体参数重建。
        ///
        /// ⚠ 不能立刻 Dispose：合成线程的快照里最多还会引用它一个同步周期（500ms），
        ///   立刻释放非托管缓冲 = 合成线程读野指针（直接崩）。所以先从池里摘掉、标记作废，
        ///   2 秒后再交给后台释放 —— 那时快照早已刷新，没人再引用它。
        /// </summary>
        public void DropFeed(InputSource? src)
        {
            if (src == null) return;
            Feed? dead = null;
            lock (_feedGate)
            {
                if (!_feeds.TryGetValue(src, out var f)) return;
                _feeds.Remove(src);
                f.Bad = true;
                dead = f;
            }

            var aud = ProdAudio.AudioBus.Instance;
            var feed = dead;
            Task.Run(async () =>
            {
                try { await Task.Delay(2000); } catch { }
                try { feed.Vmem?.Dispose(); } catch { }
                try { if (feed.Track != null) aud.RemoveTrack(feed.Track.Key); } catch { }
            });
        }

        public VmemInput? FeedFor(InputSource? src)
        {
            if (src == null) return null;

            lock (_feedGate)
            {
                if (_feeds.TryGetValue(src, out var hit))
                    return hit.Bad ? null : hit.Vmem;

                var feed = new Feed();
                _feeds[src] = feed;
                try
                {
                    var vlc = Services.VideoEngine.Instance.LibVLC;
                    if (vlc == null) { feed.Bad = true; return null; }

                    var media = Services.VideoEngine.Instance.CreateMedia(src);
                    var (w, h) = ProbeSize(media, src);

                    // 音频轨道：一路输入一条（key = src:N），具体哪路出声由 SetAudible 决定。
                    // 预览和节目共用同一条轨道 —— 同一个输入本来就只有一份声音。
                    var aud = ProdAudio.AudioBus.Instance;
                    var track = aud.EnsureTrack("src:" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    track.Mute = true;
                    track.Volume = 0f;
                    track.ToStream = true;
                    track.ToSoundCard = true;
                    feed.Track = track;

                    feed.Vmem = new VmemInput(vlc, media, w, h, src.Name, null,
                        player => ProdAudio.VlcAudioTap.Attach(player, track));
                    if (!feed.Vmem.Start())
                    {
                        feed.Bad = true;
                        return null;
                    }
                    return feed.Vmem;
                }
                catch (Exception ex)
                {
                    feed.Bad = true;
                    try { Services.ErrorLog.Info($"[监视器] 输入「{src.Name}」解码通路建立失败：{ex.Message}"); } catch { }
                    return null;
                }
            }
        }

        /// <summary>
        /// 取某输入对应总线轨道（自检/诊断用）。没建过通路返回 null。
        /// 为什么不直接暴露 Feed：轨道是"这一路声音在总线里叫什么名字、多大、静没静"，
        /// 验音量必须看它 —— 屏幕上听不出来，只能读这里。
        /// </summary>
        public ProdAudio.AudioBus.Track? TrackFor(InputSource? src)
        {
            if (src == null) return null;
            lock (_feedGate)
                return _feeds.TryGetValue(src, out var f) ? f.Track : null;
        }

        /// <summary>某输入是否出声（其余静音）。null / 0 路 = 全静音。</summary>
        public void SetAudible(IEnumerable<InputSource>? sources, float volume = 1f)
            => SetAudibleVolumeed(BuildSameVolume(sources, volume));

        private static List<(InputSource Src, float Vol)> BuildSameVolume(IEnumerable<InputSource>? sources, float volume)
        {
            var list = new List<(InputSource, float)>();
            if (sources == null) return list;
            foreach (var s in sources) if (s != null) list.Add((s, volume));
            return list;
        }

        /// <summary>
        /// 按「输入 + 各自音量」设置出声集合（2026-10-22 每格音量）。
        ///
        /// 为什么按输入而不是按图层：同一个输入在预览和节目里共用一条解码通路，
        /// 声音本来就只有一份 —— 三格同时播同一个文件，物理上也只有一路声音。
        /// 所以同一个输入被多个图层引用时，取其中**最大**的音量：
        /// 取最小会让"我只把其中一格调响"变成没反应，取最大至少符合直觉（响的那格说了算）。
        /// </summary>
        public void SetAudibleVolumeed(IReadOnlyList<(InputSource Src, float Vol)>? items)
        {
            lock (_feedGate)
            {
                var want = new Dictionary<InputSource, float>(ReferenceEqualityComparer.Instance);
                if (items != null)
                {
                    foreach (var it in items)
                    {
                        if (it.Src == null) continue;
                        if (!want.TryGetValue(it.Src, out float v)) v = 0f;
                        want[it.Src] = Math.Max(v, Math.Max(0f, it.Vol));
                    }
                }

                foreach (var kv in _feeds)
                {
                    var t = kv.Value.Track;
                    if (t == null) continue;
                    bool live = want.TryGetValue(kv.Key, out float vol) && vol > 0f;
                    t.Mute = !live;
                    t.Volume = live ? vol : 0f;
                }
            }
        }

        private (int w, int h) ProbeSize(LibVLCSharp.Shared.Media media, InputSource src)
        {
            // 只有本地文件值得 Parse（很快）；摄像头/桌面/网络流 Parse 等于先拉一遍流，启动要等好几秒。
            if (src.Type != SourceType.File) return (OutW, OutH);

            try
            {
                // ⚠ Parse 是异步的，不 Wait 就读 Tracks 一定拿到空数组（铁律 42）
                var task = media.Parse(LibVLCSharp.Shared.MediaParseOptions.ParseLocal);
                try { task?.Wait(4000); } catch { }

                foreach (var t in media.Tracks)
                {
                    if (t.TrackType == LibVLCSharp.Shared.TrackType.Video &&
                        t.Data.Video.Width > 0 && t.Data.Video.Height > 0)
                        return ((int)t.Data.Video.Width, (int)t.Data.Video.Height);
                }
            }
            catch { }
            return (OutW, OutH);
        }

        // ══════════════════════ 几何同步（UI 线程调） ══════════════════════

        /// <summary>节目侧：把工程的图层快照推给合成线程。</summary>
        public void SyncProgram(IEnumerable<Layer> layers)
        {
            var list = new List<GeomItem>();
            double cw = CanvasWidthDip > 1 ? CanvasWidthDip : 1280;
            foreach (var l in layers)
            {
                if (l == null || l.Source == null) continue;
                var feed = FeedFor(l.Source);
                if (feed == null) continue;
                list.Add(new GeomItem
                {
                    Feed = feed,
                    X = l.X,
                    Y = l.Y,
                    // 图层口径：宽 = 画布宽 × 0.5 × ViewScale（2 = 铺满）
                    SX = 0.5 * l.ViewScaleX,
                    SY = 0.5 * l.ViewScaleY,
                    Z = l.Z,
                    Visible = l.Visible,
                    Alpha = l.OpacityAlpha,     // 图层透明度（2026-10-24）：0–255，落到成品帧上
                    // 描边 / 圆角 / 阴影（2026-10-25）：传「占画布宽的比例」，合成层乘成品宽
                    BorderNorm = Math.Max(0, l.BorderThickness) / cw,
                    RadiusNorm = Math.Max(0, l.BorderRadius) / cw,
                    ShadowNorm = Math.Max(0, l.Shadow) / cw,
                    ShadowAlpha = l.ShadowAlpha,
                    BorderColor = l.BorderColorValue,
                    Fit = l.FitMode      // 填充方式（异形屏铺满用）
                });
            }
            lock (_snapGate) _pgmItems = list;
        }

        /// <summary>
        /// 成品帧底色（0xAARRGGBB）。默认不透明黑。
        /// 自检专用：验阴影必须有个亮底，黑底上叠黑色阴影等于没变。
        /// </summary>
        public uint ProgramBackground
        {
            get => _pgm.Background;
            set => _pgm.Background = value;
        }

        /// <summary>
        /// 自检用：第 index 层的**实际绘制矩形**（letterbox 内缩之后的成品帧像素坐标）。
        /// 描边 / 阴影的判据要采样「框边上」和「框外」，先得知道框到底在哪 ——
        /// 贴格子外框去采会拿到差几十像素的位置，判据就成了假的。
        /// </summary>
        public bool TryProgramLayerRect(int index, out int x, out int y, out int w, out int h)
        {
            x = y = w = h = 0;
            lock (_composeGate)
            {
                var c = _pgm;
                if (index < 0 || index >= c.Layers.Count) return false;
                var L = c.Layers[index];
                if (L.Feed == null) return false;
                c.DrawnRect(L, L.Feed.Width, L.Feed.Height, out x, out y, out w, out h);
                return w > 0 && h > 0;
            }
        }

        /// <summary>预监侧：把预览槽快照推给合成线程。</summary>
        public void SyncPreview(IEnumerable<PreviewSlot> slots)
        {
            var list = new List<GeomItem>();
            foreach (var s in slots)
            {
                if (s == null || s.Source == null) continue;
                var feed = FeedFor(s.Source);
                if (feed == null) continue;
                list.Add(new GeomItem
                {
                    Feed = feed,
                    X = s.X,
                    Y = s.Y,
                    // 预览槽口径：宽 = 画布宽 × ViewScale（1 = 铺满）
                    SX = s.ViewScaleX,
                    SY = s.ViewScaleY,
                    Z = s.Z,
                    Visible = true,
                    Alpha = 255          // 预览槽不参与透明度
                });
            }
            lock (_snapGate) _pvwItems = list;
        }

        /// <summary>叠加层（字幕/图片）同步。必须在 UI 线程调 —— 内部走 WPF 离屏渲染。</summary>
        public int SyncOverlays(IReadOnlyList<OverlayItem>? items)
        {
            if (items == null) return 0;
            lock (_composeGate)
            {
                return _overlay.Sync(items, _pgm);
            }
        }

        // ══════════════════ 倒计时牌（2026-10-24） ══════════════════

        /// <summary>
        /// 把倒计时数字叠进成品帧（推流 / 录制 / NDI / 全屏 / 监视器一起带上）。
        /// 必须在 UI 线程调用（内部走 WPF 离屏渲染）。
        /// 内容与字号都没变时是空操作 —— 界面上 10Hz 刷新也不会每秒重渲染 10 次。
        /// </summary>
        public void SetCountdown(string text, double baseFontPx, bool visible, bool negative, CountdownStyle style)
        {
            _countdown.Update(text, baseFontPx, visible, negative, style);
        }

        /// <summary>倒计时这一层当前是否在出画（自检用）。</summary>
        public bool CountdownVisible => _countdown.Visible;

        /// <summary>最近一次倒计时渲染的不透明像素数（自检用：0 = 没画出东西）。</summary>
        public int CountdownPixels => _countdown.LastOpaquePixels;

        /// <summary>倒计时数字的位置（归一化中心点 0–1，2026-10-24 老板要「可以挪动位置」）。</summary>
        public (double Nx, double Ny) CountdownPos => (_countdown.Nx, _countdown.Ny);

        /// <summary>把倒计时挪到归一化位置（会夹在画布内）。只改坐标，不重渲染位图。</summary>
        public void SetCountdownPosition(double nx, double ny) => _countdown.SetPosition(nx, ny);

        /// <summary>命中测试：输出画布上这个像素点是否落在倒计时数字上（叠加没开 → false）。</summary>
        public bool CountdownContains(int px, int py) => _countdown.Contains(px, py);

        /// <summary>倒计时数字当前的像素矩形 (x, y, w, h)，自检用。</summary>
        public (int X, int Y, int W, int H) CountdownRect => _countdown.RectNow();

        /// <summary>倒计时最近一次渲染的实心像素平均色（自检「颜色真生效」用）。</summary>
        public (byte R, byte G, byte B) CountdownTextRgb => _countdown.LastTextRgb;

        /// <summary>倒计时最近一次用的字体（自检用）。</summary>
        public string CountdownFontFamily => _countdown.LastFontFamily;

        /// <summary>倒计时重渲染次数（自检用：样式没变时不该涨）。</summary>
        public int CountdownRenderVersion => _countdown.RenderVersion;

        // ══════════════════════ 生命周期 ══════════════════════

        /// <summary>
        /// 开始一次整帧淡出（**真转场**）：把此刻的成品帧留作"旧画面"，
        /// 接下来 <paramref name="seconds"/> 秒内按 <paramref name="type"/> 演完。
        ///
        /// 用法固定为「先 BeginFade，再换布局」—— 先抓帧才抓得到旧画面。
        /// 抓不到帧（还没出过画）时静默跳过，等于硬切，不报错。
        /// Cut 直接返回（调用方本来就不会传 Cut，这里兜一层防手滑）。
        /// </summary>
        public void BeginFade(TransitionType type, double seconds)
        {
            if (type == TransitionType.Cut) { EndFadeOut(); return; }
            if (_ftbActive || _pgm.FtbAlpha > 0)
            {
                // 淡黑期间按 CUT：播出安全优先，不演转场（否则黑场和转场互相抢 FtbAlpha，
                // 观众会看到黑屏闪一下又亮）。直接把旧帧丢掉，一切从黑里出。
                EndFadeOut();
                return;
            }
            BeginFadeOut(seconds, type == TransitionType.Fade);
        }

        /// <summary>
        /// 开始一次整帧淡出。保留这个重载是为了 AlphaFade 的老调用点读起来更直白。
        /// </summary>
        public void BeginFadeOut(double seconds) => BeginFadeOut(seconds, false);

        private void BeginFadeOut(double seconds, bool twoStage)
        {
            try
            {
                if (seconds < 0.05 || !_running) { EndFadeOut(); return; }

                int rowBytes = OutW * 4;
                int need = rowBytes * OutH;
                if (_fadeBuf == IntPtr.Zero || _fadeCapBytes < need)
                {
                    if (_fadeBuf != IntPtr.Zero) { Marshal.FreeHGlobal(_fadeBuf); _fadeBuf = IntPtr.Zero; }
                    _fadeBuf = Marshal.AllocHGlobal(need);
                    _fadeCapBytes = need;
                }

                bool got = false;
                var row = new byte[rowBytes];
                WithProgram((src, stride) =>
                {
                    if (src == IntPtr.Zero) return;
                    for (int y = 0; y < OutH; y++)
                    {
                        Marshal.Copy(IntPtr.Add(src, y * stride), row, 0, rowBytes);
                        Marshal.Copy(row, 0, IntPtr.Add(_fadeBuf, y * rowBytes), rowBytes);
                    }
                    got = true;
                });
                if (!got) { EndFadeOut(); return; }

                _pgm.FadeStride = rowBytes;
                _pgm.FadeSrc = _fadeBuf;
                _pgm.FadeAlpha = 255;
                _fadeTwoStage = twoStage;
                _fadeStart = Stopwatch.GetTimestamp();
                _fadeDur = seconds;
                _fadeActive = true;
            }
            catch (Exception ex)
            {
                try { Services.ErrorLog.Info("[监视器] 转场启动失败：" + ex.Message); } catch { }
                EndFadeOut();
            }
        }

        /// <summary>
        /// 立刻结束淡出（硬切）。先断 alpha 再断指针：合成线程不会读到半截。
        /// 注意这里**不动 FtbAlpha** —— 那是 FTB 的地盘，转场自己收尾时另行复位。
        /// </summary>
        public void EndFadeOut()
        {
            _fadeActive = false;
            _fadeTwoStage = false;
            _pgm.FadeAlpha = 0;
            _pgm.FadeSrc = IntPtr.Zero;
        }

        /// <summary>合成线程每帧调一次：按经过时间推进，到点收尾。</summary>
        private void UpdateFade()
        {
            if (!_fadeActive) return;

            double el = (Stopwatch.GetTimestamp() - _fadeStart) / (double)Stopwatch.Frequency;
            if (el >= _fadeDur)
            {
                bool two = _fadeTwoStage;
                EndFadeOut();
                // 两阶段的黑场由转场自己抹掉；FTB 正在跑就别抢，交给 UpdateFtb 收。
                if (two && !_ftbActive) _pgm.FtbAlpha = 0;
                return;
            }

            if (!_fadeTwoStage)
            {
                // 交叉溶解：旧帧从全不透明线性淡到透明，新画面在底下同步透出来
                double k = 1.0 - el / _fadeDur;                 // 1 → 0
                _pgm.FadeAlpha = (int)Math.Round(k * 255.0);
                return;
            }

            // 淡到黑再淡出：两段各占一半
            double half = _fadeDur * 0.5;
            if (el < half)
            {
                _pgm.FadeAlpha = 255;                            // 旧帧全程压住新画面
                _pgm.FtbAlpha = (int)Math.Round(el / half * 255.0);   // 整幅渐渐变黑
            }
            else
            {
                if (_pgm.FadeSrc != IntPtr.Zero)
                {
                    _pgm.FadeAlpha = 0;                          // 后半段不再需要旧帧
                    _pgm.FadeSrc = IntPtr.Zero;
                }
                _pgm.FtbAlpha = (int)Math.Round((1.0 - (el - half) / half) * 255.0);  // 黑场退去
            }
        }

        // ══════════════════════ FTB 淡黑 ══════════════════════

        /// <summary>
        /// 淡到黑 / 从黑恢复。**从当前值开始**插值，所以连点两下不会跳变（0.4s 内反悔也顺滑）。
        /// 合成器没在跑（没有 monitor / 已 Stop）时立刻置位，等同硬切 —— 不假装有动画。
        /// </summary>
        public void SetFadeToBlack(bool toBlack, double seconds = 0.4)
        {
            int target = toBlack ? 255 : 0;
            // 要压黑就把正在演的转场收掉：两套动画抢同一个 FtbAlpha 会闪一下。
            // 只压黑这一侧清，恢复出画时不该打断任何东西。
            if (toBlack) EndFadeOut();
            if (seconds < 0.05 || !_running)
            {
                EndFtb(target);
                return;
            }
            _ftbFrom = _pgm.FtbAlpha;
            _ftbTo = target;
            if (_ftbFrom == _ftbTo) { EndFtb(target); return; }
            _ftbStart = Stopwatch.GetTimestamp();
            _ftbDur = seconds;
            _ftbActive = true;
        }

        /// <summary>停下插值并直接设到目标值（0 = 恢复出画，255 = 全黑）。</summary>
        public void EndFtb(int alpha = 0)
        {
            _ftbActive = false;
            _pgm.FtbAlpha = Math.Clamp(alpha, 0, 255);
        }

        private void UpdateFtb()
        {
            if (!_ftbActive) return;
            double el = (Stopwatch.GetTimestamp() - _ftbStart) / (double)Stopwatch.Frequency;
            if (el >= _ftbDur) { EndFtb(_ftbTo); return; }
            double k = el / _ftbDur;
            _pgm.FtbAlpha = (int)Math.Round(_ftbFrom + (_ftbTo - _ftbFrom) * k);
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "Cutlance.Monitor",
                Priority = ThreadPriority.AboveNormal
            };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            try { _thread?.Join(1500); } catch { }
            _thread = null;
            // 停止后若还留着全黑，下次开输出会一上来就是黑的（老板会以为程序坏了）
            try { EndFtb(0); } catch { }
        }

        private void Loop()
        {
            var sw = Stopwatch.StartNew();
            long next = 0;
            double frameTicks = 0;

            while (_running)
            {
                try
                {
                    frameTicks = Stopwatch.Frequency / Math.Max(1.0, _fps);
                    long now = sw.ElapsedTicks;

                    if (now >= next)
                    {
                        next += (long)frameTicks;
                        if (now - next > Stopwatch.Frequency / 4) next = now + (long)frameTicks;

                        if (!_paused) ComposeOnce();
                    }
                    else
                    {
                        // 1ms 粒度轮询（铁律 43：默认 15.6ms 定时精度会把 30fps 打成 23.5）
                        Thread.Sleep(1);
                    }
                }
                catch (Exception ex)
                {
                    try { Services.ErrorLog.Info("[监视器] 合成异常：" + ex.Message); } catch { }
                    Thread.Sleep(100);
                }
            }
        }

        private void ComposeOnce()
        {
            UpdateFade();
            UpdateFtb();

            List<GeomItem> pgm, pvw;
            lock (_snapGate)
            {
                pgm = _pgmItems;
                pvw = _pvwItems;
            }

            Fill(_pgm, pgm, OutW, OutH);
            Fill(_pvw, pvw, PvwW, PvwH);

            // 叠加渲染在 UI 线程改 _pgm.Overlays，这里必须互斥（否则真会读到半个 ImageLayer）
            lock (_composeGate) { _pgm.Compose(); }
            _pvw.Compose();

            Interlocked.Increment(ref _frames);

            // ══════════════════ C 方案的最后一环 ══════════════════
            // 成品帧直接分发给推流/录制/NDI（各自拷贝进自己的队列），
            // 于是输出跟「窗口是否可见、是否被遮挡、是否最小化」彻底无关。
            DispatchFrame();

            if (Interlocked.Exchange(ref _uploadPending, 1) == 0)
            {
                var d = Application.Current?.Dispatcher;
                if (d != null) d.BeginInvoke(DispatcherPriority.Render, new Action(Upload));
            }
        }

        /// <summary>把快照刷成合成器的图层表（条数对齐 + 几何更新，不重建对象）。</summary>
        private static void Fill(Compositor comp, List<GeomItem> items, int w, int h)
        {
            while (comp.Layers.Count > items.Count) comp.Layers.RemoveAt(comp.Layers.Count - 1);
            while (comp.Layers.Count < items.Count)
                comp.Layers.Add(new Compositor.Layer { Letterbox = true });

            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                var L = comp.Layers[i];
                L.Feed = it.Feed;
                L.X = (int)Math.Round(it.X * w);
                L.Y = (int)Math.Round(it.Y * h);
                L.W = Math.Max(1, (int)Math.Round(it.SX * w));
                L.H = Math.Max(1, (int)Math.Round(it.SY * h));
                L.Z = it.Z;
                L.Visible = it.Visible;
                L.Letterbox = true;
                L.FitMode = it.Fit < 0 ? 0 : (it.Fit > 2 ? 2 : it.Fit);   // 填充方式（2026-10-25）
                L.Alpha = it.Alpha < 0 ? 0 : (it.Alpha > 255 ? 255 : it.Alpha);
                // 描边 / 圆角 / 阴影：归一化比例 → 成品帧像素
                L.BorderPx = it.BorderNorm <= 0 ? 0 : Math.Max(0, (int)Math.Round(it.BorderNorm * w));
                L.RadiusPx = it.RadiusNorm <= 0 ? 0 : Math.Max(0, (int)Math.Round(it.RadiusNorm * w));
                L.ShadowPx = it.ShadowNorm <= 0 ? 0 : Math.Max(0, (int)Math.Round(it.ShadowNorm * w));
                L.ShadowAlpha = it.ShadowAlpha < 0 ? 0 : (it.ShadowAlpha > 255 ? 255 : it.ShadowAlpha);
                if (it.BorderColor != 0) L.BorderColor = it.BorderColor;
            }

            if (Environment.GetEnvironmentVariable("CUTLANCE_MON_DEBUG") == "1" && items.Count > 0)
            {
                var sb = new System.Text.StringBuilder($"[合成{comp.Width}x{comp.Height}] {items.Count} 层：");
                for (int i = 0; i < items.Count; i++)
                {
                    var L = comp.Layers[i];
                    sb.Append($" #{i}({L.X},{L.Y})={L.W}x{L.H} z{L.Z} v{(L.Visible ? 1 : 0)}");
                }
                try { Services.ErrorLog.Info(sb.ToString()); } catch { }
            }
        }

        /// <summary>UI 线程：把成品帧贴到 WriteableBitmap。</summary>
        private void Upload()
        {
            Interlocked.Exchange(ref _uploadPending, 0);

            try
            {
                _pgm.WithFront((ptr, stride) =>
                {
                    PgmBitmap.Lock();
                    PgmBitmap.WritePixels(new Int32Rect(0, 0, OutW, OutH), ptr, stride * OutH, stride);
                    PgmBitmap.AddDirtyRect(new Int32Rect(0, 0, OutW, OutH));
                    PgmBitmap.Unlock();
                });
            }
            catch { }

            try
            {
                _pvw.WithFront((ptr, stride) =>
                {
                    PvwBitmap.Lock();
                    PvwBitmap.WritePixels(new Int32Rect(0, 0, PvwW, PvwH), ptr, stride * PvwH, stride);
                    PvwBitmap.AddDirtyRect(new Int32Rect(0, 0, PvwW, PvwH));
                    PvwBitmap.Unlock();
                });
            }
            catch { }

            try { Tick?.Invoke(); } catch { }
        }

        /// <summary>在 swap 锁内读当前成品帧（录制/推流用）。</summary>
        public void WithProgram(Action<IntPtr, int> reader) => _pgm.WithFront(reader);

        public void Dispose()
        {
            Stop();

            try { EndFadeOut(); } catch { }
            try { if (_fadeBuf != IntPtr.Zero) { Marshal.FreeHGlobal(_fadeBuf); _fadeBuf = IntPtr.Zero; _fadeCapBytes = 0; } } catch { }
            try { _overlay.Dispose(); } catch { }
            try { _countdown.Dispose(); } catch { }
            try { _pgm.Dispose(); } catch { }
            try { _pvw.Dispose(); } catch { }

            lock (_feedGate)
            {
                foreach (var f in _feeds.Values)
                {
                    try { if (!f.Bad) f.Vmem.Dispose(); } catch { }
                    try { if (f.Track != null) ProdAudio.AudioBus.Instance.RemoveTrack(f.Track.Key); } catch { }
                }
                _feeds.Clear();
            }
        }
    }
}
