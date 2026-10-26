using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SynthPoc.Engine;

namespace Cutlance.Views
{
    /// <summary>
    /// 倒计时叠加的样式（2026-10-24 老板「字体/颜色自定义」）。
    /// 做成不可变小对象、签名直接拼串：内容没变就不重渲染，样式变了必须重渲染。
    /// </summary>
    internal sealed class CountdownStyle
    {
        public string FontFamilyName = "Consolas";        // 等宽是出厂值，数字跳变时不抖
        public bool Bold = true;
        public bool Italic;

        /// <summary>文字颜色（ARGB）。超时另有专用色，见 <see cref="NegativeColorHex"/>。</summary>
        public string TextColorHex = "#FFFFFFFF";

        /// <summary>超时（负数）时的文字颜色。</summary>
        public string NegativeColorHex = "#FFFF5A5A";

        /// <summary>底板颜色（含 alpha；#00000000 = 不要底板）。</summary>
        public string BackColorHex = "#66000000";

        public string Signature =>
            $"{FontFamilyName}|{(Bold ? "B" : "-")}{(Italic ? "I" : "-")}|{TextColorHex}|{NegativeColorHex}|{BackColorHex}";

        /// <summary>把 "#AARRGGBB" 解析成颜色；解析不了就用兜底色（绝不因为一个坏字符串渲染不出来）。</summary>
        public static Color Parse(string? hex, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(hex)) return fallback;
            try { return (Color)ColorConverter.ConvertFromString(hex); }
            catch { return fallback; }
        }
    }

    /// <summary>
    /// 倒计时牌 → 合成器位图叠加（2026-10-24 老板「选叠加按钮可以叠加进输出画面」）。
    ///
    /// 为什么必须走合成器叠加、不能直接摆在界面上：
    ///   C 方案下推流 / 录制 / NDI / 全屏吃的都是内存里的成品帧，
    ///   界面上画得再大也传不出去（铁律 12）。所以这里跟字幕、图片叠加走同一条路：
    ///   WPF 离屏渲染成 Pbgra32 位图 → 挂进 Compositor.Overlays → 合成时按 alpha 混进去。
    ///
    /// 成本：只在**显示的字符串或字号变了**才重渲染（倒计时一秒才变一次，字号变了才变一次），
    ///   一秒一块 600×150 的位图 ≈ 几十微秒，可以忽略；没变的时候合成线程只做一次 alpha 混合。
    ///
    /// 数字用 Consolas（等宽）：否则 1 和 8 宽度不同，每秒跳一次数字整块会左右抖。
    /// </summary>
    internal sealed class CountdownOverlay : IDisposable
    {
        private readonly int _cw, _ch;
        private readonly object _gate = new();

        private IntPtr _buf;
        private int _capW, _capH;
        private string _sig = "";
        private bool _disposed;

        /// <summary>挂到 Compositor.Overlays 的那一层（Z 比 8 路叠加(1000+) 高，永远压在最上面）。</summary>
        public Compositor.ImageLayer Layer { get; } = new() { Z = 2100, Visible = false };

        /// <summary>最近一次渲染出的不透明像素数（自检用：0 = 根本没画出东西）。</summary>
        public int LastOpaquePixels { get; private set; }

        /// <summary>最近一次渲染里「实心像素」的平均颜色（自检用：改颜色后这里会跟着变）。</summary>
        public (byte R, byte G, byte B) LastTextRgb { get; private set; }

        /// <summary>最近一次渲染用的字体（自检用）。</summary>
        public string LastFontFamily { get; private set; } = "";

        /// <summary>重渲染次数（自检用：样式/内容没变时不该涨）。</summary>
        public int RenderVersion { get; private set; }

        /// <summary>数字中心在画面上的归一化位置（0–1，默认正中）。2026-10-24 老板要「可以挪动位置」。</summary>
        public double Nx { get; private set; } = 0.5;
        public double Ny { get; private set; } = 0.5;

        /// <summary>这一层当前是否在出画（自检用）。</summary>
        public bool Visible => Layer.Visible;

        public CountdownOverlay(int canvasW, int canvasH, Compositor comp)
        {
            _cw = Math.Max(16, canvasW);
            _ch = Math.Max(16, canvasH);
            comp?.Overlays.Add(Layer);
        }

        /// <summary>
        /// 设置中心位置（归一化 0–1），并保证整块数字不出画布。
        /// ⚠ 只改合成器那一层的 X/Y，**不重渲染位图** —— 拖动时每帧都在调，
        ///   重渲染一次要几十毫秒，那样拖起来会一顿一顿的。
        /// </summary>
        public void SetPosition(double nx, double ny)
        {
            if (_disposed) return;
            lock (_gate)
            {
                double halfW = Layer.DstW / 2.0 / _cw;
                double halfH = Layer.DstH / 2.0 / _ch;
                Nx = Math.Clamp(nx, halfW, 1 - halfW);
                Ny = Math.Clamp(ny, halfH, 1 - halfH);
                ApplyPosition();
            }
        }

        /// <summary>锁内把归一化位置换算成像素坐标（调用方已持锁或本身就是单线程）。</summary>
        private void ApplyPosition()
        {
            Layer.X = (int)Math.Round(Nx * _cw - Layer.DstW / 2.0);
            Layer.Y = (int)Math.Round(Ny * _ch - Layer.DstH / 2.0);
        }

        /// <summary>
        /// 命中测试：输出画布上的这个像素点是否落在数字上（含 8px 容差，跟图层拖拽一个手感）。
        /// 叠加没开或还没渲染过 → 一律 false，避免"看不见的东西抢鼠标"。
        /// </summary>
        public bool Contains(int px, int py)
        {
            if (_disposed) return false;
            lock (_gate)
            {
                if (!Layer.Visible || Layer.Pixels == IntPtr.Zero || Layer.DstW <= 0) return false;
                // 容差 8 → 14（2026-10-25）：字号调小后数字块很小，8px 手指稍微偏一点就抓不住，
                // 表现就是「明明点在数字上却拖不动」。14px 仍然远小于整块画面，不会抢到图层。
                const int pad = 14;
                return px >= Layer.X - pad && px <= Layer.X + Layer.DstW + pad
                    && py >= Layer.Y - pad && py <= Layer.Y + Layer.DstH + pad;
            }
        }

        /// <summary>当前数字在画面上的像素矩形 (x, y, w, h)，自检/状态栏用。</summary>
        public (int X, int Y, int W, int H) RectNow()
        {
            lock (_gate) return (Layer.X, Layer.Y, Layer.DstW, Layer.DstH);
        }

        /// <summary>
        /// 更新显示（**必须在 UI 线程调用**）。
        /// </summary>
        /// <param name="text">要显示的字，如 1:00:00 / -00:07。</param>
        /// <param name="baseFontPx">按 1080p 基准的字号（面板上 40–300），这里按画布高度等比换算。</param>
        /// <param name="visible">叠加开关。</param>
        /// <param name="negative">是否已超时（默认走红色提示）。</param>
        /// <param name="style">字体 / 粗斜体 / 文字色 / 底板色（2026-10-24 老板要「字体/颜色自定义」）。</param>
        /// <returns>本次是否真的重渲染了。</returns>
        public bool Update(string text, double baseFontPx, bool visible, bool negative, CountdownStyle style)
        {
            if (_disposed) { lock (_gate) Layer.Visible = false; return false; }

            if (!visible)
            {
                lock (_gate) Layer.Visible = false;
                return false;
            }

            // 竖屏 1080×1920 也按短边换算，竖屏里字不会突然变两倍大
            double basis = Math.Min(_cw, _ch);
            double px = Math.Round(Math.Clamp(baseFontPx, 12, 400) * basis / 1080.0);

            // 签名必须把**样式**算进去：否则老板换了字体/颜色，内容没变就永远不重渲染
            string sig = $"{text}|{px:0}|{(negative ? "n" : "p")}|{style.Signature}";
            lock (_gate)
            {
                if (sig == _sig && Layer.Pixels != IntPtr.Zero)
                {
                    Layer.Visible = true;
                    return false;
                }
            }

            bool ok = Render(text, px, negative, style);
            if (ok) _sig = sig;
            else lock (_gate) Layer.Visible = false;
            return ok;
        }

        /// <summary>把「底板 + 文字」渲染成位图，按 Nx/Ny 摆到画面上。</summary>
        private bool Render(string text, double px, bool negative, CountdownStyle style)
        {
            try
            {
                Color fg = CountdownStyle.Parse(style.TextColorHex, Colors.White);
                if (negative) fg = CountdownStyle.Parse(style.NegativeColorHex, Color.FromRgb(0xFF, 0x5A, 0x5A));

                var tb = new TextBlock
                {
                    Text = text,
                    FontSize = px,
                    FontWeight = style.Bold ? FontWeights.Bold : FontWeights.Normal,
                    FontStyle = style.Italic ? FontStyles.Italic : FontStyles.Normal,
                    FontFamily = new FontFamily(string.IsNullOrWhiteSpace(style.FontFamilyName)
                                                ? "Consolas" : style.FontFamilyName),
                    Foreground = new SolidColorBrush(fg),
                    TextWrapping = TextWrapping.NoWrap,
                    // 竖直方向留一点余量，避免像素级裁掉字形上下沿
                    LineHeight = px * 1.12,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight
                };

                // 底衬：默认半透明黑圆角板 —— 比纯描边稳，画面再花也看得清（老板可换色/去掉）
                var box = new Border
                {
                    Background = new SolidColorBrush(CountdownStyle.Parse(style.BackColorHex, Color.FromArgb(0x66, 0, 0, 0))),
                    CornerRadius = new CornerRadius(px * 0.16),
                    Padding = new Thickness(px * 0.30, px * 0.08, px * 0.30, px * 0.08),
                    Child = tb
                };

                box.Measure(new Size(_cw, _ch));
                double dw = box.DesiredSize.Width, dh = box.DesiredSize.Height;
                if (dw < 1 || dh < 1) return false;

                // 字号拉到最大也不许超出画布（超出就等比缩，宁可小一点也不裁掉位数字）
                double k = Math.Min(1.0, Math.Min(_cw / dw, _ch / dh));
                int w = Math.Max(1, (int)Math.Ceiling(dw * k));
                int h = Math.Max(1, (int)Math.Ceiling(dh * k));

                box.Arrange(new Rect(0, 0, w, h));
                box.UpdateLayout();

                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(box);

                int stride = w * 4;
                var pixels = new byte[stride * h];
                rtb.CopyPixels(pixels, stride, 0);

                int solid = 0;
                long sr = 0, sg = 0, sb = 0; int lit = 0;
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    byte a = pixels[i + 3];
                    if (a > 8) solid++;
                    // 只统计"实心"像素算平均色 → 得到的是文字色（底板是半透明的，被排除）
                    if (a > 200) { sb += pixels[i]; sg += pixels[i + 1]; sr += pixels[i + 2]; lit++; }
                }

                lock (_gate)
                {
                    if (_buf == IntPtr.Zero || _capW != w || _capH != h)
                    {
                        if (_buf != IntPtr.Zero) Marshal.FreeHGlobal(_buf);
                        _buf = Marshal.AllocHGlobal(pixels.Length);
                        _capW = w;
                        _capH = h;
                    }
                    Marshal.Copy(pixels, 0, _buf, pixels.Length);

                    Layer.Pixels = _buf;
                    Layer.W = w;
                    Layer.H = h;
                    Layer.Stride = stride;
                    Layer.DstW = w;
                    Layer.DstH = h;

                    // 位置按老板拖过的地方摆；换字号后块变大，顺手夹回画布内（不许整块飘出去）
                    double halfW = w / 2.0 / _cw;
                    double halfH = h / 2.0 / _ch;
                    Nx = Math.Clamp(Nx, halfW, 1 - halfW);
                    Ny = Math.Clamp(Ny, halfH, 1 - halfH);
                    ApplyPosition();

                    Layer.Visible = true;
                }

                LastOpaquePixels = solid;
                if (lit > 0) LastTextRgb = ((byte)(sr / lit), (byte)(sg / lit), (byte)(sb / lit));
                LastFontFamily = style.FontFamilyName;
                RenderVersion++;
                return true;
            }
            catch (Exception ex)
            {
                try { Services.ErrorLog.Info("[倒计时] 叠加渲染失败：" + ex.Message); } catch { }
                return false;
            }
        }

        public void Dispose()
        {
            _disposed = true;
            lock (_gate)
            {
                if (_buf != IntPtr.Zero) { Marshal.FreeHGlobal(_buf); _buf = IntPtr.Zero; }
                Layer.Pixels = IntPtr.Zero;
                Layer.Visible = false;
            }
        }
    }
}
