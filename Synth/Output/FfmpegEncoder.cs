using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SynthPoc.Output
{
    /// <summary>
    /// P2 输出层：把合成好的 BGRA 成品帧通过 stdin 管道喂给 ffmpeg。
    ///
    /// 与老链路（gdigrab 抓播出框）的本质区别：
    ///   抓屏 → ffmpeg 拿到的是"屏幕上拼好的画面"，窗口一挡就没了；
    ///   管道 → ffmpeg 拿到的是合成器内存里的成品帧，跟窗口毫无关系。
    ///
    /// 一个实例 = 一个 ffmpeg 进程 = 一条输出通路（录制 mp4 / 推 rtmp / 推 srt / 推 udp）。
    /// 多条通路并行由 OutputBus 统一分发。
    ///
    /// 设计要点（都是踩过坑的）：
    ///   1. 二进制写必须走 StandardInput.BaseStream，不能设 StandardOutputEncoding（铁律 11）
    ///   2. stderr 必须后台异步读到底，否则 ffmpeg 一报错写满管道就死锁（铁律 10/11）
    ///   3. 帧必须拷贝进队列 —— 合成器的 front buffer 下一帧就被覆盖了
    ///   4. 队列满就丢帧并计数，绝不阻塞合成线程（阻塞合成 = 整机卡死）
    ///   5. 队列不能太浅：ffmpeg 冷启动要 200~400ms 才吃第一帧，浅队列会把这段全丢
    /// </summary>
    internal sealed class FfmpegEncoder : IDisposable
    {
        private static readonly byte[] Empty = Array.Empty<byte>();

        private readonly Process _proc;
        private readonly Stream _stdin;
        private readonly int _bytes;
        private readonly string _target;
        private readonly string _muxer;
        private readonly int _w, _h;

        private readonly BlockingCollection<byte[]> _queue;
        private readonly ConcurrentBag<byte[]> _pool = new();
        private readonly CancellationTokenSource _cts = new();

        private Thread? _writer;
        private Task? _stderrTask;
        private readonly StringBuilder _stderr = new();
        private volatile bool _disposed;

        private long _pushed, _dropped, _written;
        private long _bytesWritten, _encUs;

        public string Target => _target;
        public string Muxer => _muxer;

        /// <summary>
        /// 分段录制的段长（秒）。0 = 不分段（单个文件录到停）。
        ///
        /// 老板 2026-10-20：「达到某个容量或时间自动打包并开始录制新文件」。
        /// 实现不用「停掉 ffmpeg 再起一个」（那样换段会丢 0.3~1 秒画面），
        /// 而是用 ffmpeg 自带的 segment 分段器：同一个编码器连续出帧，
        /// 只把容器按时间切开，每段都是独立完整的 mp4。实测换段不掉帧、不断音。
        /// </summary>
        public double SegmentSeconds { get; }

        public bool Segmented => SegmentSeconds > 0;

        /// <summary>分段时的文件名模板（含 %03d 序号）；不分段时等于 Target。</summary>
        public string SegmentPattern { get; }

        private static string BuildPattern(string target)
        {
            try
            {
                string dir = Path.GetDirectoryName(target) ?? "";
                string name = Path.GetFileNameWithoutExtension(target);
                string ext = Path.GetExtension(target);
                if (string.IsNullOrEmpty(ext)) ext = ".mp4";
                return Path.Combine(dir, name + "_%03d" + ext);
            }
            catch { return target; }
        }

        /// <summary>目标视频码率（kbps）—— 录制上限靠它推算「已经写了多少字节」。</summary>
        public int VideoBitrateKbps { get; }

        /// <summary>目标音频码率（kbps），无音频时 0。</summary>
        public int AudioBitrateKbps { get; private set; }
        public bool IsStream { get; }
        public int QueueDepth { get; }
        public long Pushed => Interlocked.Read(ref _pushed);
        public long Dropped => Interlocked.Read(ref _dropped);
        public long Written => Interlocked.Read(ref _written);

        /// <summary>ffmpeg 自报「这个容器已经写出去多少字节」（-progress 的 total_size）。</summary>
        public long BytesWritten => Interlocked.Read(ref _bytesWritten);

        /// <summary>ffmpeg 自报「已经编了多长时间」（秒，-progress 的 out_time_us）。</summary>
        public double EncodedSeconds => Interlocked.Read(ref _encUs) / 1000000.0;
        public bool HasExited { get { try { return _proc.HasExited; } catch { return true; } } }
        public int ExitCode { get { try { return _proc.HasExited ? _proc.ExitCode : -1; } catch { return -1; } } }

        /// <summary>Dispose 之后 Process 已释放，退出码要提前缓存下来，报告才有得读。</summary>
        public int FinalExitCode { get; private set; } = -1;
        public string Stderr { get { lock (_stderr) return _stderr.ToString(); } }

        /// <summary>完整命令行（排障用：出问题时先看这条到底拼成什么样）。</summary>
        public string ArgsText { get; }

        /// <summary>录制到上限被自动停下时记的原因（空 = 正常手动停止）。</summary>
        public string StopReason { get; private set; } = string.Empty;

        public bool IsDisposed => _disposed;

        /// <summary>ffmpeg 进程号（诊断用：确认没有偷跑出第二个进程）。</summary>
        public int ProcessId { get { try { return _proc.Id; } catch { return -1; } } }

        /// <summary>
        /// 只停这一路输出并让它正常收尾（写完 moov 索引）。
        /// 由录制看门狗在达到大小/时长上限时调用 —— 其它通路照常推，不受影响。
        /// </summary>
        public void StopRecording(string reason)
        {
            if (_disposed) return;
            StopReason = reason ?? string.Empty;
            try { Dispose(); } catch (Exception ex) { lock (_stderr) _stderr.AppendLine("[stop] " + ex.Message); }
        }

        /// <summary>ffmpeg 进程自己在报错（例如推流地址连不上）——这类要立刻让上层知道，不能干等。</summary>
        public event Action<string>? Fatal;

        /// <summary>
        /// 队列深度按帧率算：默认给 0.30 秒的缓冲，30fps = 9 帧 ≈ 72MB。
        /// 太浅会被 ffmpeg 冷启动吃掉（实测 3 帧深度在 20 秒测试里丢 4 帧），
        /// 太深会推高推流延迟。
        /// </summary>
        public static int DepthFor(double fps, double bufferSeconds = 0.30)
        {
            int d = (int)Math.Ceiling(Math.Max(1, fps) * bufferSeconds);
            return Math.Clamp(d, 6, 16);
        }

        public FfmpegEncoder(string ffmpeg, int w, int h, double fps, string target,
                             int bitrateKbps = 6000, double bufferSeconds = 0.30,
                             string? audioUrl = null, int audioBitrateKbps = 128,
                             double segmentSeconds = 0)
        {
            _w = w;
            _h = h;
            _bytes = w * h * 4;
            _target = target;
            VideoBitrateKbps = bitrateKbps;

            var (muxer, isStream) = ResolveMuxer(target);
            _muxer = muxer;
            IsStream = isStream;
            QueueDepth = DepthFor(fps, bufferSeconds);

            // 分段录制：只对本地文件目标生效（推流没有"文件"可切）
            SegmentSeconds = (!isStream && segmentSeconds > 0) ? segmentSeconds : 0;
            SegmentPattern = SegmentSeconds > 0 ? BuildPattern(target) : target;

            var args = new List<string>
            {
                "-hide_banner", "-loglevel", "warning", "-nostdin",
                // 让 ffmpeg 每 0.5 秒往 stderr 吐一行进度（total_size / out_time_us）。
                // 录制上限必须靠这个数：Windows 上文件大小要攒几秒才刷新（实测前 8 秒一直显示 48 字节），
                // 拿 FileInfo.Length 判断「到 8MB 了没有」根本抓不住，会一路录到 15MB 才收工。
                "-progress", "pipe:2",
                "-f", "rawvideo", "-pix_fmt", "bgra",
                "-s", $"{w}x{h}", "-r", FpsToRational(fps),
                "-i", "pipe:0"
            };

            // P2.5 音频：总线混好的 PCM 从 TCP 回环进来（命名管道 ffmpeg 打不开，主程序踩过）。
            // 放在视频输入之后，再用 -map 显式指定，避免 ffmpeg 自行挑流挑错。
            bool hasAudio = !string.IsNullOrWhiteSpace(audioUrl);
            AudioBitrateKbps = hasAudio ? audioBitrateKbps : 0;
            if (hasAudio)
            {
                args.AddRange(new[]
                {
                    "-thread_queue_size", "4096",
                    "-f", "s16le", "-ar", "48000", "-ac", "2",
                    "-i", audioUrl!
                });
            }

            args.AddRange(new[]
            {
                "-c:v", "libx264", "-preset", "veryfast", "-profile:v", "high",
                "-pix_fmt", "yuv420p",
                // 每个包立即刷出：不加这条，mp4 muxer 会在内存里攒好几秒（实测 8 秒内文件一直是 48 字节、
                // -progress 的 total_size 也一直是 48），录制「按大小封顶」就没法实时判断。
                "-flush_packets", "1",
                // 带音频时必须关掉 muxer 的交错缓冲：默认 max_interleave_delta = 10 秒，
                // 音视频包要凑满 10 秒才落地（实测前 10 秒文件一直是 48 字节），
                // 录制上限根本拦不住，而且断电丢的也更多。
                "-max_interleave_delta", "0",
                "-b:v", $"{bitrateKbps}k",
                "-g", ((int)Math.Max(1, Math.Round(fps * 2))).ToString()
            });

            if (hasAudio)
            {
                args.AddRange(new[]
                {
                    "-map", "0:v:0", "-map", "1:a:0",
                    "-c:a", "aac", "-b:a", $"{audioBitrateKbps}k", "-ar", "44100", "-ac", "2",
                    // 收尾必须以「最短的输入」为准：视频管道一关（EOF），输出立刻结束。
                    // 不加这条，音频 TCP 会一直有数据，ffmpeg 永远等不到全部输入 EOF，
                    // 只能被强杀 —— 文件尾部不完整，实测就是这么丢的。
                    "-shortest"
                });
            }

            if (isStream)
            {
                // 推流：低延迟优先，别让编码器攒帧
                args.AddRange(new[]
                {
                    "-tune", "zerolatency",
                    "-maxrate", $"{bitrateKbps}k",
                    "-bufsize", $"{bitrateKbps * 2}k"
                });

                if (muxer == "flv")
                {
                    // RTMP 场景下别让 muxer 为了写时长回填而阻塞
                    args.AddRange(new[] { "-flvflags", "no_duration_filesize" });
                }
                else
                {
                    // MPEG-TS 走 UDP/SRT：把 mux 延迟压到 0，否则默认有 0.7 秒预载
                    args.AddRange(new[] { "-muxdelay", "0", "-muxpreload", "0" });
                }
            }
            else if (!Segmented)
            {
                // faststart 需要收尾时整段重写一遍文件；分段时每段都要重写，纯浪费 IO，
                // 而且录制文件是本地播放用，不需要 moov 前置。所以分段时跳过。
                args.AddRange(new[] { "-movflags", "+faststart", "-y" });
            }

            if (Segmented)
            {
                // 分段录制：编码参数照旧，只把容器换成 segment 分段器。
                // 段长只能是「关键帧的整数倍」——加 -g 时就要考虑到这点，
                // 所以段边界会在目标时刻之后的第一个关键帧处切（-g 2 秒 → 最多晚 2 秒）。
                args.AddRange(new[]
                {
                    "-f", "segment",
                    "-segment_time", SegmentSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    "-reset_timestamps", "1",     // 每段时间戳从 0 开始，单独播放不会跳到几小时后
                    "-segment_format", muxer,     // mp4
                    "-y"
                });
                args.Add(SegmentPattern);
            }
            else
            {
                args.AddRange(new[] { "-f", muxer, target });
            }

            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = JoinArgs(args),
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = false,
                CreateNoWindow = true,
                StandardErrorEncoding = Encoding.UTF8   // 铁律 10：否则 GBK 解中文路径/设备名会乱码
            };
            ArgsText = psi.Arguments;

            _proc = new Process { StartInfo = psi };
            _proc.Start();

            // 池里必须先放满 QueueDepth 块，否则第一次 Push 就取不到缓冲区，会把所有帧当"队列满"丢掉
            _queue = new BlockingCollection<byte[]>(QueueDepth);
            for (int i = 0; i < QueueDepth; i++) _pool.Add(new byte[_bytes]);

            _stdin = _proc.StandardInput.BaseStream;
            _proc.StandardInput.AutoFlush = false;

            _stderrTask = Task.Run(ReadStderr);

            _writer = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "FfmpegWriter-" + Path.GetFileName(target).Replace(':', '_'),
                Priority = ThreadPriority.AboveNormal
            };
            _writer.Start();
        }

        /// <summary>
        /// 按目标地址决定容器与延迟策略。
        /// 本地路径 → mp4；rtmp/rtmps/http → flv；srt/udp/rtp → mpegts。
        /// </summary>
        public static (string muxer, bool isStream) ResolveMuxer(string target)
        {
            string t = target.Trim();
            int sep = t.IndexOf("://", StringComparison.Ordinal);
            if (sep <= 0) return ("mp4", false);

            string scheme = t.Substring(0, sep).ToLowerInvariant();
            switch (scheme)
            {
                case "rtmp":
                case "rtmps":
                case "http":
                case "https":
                    return ("flv", true);
                case "srt":
                case "udp":
                case "rtp":
                case "mpegts":
                    return ("mpegts", true);
                case "rist":
                    return ("mpegts", true);
                default:
                    return ("mpegts", true);
            }
        }

        /// <summary>
        /// 把成品帧塞进队列。调用方（合成线程）绝不能被阻塞：队列满直接丢，只记账。
        /// </summary>
        public void Push(IntPtr src, int stride)
        {
            if (_disposed || _bytes <= 0) return;
            Interlocked.Increment(ref _pushed);

            if (!_pool.TryTake(out var buf))
            {
                // 池空 = 队列里已经攒满 QueueDepth 帧，ffmpeg 跟不上
                Interlocked.Increment(ref _dropped);
                return;
            }

            try
            {
                System.Runtime.InteropServices.Marshal.Copy(src, buf, 0, _bytes);
                if (!_queue.TryAdd(buf))
                {
                    Interlocked.Increment(ref _dropped);
                    _pool.Add(buf);
                }
            }
            catch
            {
                Interlocked.Increment(ref _dropped);
                _pool.Add(buf);
            }
        }

        private void WriterLoop()
        {
            try
            {
                foreach (var buf in _queue.GetConsumingEnumerable(_cts.Token))
                {
                    _stdin.Write(buf, 0, _bytes);
                    _stdin.Flush();
                    Interlocked.Increment(ref _written);
                    _pool.Add(buf);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                lock (_stderr) _stderr.AppendLine("[writer] " + ex.Message);
            }
        }

        private void ReadStderr()
        {
            try
            {
                string? line;
                while ((line = _proc.StandardError.ReadLine()) != null)
                {
                    // -progress 的键值行：这是唯一可信的「已经写出去多少 / 编了多长」的来源
                    if (line.StartsWith("total_size=", StringComparison.Ordinal))
                    {
                        if (long.TryParse(line.Substring(11).Trim(), out long sz) && sz > 0)
                            Interlocked.Exchange(ref _bytesWritten, sz);
                        continue;
                    }
                    if (line.StartsWith("out_time_us=", StringComparison.Ordinal))
                    {
                        if (long.TryParse(line.Substring(12).Trim(), out long us) && us > 0)
                            Interlocked.Exchange(ref _encUs, us);
                        continue;
                    }
                    if (line.StartsWith("out_time_ms=", StringComparison.Ordinal) ||
                        // 注意 out_time_us= 上面已经 continue 掉了，但 out_time=（毫秒精度的时分秒写法）
                        // 是另一个键，漏过滤会让整份验收报告的"错误输出"被几百行进度刷屏。
                        line.StartsWith("out_time=", StringComparison.Ordinal) ||
                        line.StartsWith("frame=", StringComparison.Ordinal) ||
                        line.StartsWith("fps=", StringComparison.Ordinal) ||
                        line.StartsWith("bitrate=", StringComparison.Ordinal) ||
                        line.StartsWith("speed=", StringComparison.Ordinal) ||
                        line.StartsWith("progress=", StringComparison.Ordinal) ||
                        line.StartsWith("stream_", StringComparison.Ordinal) ||
                        line.StartsWith("dup_frames=", StringComparison.Ordinal) ||
                        line.StartsWith("drop_frames=", StringComparison.Ordinal))
                    {
                        continue;   // 进度块的其它字段，不进错误日志
                    }

                    lock (_stderr) { _stderr.AppendLine(line); }
                    string low = line.ToLowerInvariant();
                    if (low.Contains("error") || low.Contains("failed") || low.Contains("invalid") ||
                        low.Contains("refused") || low.Contains("no such"))
                    {
                        if (!_proc.HasExited) Fatal?.Invoke(line);
                    }
                }
            }
            catch { }
            finally
            {
                try
                {
                    if (!_proc.HasExited)
                        Fatal?.Invoke("ffmpeg 输出管道已断开");
                }
                catch { }
            }
        }

        /// <summary>正常收尾：先停队列、关 stdin（EOF 让 ffmpeg 写完容器索引），再等进程退出。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _cts.Cancel(); } catch { }
            try { _queue.CompleteAdding(); } catch { }
            try { _writer?.Join(3000); } catch { }

            try { _stdin.Flush(); } catch { }
            try { _stdin.Close(); } catch { }      // ← 这句决定 mp4 能不能正常收尾

            bool ok = false;
            try { ok = _proc.WaitForExit(15000); } catch { }
            if (!ok)
            {
                try { _proc.Kill(); } catch { }
                lock (_stderr) _stderr.AppendLine("[encoder] 收尾超时，已强杀 ffmpeg（文件可能不完整）");
            }

            try { FinalExitCode = ok ? _proc.ExitCode : -1; } catch { }

            try { _stderrTask?.Wait(2000); } catch { }
            try { _proc.Dispose(); } catch { }
            try { _cts.Dispose(); } catch { }
            try { _queue.Dispose(); } catch { }
        }

        /// <summary>帧率要写成 ffmpeg 认的分数，29.97 不能写成 29.97（会被当成 2997/100 从而音画不同步）。</summary>
        public static string FpsToRational(double fps)
        {
            switch (Math.Round(fps, 2))
            {
                case 23.98: return "24000/1001";
                case 29.97: return "30000/1001";
                case 59.94: return "60000/1001";
                case 119.88: return "120000/1001";
                default: return fps.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        private static string JoinArgs(IEnumerable<string> args)
        {
            var sb = new StringBuilder();
            foreach (var a in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(Quote(a));
            }
            return sb.ToString();
        }

        private static string Quote(string a)
        {
            if (a.Length == 0) return "\"\"";
            if (a.IndexOf(' ') < 0 && a.IndexOf('"') < 0 && a.IndexOf('\\') < 0) return a;
            return "\"" + a.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
    }
}
