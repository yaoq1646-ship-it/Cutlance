using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Cutlance.Models;

namespace Cutlance.Services
{
    /// <summary>
    /// 组合预设的落盘读写（老板 2026-10-18）。
    ///
    /// 为什么放在 %APPDATA% 而不是工程文件里：
    /// 预设的用途是**跨工程复用**（"主讲+右下PPT"这种摆法在每期节目里都要用），
    /// 塞进 .cutlance 就变成每开一个新工程都得重新存一遍，反而不好用。
    ///
    /// 文件：%APPDATA%\Cutlance\layout-presets.json
    /// 格式与工程文件一样是 JSON（人可读、可手改、可备份）。
    /// ⚠ 任何读写失败都不抛给调用方，只返回空列表/false —— 导播软件不能因为
    ///   一个预设文件坏了就起不来。
    /// </summary>
    public static class LayoutPresetStore
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static string Dir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cutlance");

        public static string FilePath => Path.Combine(Dir, "layout-presets.json");

        /// <summary>读全部预设（文件不存在 = 空列表）。</summary>
        public static List<LayoutPreset> LoadAll()
        {
            try
            {
                if (!File.Exists(FilePath)) return new List<LayoutPreset>();
                var json = File.ReadAllText(FilePath, Encoding.UTF8);
                var list = JsonSerializer.Deserialize<List<LayoutPreset>>(json, Options);
                return (list ?? new List<LayoutPreset>())
                       .Where(p => p != null && !string.IsNullOrWhiteSpace(p.Name))
                       .ToList();
            }
            catch
            {
                // 文件损坏不该拖死程序：当作没有预设，下次保存会整个重写
                return new List<LayoutPreset>();
            }
        }

        /// <summary>整表保存（同名覆盖）。</summary>
        public static bool SaveAll(List<LayoutPreset> list, out string err)
        {
            err = "";
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(list, Options), new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                err = ex.Message;
                return false;
            }
        }

        /// <summary>按名字放一条进去（同名覆盖），返回是否成功以及提示语。</summary>
        public static bool Upsert(LayoutPreset preset, out string err)
        {
            var all = LoadAll();
            int i = all.FindIndex(p => string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) all[i] = preset;
            else all.Add(preset);
            return SaveAll(all, out err);
        }

        /// <summary>按名字删除。</summary>
        public static bool Delete(string name, out string err)
        {
            var all = LoadAll();
            int i = all.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (i < 0) { err = "没有这个预设"; return false; }
            all.RemoveAt(i);
            return SaveAll(all, out err);
        }
    }
}
