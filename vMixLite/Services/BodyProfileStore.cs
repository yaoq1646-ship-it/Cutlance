using System;
using System.IO;
using System.Text.Json;
using Cutlance.Models;

namespace Cutlance.Services
{
    /// <summary>
    /// 人体美学测算数据的本地持久化（%APPDATA%\Cutlance\body-profile.json）。
    /// 只保存输入项（身高/肚脐高/坐高/腰围/臀围/性别），所有指标与建议值都是实时算出来的。
    /// </summary>
    public static class BodyProfileStore
    {
        private static string Dir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cutlance");

        private static string FilePath => Path.Combine(Dir, "body-profile.json");

        /// <summary>读取已保存的测算数据；无文件或损坏时返回带默认值的新实例。</summary>
        public static BodyProportion Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(FilePath));
                    if (dto != null && dto.Height > 0)
                    {
                        return new BodyProportion
                        {
                            Height = dto.Height,
                            NavelHeight = dto.NavelHeight,
                            SittingHeight = dto.SittingHeight,
                            Waist = dto.Waist,
                            Hip = dto.Hip,
                            IsFemale = dto.IsFemale,
                            UseManouvrierForLeg = dto.UseManouvrierForLeg
                        };
                    }
                }
            }
            catch { }
            return new BodyProportion();
        }

        /// <summary>保存测算数据（静默失败，不影响主流程）。</summary>
        public static void Save(BodyProportion b)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var dto = new Dto
                {
                    Height = b.Height,
                    NavelHeight = b.NavelHeight,
                    SittingHeight = b.SittingHeight,
                    Waist = b.Waist,
                    Hip = b.Hip,
                    IsFemale = b.IsFemale,
                    UseManouvrierForLeg = b.UseManouvrierForLeg
                };
                File.WriteAllText(FilePath, JsonSerializer.Serialize(dto,
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        private sealed class Dto
        {
            public double Height { get; set; } = 170;
            public double NavelHeight { get; set; } = 103;
            public double SittingHeight { get; set; } = 90;
            public double Waist { get; set; } = 72;
            public double Hip { get; set; } = 95;
            public bool IsFemale { get; set; } = true;
            public bool UseManouvrierForLeg { get; set; }
        }
    }
}
