using System;
using System.Windows;
using System.Windows.Media.Animation;
using Cutlance.Models;

namespace Cutlance.Services
{
    /// <summary>
    /// 转场引擎（模块2，模块5 简化为对 UIElement 的透明动画）。
    /// 多图层场景下，转场作用于“新增/切换的输出图层容器”：
    /// - Cut：瞬时
    /// - Fade / AlphaFade：目标元素透明 0→1 淡入
    /// </summary>
    public static class TransitionEngine
    {
        public static void Run(
            TransitionType type,
            double durationSec,
            FrameworkElement target,
            Action onDone)
        {
            switch (type)
            {
                case TransitionType.Cut:
                    target.Opacity = 1.0;
                    onDone();
                    break;

                case TransitionType.Fade:
                case TransitionType.AlphaFade:
                    RunFade(target, durationSec, onDone);
                    break;
            }
        }

        private static void RunFade(FrameworkElement view, double dur, Action onDone)
        {
            var anim = new DoubleAnimation(0.0, 1.0, TimeSpan.FromSeconds(Math.Max(dur, 0.1)));
            anim.Completed += (_, _) => onDone();
            view.BeginAnimation(UIElement.OpacityProperty, anim);
        }
    }
}
