using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace CutlanceSetup
{
    /// <summary>
    /// Cutlance 安装程序（老板 2026-10-20：「把程序做成安装文件，用于在其他电脑上测试问题」）。
    ///
    /// 关键设计（都是踩过的坑）：
    ///   · net48 —— 目标机一定有（Win10 1903+/Win11 自带），安装器自己不用先装运行时；
    ///   · 装到 %LOCALAPPDATA%\Cutlance —— 免管理员、不弹 UAC，任何账户都能装，
    ///     而且程序目录可写 → 错误日志就落在 exe 旁边的 logs 里，老板一眼能找到；
    ///   · payload.zip 内嵌 —— 只有一个 setup.exe，不用旁边再放压缩包（拷 U 盘最容易丢）；
    ///   · 解压按字节报进度 —— 361MB 解压要十几秒，没进度条会以为卡死。
    /// </summary>
    internal static class Program
    {
        internal const string AppExeName = "Cutlance.exe";
        internal const string UninstallerName = "CutlanceUninstall.exe";
        internal const string UninstallRegKey =
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Cutlance";
        internal static readonly string DefaultDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cutlance");

        [STAThread]
        private static int Main(string[] args)
        {
            string dir = DefaultDir;
            bool silent = false, noLaunch = false;

            foreach (var a in args ?? new string[0])
            {
                if (a.StartsWith("--dir=", StringComparison.OrdinalIgnoreCase)) dir = a.Substring(6).Trim('"');
                else if (a.Equals("--silent", StringComparison.OrdinalIgnoreCase)) silent = true;
                else if (a.Equals("--nolaunch", StringComparison.OrdinalIgnoreCase)) noLaunch = true;
            }

            if (silent)
            {
                // 自动化验证用：不弹窗，装完就退出（返回码 0 = 成功）
                try
                {
                    Payload.Extract(dir, null, CancellationToken.None);
                    Shortcuts.Create(dir);
                    if (!noLaunch) Shortcuts.Launch(dir);
                    return 0;
                }
                catch (Exception ex)
                {
                    try
                    {
                        File.AppendAllText(Path.Combine(Path.GetTempPath(), "cutlance-setup-error.log"),
                            DateTime.Now + " " + ex + Environment.NewLine);
                    }
                    catch { }
                    return 1;
                }
            }

            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.Run(new InstallerWindow(dir, noLaunch));
            return 0;
        }
    }

    /// <summary>进度回调：百分比 0~100 + 一行状态文字。</summary>
    internal delegate void ProgressHandler(int percent, string message);

    /// <summary>把内嵌的 payload.zip 解到目标目录。</summary>
    internal static class Payload
    {
        private const string RootPrefix = "Cutlance/";   // 打包时多套了一层目录名，解压时剥掉

        private static Stream Open()
        {
            var asm = Assembly.GetExecutingAssembly();
            string name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("payload.zip", StringComparison.OrdinalIgnoreCase));
            if (name == null)
                throw new InvalidOperationException("安装包损坏：找不到内嵌的 payload.zip。");
            return asm.GetManifestResourceStream(name);
        }

        /// <summary>解压全部文件。已存在的文件直接覆盖（保留 logs 目录不动）。</summary>
        public static void Extract(string dir, ProgressHandler progress, CancellationToken ct)
        {
            Directory.CreateDirectory(dir);

            using (var zipStream = Open())
            using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Read))
            {
                // 先算总字节，进度才有意义
                long total = 0;
                var files = new List<ZipArchiveEntry>();
                foreach (var e in zip.Entries)
                {
                    if (string.IsNullOrEmpty(e.Name)) continue;   // 目录项
                    files.Add(e);
                    total += e.Length;
                }

                long done = 0;
                int lastPct = -1;
                byte[] buffer = new byte[1024 * 256];

                foreach (var e in files)
                {
                    ct.ThrowIfCancellationRequested();

                    string rel = e.FullName.Replace('\\', '/');
                    if (rel.StartsWith(RootPrefix, StringComparison.OrdinalIgnoreCase))
                        rel = rel.Substring(RootPrefix.Length);
                    if (rel.Length == 0) continue;

                    string target = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target));

                    using (var src = e.Open())
                    using (var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
                    {
                        int read;
                        while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            dst.Write(buffer, 0, read);
                            done += read;

                            if (progress != null && total > 0)
                            {
                                int pct = (int)Math.Min(99, done * 100 / total);
                                if (pct != lastPct)
                                {
                                    lastPct = pct;
                                    progress(pct, "正在释放文件… " + pct + "%");
                                }
                            }
                        }
                    }
                }
            }

            progress?.Invoke(100, "文件已释放完成");
        }
    }

    /// <summary>桌面 / 开始菜单快捷方式 + 卸载脚本。</summary>
    internal static class Shortcuts
    {
        public static string ExePath(string dir) => Path.Combine(dir, Program.AppExeName);

        public static void Create(string dir)
        {
            string exe = ExePath(dir);
            WriteUninstallerExe(dir);                    // ① 先把卸载器放到位（快捷方式与注册表都指向它）
            string uninst = Path.Combine(dir, Program.UninstallerName);

            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Cutlance");
                Directory.CreateDirectory(startMenu);

                MakeLink(Path.Combine(desktop, "Cutlance.lnk"), exe, dir, "Cutlance 直播导播台");
                MakeLink(Path.Combine(startMenu, "Cutlance.lnk"), exe, dir, "Cutlance 直播导播台");
                // 卸载入口改成那个小卸载器：有确认框、能清干净，不再是「双击就 taskkill /F」的 bat
                MakeLink(Path.Combine(startMenu, "卸载 Cutlance.lnk"), uninst, dir, "卸载 Cutlance");
            }
            catch
            {
                // 快捷方式失败不该让安装算失败：程序本体已经装好了
            }

            WriteUninstaller(dir);                       // 兜底 cdm（现在只是转调卸载器）
            RegisterUninstall(dir);                      // ② 注册表：让「设置 → 应用」里能看见、能卸载
        }

        /// <summary>把内嵌的 CutlanceUninstall.exe（85KB）写到安装目录。</summary>
        private static void WriteUninstallerExe(string dir)
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                // 资源名默认是「命名空间.相对路径」，但为避免改名后对不上，按后缀找一次
                string resName = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("CutlanceUninstall.exe", StringComparison.OrdinalIgnoreCase));
                if (resName == null) return;
                using (var s = asm.GetManifestResourceStream(resName))
                {
                    if (s == null) return;
                    string target = Path.Combine(dir, Program.UninstallerName);
                    using (var fs = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                        s.CopyTo(fs);
                }
            }
            catch
            {
                // 写不进去也不能让安装失败：程序本体才是主角，卸载还能走 uninstall.cmd
            }
        }

        /// <summary>
        /// 写 HKCU 卸载项 —— 这是「设置 → 应用 / 控制面板 → 程序和功能」能列出 Cutlance 的唯一途径。
        /// 写 HKCU 而不是 HKLM：程序本身就装在 %LOCALAPPDATA%，全程免 UAC，卸载项也该在用户级。
        /// </summary>
        private static void RegisterUninstall(string dir)
        {
            try
            {
                string uninst = Path.Combine(dir, Program.UninstallerName);
                using (var k = Registry.CurrentUser.CreateSubKey(Program.UninstallRegKey))
                {
                    if (k == null) return;
                    k.SetValue("DisplayName", "Cutlance 直播导播台");
                    k.SetValue("DisplayVersion", AppVersion(dir));
                    k.SetValue("Publisher", "Cutlance");
                    k.SetValue("DisplayIcon", ExePath(dir) + ",0");
                    k.SetValue("InstallLocation", dir);
                    k.SetValue("UninstallString", "\"" + uninst + "\"");
                    k.SetValue("QuietUninstallString", "\"" + uninst + "\" --silent");
                    k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                    k.SetValue("EstimatedSize", DirSizeKb(dir), RegistryValueKind.DWord);
                    k.SetValue("NoModify", 1, RegistryValueKind.DWord);   // 没有「修改」按钮
                    k.SetValue("NoRepair", 1, RegistryValueKind.DWord);   // 没有「修复」按钮
                }
            }
            catch { }
        }

        private static string AppVersion(string dir)
        {
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(ExePath(dir));
                if (!string.IsNullOrEmpty(vi.FileVersion)) return vi.FileVersion;
                if (!string.IsNullOrEmpty(vi.ProductVersion)) return vi.ProductVersion;
            }
            catch { }
            return "1.0.0";
        }

        /// <summary>安装目录大小（KB），用于「设置 → 应用」里显示的占用。算不出来就给 0。</summary>
        private static int DirSizeKb(string dir)
        {
            try
            {
                long total = 0;
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(f).Length; } catch { }
                }
                long kb = total / 1024;
                return (int)Math.Min(int.MaxValue, Math.Max(0, kb));
            }
            catch { return 0; }
        }

        private static void MakeLink(string linkPath, string target, string workDir, string desc)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            object shell = Activator.CreateInstance(shellType);
            object sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { linkPath });
            Type scType = sc.GetType();
            scType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
            scType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { workDir });
            scType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { target + ",0" });
            scType.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { desc });
            scType.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
        }

        /// <summary>
        /// 兜底卸载脚本：只负责转调 CutlanceUninstall.exe。
        ///
        /// 旧版是自己 `taskkill /F` + `rmdir`，两个毛病：
        ///   ① 删不掉自己所在的目录（bat 就在要删的目录里跑）→ 装完卸载会留残渣；
        ///   ② 导播中双击直接强杀节目，连问都不问。
        /// 现在逻辑全在卸载器里（有确认框、有重试、能自清理）。这里保留只是为了防
        /// 「卸载器被安全软件拦掉」这种极端情况。
        /// </summary>
        private static void WriteUninstaller(string dir)
        {
            string exe = Path.Combine(dir, Program.UninstallerName);
            string bat =
                "@echo off\r\n" +
                "if exist \"" + exe + "\" (\r\n" +
                "  start \"\" \"" + exe + "\"\r\n" +
                "  exit /b\r\n" +
                ")\r\n" +
                "rem fallback: uninstaller missing, remove the folder the hard way\r\n" +
                "taskkill /IM " + Program.AppExeName + " /F >nul 2>&1\r\n" +
                "rmdir /S /Q \"" + dir + "\"\r\n" +
                "echo Cutlance removed.\r\n" +
                "pause\r\n";
            try
            {
                // ⚠ 不能用 ASCII：老板可以自选安装目录（可能是 D:\软件\Cutlance），
                //   中文路径用 ASCII 写进 bat 会变成问号 → 卸载删错地方或删不掉。
                //   GetEncoding(0) = 系统 ANSI 代码页（中文系统即 936），与 cmd 读脚本的口径一致。
                File.WriteAllText(Path.Combine(dir, "uninstall.cmd"), bat, System.Text.Encoding.GetEncoding(0));
            }
            catch { }
        }

        public static void Launch(string dir)
        {
            try
            {
                Process.Start(new ProcessStartInfo(ExePath(dir)) { WorkingDirectory = dir, UseShellExecute = true });
            }
            catch { }
        }

        public static bool IsAppRunning()
        {
            try { return Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Program.AppExeName)).Length > 0; }
            catch { return false; }
        }
    }
}
