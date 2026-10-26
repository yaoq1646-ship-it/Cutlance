using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Cutlance.Services.Audio
{
    /// <summary>
    /// 方案 C：程序内统一音频总线。
    ///
    /// 要解决的问题（老板原话）：
    ///   「音频采集应包含选择文件的声音或摄像头声道，只在输出框里发出声音，其他位置不输出到声卡；
    ///     选择某个声音或多个声音时，输出框把添加的声音送进声卡」
    ///   「第一个文件切到输出框后，选择新文件进来时声音还是能播出」（预览/非音源层不该出声）
    ///
    /// 做法：所有播放器的音频都不再由 LibVLC 直出（见 VlcAudioTap），而是汇入本总线的 Track；
    /// 总线按 20ms 一块做软件混音（逐路音量/静音），混好的结果分两路走：
    ///   ① 声卡：老板要求的"输出框把声音送进声卡"（可整体开关）
    ///   ② 推流/录制：通过命名管道喂给 FFmpeg 的音频输入
    /// 这样"谁该出声"完全由程序说了算，预览框、非音源图层天然不会漏音。
    /// </summary>
    public sealed class AudioBus : IDisposable
    {
        public static AudioBus Instance { get; } = new AudioBus();

        public const int Rate = 48000;            // 统一 48kHz
        public const int Channels = 2;            // 立体声
        public const int BytesPerFrame = Channels * 2;
        private const int BlockFrames = 960;      // 20ms / 块
        private const int BlockBytes = BlockFrames * BytesPerFrame;

        // ===== 与 FFmpeg 的传输通道 =====
        // 曾用 Windows 命名管道（\\.\pipe\...）喂音频，但本机 ffmpeg 报
        // "Error opening input: Invalid argument"（其 file 协议打不开命名管道）。
        // 改用 TCP 回环：FFmpeg 以 tcp:// 客户端身份连进来，我们用 NetworkStream 推 PCM。
        // 好处：不影响 stdin（停止时仍能发 'q' 优雅收尾 mkv/mp4），也不依赖任何驱动。

        private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "Cutlance_audio.log");

        public static void Log(string msg)
        {
            try
            {
                File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + Environment.NewLine);
            }
            catch { }
        }

        // ================= Track =================

        /// <summary>一路音频来源（一个图层 / 预览 / 麦克风 / 其它）。</summary>
        public sealed class Track
        {
            private readonly float[] _buf;
            private readonly object _lk = new();
            private int _r, _count;
            private float[] _scratch = new float[Rate * Channels];       // 1 秒
            private byte[] _scratchBytes = new byte[Rate * Channels * 2];

            public string Key { get; }
            public volatile bool Mute = true;          // 默认静音，必须显式打开
            public volatile float Volume = 1f;
            public volatile bool ToSoundCard;          // 是否混进声卡输出
            public volatile bool ToStream;             // 是否混进推流/录制
            public volatile bool Closed;
            public long PushedFrames;

            internal Track(string key)
            {
                Key = key;
                _buf = new float[Rate * Channels * 2];   // 2 秒环形缓冲
            }

            /// <summary>LibVLC 回调：把 S16 交错 PCM 转 float 并写入。</summary>
            public void PushS16(IntPtr samples, int frames)
            {
                if (Closed || frames <= 0) return;
                int n = frames * Channels;
                if (_scratch.Length < n) _scratch = new float[n];
                if (_scratchBytes.Length < n * 2) _scratchBytes = new byte[n * 2];
                Marshal_Copy(samples, _scratchBytes, n * 2);
                var tmp = _scratch;
                var b = _scratchBytes;
                for (int i = 0; i < n; i++)
                    tmp[i] = (short)(b[i * 2] | (b[i * 2 + 1] << 8)) / 32768f;
                Write(tmp, n);
                Interlocked.Add(ref PushedFrames, frames);
            }

            private static void Marshal_Copy(IntPtr src, byte[] dst, int len)
                => System.Runtime.InteropServices.Marshal.Copy(src, dst, 0, len);

            private void Write(float[] src, int n)
            {
                lock (_lk)
                {
                    int cap = _buf.Length;
                    if (n >= cap)
                    {
                        Array.Copy(src, n - cap, _buf, 0, cap);
                        _r = 0; _count = cap;
                        return;
                    }
                    int space = cap - _count;
                    if (n > space)   // 满了丢最旧，保证不阻塞 native 回调
                    {
                        int drop = n - space;
                        _r = (_r + drop) % cap;
                        _count -= drop;
                    }
                    int w = (_r + _count) % cap;
                    int first = Math.Min(n, cap - w);
                    Array.Copy(src, 0, _buf, w, first);
                    if (n > first) Array.Copy(src, first, _buf, 0, n - first);
                    _count += n;
                }
            }

            /// <summary>读出 n 个样本并按音量累加到 dst（不足的部分保持原样 = 当作静音）。</summary>
            internal void ReadAdd(float[] dst, int n, float vol)
            {
                lock (_lk)
                {
                    int take = Math.Min(n, _count);
                    int cap = _buf.Length;
                    int r = _r;
                    int first = Math.Min(take, cap - r);
                    for (int i = 0; i < first; i++) dst[i] += _buf[r + i] * vol;
                    for (int i = first; i < take; i++) dst[i] += _buf[i - first] * vol;
                    _r = (_r + take) % cap;
                    _count -= take;
                }
            }

            /// <summary>丢弃 n 个样本（不参与混音时也要按时消耗，否则缓冲会一直积压）。</summary>
            internal void Skip(int n)
            {
                lock (_lk)
                {
                    int take = Math.Min(n, _count);
                    _r = (_r + take) % _buf.Length;
                    _count -= take;
                }
            }

            public void Clear()
            {
                lock (_lk) { _r = 0; _count = 0; }
            }
        }

        // ================= 状态 =================

        private readonly Dictionary<string, Track> _tracks = new();
        private readonly object _tracksLock = new();

        private WaveOutSink? _sink;
        private Thread? _mixThread;
        private volatile bool _running;

        private TcpListener? _tcp;
        private volatile int _tcpPort;
        private volatile NetworkStream? _stream;
        private volatile bool _pipeWanted;
        private Thread? _pipeThread;

        /// <summary>声卡输出总开关（老板要的"输出框把声音送进声卡"；关掉则只在流里有声）。</summary>
        public bool SoundCardEnabled { get; set; } = true;

        public bool SoundCardOpen => _sink?.IsOpen == true;
        public string? SoundCardError => _sink?.LastError;

        /// <summary>推流/录制是否正在从总线取音频。</summary>
        public bool PipeConnected => _stream != null;

        /// <summary>FFmpeg 要连接的音频输入地址（形如 tcp://127.0.0.1:52134）。</summary>
        public string InputUrl => _tcpPort > 0 ? "tcp://127.0.0.1:" + _tcpPort : "";

        /// <summary>本次输出是否要求接入总线音频（FFmpeg 参数据此决定是否加管道输入）。</summary>
        public bool PipeWanted => _pipeWanted;

        private AudioBus() { }

        // ================= Track 管理 =================

        public Track EnsureTrack(string key)
        {
            lock (_tracksLock)
            {
                if (_tracks.TryGetValue(key, out var t)) return t;
                t = new Track(key);
                _tracks[key] = t;
                return t;
            }
        }

        public Track? FindTrack(string key)
        {
            lock (_tracksLock)
                return _tracks.TryGetValue(key, out var t) ? t : null;
        }

        public void RemoveTrack(string key)
        {
            lock (_tracksLock)
            {
                if (_tracks.TryGetValue(key, out var t))
                {
                    t.Closed = true;
                    t.Clear();
                    _tracks.Remove(key);
                }
            }
        }

        public void ClearTracks()
        {
            lock (_tracksLock)
            {
                foreach (var t in _tracks.Values) { t.Closed = true; t.Clear(); }
                _tracks.Clear();
            }
        }

        /// <summary>当前是否存在"能出声"的轨道（供 UI 提示用）。</summary>
        public bool AnyAudible
        {
            get
            {
                lock (_tracksLock)
                    return _tracks.Values.Any(t => !t.Mute && t.Volume > 0f && (t.ToSoundCard || t.ToStream));
            }
        }

        // ================= 启动 / 停止 =================

        public void EnsureStarted()
        {
            if (_running) return;
            _running = true;
            try
            {
                if (SoundCardEnabled)
                {
                    _sink = new WaveOutSink(Rate, Channels, BlockFrames);
                    if (!_sink.Open())
                        Log("声卡打开失败：" + (_sink.LastError ?? "未知") + "（仅影响本机监听，推流不受影响）");
                    else
                        Log("声卡输出已打开（48kHz 立体声 / waveOut）");
                }
                _mixThread = new Thread(MixLoop)
                {
                    IsBackground = true,
                    Name = "Cutlance-audio-mix",
                    Priority = ThreadPriority.AboveNormal
                };
                _mixThread.Start();
                Log("音频总线已启动");
            }
            catch (Exception ex) { Log("总线启动异常：" + ex.Message); }
        }

        /// <summary>开始向 FFmpeg 提供混音音频（推流/录制启动时调用，务必在 FFmpeg 启动之前）。</summary>
        public void StartPipe()
        {
            _pipeWanted = true;
            if (_pipeThread != null) return;
            try
            {
                _tcp = new TcpListener(IPAddress.Loopback, 0);   // 由系统分配空闲端口
                _tcp.Start();
                _tcpPort = ((IPEndPoint)_tcp.LocalEndpoint).Port;
                Log("音频总线已监听 tcp://127.0.0.1:" + _tcpPort + "（等待 FFmpeg 接入）");
            }
            catch (Exception ex) { Log("音频 TCP 监听失败：" + ex.Message); _tcpPort = 0; }

            _pipeThread = new Thread(PipeAcceptLoop)
            {
                IsBackground = true,
                Name = "Cutlance-audio-tcp"
            };
            _pipeThread.Start();
        }

        /// <summary>停止通道（推流/录制停止时调用）。</summary>
        public void StopPipe()
        {
            _pipeWanted = false;
            try { _stream?.Dispose(); } catch { }
            _stream = null;
            try { _tcp?.Stop(); } catch { }     // 让阻塞中的 Accept 立刻返回
            _tcp = null;
            _tcpPort = 0;
        }

        public void FlushSoundCard() => _sink?.Flush();

        // ===== 诊断探针：把总线混音结果实时转储为裸 PCM（48k/16bit/立体声） =====
        // 用于客观验证"哪些声音真的进了输出"，平时不启用，零开销。
        private FileStream? _dumpStream;

        public void StartDump(string path)
        {
            try
            {
                StopDump();
                _dumpStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                Log("开始转储混音输出 → " + path);
            }
            catch (Exception ex) { Log("转储失败：" + ex.Message); }
        }

        public void StopDump()
        {
            try { _dumpStream?.Flush(); _dumpStream?.Dispose(); } catch { }
            _dumpStream = null;
        }

        /// <summary>当前所有轨道的状态快照（诊断用）。</summary>
        public string DumpTracks()
        {
            var sb = new StringBuilder();
            lock (_tracksLock)
            {
                foreach (var t in _tracks.Values)
                    sb.AppendLine($"  {t.Key}: mute={t.Mute} vol={t.Volume:0.00} card={t.ToSoundCard} stream={t.ToStream} frames={Interlocked.Read(ref t.PushedFrames)}");
            }
            return sb.ToString();
        }

        public void Shutdown()
        {
            _running = false;
            StopPipe();
            try { _sink?.Dispose(); } catch { }
            _sink = null;
            ClearTracks();
            Log("音频总线已停止");
        }

        public void Dispose() => Shutdown();

        // ================= 管道 =================

        private void PipeAcceptLoop()
        {
            while (_pipeWanted)
            {
                TcpClient? c = null;
                try
                {
                    c = _tcp?.AcceptTcpClient();          // 阻塞等待 ffmpeg 连入
                    if (c == null) break;
                    c.NoDelay = true;
                    _stream = c.GetStream();
                    Log("FFmpeg 已接入音频总线（TCP）");
                    while (_pipeWanted && c.Connected) Thread.Sleep(100);
                }
                catch (Exception ex)
                {
                    if (_pipeWanted && _tcp != null) Log("音频 TCP 异常：" + ex.Message);
                }
                _stream = null;
                try { c?.Close(); } catch { }
                if (_pipeWanted) Thread.Sleep(300);
            }
            _pipeThread = null;
        }

        private void WriteStream(byte[] pcm, int count)
        {
            var s = _stream;
            if (s == null) return;
            try { s.Write(pcm, 0, count); }
            catch
            {
                // 对端（FFmpeg）已退出：只标记断开，绝不拖垮混音线程
                _stream = null;
                try { s.Dispose(); } catch { }
            }
        }

        // ================= 混音循环 =================

        private void MixLoop()
        {
            var mix = new float[BlockFrames * Channels];
            var pcm = new byte[BlockBytes];
            var list = new List<Track>();

            while (_running)
            {
                try
                {
                    lock (_tracksLock) list = _tracks.Values.ToList();

                    Array.Clear(mix, 0, mix.Length);
                    foreach (var t in list)
                    {
                        if (t.Closed) continue;
                        bool audible = !t.Mute && t.Volume > 0f && (t.ToSoundCard || t.ToStream);
                        if (audible) t.ReadAdd(mix, mix.Length, t.Volume);
                        else t.Skip(mix.Length);
                    }

                    // float → S16 交错
                    for (int i = 0; i < mix.Length; i++)
                    {
                        float v = mix[i];
                        if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
                        short s = (short)(v * 32000f);
                        pcm[i * 2] = (byte)(s & 0xFF);
                        pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
                    }

                    bool paced = false;
                    var sink = _sink;
                    if (sink != null && sink.IsOpen)
                    {
                        sink.Write(pcm, 0, pcm.Length);
                        paced = true;      // waveOut 按硬件节奏消耗 → 天然节拍器
                    }
                    if (_stream != null) WriteStream(pcm, pcm.Length);

                    var dump = _dumpStream;
                    if (dump != null) { try { dump.Write(pcm, 0, pcm.Length); } catch { } }

                    if (!paced) Thread.Sleep(BlockFrames * 1000 / Rate);
                }
                catch (Exception ex)
                {
                    Log("混音循环异常：" + ex.Message);
                    Thread.Sleep(20);
                }
            }
        }
    }
}
