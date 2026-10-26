using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace SynthPoc.Output
{
    /// <summary>录制分段设置。两个都为 0 = 不分段（一个文件录到手动停）。</summary>
    internal sealed class RecordLimits
    {
        /// <summary>每段文件大小上限（GB）。0 = 不限。</summary>
        public double MaxGigabytes;

        /// <summary>每段录制时长上限（小时）。0 = 不限。</summary>
        public double MaxHours;

        public bool Any => MaxGigabytes > 0 || MaxHours > 0;

        public double MaxSeconds => MaxHours > 0 ? MaxHours * 3600.0 : 0;

        public long MaxBytes => MaxGigabytes > 0 ? (long)(MaxGigabytes * 1024.0 * 1024.0 * 1024.0) : 0;

        /// <summary>
        /// 按当前码率把「每段容量」换算成「每段时长」。
        ///
        /// 为什么要换算：ffmpeg 8.x 的 segment 分段器**没有** segment_size 参数
        /// （实测报 "Unrecognized option 'segment_size'"，老版本才有且只对部分 muxer 有效），
        /// 只能按时间切。而 x264 走的是我们给的固定码率（ABR），
        /// 字节数 ≈ 码率 × 时长，实测误差在 ±10% 内，所以再打 0.95 折留余量，
        /// 保证不会明显超出老板设的容量。
        /// </summary>
        public double SegmentSecondsFor(int videoKbps, int audioKbps)
        {
            double byTime = MaxSeconds;
            double bySize = 0;

            long maxBytes = MaxBytes;
            if (maxBytes > 0)
            {
                double bytesPerSec = Math.Max(1, videoKbps + audioKbps) * 125.0;   // kbps → 字节/秒
                bySize = maxBytes * 0.95 / bytesPerSec;
            }

            if (byTime > 0 && bySize > 0) return Math.Min(byTime, bySize);
            if (byTime > 0) return byTime;
            if (bySize > 0) return bySize;
            return 0;
        }

        public string Describe()
        {
            if (!Any) return "不分段（一个文件录到手动停止）";
            var parts = new List<string>();
            if (MaxGigabytes > 0) parts.Add($"每段 {MaxGigabytes:0.###} GB");
            if (MaxHours > 0) parts.Add($"每段 {MaxHours:0.###} 小时（{MaxHours * 60:0.#} 分钟）");
            return string.Join(" 或 ", parts) + "，到量自动封包并开新文件";
        }

        /// <summary>给状态栏/报告用的一句话（带实际核算出的段长）。</summary>
        public string DescribeActual(double segSeconds, int videoKbps, int audioKbps)
        {
            if (segSeconds <= 0) return Describe();
            var parts = new List<string>();
            if (MaxHours > 0) parts.Add($"{MaxHours:0.###} 小时");
            if (MaxGigabytes > 0) parts.Add($"{MaxGigabytes:0.###} GB");
            double estMb = segSeconds * Math.Max(1, videoKbps + audioKbps) * 125.0 / 1048576.0;
            return "每段按 " + string.Join(" 或 ", parts) + $"（实际约 {FormatDuration(segSeconds)}／{estMb:0} MB 一段）";
        }

        public static string FormatDuration(double seconds)
        {
            if (seconds < 0) seconds = 0;
            var t = TimeSpan.FromSeconds(seconds);
            if (t.TotalHours >= 1) return $"{(int)t.TotalHours} 小时 {t.Minutes} 分";
            if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes} 分 {t.Seconds} 秒";
            return $"{seconds:0.0} 秒";
        }
    }

    /// <summary>一个已完成的录制分段。</summary>
    internal sealed class RecordSegment
    {
        public string Path = "";
        public int Index;
        public long Bytes;
        public double Seconds;
        public bool Complete;
    }

    /// <summary>
    /// 分段录制看门狗（老板 2026-10-20 两轮要求）：
    ///   第一轮：「录制应该增加按大小（单位：G）录制并保存或者按时间录制（小时）并保存」
    ///   第二轮澄清：「达到某个容量或时间自动打包并开始录制新文件」
    ///
    /// 所以它**不再负责停止录制** —— 切段由 ffmpeg 的 segment 分段器自己做（无缝、不掉帧），
    /// 这里只做两件事：
    ///   ① 盯着目录，发现新段出现 = 上一段已封包完成，记下它的真实大小；
    ///   ② 供界面/报告显示「当前第几段、上一段多大」。
    ///
    /// 为什么不自己切：自己切要 kill 掉 ffmpeg 再起一个新的，换段期间会丢 0.3~1 秒画面；
    /// 直播录制备份丢内容是不可接受的，宁可让 ffmpeg 内部切容器。
    /// </summary>
    internal sealed class RecordWatchdog : IDisposable
    {
        private const int PollMs = 1000;

        private readonly OutputBus _bus;
        private readonly RecordLimits _limits;
        private readonly double _fps;

        private Thread? _thread;
        private volatile bool _running;

        private readonly List<RecordSegment> _segments = new();
        private readonly object _lock = new();

        /// <summary>每路录制当前已知的最大段序号（用来识别「出现了新的一段」）。</summary>
        private readonly Dictionary<FfmpegEncoder, int> _lastIndex = new();
        private long _lastDbgTick;
        private long _scanTick;

        /// <summary>某一路封了一段、已经开了新的一段。参数：通路、刚封好的那一段。</summary>
        public event Action<FfmpegEncoder, RecordSegment>? SegmentRolled;

        public RecordWatchdog(OutputBus bus, RecordLimits limits, double fps)
        {
            _bus = bus;
            _limits = limits;
            _fps = Math.Max(1.0, fps);
        }

        public IReadOnlyList<RecordSegment> Segments
        {
            get { lock (_lock) return _segments.ToArray(); }
        }

        /// <summary>所有已完成分段的总大小（字节）。</summary>
        public long TotalBytes
        {
            get { lock (_lock) { long t = 0; foreach (var s in _segments) t += s.Bytes; return t; } }
        }

        public int SegmentCount { get { lock (_lock) return _segments.Count; } }

        public void Start()
        {
            if (_thread != null) return;
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "RecordWatchdog" };
            _thread.Start();
        }

        private void Loop()
        {
            while (_running)
            {
                try { CheckOnce(); }
                catch (Exception ex) { Trace.Log("录制看门狗异常: " + ex.Message); }

                for (int i = 0; i < PollMs / 50 && _running; i++) Thread.Sleep(50);
            }
        }

        private void CheckOnce()
        {
            long now = Environment.TickCount64;
            bool scan = now - _scanTick >= 1500;
            if (scan) _scanTick = now;

            var sinks = _bus.Sinks;
            for (int i = 0; i < sinks.Count; i++)
            {
                var enc = sinks[i];
                if (enc.IsStream || enc.IsDisposed) continue;

                if (enc.Segmented)
                {
                    if (scan) ScanSegments(enc);
                    continue;
                }

                // 不分段：只在调试模式下报一下进度
                if (Environment.GetEnvironmentVariable("SYNTH_REC_DEBUG") == "1" && now - _lastDbgTick > 2000)
                {
                    _lastDbgTick = now;
                    double secs = enc.Written / _fps;
                    Trace.Log($"[录制] {Path.GetFileName(enc.Target)} 已编 {secs:0.0}s  写入帧 {enc.Written}  " +
                              $"丢弃 {enc.Dropped}  自报 {enc.BytesWritten / 1048576.0:0.00} MB");
                }
            }
        }

        /// <summary>
        /// 扫目录盘点分段。
        /// 段文件是 ffmpeg 按 %03d 命名的，出现更大序号 = 上一段已经 close（moov 写完了），
        /// 这时读到的文件大小才是准确值。final=true 表示 ffmpeg 已全部退出，最后一段也算封好。
        /// </summary>
        private void ScanSegments(FfmpegEncoder enc, bool final = false)
        {
            try
            {
                string pattern = enc.SegmentPattern;
                if (pattern.LastIndexOf('%') < 0) return;

                string dir = Path.GetDirectoryName(pattern) ?? ".";
                string file = Path.GetFileName(pattern);
                string prefix = file.Substring(0, file.LastIndexOf('%'));
                string ext = Path.GetExtension(file);
                if (!Directory.Exists(dir)) return;

                var found = new List<(int idx, string path)>();
                int maxIndex = -1;
                foreach (var p in Directory.GetFiles(dir, prefix + "*" + ext))
                {
                    string name = Path.GetFileNameWithoutExtension(p);
                    if (name.Length <= prefix.Length) continue;
                    if (!int.TryParse(name.Substring(prefix.Length), out int idx)) continue;
                    found.Add((idx, p));
                    if (idx > maxIndex) maxIndex = idx;
                }
                if (maxIndex < 0) return;

                found.Sort((a, b) => a.idx.CompareTo(b.idx));

                foreach (var (idx, path) in found)
                {
                    bool complete = final || idx < maxIndex;
                    long bytes = 0;
                    try { bytes = new FileInfo(path).Length; } catch { }

                    RecordSegment? seg;
                    bool fireEvent = false;
                    lock (_lock)
                    {
                        seg = _segments.Find(x => x.Path == path);
                        if (seg == null)
                        {
                            seg = new RecordSegment
                            {
                                Path = path,
                                Index = idx,
                                Seconds = enc.SegmentSeconds,
                                Complete = complete,
                                Bytes = bytes
                            };
                            _segments.Add(seg);
                            fireEvent = complete;
                        }
                        else
                        {
                            seg.Bytes = bytes;
                            if (complete && !seg.Complete) { seg.Complete = true; fireEvent = true; }
                        }
                    }

                    if (fireEvent)
                    {
                        Trace.Log($"分段已封包：{Path.GetFileName(path)}  {bytes / 1048576.0:0.0} MB");
                        SegmentRolled?.Invoke(enc, seg!);
                    }
                }

                _lastIndex[enc] = maxIndex;

                if (Environment.GetEnvironmentVariable("SYNTH_REC_DEBUG") == "1")
                {
                    double secs = enc.Written / _fps;
                    Trace.Log($"[录制] {Path.GetFileName(enc.Target)} 第 {maxIndex + 1} 段  总已编 {secs:0.0}s  " +
                              $"写入帧 {enc.Written}  丢弃 {enc.Dropped}");
                }
            }
            catch (Exception ex)
            {
                Trace.Log("扫描分段失败: " + ex.Message);
            }
        }

        public void Dispose()
        {
            _running = false;
            try { _thread?.Join(2000); } catch { }

            // 最终盘点：ffmpeg 这时候已经收尾退出，最后一段的大小才算得准
            try
            {
                foreach (var enc in _bus.Sinks)
                {
                    if (enc.IsStream || !enc.Segmented) continue;
                    ScanSegments(enc, final: true);
                }
            }
            catch { }
        }
    }
}
