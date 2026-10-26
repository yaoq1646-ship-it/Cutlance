using System;
using System.Runtime.InteropServices;
using System.Threading;
using LibVLCSharp.Shared;

namespace SynthPoc.Engine
{
    /// <summary>
    /// 一路输入：LibVLC 解码后通过 vmem 回调，把每一帧直接写到我们自己的非托管内存（BGRA）。
    ///
    /// 四缓冲说明（P1 定稿）：
    ///   _lockedIdx  VLC 正在写
    ///   _readyIdx   刚写完、等着被读
    ///   _readIdx    合成器当前持有的那帧
    ///   剩下那个    周转用
    /// 合成器**永远不主动释放**正在读的帧，只在下次取帧时把旧帧让出来。
    /// 这样任何时刻 VLC 都至少有一块能写，且合成器拿到的永远是完整帧（不会读到写了一半的）。
    /// </summary>
    internal sealed class VmemInput : IDisposable
    {
        private const int BufferCount = 4;

        private readonly object _gate = new();
        private readonly IntPtr[] _bufs = new IntPtr[BufferCount];
        private int _lockedIdx = -1;
        private int _readyIdx = -1;
        private int _readIdx = -1;

        // 委托必须被强引用持有：被 GC 回收后 VLC 回调进来就是野指针，进程直接崩
        private MediaPlayer.LibVLCVideoLockCb? _lockCb;
        private MediaPlayer.LibVLCVideoUnlockCb? _unlockCb;
        private MediaPlayer.LibVLCVideoDisplayCb? _displayCb;

        private long _frames;
        private long _dropped;
        private string _error = string.Empty;

        public string Name { get; }
        public int Width { get; }
        public int Height { get; }
        public int Stride { get; }
        public MediaPlayer Player { get; }
        public Media Media { get; }

        public long Frames => Interlocked.Read(ref _frames);
        public long Dropped => Interlocked.Read(ref _dropped);
        public string Error => _error;

        public VmemInput(LibVLC vlc, string file, int width, int height, string name,
                         string? startTime = null, bool loop = true, AudioBus.Track? audioTrack = null)
            : this(vlc, BuildMedia(vlc, file, startTime, loop), width, height, name, audioTrack)
        {
        }

        private static Media BuildMedia(LibVLC vlc, string file, string? startTime, bool loop)
        {
            var m = new Media(vlc, file, FromType.FromPath);
            if (loop) m.AddOption(":input-repeat=65535");
            if (!string.IsNullOrEmpty(startTime)) m.AddOption(":start-time=" + startTime);
            m.AddOption(":no-video-title-show");
            // 注意：不能加 :no-audio —— 加了 VLC 就完全不解析音频轨，
            // amem 回调永远收不到数据（P2.5 踩过）。
            return m;
        }

        /// <summary>
        /// P4：由调用方自己构造好 Media 再交进来。
        ///
        /// 产品里每一路输入的地址形态差别极大（本地文件 / dshow 摄像头 / rtsp / srt /
        /// screen 抓屏 / ppt 放映窗口 / NDI），主程序早就有唯一的媒体构造入口
        /// `VideoEngine.CreateMedia`（铁律 3），所有参数、解码选项都在那里拼。
        /// 让合成器自己拼 MRL 等于把那套逻辑复制一遍，两边必然分叉 ——
        /// 所以这里只负责「解码后把帧交给我」，媒体怎么来由上层决定。
        /// </summary>
        public VmemInput(LibVLC vlc, Media media, int width, int height, string name,
                         AudioBus.Track? audioTrack = null,
                         Action<MediaPlayer>? attachAudio = null)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));

            Name = name;
            Width = width;
            Height = height;
            Stride = width * 4;

            int bytes = Stride * height;
            for (int i = 0; i < BufferCount; i++)
            {
                _bufs[i] = Marshal.AllocHGlobal(bytes);
                unsafe { new Span<byte>((void*)_bufs[i], bytes).Clear(); }
            }

            Media = media;

            Player = new MediaPlayer(vlc);

            _lockCb = (opaque, planes) =>
            {
                int pick;
                lock (_gate)
                {
                    pick = -1;
                    for (int i = 0; i < BufferCount; i++)
                    {
                        if (i != _lockedIdx && i != _readyIdx && i != _readIdx) { pick = i; break; }
                    }
                    if (pick < 0)
                    {
                        // 四块全占 = 合成器读得比解码慢。抢最旧的那块，宁可丢一帧也不能让解码管线停摆。
                        pick = _readyIdx >= 0 ? _readyIdx : 0;
                        Interlocked.Increment(ref _dropped);
                    }
                    _lockedIdx = pick;
                }
                Marshal.WriteIntPtr(planes, 0, _bufs[pick]);
                return IntPtr.Zero;
            };

            _unlockCb = (opaque, picture, planes) => { };

            _displayCb = (opaque, picture) =>
            {
                lock (_gate)
                {
                    if (_lockedIdx >= 0)
                    {
                        _readyIdx = _lockedIdx;
                        _lockedIdx = -1;
                    }
                }
                Interlocked.Increment(ref _frames);
            };

            Player.SetVideoFormat("RV32", (uint)width, (uint)height, (uint)Stride);
            Player.SetVideoCallbacks(_lockCb, _unlockCb, _displayCb);

            // P2.5：音频走 amem 回调汇入总线。必须在 Play() 之前挂上，
            // 且顺序是「先 set_callbacks 再 set_format」（主程序实测结论）。
            //
            // P4：产品里已经有自己的一套音频总线（带声卡输出、推流供流），
            // 切换台不该再起第二套 —— 所以留了 attachAudio 钩子，由上层把
            // 「怎么把这一路的声音接进产品音频总线」注入进来（媒体构造同理，走 VideoEngine）。
            if (attachAudio != null) attachAudio(Player);
            else if (audioTrack != null) VlcAudioTap.Attach(Player, audioTrack);

            Player.EncounteredError += (s, e) => _error = "VLC 报错（解码/打开失败）";
        }

        public bool Start()
        {
            try { return Player.Play(Media); }
            catch (Exception ex) { _error = ex.Message; return false; }
        }

        /// <summary>
        /// 取当前最新完整帧。没有新帧时沿用上一次那帧（返回同一指针），保证画面不闪。
        /// 返回 IntPtr.Zero = 还没出过任何一帧。
        /// </summary>
        public IntPtr Acquire()
        {
            lock (_gate)
            {
                if (_readyIdx >= 0 && _readyIdx != _readIdx)
                {
                    _readIdx = _readyIdx;   // 旧的那帧在这句里自动让出来
                    _readyIdx = -1;
                }
                return _readIdx >= 0 ? _bufs[_readIdx] : IntPtr.Zero;
            }
        }

        public void Dispose()
        {
            // 顺序不能反：必须先让 VLC 停下来，之后才能碰委托和缓冲区
            try { Player.Stop(); } catch { }
            _lockCb = null;
            _unlockCb = null;
            _displayCb = null;

            try { Player.Dispose(); } catch { }
            try { Media.Dispose(); } catch { }

            lock (_gate)
            {
                _lockedIdx = _readyIdx = _readIdx = -1;
                for (int i = 0; i < BufferCount; i++)
                {
                    if (_bufs[i] != IntPtr.Zero) { Marshal.FreeHGlobal(_bufs[i]); _bufs[i] = IntPtr.Zero; }
                }
            }
        }
    }
}
