using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Xps.Packaging;

namespace Cutlance.Services
{
    /// <summary>
    /// 办公文档 → 页面图片（老板 2026-10-19：表格进预览、按 Excel 打印分页翻页）。
    ///
    /// 为什么这么做（而不是"抓 Excel 窗口"）：
    ///   · 表格没有"放映"这回事，窗口里看到的是一块滚动视图，抓窗口既不稳定也没法翻页；
    ///   · 老板要的是"按行列顺序依次读取"，也就是**按打印分页**一页页看；
    ///   · 导出成图片后就是普通的静态图片输入 —— VLC 本来就支持 PNG（`:image-duration=-1` 常显），
    ///     翻页 = 换 Location 指向下一张 + 重建媒体，整条链路不需要新造轮子。
    ///
    /// 出图路线（2026-10-19 反复实测后定下来的）：
    ///   Excel `ExportAsFixedFormat(xlTypeXPS)` → 生成 XPS（**Excel 窗口隐藏也能导，分页就是打印分页**）
    ///   → 用 WPF 自带的 XPS 渲染器逐页渲染成 PNG。
    ///
    /// ⚠ 为什么不用更"直觉"的 `Range.CopyPicture` + 剪贴板：
    ///   实测（本机 Office 16）Excel 隐藏时 CopyPicture 出来的是**一张白纸**，
    ///   只有一圈浅灰边框（连 Visible=true 也试过，同样空）；经 ChartObject 中转也一样空。
    ///   而 XPS 是走 Excel 自己的打印管线，隐藏窗口照样出内容，且天然按打印分页 —— 正合需求。
    ///   顺带还省掉"导出时会占用系统剪贴板"这个副作用。
    /// </summary>
    public static class OfficePageService
    {
        /// <summary>渲染 DPI（150 足够让表格文字在 1080p 输出上清晰，又不至于文件太大）。</summary>
        private const double RenderDpi = 150.0;

        /// <summary>
        /// 把 Excel 按打印分页导出成 PNG 序列。返回每页图片的绝对路径（顺序 = 打印顺序）。
        /// 在独立的 STA 线程里跑，UI 线程不会被 COM / 渲染阻塞。
        /// </summary>
        public static Task<List<string>> ExportExcelPagesAsync(string xlsxPath, string outDir, int maxPages = 200)
            => RunStaAsync(() => ExportExcelPages(xlsxPath, outDir, maxPages));

        /// <summary>
        /// 把 PowerPoint 每一页导出成 PNG —— **只做兜底，不是主路**。
        ///
        /// 老板 2026-10-19 20:34 明确：「PPT 动画是直播内容的重要组成部分，不能简单转成图片」。
        /// 所以 `+PPT` 的主路是 `PptShowService` 抓放映窗口（动画/转场/内嵌视频都照常播），
        /// 本方法只在**抓不到放映窗**（没装 PowerPoint / 被 Office 安全提示挡住 / 文件损坏）时
        /// 兜底出画面，并且调用方必须在状态栏明说「动画不会动」。
        ///
        /// 顺带对齐 vMix 的事实：vMix 的 PowerPoint 输入正是这条路（官方知识库：只支持静态页，
        /// 要动画得另开一台机走 Desktop Capture/NDI）—— 也就是说单机带动画，我们比 vMix 原版更省事。
        /// </summary>
        public static Task<List<string>> ExportPptSlidesAsync(string pptPath, string outDir)
            => RunStaAsync(() => ExportPptSlides(pptPath, outDir));

        private static List<string> ExportPptSlides(string path, string outDir)
        {
            Directory.CreateDirectory(outDir);

            var progId = Type.GetTypeFromProgID("PowerPoint.Application");
            if (progId == null)
                throw new InvalidOperationException("这台电脑没有安装 PowerPoint（或 COM 未注册）。");

            dynamic? app = null;
            dynamic? pres = null;
            try
            {
                app = Activator.CreateInstance(progId)!;
                try { app.DisplayAlerts = 1; } catch { }   // 1 = ppAlertsNone，别弹框
                // ⚠ PowerPoint 不允许 Application.Visible = false（这点和 Excel 不同），
                //   但能把演示文稿用 WithWindow := false 打开 → 不出现窗口、不抢焦点。
                //   Open(FileName, ReadOnly, Untitled, WithWindow)
                pres = app.Presentations.Open(path, true, false, false);
                // 18 = ppSaveAsPNG：整份演示文稿逐页导出成 PNG，文件名由 PowerPoint 自动编号
                pres.SaveAs(Path.Combine(outDir, "slide"), 18);
            }
            finally
            {
                try { if (pres != null) pres.Close(); } catch { }
                try { if (app != null) app.Quit(); } catch { }
                try { if (pres != null) Marshal.FinalReleaseComObject(pres); } catch { }
                try { if (app != null) Marshal.FinalReleaseComObject(app); } catch { }
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            // PowerPoint 会把图片放进一个以导出名命名的子目录里（各版本/各语言下
            // 文件名是「幻灯片1.PNG」「Slide1.PNG」这种），所以递归扫一遍，
            // 再按文件名里的数字自然排序 —— 直接按字符串排会把 10 排到 2 前面。
            var found = new List<string>();
            foreach (var f in Directory.GetFiles(outDir, "*.png", SearchOption.AllDirectories))
                found.Add(f);
            foreach (var f in Directory.GetFiles(outDir, "*.PNG", SearchOption.AllDirectories))
                if (!found.Contains(f)) found.Add(f);

            if (found.Count == 0)
                throw new InvalidOperationException("PowerPoint 没有导出任何一页（文件可能损坏或被保护）。");

            found.Sort((a, b) =>
            {
                int na = TrailingNumber(Path.GetFileNameWithoutExtension(a));
                int nb = TrailingNumber(Path.GetFileNameWithoutExtension(b));
                if (na != nb) return na.CompareTo(nb);
                return string.CompareOrdinal(a, b);
            });
            return found;
        }

        /// <summary>取文件名末尾的数字（「幻灯片12」→ 12）；没有数字返回 int.MaxValue。</summary>
        private static int TrailingNumber(string name)
        {
            int end = name.Length - 1;
            while (end >= 0 && !char.IsDigit(name[end])) end--;
            if (end < 0) return int.MaxValue;
            int start = end;
            while (start >= 0 && char.IsDigit(name[start])) start--;
            return int.TryParse(name.Substring(start + 1, end - start), out int v) ? v : int.MaxValue;
        }

        private static Task<T> RunStaAsync<T>(Func<T> work)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var th = new Thread(() =>
            {
                try { tcs.TrySetResult(work()); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            })
            { IsBackground = true, Name = "cutlance-office-export" };
            th.SetApartmentState(ApartmentState.STA);   // COM 自动化 + WPF 渲染的硬要求
            th.Start();
            return tcs.Task;
        }

        private static List<string> ExportExcelPages(string path, string outDir, int maxPages)
        {
            Directory.CreateDirectory(outDir);
            string xpsPath = Path.Combine(outDir, "book.xps");

            var progId = Type.GetTypeFromProgID("Excel.Application");
            if (progId == null)
                throw new InvalidOperationException("这台电脑没有安装 Excel（或 COM 未注册），无法读取表格分页。");

            // ① Excel → XPS（走打印管线，分页就是 Excel 的打印分页）
            dynamic? app = null;
            dynamic? wb = null;
            try
            {
                app = Activator.CreateInstance(progId)!;
                app.Visible = false;
                app.DisplayAlerts = false;
                // Open(FileName, UpdateLinks, ReadOnly)
                wb = app.Workbooks.Open(path, 0, true);
                // ExportAsFixedFormat(Type, Filename)：1 = xlTypeXPS
                wb.ExportAsFixedFormat(1, xpsPath);
            }
            finally
            {
                try { if (wb != null) wb.Close(false); } catch { }
                try { if (app != null) app.Quit(); } catch { }
                try { if (wb != null) Marshal.FinalReleaseComObject(wb); } catch { }
                try { if (app != null) Marshal.FinalReleaseComObject(app); } catch { }
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            if (!File.Exists(xpsPath) || new FileInfo(xpsPath).Length < 1024)
                throw new InvalidOperationException("Excel 没有导出任何页面（表格可能是空的 / 打印区域为空）");

            // ② XPS → PNG（WPF 自带渲染器，无需任何第三方库）
            var pages = RenderXpsPages(xpsPath, outDir, maxPages);

            try { File.Delete(xpsPath); } catch { }
            return pages;
        }

        private static List<string> RenderXpsPages(string xpsPath, string outDir, int maxPages)
        {
            var pages = new List<string>();
            double scale = RenderDpi / 96.0;

            using var xps = new XpsDocument(xpsPath, FileAccess.Read);
            var seq = xps.GetFixedDocumentSequence();

            foreach (DocumentReference docRef in seq.References)
            {
                var fixedDoc = docRef.GetDocument(false);
                foreach (PageContent pageContent in fixedDoc.Pages)
                {
                    if (pages.Count >= maxPages) return pages;

                    var page = pageContent.GetPageRoot(false);
                    var size = new Size(page.Width, page.Height);
                    page.Measure(size);
                    page.Arrange(new Rect(size));
                    page.UpdateLayout();

                    var rtb = new RenderTargetBitmap(
                        (int)Math.Ceiling(page.Width * scale),
                        (int)Math.Ceiling(page.Height * scale),
                        RenderDpi, RenderDpi, PixelFormats.Pbgra32);
                    rtb.Render(page);
                    rtb.Freeze();

                    string png = Path.Combine(outDir, $"p{pages.Count + 1:0000}.png");
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(rtb));
                    using (var fs = new FileStream(png, FileMode.Create, FileAccess.Write))
                        enc.Save(fs);
                    pages.Add(png);
                }
            }
            return pages;
        }
    }
}
