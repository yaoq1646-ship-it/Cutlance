namespace Cutlance.Models
{
    /// <summary>显示器信息（模块7：HDMI 全屏输出用）。坐标/尺寸均为物理像素。</summary>
    public sealed class MonitorInfo
    {
        public int Index { get; set; }
        public string DeviceName { get; set; } = "";
        public int Left { get; set; }
        public int Top { get; set; }
        public int Right { get; set; }
        public int Bottom { get; set; }
        public bool IsPrimary { get; set; }

        public int Width => Right - Left;
        public int Height => Bottom - Top;

        public override string ToString() =>
            $"显示器 {Index + 1} · {Width}×{Height}{(IsPrimary ? "（主屏）" : "")}";
    }
}
