using System;
using System.IO;
using System.IO.Compression;

namespace Cutlance.Services
{
    /// <summary>
    /// 从 vMixLite 改名成 Cutlance 的一次性数据搬运。
    ///
    /// 为什么需要：改名不只是换显示文字 —— 配置目录、日志目录、工程默认目录的路径全都跟着变了。
    /// 如果不管，老板升级后会「快捷键没了、最近工程空了、以前的工程找不到」，等于把用户数据弄丢。
    ///
    /// 规则：
    ///   · 旧目录不存在 → 什么都不做；
    ///   · 旧目录存在 → 先把文件补拷到新目录（只补缺，绝不覆盖新数据），
    ///     再把旧目录整个打成 zip 归档，最后删掉旧目录。
    ///
    /// 为什么要删旧目录（老板 2026-10-20 要求「安装后不要出现 vMixLite 目录名」）：
    ///   旧版留在 %APPDATA%\vMixLite 的目录会一直躺在盘上，看起来像"根本没改名"。
    ///   直接删有丢数据的风险，所以走「先归档成 zip、校验条目数对得上、再删」这条链，
    ///   任何一步失败都保留原目录 —— 宁可留一个旧目录，也不能丢老板的东西。
    ///
    /// 兜底开关：设置环境变量 CUTLANCE_KEEP_LEGACY=1 可以只搬迁不清理（排查问题用）。
    /// </summary>
    internal static class LegacyMigration
    {
        private const string OldName = "vMixLite";
        private const string NewName = "Cutlance";

        public static void Run()
        {
            try
            {
                string roam = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

                bool keep = Environment.GetEnvironmentVariable("CUTLANCE_KEEP_LEGACY") == "1";

                // 漫游配置：快捷键、布局预设、最近工程、体型档案、推流设置
                Migrate(Path.Combine(roam, OldName), Path.Combine(roam, NewName),
                        Path.Combine(roam, NewName, "_旧版归档"), keep, archive: true);

                // 本地数据：错误日志
                Migrate(Path.Combine(local, OldName), Path.Combine(local, NewName),
                        Path.Combine(local, NewName, "_旧版归档"), keep, archive: true);

                // 工程默认目录
                Migrate(Path.Combine(docs, OldName + " 工程"), Path.Combine(docs, NewName + " 工程"),
                        Path.Combine(docs, NewName + " 工程", "_旧版工程归档"), keep, archive: true);

                // 录制目录：可能有几 GB 录像，绝不打包（打 zip 会让启动卡住），
                // 只在新目录还不存在时整目录改名，否则确认文件都在新目录才删。
                Migrate(Path.Combine(videos, OldName), Path.Combine(videos, NewName),
                        "", keep, archive: false);
            }
            catch
            {
                // 搬运失败绝不能影响启动：最坏情况只是老板要重新设一次快捷键
            }
        }

        private static void Migrate(string oldDir, string newDir, string archiveDir, bool keepLegacy, bool archive)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(oldDir) || string.IsNullOrWhiteSpace(newDir)) return;
                if (string.Equals(oldDir, newDir, StringComparison.OrdinalIgnoreCase)) return;
                if (!Directory.Exists(oldDir)) return;

                // ① 最理想：新目录还不存在 → 整个目录改名。NTFS 同盘是元数据操作，
                //    再大的目录也是瞬间完成，不拷贝、不占双份空间、不会丢东西。
                if (!Directory.Exists(newDir))
                {
                    try
                    {
                        Directory.Move(oldDir, newDir);
                        try { ErrorLog.Info($"[改名迁移] {oldDir} → {newDir}（整目录改名）"); } catch { }
                        return;
                    }
                    catch { /* 跨盘/被占用 → 走下面的补拷 */ }
                }

                // ② 新目录已经在用 → 补拷：overwrite:false，新目录里已有的文件一个都不动
                CopyDir(oldDir, newDir);
                try { ErrorLog.Info($"[改名迁移] {oldDir} → {newDir}（补拷）"); } catch { }

                if (keepLegacy) return;

                // ③ 媒体目录：不打包（几 GB 会卡住启动），逐个核对文件已在目标里才删
                if (!archive)
                {
                    if (AllCopied(oldDir, newDir)) DeleteDir(oldDir);
                    return;
                }

                // ④ 配置/工程目录：打成 zip 归档 → 校验条目数 → 删旧目录
                if (ZipAndVerify(oldDir, archiveDir))
                    DeleteDir(oldDir);
            }
            catch { }
        }

        /// <summary>旧目录里每个文件都能在目标目录找到同名同长度的，才算搬完。</summary>
        private static bool AllCopied(string from, string to)
        {
            try
            {
                foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
                {
                    string rel = f.Substring(from.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    string dst = Path.Combine(to, rel);
                    if (!File.Exists(dst)) return false;
                    if (new FileInfo(f).Length != new FileInfo(dst).Length) return false;
                }
                return true;
            }
            catch { return false; }
        }

        private static void CopyDir(string from, string to)
        {
            Directory.CreateDirectory(to);

            foreach (var f in Directory.GetFiles(from))
            {
                try { File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: false); }
                catch { }
            }

            foreach (var d in Directory.GetDirectories(from))
            {
                try { CopyDir(d, Path.Combine(to, Path.GetFileName(d))); }
                catch { }
            }
        }

        /// <summary>把整个旧目录打成 zip 并校验条目数；条目数对得上才算成功。</summary>
        private static bool ZipAndVerify(string dir, string archiveDir)
        {
            string zipPath = "";
            try
            {
                if (string.IsNullOrWhiteSpace(archiveDir)) return false;
                Directory.CreateDirectory(archiveDir);

                // 归档文件名不能带旧名（否则盘上还是能看到 vMixLite 字样）
                string tag = dir.IndexOf("工程", StringComparison.Ordinal) >= 0 ? "旧工程"
                           : dir.IndexOf("Video", StringComparison.OrdinalIgnoreCase) >= 0 ? "旧录像"
                           : "旧配置";
                zipPath = Path.Combine(archiveDir,
                    $"_旧版数据备份-{tag}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.zip");

                int count = 0;
                using (var fs = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        string rel = f.Substring(dir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        var entry = zip.CreateEntry(rel, CompressionLevel.Optimal);
                        using (var es = entry.Open())
                        using (var src = File.OpenRead(f))
                            src.CopyTo(es);
                        count++;
                    }
                }

                using (var check = ZipFile.OpenRead(zipPath))
                    return check.Entries.Count == count;
            }
            catch
            {
                // 归档失败 → 把半成品删掉，保留旧目录
                try { if (zipPath.Length > 0 && File.Exists(zipPath)) File.Delete(zipPath); } catch { }
                return false;
            }
        }

        private static bool DeleteDir(string dir)
        {
            try
            {
                // 只读属性会让 Delete 抛异常，先清掉
                foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var attr = File.GetAttributes(f);
                        if ((attr & FileAttributes.ReadOnly) != 0)
                            File.SetAttributes(f, attr & ~FileAttributes.ReadOnly);
                    }
                    catch { }
                }
                Directory.Delete(dir, true);
                return !Directory.Exists(dir);
            }
            catch { return false; }
        }
    }
}
