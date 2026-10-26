using System;

namespace SynthPoc.Engine
{
    /// <summary>
    /// 超小缩略图：把一路 vmem 帧按等比内接缩到几百像素的小位图（预监窗用）。
    ///
    /// 为什么不用 WPF 的 Image + ScaleTransform：
    ///   那还是要把整帧 1920×1080 上传到 UI 线程，10fps 就是每秒 80MB 的拷贝 + 布局换算，
    ///   界面一忙预监就掉帧。这里先在合成侧抽点缩到 320×180，再一次性传上去。
    /// </summary>
    internal static unsafe class Thumbnail
    {
        /// <summary>把源帧等比内接画进 dst（BGRA，dw×dh，stride = dw*4），空余处填黑。</summary>
        public static void Render(IntPtr src, int sw, int sh, int ss, byte[] dst, int dw, int dh)
        {
            if (src == IntPtr.Zero || sw <= 0 || sh <= 0) return;
            if (dst.Length < dw * dh * 4) return;

            Array.Clear(dst, 0, dw * dh * 4);

            double scale = Math.Min((double)dw / sw, (double)dh / sh);
            int bw = Math.Max(1, (int)Math.Round(sw * scale));
            int bh = Math.Max(1, (int)Math.Round(sh * scale));
            int bx = (dw - bw) / 2;
            int by = (dh - bh) / 2;

            fixed (byte* dp = dst)
            {
                for (int y = 0; y < bh; y++)
                {
                    int sy = (int)((long)y * sh / bh);
                    uint* srow = (uint*)((byte*)src + (long)sy * ss);
                    uint* drow = (uint*)(dp + (long)(by + y) * dw * 4) + bx;
                    for (int x = 0; x < bw; x++)
                    {
                        int sx = (int)((long)x * sw / bw);
                        drow[x] = srow[sx];
                    }
                }
            }
        }
    }
}
