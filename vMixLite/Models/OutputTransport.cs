using System;

namespace Cutlance.Models
{
    /// <summary>
    /// 局域网视频输出协议。
    ///
    /// ── 现状与规划 ───────────────────────────────────────────────────────
    ///   · **Ndi（已实现）**：专有协议，但生态极广 —— PTZ 摄像机、OBS、vMix、
    ///     几乎全部直播/制作软件都认。本程序用 NDI 官方 SDK 的 .NET 层直接收发。
    ///   · **Omt（预留）**：Open Media Transport，vMix 团队发起的 **MIT 开源**替代协议
    ///     （vMix 29+ 原生支持）。选它的理由是三条：
    ///       ① 无许可费、可自由分发（NDI 的 SDK 是免费但专有，商业实施有条款）；
    ///       ② 官方参考实现就是 **C#**（openmediatransport/libomtnet），与本项目技术栈天然一致；
    ///       ③ 传输语义与 NDI 同类（局域网低延迟、帧内压缩、mDNS/DNS-SD 发现），
    ///          接入时**链路完全同构** —— 抓屏、分发、UI 都不用动。
    ///     本程序定位是「对标 vMix 28 + 叠加 vMix 29 特性」，OMT 正属于 29 那一档。
    ///
    /// ── 预留在哪（将来怎么加）───────────────────────────────────────────
    /// 输出侧所有「拿到一帧 BGRA → 送出去」的代码都只依赖 <see cref="ILanFrameSink"/>。
    /// 将来接 OMT 只需要：
    ///   ① 写一个 OmtSenderSession 实现 ILanFrameSink；
    ///   ② 在 NdiOutputService.Start 的分支里按 Transport 选工厂。
    /// 抓屏链路（gdigrab → rawvideo）、主窗口、工程持久化**全部不用改**。
    /// </summary>
    public enum OutputTransportKind
    {
        /// <summary>NDI（已实现）。</summary>
        Ndi,

        /// <summary>OMT / Open Media Transport（预留，尚未实现）。</summary>
        Omt
    }

    /// <summary>
    /// 「局域网帧接收端」抽象：把一帧 BGRA 广播出去。
    /// NDI 是一种实现；将来 OMT 是另一种实现 —— 抓屏侧对两者一视同仁。
    ///
    /// ⚠ 为什么要有这层抽象：抓屏 + 逐帧分发那段代码（读 ffmpeg stdout、凑满一帧、
    ///   按帧率送出去）是**协议无关**的。把它直接写死在 NDI 上，将来加 OMT 就得复制一遍，
    ///   之后每修一个 bug 都要改两处 —— 这正是本项目在别处（如输入预处理层）
    ///   反复避免的重复。所以一开始就把"送帧"这一步抽成接口。
    /// </summary>
    public interface ILanFrameSink : IDisposable
    {
        /// <summary>对外广播的源名（别的机器上看到的名字）。</summary>
        string Name { get; }

        /// <summary>当前连上来的接收端数量（「有没有人真的在看」）。</summary>
        int Connections { get; }

        /// <summary>已发送帧数（状态显示用）。</summary>
        long FramesSent { get; }

        /// <summary>送一帧紧凑 BGRA（长度须 ≥ width × height × 4）。</summary>
        void SendBgra(byte[] bgra, int width, int height, int fpsN, int fpsD);
    }
}
