using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CutlanceUninstall
{
    /// <summary>
    /// Cutlance 卸载器（老板 2026-10-22：「程序如果不用了，是否应该从 windows 可以删除」）。
    ///
    /// 目标：让 Windows「设置 → 应用」和「控制面板 → 程序和功能」里真的能卸载，
    /// 而不是让用户自己去翻 %LOCALAPPDATA% 找 uninstall.cmd。
    ///
    /// 三个必须解决的老问题（旧 uninstall.cmd 全都有）：
    ///   ① **自己删不掉自己** —— 卸载器在安装目录里跑，删目录时会被占用。
    ///      解法：确认完之后把自己复制到 %TEMP% 再重启执行，由「站在目录外面」的副本删目录。
    ///   ② **导播中被强杀** —— 旧脚本第一行 taskkill /F，误点直接杀节目。
    ///      解法：先检测进程，GUI 模式必须用户点「是」才关，且先宽后严（CloseMainWindow → Kill）。
    ///   ③ **留下空文件夹 / 注册表残渣** —— 旧脚本只删了 lnk 没删文件夹，注册表压根没写过。
    ///      解法：开始菜单目录整个删，注册表项一并清。
    ///
    /// 保留不删的：工程文件（.cutlance）、录像文件、排障日志 %LOCALAPPDATA%\vMixLite\logs。
    ///
    /// 命令行：
    ///   CutlanceUninstall.exe              正常卸载（有确认框）
    ///   CutlanceUninstall.exe --silent     静默卸载（自动化验证用，不弹窗，返回码 0/非 0）
    ///   CutlanceUninstall.exe --dir=路径    指定安装目录（默认从注册表 InstallLocation 读）
    ///   CutlanceUninstall.exe --yes        跳过确认（内部用：复制到 temp 后重启的第二段）
    ///   CutlanceUninstall.exe --from-temp  标记「我已是临时副本」（内部用，防无限重启）
    /// </summary>
    internal static class Program
    {
        private const string AppExeName = "Cutlance.exe";
        private const string DisplayName = "Cutlance 直播导播台";
        private const string RegSubKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Cutlance";

        [STAThread]
        private static int Main(string[] args)
        {
            bool silent = false, yes = false, fromTemp = false;
            string dirArg = null;

            foreach (var raw in args ?? new string[0])
            {
                string a = (raw ?? "").Trim();
                if (a.Equals("--silent", StringComparison.OrdinalIgnoreCase)) silent = true;
                else if (a.Equals("--yes", StringComparison.OrdinalIgnoreCase)) yes = true;
                else if (a.Equals("--from-temp", StringComparison.OrdinalIgnoreCase)) fromTemp = true;
                else if (a.StartsWith("--dir=", StringComparison.OrdinalIgnoreCase)) dirArg = a.Substring(6).Trim('"');
            }

            string dir = ResolveDir(dirArg, fromTemp);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                if (!silent)
                    MessageBox.Show("找不到 Cutlance 的安装目录，可能已经被删除了。\n\n" +
                                    "如需清理残留，请手动到「设置 → 应用」刷新，或删除下面的目录：\n" +
                                    (dir ?? DefaultDir()),
                        DisplayName + " · 卸载", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                RemoveRegKey();   // 目录都没了，注册表项留着就是死链接
                return 2;
            }

            // ---------- ① 运行中就问一句，别学旧脚本上来就 taskkill /F ----------
            var running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppExeName));
            if (running.Length > 0)
            {
                if (!silent && !yes)
                {
                    var r = MessageBox.Show(
                        "Cutlance 正在运行。\n\n必须先关闭它才能卸载（正在录制 / 推流的内容会中断）。\n\n现在关闭并继续吗？",
                        DisplayName + " · 卸载", MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                    if (r != DialogResult.Yes) return 3;
                }
                CloseApp(running);
            }

            // ---------- ② 确认 ----------
            if (!silent && !yes)
            {
                string msg =
                    "确定要卸载 " + DisplayName + " 吗？\n\n" +
                    "将删除安装目录：\n" + dir + "\n\n" +
                    "不会删除：你的工程文件（.cutlance）、录像文件、排障日志。";
                if (MessageBox.Show(msg, DisplayName + " · 卸载", MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                    return 3;
            }

            // ---------- ③ 站到目录外面去删：先复制自己到 %TEMP% 再重启 ----------
            // 原因：卸载器本体就在 dir 里，进程活着 → Windows 不允许删这个目录。
            // ⚠ 关键：**本进程必须立刻退出，绝不能 WaitForExit**。
            //   父子两个进程同时活着的话，父进程的 exe 还锁在安装目录里，副本照样删不掉
            //   （反过来死锁：父等子删完、子等父解锁）。所以这里 Start 完就 return。
            if (!fromTemp)
            {
                string tmpExe = Path.Combine(Path.GetTempPath(),
                    "CutlanceUninstall_" + Process.GetCurrentProcess().Id + ".exe");
                try
                {
                    File.Copy(Application.ExecutablePath, tmpExe, true);
                    var psi = new ProcessStartInfo(tmpExe,
                        "--from-temp --yes --dir=\"" + dir + "\"" + (silent ? " --silent" : ""))
                    {
                        UseShellExecute = false,
                        CreateNoWindow = silent,
                        WindowStyle = silent ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
                    };
                    Process.Start(psi);
                    Thread.Sleep(400);   // 给副本一点启动时间，然后本进程退出解锁目录
                    return 0;
                }
                catch (Exception ex)
                {
                    if (!silent)
                        MessageBox.Show("无法启动卸载进程：" + ex.Message + "\n\n请手动删除：" + dir,
                            DisplayName + " · 卸载", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 5;
                }
            }

            // ---------- ④ 这里是临时副本（站在安装目录外面）：清外围 → 删目录 ----------
            // 先等原进程真正退出，否则删到 CutlanceUninstall.exe 会报占用。
            Thread.Sleep(1200);
            RemoveShortcuts();
            RemoveRegKey();
            bool ok = DeleteDirWithRetry(dir);
            CleanupSelf();

            if (!silent)
            {
                MessageBox.Show(
                    ok
                        ? "卸载完成。\n\n安装目录已删除。\n排障日志保留在 %LOCALAPPDATA%\\vMixLite\\logs（可手动删）。"
                        : "卸载没完全成功，下面的目录可能仍有文件被占用：\n" + dir +
                          "\n\n请关掉所有 Cutlance 相关窗口后手动删除该文件夹。",
                    DisplayName + " · 卸载", MessageBoxButtons.OK,
                    ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            return ok ? 0 : 4;
        }

        // ==================== 目录定位 ====================

        private static string DefaultDir()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cutlance");
        }

        private static string ResolveDir(string dirArg, bool fromTemp)
        {
            // ① 显式参数（注册表里的 UninstallString 不带，但 --silent 验证会带）
            if (!string.IsNullOrEmpty(dirArg) && Directory.Exists(dirArg)) return dirArg;

            // ② 注册表 InstallLocation —— 安装时写进去的，改了安装目录也认得
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RegSubKey))
                {
                    var v = k == null ? null : k.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrEmpty(v) && Directory.Exists(v)) return v;
                }
            }
            catch { }

            // ③ 自身所在目录（正常安装时卸载器和程序同目录）
            try
            {
                var self = Path.GetDirectoryName(Application.ExecutablePath);
                if (!fromTemp && !string.IsNullOrEmpty(self) && File.Exists(Path.Combine(self, AppExeName)))
                    return self;
            }
            catch { }

            // ④ 兜底默认目录
            string d = DefaultDir();
            if (Directory.Exists(d)) return d;

            return !string.IsNullOrEmpty(dirArg) ? dirArg : d;
        }

        // ==================== 关程序：先宽后严 ====================

        private static void CloseApp(Process[] procs)
        {
            foreach (var p in procs)
            {
                try
                {
                    if (!p.HasExited) p.CloseMainWindow();   // 走正常关闭：让程序自己保存工程、收尾输出
                }
                catch { }
            }

            // 最多等 6 秒让它自己退（导播中可能要停推流、关 ffmpeg）
            for (int i = 0; i < 60; i++)
            {
                if (Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppExeName)).Length == 0) return;
                Thread.Sleep(100);
            }

            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppExeName)))
            {
                try { if (!p.HasExited) p.Kill(); } catch { }
            }
            Thread.Sleep(800);
        }

        // ==================== 清快捷方式 ====================

        private static void RemoveShortcuts()
        {
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                TryDeleteFile(Path.Combine(desktop, "Cutlance.lnk"));

                string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                // 整个 Cutlance 文件夹一起删（旧脚本只删了里面两个 lnk，留了个空文件夹）
                string folder = Path.Combine(programs, "Cutlance");
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
                TryDeleteFile(Path.Combine(programs, "Cutlance.lnk"));
            }
            catch { }
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        // ==================== 清注册表 ====================

        internal static void RemoveRegKey()
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(RegSubKey, false); } catch { }
        }

        // ==================== 删目录（带重试）====================

        private static bool DeleteDirWithRetry(string dir)
        {
            for (int i = 0; i < 15; i++)
            {
                try
                {
                    if (!Directory.Exists(dir)) return true;
                    Directory.Delete(dir, true);
                }
                catch { }
                Thread.Sleep(400);
            }
            return !Directory.Exists(dir);
        }

        /// <summary>临时副本删掉自己（删不掉也无所谓，%TEMP% 本来就会被系统清）。</summary>
        private static void CleanupSelf()
        {
            try
            {
                string self = Application.ExecutablePath;
                string bat = Path.Combine(Path.GetTempPath(), "cutlance-uninstall-selfdel.bat");
                // 用系统 ANSI 代码页写：%TEMP% 的路径里可能带中文用户名
                File.WriteAllText(bat,
                    "@echo off\r\n" +
                    "ping -n 3 127.0.0.1 >nul\r\n" +
                    "del /f /q \"" + self + "\" >nul 2>&1\r\n" +
                    "del /f /q \"%~f0\" >nul 2>&1\r\n",
                    System.Text.Encoding.GetEncoding(0));
                Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + bat + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }
            catch { }
        }
    }
}
