using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace Cutlance.Services
{
    /// <summary>
    /// 错误与运行日志（老板 2026-10-20：「设立错误问题保存目录，让文件报错时把错误信息都保存进去，
    /// 方便进一步分析错误是什么原因」）。
    ///
    /// ── 为什么不继续写 %TEMP%\cutlance-crash.log ─────────────────────────
    ///   TEMP 藏在 `C:\Users\xxx\AppData\Local\Temp` 里，装到别的电脑上根本找不到；
    ///   而且只有"炸了"才写一行，看不出当时在干什么、什么环境。
    ///
    /// ── 目录（三选一，按顺序探测，第一个能写的就是它）────────────────────
    ///   ① `{程序目录}\logs`        —— 装在 C:\Cutlance、D:\Cutlance 这类可写位置时用这个，
    ///                                 日志就在程序旁边，老板一眼能找到、能直接发出去
    ///   ② `%LOCALAPPDATA%\Cutlance\logs` —— 装在 Program Files 时（普通用户无写权限）退到这里
    ///   ③ `%TEMP%\Cutlance-logs`   —— 上面都不行时的最后兜底
    ///
    /// ── 三类文件 ───────────────────────────────────────────────────────
    ///   · `errors.log`        所有错误的累积账本（只增不改，超 4MB 轮转成 errors.1.log）
    ///   · `error-<时间>.txt`  单个致命错误的完整快照（整份发出去就能定位）
    ///   · `run-<时间>.log`    本次运行的会话日志：启动环境 + 错误 + 退出
    ///     —— 出问题时老板只要把「logs 目录」整个打给我，环境、时间、堆栈全在
    ///
    /// 约定：**任何写盘失败都不许影响程序本身**（磁盘满 / 文件被占用 / 权限不足都吞掉），
    ///       所以这里所有方法都是 try/catch 包死的，宁可丢日志也不能因为日志丢掉程序。
    /// </summary>
    public static class ErrorLog
    {
        private const int MaxErrorsLogBytes = 4 * 1024 * 1024;   // 4MB 轮转
        private const int KeepRunFiles = 20;                     // 只留最近 20 次运行记录

        private static readonly object Lock = new();
        private static bool _inited;
        private static string _dir = "";

        /// <summary>日志目录的绝对路径（未初始化时也会算，供界面显示）。</summary>
        public static string Directory
        {
            get
            {
                if (!_inited) Init();
                return _dir;
            }
        }

        /// <summary>errors.log 的完整路径。</summary>
        public static string ErrorsFile => Path.Combine(Directory, "errors.log");

        /// <summary>本次运行的会话日志路径（每次启动一个文件）。</summary>
        public static string RunFile { get; private set; } = "";

        /// <summary>目录是「程序旁边的 logs」还是「用户目录」——界面提示用。</summary>
        public static bool IsPortableDir { get; private set; }

        // ===================== 初始化 =====================

        private static void Init()
        {
            if (_inited) return;
            lock (Lock)
            {
                if (_inited) return;
                _inited = true;

                foreach (var (path, portable) in CandidateDirs())
                {
                    if (!TryPrepare(path)) continue;
                    _dir = path;
                    IsPortableDir = portable;
                    break;
                }
                if (_dir.Length == 0)
                {
                    // 连一个可写的都没有：给个名义路径，写入时会静默失败，但界面不会崩
                    _dir = Path.Combine(Path.GetTempPath(), "Cutlance-logs");
                }

                try
                {
                    RunFile = Path.Combine(_dir, $"run-{DateTime.Now:yyyyMMdd_HHmmss}.log");
                    Directory2Ensure();
                    PruneOldRuns();
                }
                catch { RunFile = ""; }

                WriteEnvironment();
            }
        }

        /// <summary>候选目录（按优先级）。第一个能写的就是最终目录。</summary>
        private static IEnumerable<(string path, bool portable)> CandidateDirs()
        {
            string exeDir = "";
            try { exeDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'); } catch { }
            if (exeDir.Length > 0) yield return (Path.Combine(exeDir, "logs"), true);

            string local = "";
            try
            {
                local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            }
            catch { }
            if (!string.IsNullOrWhiteSpace(local))
                yield return (Path.Combine(local, "Cutlance", "logs"), false);

            yield return (Path.Combine(Path.GetTempPath(), "Cutlance-logs"), false);
        }

        /// <summary>建目录 + 实测写一个探针文件（光看 Directory.Exists 不够，权限问题要到写才暴露）。</summary>
        private static bool TryPrepare(string dir)
        {
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".write-test");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        private static void Directory2Ensure()
        {
            try { System.IO.Directory.CreateDirectory(_dir); } catch { }
        }

        private static void PruneOldRuns()
        {
            try
            {
                var files = new DirectoryInfo(_dir).GetFiles("run-*.log");
                if (files.Length <= KeepRunFiles) return;
                Array.Sort(files, (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
                int drop = files.Length - KeepRunFiles;
                for (int i = 0; i < drop; i++)
                {
                    try { files[i].Delete(); } catch { }
                }
            }
            catch { }
        }

        // ===================== 写环境信息 =====================

        /// <summary>
        /// 启动时把"当时的机器长什么样"记下来。
        /// 为什么必须记：安装到别的电脑后报错，最常见的原因是环境差异
        /// （没装 NDI / 显卡不支持 NVENC / 屏幕缩放到 4K / .NET 运行时版本），
        /// 没有这些信息只能靠猜。
        /// </summary>
        private static void WriteEnvironment()
        {
            var sb = new StringBuilder();
            sb.AppendLine("================ Cutlance 启动 ================");
            sb.AppendLine($"时间      : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            try { sb.AppendLine($"版本      : {typeof(ErrorLog).Assembly.GetName().Version}"); } catch { }
            sb.AppendLine($"程序目录  : {Safe(() => AppDomain.CurrentDomain.BaseDirectory)}");
            sb.AppendLine($"日志目录  : {_dir}");
            sb.AppendLine($"系统      : {Safe(() => Environment.OSVersion.VersionString)} ({Safe(() => Environment.OSVersion.Platform.ToString())})");
            sb.AppendLine($"64位进程  : {Environment.Is64BitProcess}");
            sb.AppendLine($".NET      : {Safe(() => Environment.Version.ToString())}");
            sb.AppendLine($"CPU       : {Safe(() => Environment.ProcessorCount.ToString())} 核 / {Safe(() => Environment.MachineName)}");
            try
            {
                sb.AppendLine($"可用内存  : {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024} MB");
            }
            catch { }
            try
            {
                sb.AppendLine($"屏幕      : {System.Windows.SystemParameters.PrimaryScreenWidth}×{System.Windows.SystemParameters.PrimaryScreenHeight} DIP" +
                              $" / 工作区 {System.Windows.SystemParameters.WorkArea.Width:0}×{System.Windows.SystemParameters.WorkArea.Height:0}");
            }
            catch { }
            sb.AppendLine();
            AppendText(RunFile, sb.ToString());
        }

        private static string Safe(Func<string> f)
        {
            try { return f() ?? ""; } catch { return "?"; }
        }

        // ===================== 对外写日志 =====================

        /// <summary>一般信息（关键动作、降级、被吞掉的异常）。只进会话日志，不进错误账本。</summary>
        public static void Info(string message)
        {
            try
            {
                if (!_inited) Init();
                AppendText(RunFile, $"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");
            }
            catch { }
        }

        /// <summary>
        /// 记一条错误。三种文件都写：
        ///   · errors.log        累积账本（含环境头，方便单独发这一个文件也能看懂）
        ///   · run-*.log         本次运行的时间线
        ///   · error-*.txt       单条快照（致命错误才写，方便整份转发）
        /// </summary>
        public static void Error(string title, Exception? ex, bool snapshot = true)
        {
            try
            {
                if (!_inited) Init();

                string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                var body = new StringBuilder();
                body.AppendLine("==================================================");
                body.AppendLine($"[{stamp}] {title}");
                body.AppendLine($"程序版本: {Safe(() => typeof(ErrorLog).Assembly.GetName().Version?.ToString() ?? "")}   " +
                                $"系统: {Safe(() => Environment.OSVersion.VersionString)}   " +
                                $"日志目录: {_dir}");
                body.AppendLine(ex == null ? "(无异常对象)" : ex.ToString());
                if (ex is AggregateException ag && ag.InnerExceptions.Count > 1)
                {
                    body.AppendLine("---- 内部异常 ----");
                    foreach (var inner in ag.InnerExceptions) body.AppendLine(inner.ToString());
                }
                body.AppendLine();

                AppendText(ErrorsFile, body.ToString(), rotate: true);
                AppendText(RunFile, body.ToString());

                if (snapshot)
                {
                    string file = Path.Combine(_dir, $"error-{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                    // 快照带完整环境头：单独发这一个文件出去也能定位
                    var head = new StringBuilder();
                    head.AppendLine($"======== Cutlance 错误报告 {stamp} ========");
                    head.AppendLine($"程序版本: {Safe(() => typeof(ErrorLog).Assembly.GetName().Version?.ToString() ?? "")}");
                    head.AppendLine($"程序目录: {Safe(() => AppDomain.CurrentDomain.BaseDirectory)}");
                    head.AppendLine($"系统    : {Safe(() => Environment.OSVersion.VersionString)}  {Environment.ProcessorCount} 核");
                    head.AppendLine($".NET    : {Safe(() => Environment.Version.ToString())}");
                    head.AppendLine();
                    head.Append(body);
                    try
                    {
                        File.WriteAllText(file, head.ToString(), new UTF8Encoding(false));
                    }
                    catch { }
                }
            }
            catch
            {
                // 日志本身失败不能再抛（否则递归炸）
            }
        }

        /// <summary>
        /// 打开日志目录（资源管理器）。返回是否成功，调用方据此写状态栏。
        /// </summary>
        public static bool OpenFolder()
        {
            try
            {
                if (!_inited) Init();
                System.IO.Directory.CreateDirectory(_dir);
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "\"" + _dir + "\"",
                    UseShellExecute = true
                });
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ===================== 落盘细节 =====================

        private static void AppendText(string path, string text, bool rotate = false)
        {
            if (string.IsNullOrEmpty(path)) return;
            lock (Lock)
            {
                try
                {
                    System.IO.Directory.CreateDirectory(_dir);
                    if (rotate) RotateIfBig(path);
                    File.AppendAllText(path, text, new UTF8Encoding(false));
                }
                catch { }
            }
        }

        private static void RotateIfBig(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length < MaxErrorsLogBytes) return;
                string old = Path.Combine(_dir, "errors.1.log");
                try { if (File.Exists(old)) File.Delete(old); } catch { }
                try { File.Move(path, old); } catch { }
            }
            catch { }
        }
    }
}
