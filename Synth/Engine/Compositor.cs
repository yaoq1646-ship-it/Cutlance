using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace SynthPoc.Engine
{
    /// <summary>
    /// CPU 合成器：把 N 路的 vmem 帧按图层矩形拼成一张 BGRA 成品帧。
    ///
    /// 这是 C 方案的核心 —— 成品帧在内存里真实存在，
    /// 所以「窗口被遮挡 / 最小化 / 缩到托盘」都不影响出画。
    ///
    /// 双缓冲：合成线程写 _back，写完交换；呈现方在 swap 锁内读 _front。
    /// </summary>
    internal sealed unsafe class Compositor : IDisposable
    {
        private readonly object _swapGate = new();
        private IntPtr _front;
        private IntPtr _back;
        private bool _disposed;

        private readonly List<Layer> _ordered = new();

        public int Width { get; }
        public int Height { get; }
        public int Stride => Width * 4;
        public uint Background { get; set; } = 0xFF000000u;   // BGRA 不透明黑

        /// <summary>最近一次 Compose 的耗时（毫秒）。</summary>
        public double LastComposeMs { get; private set; }

        public List<Layer> Layers { get; } = new();

        /// <summary>
        /// 位图叠加层（P4）：字幕/图片这类不是视频源的图层。
        /// 上层把 WPF 离屏渲染出来的 BGRA 位图挂进来，合成时按 alpha 通道混上去。
        /// 文字画面不变就不用重渲染 —— 位图内容由上层维护，这里只管混合。
        /// </summary>
        public List<ImageLayer> Overlays { get; } = new();

        // ── P3：节目/预监模式 ────────────────────────────────────────────────
        /// <summary>true = 整屏只画「节目」那一路（P3）；false = 多画面拼接（P1/P2）。</summary>
        public bool ProgramMode;

        /// <summary>当前节目源。</summary>
        public VmemInput? Pgm;

        /// <summary>转场中的下一个节目源（null = 没在转场）。</summary>
        public VmemInput? PgmB;

        /// <summary>PgmB 的不透明度 0…1。0 = 全 A，1 = 全 B。</summary>
        public double Mix;

        // ── 整帧淡出（2026-10-22）：布局切换 / 切画面的**真转场** ──────────────
        /// <summary>
        /// 要淡出的旧成品帧（不透明 BGRA，stride = 画布宽 ×4）。
        /// 上层在切换前把「当前这一帧」拷进来，随后按 FadeAlpha 逐帧递减。
        /// 为什么需要它：C 方案下每个图层只是透明 Grid，对元素做 Opacity 动画**看不见任何东西**，
        /// 转场必须落在成品帧上（这也是铁律「两路同帧混合」的正确落点）。
        /// </summary>
        public IntPtr FadeSrc;

        /// <summary>FadeSrc 的行距。</summary>
        public int FadeStride;

        /// <summary>FadeSrc 的不透明度 0…255（255 = 完全盖住新画面，0 = 已淡完）。</summary>
        public int FadeAlpha;

        // ── FTB 淡黑（2026-10-22）：节目总闸，必须落在成品帧上 ────────────────
        /// <summary>
        /// 整帧压黑的不透明度 0…255（0 = 正常出画，255 = 全黑）。
        ///
        /// 为什么必须在这里做：FTB 老实现对界面上的黑矩形做透明度动画，
        /// 但 C 方案下推流/录制/NDI/全屏吃的是这块内存里的成品帧，
        /// 界面怎么黑都跟它们没关系 —— 直播出状况时按 FTB 等于没按。
        /// 顺序上它排在最后（图层 → 叠加 → 转场旧帧 → FTB），所以是真正的总闸。
        /// </summary>
        public int FtbAlpha;

        private readonly Layer _pgmA = new() { Letterbox = true };
        private readonly Layer _pgmB = new() { Letterbox = true };

        public Compositor(int width, int height)
        {
            Width = width;
            Height = height;
            int bytes = Stride * height;
            _front = System.Runtime.InteropServices.Marshal.AllocHGlobal(bytes);
            _back = System.Runtime.InteropServices.Marshal.AllocHGlobal(bytes);
            Clear(_front);
            Clear(_back);
        }

        public sealed class Layer
        {
            public VmemInput Feed = null!;
            public string Label = string.Empty;
            public int X, Y, W, H;       // 目标矩形（成品帧坐标）
            public int Z;
            public bool Visible = true;

            /// <summary>true = 按源宽高比等比内接（不留变形），false = 直接拉伸填满。</summary>
            public bool Letterbox = true;

            /// <summary>
            /// 填充方式（2026-10-25，异形屏用）：
            ///   -1 = 未指定，跟 <see cref="Letterbox"/> 走（POC/老代码行为不变）
            ///    0 = 等比内接（留黑边）
            ///    1 = 等比铺满后裁掉多余（画面填满格子，不变形）
            ///    2 = 直接拉伸铺满（会变形，异形屏做 1:1 像素映射时用）
            /// </summary>
            public int FitMode = -1;

            /// <summary>实际生效的填充方式（把 -1 折成 0/2）。</summary>
            public int EffectiveFit => FitMode >= 0 ? (FitMode > 2 ? 2 : FitMode) : (Letterbox ? 0 : 2);

            /// <summary>
            /// 这一层的整块不透明度 0…255（255 = 完全不透明）。
            /// 2026-10-24：图层透明度落在成品帧上（界面上的 WPF Opacity 传不出去）。
            /// Blit 已经支持 alpha 混合，这里只是把值传进去。
            /// </summary>
            public int Alpha = 255;

            // ── 描边 / 阴影（2026-10-25）──────────────────────────────────
            // 落点在成品帧：以前「描边」只画在 WPF 界面层，推流 / 录制里根本看不到。
            // ⚠ 描的是**实际画面矩形**（Letterbox 内缩后的那块），不是格子外框 ——
            //   16:9 素材放进格子会留黑边，框贴外框的话会飘在画面外面。
            /// <summary>描边厚度（成品帧像素，0 = 不描边）。</summary>
            public int BorderPx;
            /// <summary>描边颜色，0xAARRGGBB（与 FillRect 同口径；#FF33C6F5 直接当 uint 用）。</summary>
            public uint BorderColor = 0xFF33C6F5u;
            /// <summary>圆角半径（成品帧像素，0 = 直角）。半径大于半边时自动夹到最大。</summary>
            public int RadiusPx;
            /// <summary>阴影向外扩散的像素（0 = 无阴影）。四周均匀，不偏移（分屏贴边时不会一边深一边浅）。</summary>
            public int ShadowPx;
            /// <summary>阴影最深处的 alpha 0…255。</summary>
            public int ShadowAlpha = 150;

            /// <summary>调试用：给这一层描边 + 画一条半透明色条，方便截图确认 8 路各自独立。</summary>
            public bool Debug = false;
            public uint DebugColor = 0xFFFFFFFFu;

            internal int[] MapX = Array.Empty<int>();
            internal int MapSrcW = -1, MapDstW = -1;

            internal void EnsureMap(int srcW, int dstW)
            {
                if (MapSrcW == srcW && MapDstW == dstW && MapX.Length == dstW) return;
                var m = new int[dstW];
                for (int i = 0; i < dstW; i++)
                {
                    int s = (int)((i + 0.5) * srcW / dstW);
                    if (s >= srcW) s = srcW - 1;
                    if (s < 0) s = 0;
                    m[i] = s;
                }
                MapX = m;
                MapSrcW = srcW;
                MapDstW = dstW;
            }
        }

        /// <summary>
        /// 位图叠加层：一块 BGRA（直通 alpha，非预乘）像素 + 目标矩形。
        /// 像素内存由上层分配/释放（WPF 渲染出来拷进非托管缓冲），这里不接管所有权。
        /// </summary>
        public sealed class ImageLayer
        {
            public IntPtr Pixels;
            public int W, H, Stride;        // 源像素尺寸/行距
            public int X, Y, DstW, DstH;    // 目标矩形（成品帧坐标）
            public int Z = 100;
            public bool Visible = true;

            /// <summary>额外整体不透明度 0…1（字幕淡入淡出用；纯静态时保持 1）。</summary>
            public double Alpha = 1.0;

            internal int[] MapX = Array.Empty<int>();
            internal int MapSrcW = -1, MapDstW = -1;

            internal void EnsureMap(int srcW, int dstW)
            {
                if (MapSrcW == srcW && MapDstW == dstW && MapX.Length == dstW) return;
                var m = new int[dstW];
                for (int i = 0; i < dstW; i++)
                {
                    int s = (int)((i + 0.5) * srcW / dstW);
                    if (s >= srcW) s = srcW - 1;
                    if (s < 0) s = 0;
                    m[i] = s;
                }
                MapX = m;
                MapSrcW = srcW;
                MapDstW = dstW;
            }
        }

        public void Compose()
        {
            long t0 = Stopwatch.GetTimestamp();

            Clear(_back);

            if (ProgramMode)
            {
                ComposeProgram();
                // 节目画面上再叠图层（画中画）：切换台里"节目"是底，工程里的图层浮在上面
                ComposeLayers();
            }
            else
            {
                ComposeLayers();
            }

            ComposeOverlays();

            // 整帧淡出：旧画面盖在最上层，按 alpha 逐帧让位给新画面（交叉溶解）
            if (FadeSrc != IntPtr.Zero && FadeAlpha > 0) BlendOpaqueTop();

            // FTB 总闸排在最后：转场进行中按 FTB 也要能压黑（播出安全优先于效果）
            if (FtbAlpha > 0) BlendBlack(FtbAlpha);

            lock (_swapGate)
            {
                (_front, _back) = (_back, _front);
            }

            LastComposeMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        }

        /// <summary>
        /// 整屏一个节目源（可选第二路按 Mix 叠上去做溶解转场）。
        ///
        /// 为什么转场能零黑屏：两路解码各自照常出帧，合成时只是把 A/B 按比例混在一起，
        /// 不存在「切源 → 等新源出第一帧」的窗口期；老链路切源要重开播放器，
        /// 那几百毫秒就是黑屏的来源。
        /// </summary>
        private void ComposeProgram()
        {
            double mix = Mix;
            if (mix < 0) mix = 0; else if (mix > 1) mix = 1;

            _pgmA.X = 0; _pgmA.Y = 0; _pgmA.W = Width; _pgmA.H = Height;
            _pgmB.X = 0; _pgmB.Y = 0; _pgmB.W = Width; _pgmB.H = Height;

            if (Pgm != null)
            {
                _pgmA.Feed = Pgm;
                var a = Pgm.Acquire();
                if (a != IntPtr.Zero) Blit(_pgmA, a, 255);
            }

            if (PgmB != null && mix > 0.002)
            {
                _pgmB.Feed = PgmB;
                var b = PgmB.Acquire();
                if (b != IntPtr.Zero) Blit(_pgmB, b, (int)Math.Round(mix * 255.0));
            }
        }

        /// <summary>
        /// 图层合成（画中画/分屏）：按 Z 序把每路视频帧画到自己的矩形上。
        /// 没节目模式时这是主体；有节目时它是节目画面之上的浮层。
        /// </summary>
        private void ComposeLayers()
        {
            if (Layers.Count == 0) return;

            _ordered.Clear();
            for (int i = 0; i < Layers.Count; i++) _ordered.Add(Layers[i]);
            _ordered.Sort((a, b) => a.Z.CompareTo(b.Z));

            for (int i = 0; i < _ordered.Count; i++)
            {
                var L = _ordered[i];
                if (!L.Visible || L.W <= 0 || L.H <= 0) continue;

                var src = L.Feed.Acquire();
                if (src != IntPtr.Zero) Blit(L, src, L.Alpha);
                DrawChrome(L);              // 描边 / 阴影贴在实际画面矩形上（2026-10-25）
                if (L.Debug) Decorate(L);
            }
        }

        /// <summary>
        /// 位图叠加（字幕/图片）：按 Z 序按 alpha 通道混合。
        ///
        /// 与视频图层的差别：视频图层整块覆盖（alpha=255 走直拷），
        /// 这里每个像素自己的 alpha 都要参与 —— 文字描边/半透明 PNG 靠的就是这个。
        ///
        /// ⚠ 源像素是 **预乘 alpha**（WPF 的 RenderTargetBitmap 只吃 Pbgra32）：
        ///   颜色值已经乘过 alpha，所以混合公式是 d = s + d×(1-a)，
        ///   不是直通 alpha 那套 (s×a + d×(1-a))。用错公式的结果是边缘发灰、白字变暗。
        /// </summary>
        private void ComposeOverlays()
        {
            if (Overlays.Count == 0) return;

            _orderedImg.Clear();
            for (int i = 0; i < Overlays.Count; i++)
            {
                var o = Overlays[i];
                if (o.Visible && o.Pixels != IntPtr.Zero && o.DstW > 0 && o.DstH > 0) _orderedImg.Add(o);
            }
            if (_orderedImg.Count == 0) return;
            if (_orderedImg.Count > 1) _orderedImg.Sort((a, b) => a.Z.CompareTo(b.Z));

            for (int i = 0; i < _orderedImg.Count; i++) BlitAlpha(_orderedImg[i]);
        }

        private readonly List<ImageLayer> _orderedImg = new();

        /// <summary>
        /// 把 FadeSrc（**不透明**的旧成品帧）按 FadeAlpha 混到 _back 上，实现交叉溶解。
        ///
        /// ⚠ 公式与 BlitAlpha 不同：旧帧没乘过 alpha，要用直通 alpha
        ///   out = s×a + d×(1−a)；照搬预乘公式（d = s + d×(1−a)）会明显发白发亮。
        /// ⚠ 每帧遍历整屏（1080p ≈ 2.1M 像素），转场期间合成耗时约 +4~8ms；
        ///   所以只在淡出窗口内调用，平时 FadeAlpha=0 直接跳过。
        /// </summary>
        private void BlendOpaqueTop()
        {
            int a = FadeAlpha;
            if (a > 255) a = 255;

            if (a >= 255)
            {
                for (int y = 0; y < Height; y++)
                    Buffer.MemoryCopy((byte*)FadeSrc + (long)y * FadeStride,
                                      (byte*)_back + (long)y * Stride, Stride, Stride);
                return;
            }

            int ia = 255 - a;
            for (int y = 0; y < Height; y++)
            {
                uint* s = (uint*)((byte*)FadeSrc + (long)y * FadeStride);
                uint* d = (uint*)((byte*)_back + (long)y * Stride);
                for (int x = 0; x < Width; x++)
                {
                    uint sv = s[x], dv = d[x];
                    uint b = (uint)((((int)(sv & 0xFF) * a) + ((int)(dv & 0xFF) * ia)) / 255);
                    uint g = (uint)((((int)((sv >> 8) & 0xFF) * a) + ((int)((dv >> 8) & 0xFF) * ia)) / 255);
                    uint r = (uint)((((int)((sv >> 16) & 0xFF) * a) + ((int)((dv >> 16) & 0xFF) * ia)) / 255);
                    d[x] = 0xFF000000u | (r << 16) | (g << 8) | b;
                }
            }
        }

        /// <summary>
        /// 整帧压黑（FTB）：out = d×(1−a)，a = FtbAlpha/255。
        ///
        /// 黑色是 0,0,0，所以不需要「取源色再混」，只需把每个通道按剩余不透明度缩小 ——
        /// 比 BlendOpaqueTop 少一半乘加。alpha 通道固定写 255（画面始终不透明，黑是画出来的黑，
        /// 不是把整帧做成透明 —— 输出管线要的是不透明 BGRA）。
        /// ⚠ 同样只在淡黑窗口内调用（0.4s ≈ 12 帧），平时 FtbAlpha=0 直接跳过。
        /// </summary>
        private void BlendBlack(int alpha)
        {
            int a = alpha;
            if (a > 255) a = 255;
            if (a >= 255) { Clear(_back); return; }

            int ia = 255 - a;
            Span<int> map = stackalloc int[256];
            for (int i = 0; i < 256; i++) map[i] = i * ia / 255;

            for (int y = 0; y < Height; y++)
            {
                uint* d = (uint*)((byte*)_back + (long)y * Stride);
                for (int x = 0; x < Width; x++)
                {
                    uint dv = d[x];
                    uint b = (uint)map[(int)(dv & 0xFF)];
                    uint g = (uint)map[(int)((dv >> 8) & 0xFF)];
                    uint r = (uint)map[(int)((dv >> 16) & 0xFF)];
                    d[x] = 0xFF000000u | (r << 16) | (g << 8) | b;
                }
            }
        }

        private void BlitAlpha(ImageLayer o)
        {
            int bx = o.X, by = o.Y, bw = o.DstW, bh = o.DstH;
            int x0 = Math.Max(0, bx), x1 = Math.Min(Width, bx + bw);
            int y0 = Math.Max(0, by), y1 = Math.Min(Height, by + bh);
            if (x1 <= x0 || y1 <= y0) return;

            double ga = o.Alpha;
            if (ga < 0) ga = 0; else if (ga > 1) ga = 1;
            if (ga <= 0.002) return;
            int global = (int)Math.Round(ga * 255);

            o.EnsureMap(o.W, bw);
            var mapX = o.MapX;

            for (int y = y0; y < y1; y++)
            {
                int sy = (int)((long)(y - by) * o.H / bh);
                if (sy >= o.H) sy = o.H - 1;
                if (sy < 0) sy = 0;

                uint* srow = (uint*)((byte*)o.Pixels + (long)sy * o.Stride);
                uint* drow = (uint*)((byte*)_back + (long)y * Stride);

                for (int x = x0; x < x1; x++)
                {
                    uint sv = srow[mapX[x - bx]];
                    int sa = (int)((sv >> 24) & 0xFF);
                    if (sa == 0) continue;                    // 全透明像素直接跳过（字间大片空白）
                    if (global < 255) sa = sa * global / 255;
                    if (sa >= 255) { drow[x] = 0xFF000000u | (sv & 0x00FFFFFFu); continue; }

                    // 预乘 alpha：源色已乘过 alpha，直接加上「背景按剩余不透明度留下的部分」
                    uint dv = drow[x];
                    int ia = 255 - sa;
                    int b = (int)(sv & 0xFF) + (int)(dv & 0xFF) * ia / 255;
                    int g = (int)((sv >> 8) & 0xFF) + (int)((dv >> 8) & 0xFF) * ia / 255;
                    int r = (int)((sv >> 16) & 0xFF) + (int)((dv >> 16) & 0xFF) * ia / 255;
                    if (b > 255) b = 255;
                    if (g > 255) g = 255;
                    if (r > 255) r = 255;
                    drow[x] = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
                }
            }
        }

        /// <summary>在 swap 锁内访问当前成品帧（预览上传 / 写 ffmpeg 管道 / 导出截图都走这里）。</summary>
        public void WithFront(Action<IntPtr, int> reader)
        {
            lock (_swapGate) reader(_front, Stride);
        }

        /// <summary>预览上传用；语义同 WithFront。</summary>
        public void PresentTo(Action<IntPtr, int> writer) => WithFront(writer);

        private void Clear(IntPtr buf)
        {
            new Span<uint>((void*)buf, Width * Height).Fill(Background);
        }

        /// <summary>
        /// 把一个源画进成品帧。alpha = 255 为直接覆盖（快路径整行 memcpy），
        /// alpha &lt; 255 为按比例混合（转场用）—— 只在转场那 1 秒里走这条路。
        /// </summary>
        private void Blit(Layer L, IntPtr src, int alpha)
        {
            var feed = L.Feed;
            int sw = feed.Width, sh = feed.Height, ss = feed.Stride;
            if (alpha < 0) alpha = 0; else if (alpha > 255) alpha = 255;

            DrawnRect(L, sw, sh, out int bx, out int by, out int bw, out int bh);

            int x0 = Math.Max(0, bx), x1 = Math.Min(Width, bx + bw);
            int y0 = Math.Max(0, by), y1 = Math.Min(Height, by + bh);
            if (x1 <= x0 || y1 <= y0) return;

            // 铺满裁切（FitMode=1）：等比放大到铺满格子，多出来的从**源中心**裁掉 ——
            // 16:9 素材进 32:9 的格子时，上下被裁、左右不动，主体仍居中。
            bool cropFill = L.EffectiveFit == 1 && sw > 0 && sh > 0;
            int srcW = sw, srcH = sh, srcX = 0, srcY = 0;
            if (cropFill)
            {
                double sc = Math.Max((double)bw / sw, (double)bh / sh);
                srcW = Math.Min(sw, Math.Max(1, (int)Math.Round(bw / sc)));
                srcH = Math.Min(sh, Math.Max(1, (int)Math.Round(bh / sc)));
                srcX = (sw - srcW) / 2;
                srcY = (sh - srcH) / 2;
            }

            // 快路径：源尺寸 == 绘制尺寸，整行直拷
            if (!cropFill && sw == bw && sh == bh && alpha >= 255)
            {
                int rowBytes = (x1 - x0) * 4;
                for (int y = y0; y < y1; y++)
                {
                    byte* s = (byte*)src + (long)(y - by) * ss + (long)(x0 - bx) * 4;
                    byte* d = (byte*)_back + (long)y * Stride + (long)x0 * 4;
                    Buffer.MemoryCopy(s, d, rowBytes, rowBytes);
                }
                return;
            }

            L.EnsureMap(srcW, bw);
            var mapX = L.MapX;
            int ia = 255 - alpha;

            for (int y = y0; y < y1; y++)
            {
                int sy = srcY + (int)((long)(y - by) * srcH / bh);
                if (sy >= srcY + srcH) sy = srcY + srcH - 1;
                if (sy < 0) sy = 0;

                uint* srow = (uint*)((byte*)src + (long)sy * ss);
                uint* drow = (uint*)((byte*)_back + (long)y * Stride);

                if (alpha >= 255)
                {
                    for (int x = x0; x < x1; x++)
                        drow[x] = srow[srcX + mapX[x - bx]];
                }
                else
                {
                    for (int x = x0; x < x1; x++)
                    {
                        uint sv = srow[srcX + mapX[x - bx]];
                        uint dv = drow[x];
                        int b = ((int)(sv & 0xFF) * alpha + (int)(dv & 0xFF) * ia) / 255;
                        int g = ((int)((sv >> 8) & 0xFF) * alpha + (int)((dv >> 8) & 0xFF) * ia) / 255;
                        int r = ((int)((sv >> 16) & 0xFF) * alpha + (int)((dv >> 16) & 0xFF) * ia) / 255;
                        drow[x] = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
                    }
                }
            }
        }

        /// <summary>
        /// 这一层实际画到成品帧上的矩形（Letterbox 等比内缩之后）。
        /// 描边 / 阴影 / 调试框都必须用这一份计算结果 —— 三处各算一遍，迟早会跑偏。
        /// </summary>
        public void DrawnRect(Layer L, int sw, int sh, out int bx, out int by, out int bw, out int bh)
        {
            bx = L.X; by = L.Y;
            bw = Math.Max(1, L.W); bh = Math.Max(1, L.H);

            // 铺满裁切 / 拉伸：都占满整个格子（裁切时多余的部分在 Blit 里从源中心裁掉）
            if (L.EffectiveFit != 0) return;

            if (!L.Letterbox || sw <= 0 || sh <= 0) return;

            double scale = Math.Min((double)L.W / sw, (double)L.H / sh);
            bw = Math.Max(1, (int)Math.Round(sw * scale));
            bh = Math.Max(1, (int)Math.Round(sh * scale));
            bx = L.X + (L.W - bw) / 2;
            by = L.Y + (L.H - bh) / 2;
        }

        /// <summary>
        /// 给一层画「阴影 + 描边」（2026-10-25）。
        ///
        /// ── 为什么必须做在合成器里 ──
        ///   界面上的 WPF Border 只是给操作员看的取景框，推流/录制吃的是成品帧，
        ///   想让观众看到描边，只能把像素画进这一帧。
        ///
        /// ── 阴影为什么四周均匀、不做偏移 ──
        ///   分屏时格子贴着画面边，偏移式投影会一边深、一边被裁掉；
        ///   均匀外扩在分屏和画中画两种场景都不会露馅。
        ///
        /// ── 成本 ──
        ///   只画环带、不遍历整块矩形：1080p 全屏 20px 描边 + 20px 阴影约 30 万像素，
        ///   不到整帧（200 万）的两成。
        /// </summary>
        private void DrawChrome(Layer L)
        {
            if (L.W <= 0 || L.H <= 0) return;
            if (L.ShadowPx <= 0 && L.BorderPx <= 0) return;

            var feed = L.Feed;
            DrawnRect(L, feed.Width, feed.Height, out int bx, out int by, out int bw, out int bh);

            int r = L.RadiusPx;
            if (r < 0) r = 0;
            int maxR = Math.Min(bw, bh) / 2;
            if (r > maxR) r = maxR;

            // ① 阴影：由外向内一圈圈画，越靠外越淡。
            //    用 t² 衰减而不是线性 —— 线性会在最外沿留一道看得见的硬边。
            if (L.ShadowPx > 0 && L.ShadowAlpha > 0)
            {
                int sp = Math.Min(L.ShadowPx, Math.Max(Width, Height));   // 防御病态值
                for (int d = sp; d >= 1; d--)
                {
                    double t = 1.0 - (double)(d - 1) / sp;
                    int a = (int)(L.ShadowAlpha * t * t);
                    if (a <= 2) continue;
                    DrawRoundRing(bx - d, by - d, bw + 2 * d, bh + 2 * d, r + d, 1, 0xFF000000u, a);
                }
            }

            // ② 描边压在阴影上面（先影后框，框边才干净）
            if (L.BorderPx > 0)
                DrawRoundRing(bx, by, bw, bh, r, L.BorderPx, L.BorderColor, 255);
        }

        /// <summary>
        /// 画一个圆角矩形环（厚度 t）。逐行算出左右两条带，**不遍历整块矩形** ——
        /// 描边/阴影只占环带，扫全块等于白扫 200 万像素（1080p 全屏）。
        /// </summary>
        private void DrawRoundRing(int x, int y, int w, int h, int radius, int t, uint color, int alpha)
        {
            if (w <= 0 || h <= 0 || t <= 0 || alpha <= 0 || _back == IntPtr.Zero) return;
            if (t * 2 > w) t = Math.Max(1, w / 2);
            if (t * 2 > h) t = Math.Max(1, h / 2);

            if (radius < 0) radius = 0;
            int maxR = Math.Min(w, h) / 2;
            if (radius > maxR) radius = maxR;

            int y0 = Math.Max(0, y), y1 = Math.Min(Height, y + h);
            if (y1 <= y0) return;

            int innerY = y + t, innerH = h - 2 * t;
            int innerX = x + t, innerW = w - 2 * t;
            int innerR = Math.Max(0, radius - t);

            for (int row = y0; row < y1; row++)
            {
                int insOut = RoundInset(row, y, h, radius);
                int lo = x + insOut, ro = x + w - insOut;
                if (ro <= lo) continue;

                // 落在上下两条厚边里 → 这一行整段都是环，不用算内层
                if (innerH <= 0 || row < innerY || row >= innerY + innerH)
                {
                    FillSpan(row, lo, ro, color, alpha);
                    continue;
                }

                int insIn = RoundInset(row, innerY, innerH, innerR);
                int li = innerX + insIn;
                int ri = innerX + innerW - insIn;

                int a0 = lo, a1 = Math.Min(Math.Max(li, lo), ro);
                if (a1 > a0) FillSpan(row, a0, a1, color, alpha);

                int b0 = Math.Min(Math.Max(ri, lo), ro), b1 = ro;
                if (b1 > b0) FillSpan(row, b0, b1, color, alpha);
            }
        }

        /// <summary>
        /// 圆角矩形在第 row 行左右各要缩进多少像素（圆角只影响上下两端各 radius 行）。
        /// dy = 离最近的水平边的行距，dx = √(r² − (r−dy)²)，缩进 = r − dx。
        /// </summary>
        private static int RoundInset(int row, int y, int h, int radius)
        {
            if (radius <= 0 || h <= 0) return 0;
            int dyTop = row - y;
            int dyBottom = (y + h - 1) - row;
            int dy = dyTop < dyBottom ? dyTop : dyBottom;
            if (dy >= radius) return 0;
            if (dy < 0) dy = 0;
            double dr = radius - dy;
            double dx = Math.Sqrt((double)radius * radius - dr * dr);
            return (int)Math.Round(radius - dx);
        }

        /// <summary>水平填充一段（带 alpha 混合）。描边/阴影都走这里，避免每个像素各写一份公式。</summary>
        private void FillSpan(int row, int x0, int x1, uint color, int alpha)
        {
            int a = alpha < 0 ? 0 : (alpha > 255 ? 255 : alpha);
            if (a == 0) return;
            if (row < 0 || row >= Height) return;

            int s0 = Math.Max(0, x0), s1 = Math.Min(Width, x1);
            if (s1 <= s0) return;

            uint* d = (uint*)((byte*)_back + (long)row * Stride);
            if (a >= 255)
            {
                for (int x = s0; x < s1; x++) d[x] = 0xFF000000u | (color & 0x00FFFFFFu);
                return;
            }

            int ia = 255 - a;
            int cb = (int)(color & 0xFF), cg = (int)((color >> 8) & 0xFF), cr = (int)((color >> 16) & 0xFF);
            for (int x = s0; x < s1; x++)
            {
                uint dv = d[x];
                int b = (cb * a + (int)(dv & 0xFF) * ia) / 255;
                int g = (cg * a + (int)((dv >> 8) & 0xFF) * ia) / 255;
                int r = (cr * a + (int)((dv >> 16) & 0xFF) * ia) / 255;
                d[x] = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
            }
        }

        private void Decorate(Layer L)
        {
            // 半透明色条（验证 alpha 混合链路真的在工作）
            int barH = Math.Max(6, L.H / 12);
            FillRect(L.X, L.Y, L.W, barH, L.DebugColor, 140);

            // 4px 描边
            const int t = 4;
            FillRect(L.X, L.Y, L.W, t, L.DebugColor, 255);
            FillRect(L.X, L.Y + L.H - t, L.W, t, L.DebugColor, 255);
            FillRect(L.X, L.Y, t, L.H, L.DebugColor, 255);
            FillRect(L.X + L.W - t, L.Y, t, L.H, L.DebugColor, 255);
        }

        private void FillRect(int x, int y, int w, int h, uint color, int alpha)
        {
            int x0 = Math.Max(0, x), x1 = Math.Min(Width, x + w);
            int y0 = Math.Max(0, y), y1 = Math.Min(Height, y + h);
            if (x1 <= x0 || y1 <= y0) return;

            if (alpha >= 255)
            {
                for (int yy = y0; yy < y1; yy++)
                {
                    uint* d = (uint*)((byte*)_back + (long)yy * Stride);
                    for (int xx = x0; xx < x1; xx++) d[xx] = color;
                }
                return;
            }

            int ia = 255 - alpha;
            for (int yy = y0; yy < y1; yy++)
            {
                uint* d = (uint*)((byte*)_back + (long)yy * Stride);
                for (int xx = x0; xx < x1; xx++)
                {
                    uint dv = d[xx];
                    int b = (int)(color & 0xFF) * alpha + (int)(dv & 0xFF) * ia;
                    int g = (int)((color >> 8) & 0xFF) * alpha + (int)((dv >> 8) & 0xFF) * ia;
                    int r = (int)((color >> 16) & 0xFF) * alpha + (int)((dv >> 16) & 0xFF) * ia;
                    d[xx] = 0xFF000000u | ((uint)(r / 255) << 16) | ((uint)(g / 255) << 8) | (uint)(b / 255);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (_swapGate)
            {
                if (_front != IntPtr.Zero) { System.Runtime.InteropServices.Marshal.FreeHGlobal(_front); _front = IntPtr.Zero; }
                if (_back != IntPtr.Zero) { System.Runtime.InteropServices.Marshal.FreeHGlobal(_back); _back = IntPtr.Zero; }
            }
        }
    }
}
