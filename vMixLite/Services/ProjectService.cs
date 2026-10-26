using System;
using System.IO;
using System.Text;
using System.Text.Json;
using Cutlance.Models;

namespace Cutlance.Services
{
    /// <summary>
    /// 工程文件（*.cutlance）读写（模块9）。
    /// 只负责「序列化 + 落盘 / 读取 + 解析」；真正的「收集当前状态 / 还原到界面」
    /// 由 MainWindow 完成（只有它持有 Inputs / Layers / Overlays 等界面状态）。
    ///
    /// 采用 JSON：人可读、可手工改、可进版本库；后缀 .cutlance 便于本程序双击/筛选识别。
    /// </summary>
    public static class ProjectService
    {
        /// <summary>工程文件后缀（不带点）。</summary>
        public const string Extension = "cutlance";

        /// <summary>
        /// 改名前的旧后缀。老板手里已有的工程还是 .vmlite，
        /// 双击 / 拖到 exe 上 / 打开对话框都得照样认，不能因为改个名就打不开了。
        /// </summary>
        public const string LegacyExtension = "vmlite";

        /// <summary>打开/保存对话框的筛选器（旧后缀也列出来，方便老板找老工程）。</summary>
        public const string FileFilter =
            "Cutlance 工程 (*.cutlance)|*.cutlance|旧版工程 (*.vmlite)|*.vmlite|JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*";

        /// <summary>工程默认目录：我的文档\Cutlance 工程。</summary>
        public static string DefaultDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Cutlance 工程");

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            // 中文不转义，工程文件里直接可读
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>保存工程到指定路径（自动建目录；刷新 SavedAt）。</summary>
        public static bool Save(ProjectFile p, string path, out string err)
        {
            err = "";
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                p.SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                File.WriteAllText(path, JsonSerializer.Serialize(p, Options), new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                err = ex.Message;
                return false;
            }
        }

        /// <summary>读取工程文件；失败返回 null 并给出原因。</summary>
        public static ProjectFile? Load(string path, out string err)
        {
            err = "";
            try
            {
                if (!File.Exists(path)) { err = "文件不存在：" + path; return null; }
                var p = JsonSerializer.Deserialize<ProjectFile>(File.ReadAllText(path), Options);
                if (p == null) { err = "工程文件内容为空或格式不正确。"; return null; }
                return p;
            }
            catch (Exception ex)
            {
                err = "工程文件解析失败：" + ex.Message;
                return null;
            }
        }

        /// <summary>工程文件名（不含路径与后缀），用于窗口标题显示。</summary>
        public static string TitleOf(string path)
        {
            try { return Path.GetFileNameWithoutExtension(path); }
            catch { return "工程"; }
        }
    }
}
