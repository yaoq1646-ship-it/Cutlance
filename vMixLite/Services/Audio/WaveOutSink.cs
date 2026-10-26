using System;
using System.Runtime.InteropServices;

namespace Cutlance.Services.Audio
{
    /// <summary>
    /// 极简声卡输出（Win32 waveOut，纯 P/Invoke，不引入任何第三方音频库）。
    ///
    /// 为什么需要它：方案 C 把「播放器音频」从 LibVLC 直出改为程序内接管，
    /// 那么"让本机听到输出框的声音"这件事必须由我们自己完成 —— 就是把总线混好的
    /// PCM 送进系统默认播放设备。waveOut 共享模式会走系统混音器，兼容性足够。
    ///
    /// 时钟作用：waveOut 按其硬件节奏消耗缓冲，Write() 会等空闲缓冲，
    /// 因此它天然成为整条音频总线的"节拍器"（音频不会越播越快）。
    /// </summary>
    internal sealed class WaveOutSink : IDisposable
    {
        private const uint WAVE_MAPPER = 0xFFFFFFFF;
        private const uint CALLBACK_EVENT = 0x00050000;
        private const uint WHDR_DONE = 0x00000001;
        private const uint WHDR_INQUEUE = 0x00000010;

        private const int BufferCount = 6;   // 6 × 20ms = 120ms 缓冲，抗抖动

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        [DllImport("winmm.dll", SetLastError = true)]
        private static extern int waveOutOpen(out IntPtr hwo, uint uDeviceID, ref WAVEFORMATEX pwfx,
            IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);

        [DllImport("winmm.dll")]
        private static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, uint cbwh);

        [DllImport("winmm.dll")]
        private static extern int waveOutWrite(IntPtr hwo, IntPtr pwh, uint cbwh);

        [DllImport("winmm.dll")]
        private static extern int waveOutReset(IntPtr hwo);

        [DllImport("winmm.dll")]
        private static extern int waveOutClose(IntPtr hwo);

        [DllImport("winmm.dll")]
        private static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, uint cbwh);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool bManualReset,
            bool bInitialState, string? lpName);

        [DllImport("kernel32.dll")]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll")]
        private static extern bool ResetEvent(IntPtr hEvent);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr hObject);

        private readonly int _rate;
        private readonly int _channels;
        private readonly int _blockBytes;
        private readonly object _lk = new();

        private IntPtr _hwo = IntPtr.Zero;
        private IntPtr _evt = IntPtr.Zero;
        private IntPtr[] _hdrPtrs = Array.Empty<IntPtr>();
        private IntPtr[] _dataPtrs = Array.Empty<IntPtr>();
        private int _next;
        private bool _opened;

        /// <summary>是否已成功打开声卡（远程桌面/无声卡时可能为 false，此时只是本地听不到，推流不受影响）。</summary>
        public bool IsOpen => _opened;

        public string? LastError { get; private set; }

        public WaveOutSink(int rate, int channels, int blockFrames)
        {
            _rate = rate;
            _channels = channels;
            _blockBytes = blockFrames * channels * 2;
        }

        /// <summary>打开默认播放设备（16bit 立体声 PCM）。失败不抛异常，仅记录原因。</summary>
        public bool Open()
        {
            lock (_lk)
            {
                if (_opened) return true;
                try
                {
                    var fmt = new WAVEFORMATEX
                    {
                        wFormatTag = 1,                          // WAVE_FORMAT_PCM
                        nChannels = (ushort)_channels,
                        nSamplesPerSec = (uint)_rate,
                        wBitsPerSample = 16,
                        nBlockAlign = (ushort)(_channels * 2),
                        nAvgBytesPerSec = (uint)(_rate * _channels * 2),
                        cbSize = 0
                    };

                    _evt = CreateEvent(IntPtr.Zero, false, false, null);
                    if (_evt == IntPtr.Zero) { LastError = "CreateEvent 失败"; return false; }

                    int rc = waveOutOpen(out _hwo, WAVE_MAPPER, ref fmt, _evt, IntPtr.Zero, CALLBACK_EVENT);
                    if (rc != 0)
                    {
                        LastError = "waveOutOpen 失败，错误码 " + rc + "（可能无可用播放设备）";
                        CloseHandle(_evt); _evt = IntPtr.Zero; _hwo = IntPtr.Zero;
                        return false;
                    }

                    _hdrPtrs = new IntPtr[BufferCount];
                    _dataPtrs = new IntPtr[BufferCount];
                    int hdrSize = Marshal.SizeOf<WAVEHDR>();
                    for (int i = 0; i < BufferCount; i++)
                    {
                        _dataPtrs[i] = Marshal.AllocHGlobal(_blockBytes);
                        _hdrPtrs[i] = Marshal.AllocHGlobal(hdrSize);
                        var h = new WAVEHDR { lpData = _dataPtrs[i], dwBufferLength = (uint)_blockBytes };
                        Marshal.StructureToPtr(h, _hdrPtrs[i], false);
                        waveOutPrepareHeader(_hwo, _hdrPtrs[i], (uint)hdrSize);
                    }
                    _next = 0;
                    _opened = true;
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    return false;
                }
            }
        }

        /// <summary>写入一块 PCM（16bit 交错）。等空闲缓冲；超时则丢弃本块，绝不阻塞死。</summary>
        public void Write(byte[] pcm, int offset, int count)
        {
            if (!_opened) return;
            lock (_lk)
            {
                if (!_opened) return;
                int hdrSize = Marshal.SizeOf<WAVEHDR>();
                for (int guard = 0; guard < 60; guard++)     // 最长等 ~1.2s
                {
                    int i = _next;
                    var h = Marshal.PtrToStructure<WAVEHDR>(_hdrPtrs[i]);
                    if ((h.dwFlags & WHDR_INQUEUE) == 0)
                    {
                        int n = Math.Min(count, _blockBytes);
                        Marshal.Copy(pcm, offset, _dataPtrs[i], n);
                        h.lpData = _dataPtrs[i];
                        h.dwBufferLength = (uint)n;
                        h.dwFlags &= ~WHDR_DONE;
                        Marshal.StructureToPtr(h, _hdrPtrs[i], false);
                        waveOutWrite(_hwo, _hdrPtrs[i], (uint)hdrSize);
                        _next = (_next + 1) % BufferCount;
                        return;
                    }
                    WaitForSingleObject(_evt, 20);
                    ResetEvent(_evt);
                }
            }
        }

        /// <summary>清空当前排队的音频（切换音源时避免残留旧声音）。</summary>
        public void Flush()
        {
            lock (_lk)
            {
                if (!_opened) return;
                try
                {
                    waveOutReset(_hwo);
                    int hdrSize = Marshal.SizeOf<WAVEHDR>();
                    for (int i = 0; i < BufferCount; i++)
                    {
                        var h = Marshal.PtrToStructure<WAVEHDR>(_hdrPtrs[i]);
                        h.dwBufferLength = 0;
                        h.dwFlags &= ~WHDR_DONE;
                        Marshal.StructureToPtr(h, _hdrPtrs[i], false);
                        waveOutPrepareHeader(_hwo, _hdrPtrs[i], (uint)hdrSize);
                    }
                    _next = 0;
                }
                catch { }
            }
        }

        public void Dispose()
        {
            lock (_lk)
            {
                if (!_opened && _hwo == IntPtr.Zero) return;
                try { waveOutReset(_hwo); } catch { }
                int hdrSize = Marshal.SizeOf<WAVEHDR>();
                for (int i = 0; i < _hdrPtrs.Length; i++)
                {
                    try
                    {
                        if (_hdrPtrs[i] != IntPtr.Zero)
                            waveOutUnprepareHeader(_hwo, _hdrPtrs[i], (uint)hdrSize);
                    }
                    catch { }
                    try { if (_hdrPtrs[i] != IntPtr.Zero) Marshal.FreeHGlobal(_hdrPtrs[i]); } catch { }
                    try { if (_dataPtrs[i] != IntPtr.Zero) Marshal.FreeHGlobal(_dataPtrs[i]); } catch { }
                }
                _hdrPtrs = Array.Empty<IntPtr>();
                _dataPtrs = Array.Empty<IntPtr>();
                try { if (_hwo != IntPtr.Zero) waveOutClose(_hwo); } catch { }
                try { if (_evt != IntPtr.Zero) CloseHandle(_evt); } catch { }
                _hwo = IntPtr.Zero; _evt = IntPtr.Zero; _opened = false;
            }
        }
    }
}
