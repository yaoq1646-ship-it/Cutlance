using System;

namespace Cutlance.Services
{
    /// <summary>
    /// 节目成品帧源（合成器出口）。
    ///
    /// ── 为什么要有这层抽象 ────────────────────────────────────────────────
    ///   老链路里输出侧（推流/录制/NDI/全屏）各自去抓「播出框」那块屏幕区域，
    ///   于是窗口一被盖住、一最小化、一换缩放，输出就断（MEMORY 铁律 2 的根因）。
    ///   换成合成器之后，成品帧本来就在内存里 —— 输出侧只要按接口把帧拿走即可，
    ///   跟窗口状态彻底脱钩。Services 层不认识 Views.MonitorBridge，所以定义这个接口。
    ///
    /// ── 回调约定（很重要）──────────────────────────────────────────────
    ///   sink 在**合成线程的 swap 锁内**被调用，一帧分发多路。回调里必须：
    ///     ① 立刻把像素拷走（源指针下一帧就被覆盖）；
    ///     ② 绝不阻塞、绝不做 IO、绝不碰 UI —— 阻塞合成 = 整机卡死。
    ///   帧格式固定为 32 位 BGRA（stride 由回调参数给出，不要假设 stride = W×4）。
    /// </summary>
    public interface IProgramFrames
    {
        /// <summary>成品帧宽度（像素）。</summary>
        int Width { get; }

        /// <summary>成品帧高度（像素）。</summary>
        int Height { get; }

        /// <summary>成品帧标称帧率。</summary>
        double Fps { get; }

        /// <summary>挂一路消费者。同一委托重复挂要去重（由实现负责）。</summary>
        void AddSink(Action<IntPtr, int> sink);

        /// <summary>摘掉一路消费者。</summary>
        void RemoveSink(Action<IntPtr, int> sink);
    }
}
