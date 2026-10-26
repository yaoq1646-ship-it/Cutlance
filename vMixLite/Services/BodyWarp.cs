using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Cutlance.Models;

namespace Cutlance.Services
{
    /// <summary>
    /// 模块10：美体「分段局部形变」的坐标映射图生成器。
    ///
    /// 原理：FFmpeg 的 remap 滤镜按两张映射图逐像素重采样——
    ///   映射图像素值 = 源图坐标（16 位灰度 PGM，大端，值就是像素坐标本身，不是 0~65535 归一化）；
    ///   本类按「腰线→臀线横向压窄 + 臀线以下纵向拉长」算出这两张图。
    ///
    /// 为什么这么做：这样才是真正的「分段」——腰部单独收窄、腿部单独拉长，
    /// 头部与胸部完全不动（过渡带羽化，无接缝），能实际改变腰臀比与腿身比的观感；
    /// 而整幅近似方式是把整个画面一起缩放/桶形，改不了部位。
    ///
    /// 生成结果按参数哈希缓存到 %TEMP%\Cutlance\warp，参数不变则直接复用（4 MB 左右两张图）。
    /// </summary>
    public static class BodyWarp
    {
        /// <summary>映射图缓存目录。</summary>
        public static string CacheDir =>
            Path.Combine(Path.GetTempPath(), "Cutlance", "warp");

        /// <summary>
        /// 确保映射图存在并返回两个文件路径。返回 false 时 xmap/ymap 为 null
        /// （不需要 remap，属正常情况）；error 非空表示生成失败。
        /// </summary>
        public static bool Ensure(VideoFx fx, int w, int h,
            out string? xmap, out string? ymap, out string error)
        {
            xmap = null;
            ymap = null;
            error = "";
            if (fx == null || !fx.WarpActive) return false;
            if (w < 8 || h < 8) { error = $"输出分辨率过小（{w}×{h}），分段形变需要至少 8×8"; return false; }

            try
            {
                Directory.CreateDirectory(CacheDir);
                var key = Key(fx, w, h);
                var xp = Path.Combine(CacheDir, "x_" + key + ".pgm");
                var yp = Path.Combine(CacheDir, "y_" + key + ".pgm");
                if (File.Exists(xp) && File.Exists(yp))
                {
                    xmap = xp; ymap = yp;
                    return true;
                }

                Generate(fx, w, h, xp, yp);
                TrimCache();
                xmap = xp; ymap = yp;
                return true;
            }
            catch (Exception ex)
            {
                error = "生成分段形变映射图失败：" + ex.Message;
                xmap = ymap = null;
                return false;
            }
        }

        /// <summary>按参数生成一组缓存键（参数变则重算）。</summary>
        private static string Key(VideoFx fx, int w, int h)
        {
            var s = string.Format(CultureInfo.InvariantCulture,
                // ⚠ 开头的 v2 是「算法版本」：改了映射公式就必须改它。
                //   否则同一套参数会命中旧公式生成的缓存图，改了代码却看不到效果（10-18 踩过）。
                "v2_{0}x{1}_{2:0.##}_{3:0.##}_{4}_{5}_{6:0.##}_{7:0.##}_{8:0.####}",
                w, h, fx.WaistY, fx.HipY, fx.SlimSeg, fx.LegSeg, fx.WarpCenterX, fx.WarpWidth,
                // ⚠ 有效宽高比必须进键：同一套滑块在「横屏信号进竖屏框」和「竖屏信号满框」
                //   两种情况下算出的映射图完全不同，漏进键会直接复用到错的那张。
                fx.ContentAspect);
            // FNV-1a 32 位：短、稳定、够用（仅用于文件名）
            unchecked
            {
                uint hash = 2166136261;
                foreach (var ch in s)
                {
                    hash ^= ch;
                    hash *= 16777619;
                }
                return hash.ToString("x8");
            }
        }

        /// <summary>计算并写出两张映射图（16 位大端 PGM，值 = 源坐标）。</summary>
        private static void Generate(VideoFx fx, int w, int h, string xPath, string yPath)
        {
            // —— 有效画面区域（老板 2026-10-18 第 4 条：要跟着进来的信号走）——
            // 竖屏框里放横屏信号时，画面只占中间一条，上下是黑边；
            // 腰线/臀线必须按「那条带」的百分比算，否则 46% 会落到黑边上，整段形变错位。
            var (ax, ay, aw, ah) = fx.ActiveRect(w, h);
            double activeBottom = ay + ah;

            // —— 参数换算（全部以有效画面为基准）——
            double waistY = ay + fx.WaistY / 100.0 * ah;
            double hipY = ay + fx.HipY / 100.0 * ah;
            double minGap = Math.Max(4.0, 0.03 * ah);
            if (hipY < waistY + minGap) hipY = waistY + minGap;      // 防腰/臀线倒置
            double feather = Math.Max(3.0, 0.06 * ah);               // 曲线过渡带高度

            double slim = fx.SlimSeg / 10.0 * 0.22;                  // 最多 22% 横向压窄
            double s = Math.Max(0.5, 1.0 - slim);
            double A = (1.0 / s) - 1.0;                              // 横向采样放大系数
            double legK = 1.0 + fx.LegSeg / 10.0 * 0.30;             // 最多 +30% 腿段拉长

            double cx = ax + fx.WarpCenterX / 100.0 * aw;                 // 作用中心（有效画面内）
            double r0 = Math.Max(0.08 * aw, fx.WarpWidth / 200.0 * aw);   // 作用半径（半宽）
            double rFit = 0.70 * r0;                                 // 全效半径，之外逐渐衰减到 0
            double maxX = w - 1.0, maxY = h - 1.0;

            var xb = new byte[w * h * 2];
            var yb = new byte[w * h * 2];

            for (int y = 0; y < h; y++)
            {
                // —— 腿段纵向映射：臀线以下逐渐拉长（平滑起步，避免折痕）——
                // ⚠ 只在有效画面内做：底黑边要是跟着一起拉长，会在黑边里拉出接缝。
                //   有效画面满到帧底时（竖屏信号进竖屏框）行为与原来完全一致。
                double ySrc;
                if (y >= hipY && y < activeBottom)
                {
                    double kk = 1.0 + (legK - 1.0) * SmoothStep(0, feather, y - hipY);
                    ySrc = hipY + (y - hipY) / kk;
                    if (ySrc > activeBottom) ySrc = activeBottom;
                }
                else ySrc = y;
                if (ySrc < 0) ySrc = 0; else if (ySrc > maxY) ySrc = maxY;
                int yv = (int)Math.Round(ySrc);

                // —— 腰段纵向权重：腰线以上渐入、臀线以下渐出 ——
                double wy = SmoothStep(waistY - feather, waistY, y) *
                            (1.0 - SmoothStep(hipY, hipY + feather, y));

                int rowBase = y * w * 2;
                for (int x = 0; x < w; x++)
                {
                    // —— 腰段横向：整段朝中心压窄，两端按作用宽度平滑衰减 ——
                    // ⚠ 符号必须是「往外扩散采样」：输出像素 x 去源图取 cx + (x-cx)/s。
                    //   写成 x + (cx-x)*A（= 往里收采样）会让画面里的人**变胖**，
                    //   却和纵向腿段（用除法）方向相反 —— 2026-10-18 真人竖屏素材实测暴露。
                    double xSrc;
                    if (wy > 0.001 && A > 0.0001)
                    {
                        double u = Math.Abs(x - cx);
                        double tap = 1.0 - SmoothStep(rFit, r0, u);
                        xSrc = x + (x - cx) * A * wy * tap;
                    }
                    else xSrc = x;
                    if (xSrc < 0) xSrc = 0; else if (xSrc > maxX) xSrc = maxX;
                    int xv = (int)Math.Round(xSrc);

                    int o = rowBase + x * 2;
                    xb[o] = (byte)((xv >> 8) & 0xFF); xb[o + 1] = (byte)(xv & 0xFF);
                    yb[o] = (byte)((yv >> 8) & 0xFF); yb[o + 1] = (byte)(yv & 0xFF);
                }
            }

            var header = Encoding.ASCII.GetBytes(string.Format(CultureInfo.InvariantCulture,
                "P5\n{0} {1}\n65535\n", w, h));
            WriteAtomic(xPath, header, xb);
            WriteAtomic(yPath, header, yb);
        }

        /// <summary>
        /// 控制缓存体积：每套映射图（1080p）约 8 MB，拖滑块会不断产生新键，
        /// 因此只保留最近 16 个文件，其余按最后写入时间清掉。失败一律忽略。
        /// </summary>
        private static void TrimCache()
        {
            try
            {
                var files = new DirectoryInfo(CacheDir).GetFiles("*.pgm");
                if (files.Length <= 16) return;
                foreach (var f in files.OrderByDescending(x => x.LastWriteTimeUtc).Skip(16))
                {
                    try { f.Delete(); } catch { }
                }
            }
            catch { }
        }

        /// <summary>先写临时文件再改名，避免 ffmpeg 读到半截文件。</summary>
        private static void WriteAtomic(string path, byte[] header, byte[] body)
        {
            var tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(header, 0, header.Length);
                fs.Write(body, 0, body.Length);
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        private static double SmoothStep(double e0, double e1, double x)
        {
            if (e1 <= e0) return x >= e1 ? 1.0 : 0.0;
            double t = (x - e0) / (e1 - e0);
            if (t <= 0) return 0.0;
            if (t >= 1) return 1.0;
            return t * t * (3 - 2 * t);
        }
    }
}
