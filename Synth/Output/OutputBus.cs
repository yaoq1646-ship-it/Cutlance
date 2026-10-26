using System;
using System.Collections.Generic;
using System.IO;

namespace SynthPoc.Output
{
    /// <summary>
    /// 输出的分发中心：一条成品帧进来，同时喂给所有通路。
    ///
    /// 真实现场常见的组合就是「边录边推」——录一份高码率原件，同时推一路 6M 直播流。
    /// 老链路靠两个 ffmpeg 各自抓屏，抓的是同一个屏幕矩形，靠屏幕"凑"出一致；
    /// 现在是同一块内存分两路编码，天然完全同步。
    ///
    /// 每条通路独立进程、独立队列：一路卡住/断线不影响另一路。
    /// </summary>
    internal sealed class OutputBus : IDisposable
    {
        private readonly List<FfmpegEncoder> _sinks = new();
        private bool _disposed;

        public IReadOnlyList<FfmpegEncoder> Sinks => _sinks;
        public int Count => _sinks.Count;

        /// <summary>至少一路还活着 —— 用来判断要不要继续往管道里推。</summary>
        public bool AnyAlive
        {
            get
            {
                for (int i = 0; i < _sinks.Count; i++)
                    if (!_sinks[i].HasExited) return true;
                return false;
            }
        }

        /// <summary>
        /// 加一条通路。target 可以是本地 mp4 路径，也可以是 rtmp:// srt:// udp:// 地址。
        /// 失败只影响这一条，抛给调用方自己决定（通常记状态栏，不影响其它通路）。
        /// </summary>
        public FfmpegEncoder Add(string ffmpegPath, int w, int h, double fps, string target,
                                 int bitrateKbps = 6000, double bufferSeconds = 0.30,
                                 string? audioUrl = null, double segmentSeconds = 0)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(OutputBus));

            bool isStream = target.Trim().IndexOf("://", StringComparison.Ordinal) > 0;
            if (!isStream)
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(target));
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            }

            var enc = new FfmpegEncoder(ffmpegPath, w, h, fps, target, bitrateKbps, bufferSeconds,
                                        audioUrl, segmentSeconds: segmentSeconds);
            _sinks.Add(enc);
            return enc;
        }

        /// <summary>把当前成品帧分发给所有通路。某一路已退出就跳过它（不拖累其它路）。</summary>
        public void Push(IntPtr src, int stride)
        {
            for (int i = 0; i < _sinks.Count; i++)
            {
                var s = _sinks[i];
                if (s.HasExited) continue;
                s.Push(src, stride);
            }
        }

        /// <summary>
        /// 回传汇总：总写入帧数 / 总丢弃帧数，状态栏和报告都用它。
        /// </summary>
        public (long written, long dropped, long pushed) Totals()
        {
            long w = 0, d = 0, p = 0;
            for (int i = 0; i < _sinks.Count; i++)
            {
                w += _sinks[i].Written;
                d += _sinks[i].Dropped;
                p += _sinks[i].Pushed;
            }
            return (w, d, p);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // 先停所有通路，让 ffmpeg 各自收尾写完容器，再释放上层缓冲。
            // 列表刻意不清空：收尾报告还要读每条通路的最终写入帧数与退出码。
            for (int i = 0; i < _sinks.Count; i++)
            {
                try { _sinks[i].Dispose(); } catch { }
            }
        }
    }
}
