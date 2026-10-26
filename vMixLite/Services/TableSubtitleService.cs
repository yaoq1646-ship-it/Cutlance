using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Cutlance.Services
{
    /// <summary>一张表读出来的内容：表头 + 每行单元格文本。</summary>
    public sealed class TableData
    {
        public string SheetName { get; set; } = "";
        public List<string> Headers { get; } = new();
        public List<string[]> Rows { get; } = new();

        /// <summary>某一行的简短摘要（状态栏用）：优先取第 1 列。</summary>
        public string SummaryOf(int index)
        {
            if (index < 0 || index >= Rows.Count) return "";
            var row = Rows[index];
            foreach (var c in row)
                if (!string.IsNullOrWhiteSpace(c)) return c;
            return "(空行)";
        }

        /// <summary>造一条示例行（样式预览用；表还没导入时也能看到效果）。</summary>
        public static string[] SampleRow() => new[]
        {
            "张旭旭  教授", "南京鼓楼医院集团宿迁医院", "示例备注"
        };
    }

    /// <summary>
    /// 字幕条外观参数。
    ///
    /// 老板 2026-10-19 21:42：
    ///   「1 设定把 字幕条大小 以及所处位置 都是可以移动的 …
    ///     3 字幕条文字颜色可以更换」
    ///
    /// 大小 / 位置 / 颜色全部在这里调，改完重渲染 PNG 即可 —— 不用碰叠加层逻辑。
    /// ⚠ 老板 2026-10-19 22:15 定了口径：「通道编辑也不能动字幕条了」——
    ///   所以叠加层编辑区的位置/缩放对字幕那一路是**锁定**的（50 / 50 / 100），
    ///   这里才是唯一能挪字幕条的地方。原因很实在：叠加层缩放会把整幅 1920×1080 图放大，
    ///   字幕条跟着一起放大、还会被画布边缘裁掉；在这里改则所有条子统一、翻页不跑位。
    /// </summary>
    public sealed class SubtitleStyle
    {
        /// <summary>第 1 列字号（主字幕）。</summary>
        public double MainFontSize { get; set; } = 68;

        /// <summary>第 2 列字号（副字幕）。</summary>
        public double SubFontSize { get; set; } = 40;

        /// <summary>第 3 列起的字号（补充信息）。</summary>
        public double ExtraFontSize { get; set; } = 30;

        /// <summary>条子宽度（像素；画布 1920 宽）。</summary>
        public double BarWidth { get; set; } = 1280;

        /// <summary>条子底边距画面底边多少像素（越大越往上）。</summary>
        public double MarginBottom { get; set; } = 110;

        /// <summary>水平偏移（像素，正数向右）。0 = 正中。</summary>
        public double OffsetX { get; set; }

        public string MainColor { get; set; } = "#FFFFFFFF";
        public string SubColor { get; set; } = "#FFDAE2EC";
        public string ExtraColor { get; set; } = "#FFB9C4D0";
        public string AccentColor { get; set; } = "#FF6EC7E8";

        /// <summary>条子底色（RGB，透明度单独走 <see cref="BackAlpha"/>）。</summary>
        public string BackColor { get; set; } = "#10141A";

        /// <summary>条子背景不透明度 0–255（0 = 全透明，只剩字，压在画面上不遮内容）。</summary>
        public int BackAlpha { get; set; } = 0xCC;

        /// <summary>左侧那道强调竖条要不要。</summary>
        public bool ShowAccent { get; set; } = true;

        /// <summary>主字幕是否加粗。</summary>
        public bool BoldMain { get; set; } = true;

        public string FontFamily { get; set; } = "微软雅黑";

        public SubtitleStyle Clone() => new()
        {
            MainFontSize = MainFontSize, SubFontSize = SubFontSize, ExtraFontSize = ExtraFontSize,
            BarWidth = BarWidth, MarginBottom = MarginBottom, OffsetX = OffsetX,
            MainColor = MainColor, SubColor = SubColor, ExtraColor = ExtraColor, AccentColor = AccentColor,
            BackColor = BackColor, BackAlpha = BackAlpha, ShowAccent = ShowAccent,
            BoldMain = BoldMain, FontFamily = FontFamily,
        };
    }

    /// <summary>
    /// 表格 → 字幕条（老板 2026-10-19 20:38：
    /// 「通过读取表格中不同行列，可以实现一个表格中不同行列通过翻页直接实现上屏幕，
    ///   一次把很多字幕都轻松实现」）。
    ///
    /// 做法：
    ///   ① 用 Excel COM 把整张表读成「表头 + 每行单元格」（只读、隐藏窗口、不碰剪贴板）；
    ///   ② **每一行渲染成一张 1920×1080 的透明 PNG**，内容是一条压在画面底部的字幕条
    ///      （第 1 列大字 = 主字幕，第 2 列小字 = 副字幕，第 3 列起拼成第三行）；
    ///   ③ 整串图片挂到某一路**图片叠加通道**上，翻页 = 换 `ImagePath`。
    ///
    /// 为什么不用「整页表格图」（那是 `+表格` 的用法）：
    ///   投屏看报表才需要整页；播字幕要的是「一条一条上屏」，
    ///   一行一张透明字幕条才能压在摄像/画面上，而且翻页手感就是「下一条字幕」。
    ///
    /// 为什么是透明 PNG：图片叠加通道本来就是按 PNG 透明通道合成的，
    ///   字幕条之外的区域留空，节目画面一点不受影响。
    /// </summary>
    public static class TableSubtitleService
    {
        public const int CanvasWidth = 1920;
        public const int CanvasHeight = 1080;

        /// <summary>最多读这么多行（防止有人把整本流水账塞进来把内存吃光）。</summary>
        public const int MaxRows = 300;

        /// <summary>最多认这么多列（多出来的忽略，字幕条放不下）。</summary>
        private const int MaxCols = 8;

        /// <summary>读表 + 逐行出图。返回 (表格内容, 每行一张 PNG 的绝对路径)。</summary>
        public static Task<(TableData Data, List<string> Images)> BuildAsync(
            string xlsxPath, string outDir, SubtitleStyle? style = null)
            => RunStaAsync(() => Build(xlsxPath, outDir, style ?? new SubtitleStyle()));

        /// <summary>按新样式重渲染一遍（表已经读进内存，不用再开 Excel）。</summary>
        public static Task<List<string>> RerenderAsync(TableData data, SubtitleStyle style, string outDir)
            => RunStaAsync(() => RenderAll(data, style, outDir));

        /// <summary>
        /// 渲染一张预览图（样式窗口里实时看效果）。
        /// 带底色那版（withBackdrop = true）会画一层深灰渐变模拟节目画面，
        /// 否则透明区域在窗口里看不见，就判断不出字幕条到底压在画面的什么位置。
        ///
        /// <paramref name="scale"/>：输出尺寸倍率，默认 1.0（1920×1080）。
        ///   绘制永远按 1920×1080 的逻辑坐标算，缩放只作用在最后的位图上，
        ///   所以缩小渲染也不会让字幕条位置偏一点（需要省 CPU 时才用 0.5）。
        /// </summary>
        public static BitmapSource RenderPreview(string[] cells, SubtitleStyle style, bool withBackdrop,
                                                 double scale = 1.0)
            => RenderBitmap(cells, style, withBackdrop, scale);

        private static Task<T> RunStaAsync<T>(Func<T> work)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var th = new Thread(() =>
            {
                try { tcs.TrySetResult(work()); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            })
            { IsBackground = true, Name = "cutlance-table-subtitle" };
            th.SetApartmentState(ApartmentState.STA);   // Excel COM 与 WPF 离屏渲染都要求 STA
            th.Start();
            return tcs.Task;
        }

        // ================= ① 读表 =================

        private static (TableData, List<string>) Build(string path, string outDir, SubtitleStyle style)
        {
            var data = ReadTable(path);
            if (data.Rows.Count == 0)
                throw new InvalidOperationException("这张表里没有可用的数据行（第一行当归为表头了）。");
            return (data, RenderAll(data, style, outDir));
        }

        /// <summary>
        /// 逐行出图。
        ///
        /// ⚠ **必须写到一个全新目录**：WPF 的 `BitmapImage(new Uri(路径))` 按路径缓存，
        ///   同一路径重写文件后画面不会刷新（这是老坑）。
        ///   重渲染时换一个 guid 目录 → 路径全变 → 天然绕开缓存，旧目录留着也不占多少空间。
        /// </summary>
        private static List<string> RenderAll(TableData data, SubtitleStyle style, string outDir)
        {
            Directory.CreateDirectory(outDir);
            var images = new List<string>(data.Rows.Count);
            for (int i = 0; i < data.Rows.Count; i++)
            {
                string png = Path.Combine(outDir, $"sub{i + 1:0000}.png");
                RenderRow(data.Rows[i], style, png);
                images.Add(png);
            }
            return images;
        }

        private static TableData ReadTable(string path)
        {
            var progId = Type.GetTypeFromProgID("Excel.Application");
            if (progId == null)
                throw new InvalidOperationException("这台电脑没有安装 Excel（或 COM 未注册），读不了表格。");

            var data = new TableData();
            dynamic? app = null;
            dynamic? wb = null;
            dynamic? ws = null;
            try
            {
                app = Activator.CreateInstance(progId)!;
                app.Visible = false;
                app.DisplayAlerts = false;
                wb = app.Workbooks.Open(path, 0, true);        // Open(FileName, UpdateLinks, ReadOnly)
                ws = wb.Worksheets.Item(1);                    // 第 1 张工作表
                try { data.SheetName = (string)ws.Name; } catch { }

                object? v = ws.UsedRange.Value;                // object[,]（1 基），单格时是标量
                if (v is not Array arr)
                {
                    // 只有一个单元格 —— 当表头读，没有数据行
                    data.Headers.Add(CellText(v));
                    return data;
                }

                int rLo = arr.GetLowerBound(0), rHi = arr.GetUpperBound(0);
                int cLo = arr.GetLowerBound(1), cHi = arr.GetUpperBound(1);
                int cols = Math.Min(cHi - cLo + 1, MaxCols);
                if (cols <= 0) return data;

                // 第一行 = 表头
                for (int c = 0; c < cols; c++) data.Headers.Add(CellText(arr.GetValue(rLo, cLo + c)));

                for (int r = rLo + 1; r <= rHi && data.Rows.Count < MaxRows; r++)
                {
                    var cells = new string[cols];
                    bool any = false;
                    for (int c = 0; c < cols; c++)
                    {
                        cells[c] = CellText(arr.GetValue(r, cLo + c));
                        if (cells[c].Length > 0) any = true;
                    }
                    if (!any) continue;                        // 整行空白：跳过（表格下方经常有一堆空行）
                    data.Rows.Add(cells);
                }
            }
            finally
            {
                try { if (ws != null) Marshal.ReleaseComObject(ws); } catch { }
                try { if (wb != null) wb.Close(false); } catch { }
                try { if (app != null) app.Quit(); } catch { }
                try { if (wb != null) Marshal.FinalReleaseComObject(wb); } catch { }
                try { if (app != null) Marshal.FinalReleaseComObject(app); } catch { }
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            return data;
        }

        /// <summary>单元格 → 文本。Excel 把数字给成 double，直接 ToString 会变成 "1" / "2.5" 但整数会带 .0。</summary>
        private static string CellText(object? v)
        {
            if (v == null) return "";
            if (v is double d)
            {
                if (Math.Abs(d) < 1e15 && Math.Abs(d - Math.Round(d)) < 1e-9)
                    return ((long)Math.Round(d)).ToString(CultureInfo.InvariantCulture);
                return d.ToString("0.####", CultureInfo.InvariantCulture);
            }
            if (v is DateTime dt) return dt.ToString("yyyy-MM-dd");
            return (v.ToString() ?? "").Trim();
        }

        // ================= ② 渲染 =================

        private static void RenderRow(string[] cells, SubtitleStyle style, string pngPath)
        {
            var rtb = RenderBitmap(cells, style, false);

            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = new FileStream(pngPath, FileMode.Create, FileAccess.Write, FileShare.None);
            enc.Save(fs);
        }

        private static BitmapSource RenderBitmap(string[] cells, SubtitleStyle style, bool withBackdrop,
                                                 double scale = 1.0)
        {
            scale = double.IsNaN(scale) ? 1.0 : Math.Clamp(scale, 0.25, 1.0);

            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                if (withBackdrop)
                {
                    // 模拟节目画面：深蓝灰渐变。只为让"透明处"看得见，不会进 PNG。
                    var bg = new LinearGradientBrush(
                        Color.FromRgb(0x1C, 0x22, 0x2E), Color.FromRgb(0x3A, 0x45, 0x58), 90);
                    dc.DrawRectangle(bg, null, new Rect(0, 0, CanvasWidth, CanvasHeight));
                }
                // 绘制永远按 1920×1080 的逻辑坐标算（和落盘 PNG 完全同一套参数），
                // 缩放只作用在最后的输出位图上 —— 绝不会因为降分辨率让字幕条位置偏一点。
                DrawSubtitle(dc, cells, style);
            }

            int w = Math.Max(2, (int)Math.Round(CanvasWidth * scale));
            int h = Math.Max(2, (int)Math.Round(CanvasHeight * scale));
            if (w != CanvasWidth || h != CanvasHeight)
                dv.Transform = new ScaleTransform((double)w / CanvasWidth, (double)h / CanvasHeight);

            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }

        /// <summary>
        /// 画一条字幕：底部居中的圆角条 + 左侧强调竖条 + 最多三行文字。
        ///
        /// ⚠ 条子宽度默认**固定** 1280（居中），不按文字长短伸缩 ——
        ///   否则每翻一条，条子的左右边界都在跳，画面看着很乱。
        ///   要改宽度请用 <see cref="SubtitleStyle.BarWidth"/>，仍然是"所有行同一个宽度"。
        /// ⚠ 测高和绘制必须用**同一个** MaxTextWidth，中途改宽度会导致二次换行、
        ///   文字溢出到条子外面（第一版就栽在这）。
        /// </summary>
        private static void DrawSubtitle(DrawingContext dc, string[] cells, SubtitleStyle st)
        {
            const double padH = 48, padV = 30, gap = 10;
            const double accentW = 6, accentGap = 16;

            double barW = Math.Clamp(st.BarWidth, 320, CanvasWidth);
            double accentRoom = st.ShowAccent ? accentW + accentGap : 0;
            double maxContentW = Math.Max(80, barW - padH * 2 - accentRoom);

            var texts = new List<FormattedText>();
            var family = new FontFamily(string.IsNullOrWhiteSpace(st.FontFamily) ? "微软雅黑" : st.FontFamily);

            void AddField(string s, double size, Brush brush, bool bold)
            {
                if (string.IsNullOrWhiteSpace(s) || size < 6) return;
                var ft = new FormattedText(
                    s.Trim(),
                    CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight,
                    new Typeface(family, FontStyles.Normal,
                                 bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
                    size, brush, 1.0)
                {
                    MaxTextWidth = maxContentW,
                    MaxLineCount = 3,
                    Trimming = TextTrimming.CharacterEllipsis
                };
                texts.Add(ft);
            }

            AddField(cells.Length > 0 ? cells[0] : "", st.MainFontSize, BrushOf(st.MainColor, Colors.White), st.BoldMain);
            AddField(cells.Length > 1 ? cells[1] : "", st.SubFontSize, BrushOf(st.SubColor, Color.FromRgb(0xDA, 0xE2, 0xEC)), false);
            if (cells.Length > 2)
            {
                var rest = new List<string>();
                for (int i = 2; i < cells.Length; i++)
                    if (!string.IsNullOrWhiteSpace(cells[i])) rest.Add(cells[i].Trim());
                if (rest.Count > 0)
                    AddField(string.Join("  |  ", rest), st.ExtraFontSize,
                             BrushOf(st.ExtraColor, Color.FromRgb(0xB9, 0xC4, 0xD0)), false);
            }

            double contentH = 0;
            foreach (var t in texts) contentH += t.Height;
            if (texts.Count > 1) contentH += gap * (texts.Count - 1);

            double barH = contentH + padV * 2;
            double barX = Math.Clamp((CanvasWidth - barW) / 2.0 + st.OffsetX, 0, CanvasWidth - barW);
            double barY = Math.Clamp(CanvasHeight - st.MarginBottom - barH, 0, CanvasHeight - barH);

            int alpha = Math.Clamp(st.BackAlpha, 0, 255);
            if (alpha > 0)
            {
                var baseColor = BrushOf(st.BackColor, Color.FromRgb(0x10, 0x14, 0x1A)).Color;
                var back = new SolidColorBrush(Color.FromArgb((byte)alpha, baseColor.R, baseColor.G, baseColor.B));
                back.Freeze();
                dc.DrawRoundedRectangle(back, null, new Rect(barX, barY, barW, barH), 18, 18);
            }

            if (st.ShowAccent)
            {
                var accent = BrushOf(st.AccentColor, Color.FromRgb(0x6E, 0xC7, 0xE8));
                dc.DrawRoundedRectangle(accent, null,
                    new Rect(barX + padH - accentGap, barY + 22, accentW, Math.Max(8, barH - 44)), 3, 3);
            }

            double y = barY + padV;
            double x = barX + padH + accentRoom;
            foreach (var t in texts)
            {
                dc.DrawText(t, new Point(x, y));
                y += t.Height + gap;
            }
        }

        /// <summary>颜色字符串 → 冻结的画刷。解析失败时退回兜底色（绝不抛异常把整张表废掉）。</summary>
        private static SolidColorBrush BrushOf(string? hex, Color fallback)
        {
            Color c = fallback;
            try
            {
                if (!string.IsNullOrWhiteSpace(hex))
                {
                    var o = ColorConverter.ConvertFromString(hex.Trim());
                    if (o is Color cc) c = cc;
                }
            }
            catch { }
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
