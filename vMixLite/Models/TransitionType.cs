namespace Cutlance.Models
{
    /// <summary>转场类型。AlphaFade 为 vMix 29 新增转场（alpha 通道交叉溶解）。</summary>
    public enum TransitionType
    {
        Cut,       // 硬切
        Fade,      // 闪黑淡入淡出
        AlphaFade  // vMix 29：交叉溶解（alpha mix，一个图层淡入叠在另一个之上）
    }
}
