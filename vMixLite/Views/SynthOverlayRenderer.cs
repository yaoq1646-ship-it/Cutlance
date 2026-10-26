using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cutlance.Models;
using SynthPoc.Engine;

namespace Cutlance.Views
{
    /// <summary>
    /// 把工程里的叠加通道（文字标题 / 图片）渲染成合成器能直接混的位图。
    ///
    /// 为什么走 WPF 离屏渲染而不是自己在合成器里描字：
    ///   字幕的字体、粗细、斜体、描边、图片缩放，主界面已经有一套成熟效果，
    ///   自己再写一遍排版必然两边不一致（老板改字号，输出和监看长得不一样 = 事故）。
    ///   所以这里用同一套 WPF 元素，只是把它渲染到内存位图，再交给 CPU 合成器。
    ///
    /// 成本控制：
    ///   · 逐通道渲染，而不是整屏 1920×1080 —— 一块字幕条通常只有 40 万像素，
    ///     整屏是 200 万，混一遍差 5 倍。
    ///   · 只有内容变了（文字/位置/字号/图片）才重渲染；没变就一直复用同一块内存。
    ///   · 读写各持自己的锁：UI 线程重渲染不会把合成线程卡住几十毫秒。
    /// </summary>
    internal sealed class SynthOverlayRenderer : IDisposable
    {
        private sealed class Slot
        {
            public OverlayItem Item = null!;
            public Compositor.ImageLayer Layer = null!;
            public IntPtr Buf;
            public int CapW, CapH;          // 已分配缓冲尺寸
            public string Signature = "";
            public readonly object Gate = new();
        }

        private readonly Dictionary<OverlayItem, Slot> _slots = new();
        private readonly int _cw, _ch;
        private bool _disposed;

        public SynthOverlayRenderer(int canvasW, int canvasH)
        {
            _cw = canvasW;
            _ch = canvasH;
        }

        public int ActiveCount
        {
            get
            {
                int n = 0;
                foreach (var s in _slots.Values) if (s.Layer.Visible) n++;
                return n;
            }
        }

        /// <summary>
        /// 同步一轮（必须在 UI 线程调用）。内容变了的通道会重渲染，没变的原样保留。
        /// 返回本次重渲染了几个通道（自检/日志用）。
        /// </summary>
        public int Sync(IReadOnlyList<OverlayItem> items, Compositor comp)
        {
            if (_disposed || comp == null) return 0;
            int rendered = 0;

            // ① 下线：通道被清空/删除 → 从合成器摘掉并释放缓冲
            var alive = new HashSet<OverlayItem>(items);
            var dead = new List<OverlayItem>();
            foreach (var kv in _slots)
                if (!alive.Contains(kv.Key)) dead.Add(kv.Key);
            foreach (var o in dead) RemoveSlot(o, comp);

            // ② 每个有内容的通道：算签名，变了就重渲染
            for (int i = 0; i < items.Count; i++)
            {
                var o = items[i];
                if (!o.IsOn)
                {
                    if (_slots.TryGetValue(o, out var off)) { off.Layer.Visible = false; }
                    continue;
                }

                string sig = Signature(o);
                if (_slots.TryGetValue(o, out var slot) && slot.Signature == sig && slot.Layer.Pixels != IntPtr.Zero)
                {
                    slot.Layer.Visible = true;
                    continue;
                }

                if (slot == null)
                {
                    slot = new Slot
                    {
                        Item = o,
                        Layer = new Compositor.ImageLayer { Z = 1000 - o.ChannelIndex }
                    };
                    _slots[o] = slot;
                    comp.Overlays.Add(slot.Layer);
                }

                if (RenderSlot(slot, o)) rendered++;
                slot.Signature = sig;
            }

            return rendered;
        }

        private static string Signature(OverlayItem o)
        {
            return string.Join("|",
                o.Type, o.Text, o.ImagePath, o.ImageScale.ToString("0.##"),
                o.XPct.ToString("0.##"), o.YPct.ToString("0.##"),
                o.FontSize, o.Bold, o.Italic, o.FontFamilyName, o.ColorHex,
                o.HasContent, o.Visible);
        }

        /// <summary>渲染一个通道到它自己的非托管缓冲。</summary>
        private bool RenderSlot(Slot s, OverlayItem o)
        {
            try
            {
                FrameworkElement el = BuildElement(o);
                if (el == null) { s.Layer.Visible = false; return false; }

                // 先量出真实尺寸（限制在画布之内，超出的等比缩）
                el.Measure(new Size(_cw, _ch));
                double dw = el.DesiredSize.Width, dh = el.DesiredSize.Height;
                if (dw < 1 || dh < 1) { s.Layer.Visible = false; return false; }

                double k = Math.Min(1.0, Math.Min((double)_cw / dw, (double)_ch / dh));
                int w = Math.Max(1, (int)Math.Ceiling(dw * k));
                int h = Math.Max(1, (int)Math.Ceiling(dh * k));

                el.Arrange(new Rect(0, 0, w, h));

                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(el);

                int stride = w * 4;
                int bytes = stride * h;
                var px = new byte[bytes];
                rtb.CopyPixels(px, stride, 0);

                lock (s.Gate)
                {
                    if (s.Buf == IntPtr.Zero || s.CapW != w || s.CapH != h)
                    {
                        if (s.Buf != IntPtr.Zero) Marshal.FreeHGlobal(s.Buf);
                        s.Buf = Marshal.AllocHGlobal(bytes);
                        s.CapW = w;
                        s.CapH = h;
                    }
                    Marshal.Copy(px, 0, s.Buf, bytes);

                    s.Layer.Pixels = s.Buf;
                    s.Layer.W = w;
                    s.Layer.H = h;
                    s.Layer.Stride = stride;

                    // 中心对齐到 (XPct%, YPct%)，与主界面 PositionOverlay 的规则一致
                    int cx = (int)Math.Round(o.XPct / 100.0 * _cw);
                    int cy = (int)Math.Round(o.YPct / 100.0 * _ch);
                    s.Layer.X = cx - w / 2;
                    s.Layer.Y = cy - h / 2;
                    s.Layer.DstW = w;
                    s.Layer.DstH = h;
                    s.Layer.Visible = true;

                    // 自检：非透明像素数 = 真的画出东西了吗（文字/图片渲染失败时这里会是 0）
                    if (Environment.GetEnvironmentVariable("CUTLANCE_SYNTH_OVDEBUG") == "1")
                    {
                        int solid = 0;
                        for (int i = 3; i < px.Length; i += 4) if (px[i] > 8) solid++;
                        Services.ErrorLog.Info(
                            $"[切换台] 叠加#{o.ChannelIndex} {o.Type} {w}×{h} @({s.Layer.X},{s.Layer.Y}) " +
                            $"不透明像素 {solid}/{w * h}");
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Services.ErrorLog.Info($"[切换台] 叠加通道 {o.ChannelIndex} 渲染失败：{ex.Message}");
                s.Layer.Visible = false;
                return false;
            }
        }

        private FrameworkElement? BuildElement(OverlayItem o)
        {
            if (o.Type == OverlayType.Text)
            {
                Color c;
                try { c = (Color)ColorConverter.ConvertFromString(o.ColorHex); }
                catch { c = Colors.White; }

                return new TextBlock
                {
                    Text = o.Text ?? "",
                    FontSize = o.FontSize > 0 ? o.FontSize : 36,
                    FontWeight = o.Bold ? FontWeights.Bold : FontWeights.Normal,
                    FontStyle = o.Italic ? FontStyles.Italic : FontStyles.Normal,
                    FontFamily = new FontFamily(string.IsNullOrWhiteSpace(o.FontFamilyName)
                                                ? "Microsoft YaHei UI" : o.FontFamilyName),
                    Foreground = new SolidColorBrush(c),
                    TextWrapping = TextWrapping.Wrap,
                    // 字幕要压在画面上看得清：加一圈深色描边（比投影稳，不挑背景）
                    // 用 Stroke 会给字形描边，但 TextBlock 没有 Stroke —— 用阴影代替
                    Effect = new System.Windows.Media.Effects.DropShadowEffect
                    {
                        Color = Colors.Black,
                        BlurRadius = 6,
                        ShadowDepth = 0,
                        Opacity = 0.9
                    }
                };
            }

            if (string.IsNullOrWhiteSpace(o.ImagePath) || !System.IO.File.Exists(o.ImagePath))
                return null;

            double cw = _cw, chh = _ch;
            double k = Math.Clamp(o.ImageScale / 100.0, 0.05, 4.0);

            var bmp = new BitmapImage();
            bmp.BeginInit();
            // ⚠ 必须 OnLoad：默认 OnDemand 是惰性解码，等 RenderTargetBitmap.Render 时
            //   图片还没解完 → 渲染出一张全透明的空图（实测现象：文字上去了、图片没上去，
            //   日志还显示"重渲染成功"，因为没有异常）。
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;   // 同一路径换图要能刷新（铁律 23 同理）
            bmp.UriSource = new Uri(o.ImagePath, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();

            // 目标尺寸：只缩不放 —— 与主界面口径严格一致。
            // 主界面里 Image 只设了 MaxWidth/MaxHeight、不设 Width，
            // 摆进 Canvas（不受约束）时元素保持原始像素尺寸，Max* 只当上限。
            // 这里要是按 maxW 放大，小图会被撑到 40% 画布宽，中心对齐后左右各溢出画布外。
            double maxW = cw * k, maxH = chh * k;
            double iw = bmp.PixelWidth > 0 ? bmp.PixelWidth : 1;
            double ih = bmp.PixelHeight > 0 ? bmp.PixelHeight : 1;
            double sc = Math.Min(1.0, Math.Min(maxW / iw, maxH / ih));
            int tw = Math.Max(1, (int)Math.Round(iw * sc));
            int th = Math.Max(1, (int)Math.Round(ih * sc));

            var img = new Image
            {
                Source = bmp,
                Stretch = Stretch.Fill,
                // ⚠ 必须显式给宽高。用 Viewbox/Stretch=Uniform 包一层的话，
                //   Measure(1920,1080) 会返回「填满约束」的 1920×768，
                //   再按中心对齐就把图推到画布外面去了（实测渲染成 1920×768 @(-672,-190)，
                //   日志里"不透明像素 146 万"看着很成功，实际画面里一个像素都看不到）。
                Width = tw,
                Height = th
            };
            return img;
        }

        private void RemoveSlot(OverlayItem o, Compositor comp)
        {
            if (!_slots.TryGetValue(o, out var s)) return;
            _slots.Remove(o);
            comp.Overlays.Remove(s.Layer);
            lock (s.Gate)
            {
                if (s.Buf != IntPtr.Zero) { Marshal.FreeHGlobal(s.Buf); s.Buf = IntPtr.Zero; }
                s.Layer.Pixels = IntPtr.Zero;
            }
        }

        public void Dispose()
        {
            _disposed = true;
            foreach (var s in _slots.Values)
            {
                lock (s.Gate)
                {
                    if (s.Buf != IntPtr.Zero) { Marshal.FreeHGlobal(s.Buf); s.Buf = IntPtr.Zero; }
                    s.Layer.Pixels = IntPtr.Zero;
                }
            }
            _slots.Clear();
        }
    }
}
