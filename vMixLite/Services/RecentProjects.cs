using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Cutlance.Services
{
    /// <summary>
    /// 最近打开的工程列表（%APPDATA%\Cutlance\recent.json，最多 8 条，最新在前）。
    /// 用于「文件 → 最近工程」快速回到上次的工作状态。
    /// </summary>
    public static class RecentProjects
    {
        private const int MaxCount = 8;

        private static string Dir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cutlance");

        private static string FilePath => Path.Combine(Dir, "recent.json");

        public static List<string> Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new List<string>();
                var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath));
                if (list == null) return new List<string>();
                // 过滤已不该存在的路径（不删文件，只在列表里隐去）
                return list.Where(p => !string.IsNullOrWhiteSpace(p)).Take(MaxCount).ToList();
            }
            catch { return new List<string>(); }
        }

        /// <summary>把某个工程置顶到最近列表（已存在则上移，不重复）。</summary>
        public static void Add(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                var list = Load();
                list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                list.Insert(0, path);
                if (list.Count > MaxCount) list = list.Take(MaxCount).ToList();
                Save(list);
            }
            catch { }
        }

        public static void Clear()
        {
            try { Save(new List<string>()); } catch { }
        }

        private static void Save(List<string> list)
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(list,
                new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
