using System.Collections.Generic;

namespace Cutlance.Models
{
    /// <summary>
    /// 组合预设（老板 2026-10-18）：
    /// 「把当前这套摆法起个名存下来（如「主讲+右下PPT」），下次一键调回。」
    ///
    /// 存什么：**每一路的几何 + 描边**，以及它指向哪个输入（按输入编号 + 名字双保险）。
    /// 不存什么：媒体本身（输入还在列表里就按编号/名字重新认，认不到就跳过这一路并报数）。
    ///
    /// 为什么只存「摆法」不存播放状态：预设的用途是复用排版，不是复用播放进度。
    /// 存进度会把「上次播到 03:21」这种无意义状态带进来。
    /// </summary>
    public sealed class LayoutPreset
    {
        public string Name { get; set; } = "";

        /// <summary>保存时间（仅作记录，界面上可显示"什么时候存的"）。</summary>
        public string SavedAt { get; set; } = "";

        /// <summary>预览框摆法（多路）。</summary>
        public List<PresetItem> Items { get; set; } = new();
    }

    /// <summary>预设里的一路。几何口径 = 占框比例（1.00 = 铺满），与精调面板显示的数字完全一致。</summary>
    public sealed class PresetItem
    {
        /// <summary>输入编号（左侧列表里的 #N）—— 首选匹配依据。</summary>
        public int SourceNumber { get; set; }

        /// <summary>输入名 —— 编号对不上时按名字兜底（换工程后编号可能变）。</summary>
        public string SourceName { get; set; } = "";

        public double X { get; set; }
        public double Y { get; set; }
        public double W { get; set; } = 1;
        public double H { get; set; } = 1;
        public int Z { get; set; } = 1;
        public bool AspectLock { get; set; } = true;
        public double BorderThickness { get; set; }
        public double BorderRadius { get; set; }
        public string BorderColor { get; set; } = "#FF33C6F5";
    }
}
