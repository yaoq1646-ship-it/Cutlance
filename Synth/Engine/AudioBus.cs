using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace SynthPoc.Engine
{
    /// <summary>
    /// P2.5 音频总线：把每路 VLC 解码出来的 PCM 混成一路，实时喂给 ffmpeg。
    ///
    /// 与主程序 Cutlance.Services.Audio.AudioBus 的关系：
    ///   思路与数据结构完全一致（20ms 一块软件混音、Track 环形缓冲、丢最旧不阻塞 native 回调），
    ///   这里去掉声卡输出、去掉 UI 相关，只保留「混音 → TCP 供流」这条 POC 必须的链路。
    ///   最终并入产品时以主程序那份为准（那边已经跑了一轮真机验证）。
    ///
    /// 为什么是 TCP 而不是命名管道：
    ///   主程序踩过 —— ffmpeg 的 file 协议打不开 Windows 命名管道（Invalid argument）。
    ///   本机回环 TCP 是 ffmpeg 原生支持的输入（tcp://），不依赖任何驱动。
    ///
    /// 为什么混音节拍要用绝对时间基准：
    ///   Sleep(20) 的实际精度受系统定时器影响，逐块累加会持续偏慢，
    ///   几分钟后音频就比画面落后一大截（观众听出来是"声音越来越晚"）。
    ///   用 start + n×周期 重算，误差不会累积。
    /// </summary>
    internal sealed class AudioBus : IDisposable
    {
        public static AudioBus Instance { get; } = new();

        public const int Rate = 48000;
        public const int Channels = 2;
        public const int BytesPerFrame = Channels * 2;
        private const int BlockFrames = 960;               // 20ms / 块
        private const int BlockBytes = BlockFrames * BytesPerFrame;
        private const int MaxLagBlocks = 6;                // 落后超过 120ms 就重对齐，宁可丢音频也不追

        private readonly Dictionary<string, Track> _tracks = new();
        private readonly object _tracksLock = new();

        private Thread? _mixThread;
        private volatile bool _running;

        private TcpListener? _tcp;
        private volatile int _tcpPort;
        private readonly List<ClientSink> _clients = new();
        private readonly object _clientsLock = new();
        private Thread? _acceptThread;

        private long _blocksWritten, _blocksSkipped;
        public long BlocksWritten => Interlocked.Read(ref _blocksWritten);
        public long BlocksDropped => Interlocked.Read(ref _blocksSkipped);
        public int ClientCount { get { lock (_clientsLock) return _clients.Count; } }
        public bool PipeConnected => ClientCount > 0;

        /// <summary>
        /// 一路 ffmpeg 的音频出口。
        ///
        /// 为什么要独立线程 + 队列（实测教训）：
        ///   直接在混音线程里对所有客户端 Write，只要有一路读得慢（比如正在写 mp4、
        ///   磁盘一卡），TCP 缓冲满 → Write 阻塞甚至超时 → 这一路被摘掉 →
        ///   它的音频流提前 EOF → ffmpeg 的 -shortest 把整段输出截断（实测 451 帧只剩 386 帧）。
        ///   拆成「混音线程只入队、每路自己的线程慢慢写」，一路卡住不连累另一路。
        /// </summary>
        private sealed class ClientSink
        {
            public TcpClient Tcp = null!;
            public readonly BlockingCollection<byte[]> Queue = new(60);   // 60 × 20ms = 1.2 秒缓冲
            public Thread? Writer;
            public long Written, Dropped;

            public void Enqueue(byte[] block)
            {
                if (!Queue.IsAddingCompleted)
                {
                    if (!Queue.TryAdd(block)) Interlocked.Increment(ref Dropped);
                }
            }
        }

        /// <summary>ffmpeg 要连的音频地址，形如 tcp://127.0.0.1:52134。空 = 没启动。</summary>
        public string InputUrl => _tcpPort > 0 ? "tcp://127.0.0.1:" + _tcpPort : "";

        private AudioBus() { }

        // ================= Track =================
        public sealed class Track
        {
            private readonly float[] _buf;
            private readonly object _lk = new();
            private int _r, _count;
            private float[] _scratch = new float[Rate * Channels];
            private byte[] _scratchBytes = new byte[Rate * Channels * 2];

            public string Key { get; }
            public volatile bool Mute = true;        // 默认静音，必须显式打开
            public volatile float Volume = 1f;
            public volatile bool ToStream = true;
            public volatile bool Closed;
            public long PushedFrames;

            internal Track(string key)
            {
                Key = key;
                _buf = new float[Rate * Channels * 2];   // 2 秒环形缓冲
            }

            /// <summary>LibVLC 回调入口：S16 交错 PCM → float 环形缓冲。</summary>
            public void PushS16(IntPtr samples, int frames)
            {
                if (Closed || frames <= 0) return;
                int n = frames * Channels;
                if (_scratch.Length < n) _scratch = new float[n];
                if (_scratchBytes.Length < n * 2) _scratchBytes = new byte[n * 2];

                System.Runtime.InteropServices.Marshal.Copy(samples, _scratchBytes, 0, n * 2);
                var tmp = _scratch;
                var b = _scratchBytes;
                for (int i = 0; i < n; i++)
                    tmp[i] = (short)(b[i * 2] | (b[i * 2 + 1] << 8)) / 32768f;

                Write(tmp, n);
                Interlocked.Add(ref PushedFrames, frames);
            }

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
                    if (n > space)   // 满了丢最旧，绝不阻塞 native 回调
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

            internal void Skip(int n)
            {
                lock (_lk)
                {
                    int take = Math.Min(n, _count);
                    _r = (_r + take) % _buf.Length;
                    _count -= take;
                }
            }

            public void Clear() { lock (_lk) { _r = 0; _count = 0; } }
        }

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

        public void ClearTracks()
        {
            lock (_tracksLock)
            {
                foreach (var t in _tracks.Values) { t.Closed = true; t.Clear(); }
                _tracks.Clear();
            }
        }

        // ================= 启动 / 停止 =================
        public void Start()
        {
            if (_running) return;
            _running = true;

            try
            {
                _tcp = new TcpListener(IPAddress.Loopback, 0);
                _tcp.Start();
                _tcpPort = ((IPEndPoint)_tcp.LocalEndpoint).Port;
                Trace.Log($"音频总线监听 {InputUrl}");
            }
            catch (Exception ex)
            {
                Trace.Log("音频 TCP 监听失败: " + ex.Message);
                _tcpPort = 0;
            }

            _acceptThread = new Thread(AcceptLoop)
            {
                IsBackground = true,
                Name = "SynthAudio-Tcp"
            };
            _acceptThread.Start();

            _mixThread = new Thread(MixLoop)
            {
                IsBackground = true,
                Name = "SynthAudio-Mix",
                Priority = ThreadPriority.AboveNormal
            };
            _mixThread.Start();
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    var client = _tcp!.AcceptTcpClient();
                    client.NoDelay = true;
                    client.SendTimeout = 0;               // 不设超时：慢是慢，但别因为慢就断流
                    client.SendBufferSize = 512 * 1024;

                    var sink = new ClientSink { Tcp = client };
                    sink.Writer = new Thread(() => WriteLoop(sink))
                    {
                        IsBackground = true,
                        Name = "SynthAudio-Writer"
                    };
                    sink.Writer.Start();

                    lock (_clientsLock) _clients.Add(sink);
                    Trace.Log($"ffmpeg 接入音频通道（当前 {ClientCount} 路）");

                    // 每路单独看它什么时候断，断了就摘掉 —— 别让它拖住其它输出
                    var t = new Thread(() => WatchClient(sink))
                    {
                        IsBackground = true,
                        Name = "SynthAudio-Watch"
                    };
                    t.Start();
                }
                catch (Exception ex)
                {
                    if (!_running) break;
                    Trace.Log("音频接入异常: " + ex.Message);
                    Thread.Sleep(200);
                }
            }
        }

        private void WriteLoop(ClientSink sink)
        {
            try
            {
                foreach (var block in sink.Queue.GetConsumingEnumerable())
                {
                    sink.Tcp.GetStream().Write(block, 0, block.Length);
                    Interlocked.Increment(ref sink.Written);
                }
            }
            catch
            {
                // 对端断开/写失败：正常路径，直接收摊
            }
            finally
            {
                try { sink.Queue.CompleteAdding(); } catch { }
                lock (_clientsLock) _clients.Remove(sink);
                try { sink.Tcp.Close(); } catch { }
                Trace.Log($"ffmpeg 音频通道断开（剩 {ClientCount} 路）");
            }
        }

        private void WatchClient(ClientSink sink)
        {
            try
            {
                while (_running && sink.Tcp.Client.Connected)
                {
                    // Poll 返回 true 且没有待读数据 = 对端已关闭
                    if (sink.Tcp.Client.Poll(300000, SelectMode.SelectRead) && sink.Tcp.Client.Available == 0) break;
                    Thread.Sleep(150);
                }
            }
            catch { }

            try { sink.Queue.CompleteAdding(); } catch { }
        }

        private void MixLoop()
        {
            var mix = new float[BlockFrames * Channels];
            var pcm = new byte[BlockBytes];
            var list = new List<Track>();

            double periodTicks = Stopwatch.Frequency * (double)BlockFrames / Rate;
            long start = Stopwatch.GetTimestamp();
            long n = 0;

            while (_running)
            {
                long due = start + (long)(n * periodTicks);
                long now = Stopwatch.GetTimestamp();

                if (due > now)
                {
                    double ms = (due - now) * 1000.0 / Stopwatch.Frequency;
                    if (ms > 2.0) Thread.Sleep(1);
                    else Thread.SpinWait(80);
                    continue;
                }

                // 落后太多（TCP 写阻塞 / 系统卡顿）：平移时间轴，不追块
                if (now - due > (long)(periodTicks * MaxLagBlocks))
                {
                    long lag = now - due;
                    start += lag;
                    due += lag;
                    Interlocked.Increment(ref _blocksSkipped);
                }

                try
                {
                    lock (_tracksLock) list = new List<Track>(_tracks.Values);

                    Array.Clear(mix, 0, mix.Length);
                    foreach (var t in list)
                    {
                        if (t.Closed) continue;
                        bool audible = !t.Mute && t.Volume > 0f && t.ToStream;
                        if (audible) t.ReadAdd(mix, mix.Length, t.Volume);
                        else t.Skip(mix.Length);
                    }

                    for (int i = 0; i < mix.Length; i++)
                    {
                        float v = mix[i];
                        if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
                        short s = (short)(v * 32000f);
                        pcm[i * 2] = (byte)(s & 0xFF);
                        pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
                    }

                    // 广播给所有已接入的 ffmpeg（录制 + 推流同时要声音）
                    // 只入队、不在这里写盘 —— 混音线程必须准点，不能被任何一个慢客户端拖住
                    ClientSink[] clients;
                    lock (_clientsLock) clients = _clients.ToArray();

                    foreach (var c in clients) c.Enqueue((byte[])pcm.Clone());

                    if (clients.Length > 0) Interlocked.Increment(ref _blocksWritten);
                }
                catch (Exception ex)
                {
                    Trace.Log("混音异常: " + ex.Message);
                }

                n++;
            }
        }

        public void Dispose()
        {
            _running = false;
            try { _tcp?.Stop(); } catch { }
            try { _mixThread?.Join(1500); } catch { }

            lock (_clientsLock)
            {
                foreach (var c in _clients)
                {
                    try { c.Queue.CompleteAdding(); } catch { }
                    try { c.Tcp.Close(); } catch { }
                }
                _clients.Clear();
            }

            try { _acceptThread?.Join(1500); } catch { }
            ClearTracks();
        }
    }
}
