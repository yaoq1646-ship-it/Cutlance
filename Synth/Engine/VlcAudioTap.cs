using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace SynthPoc.Engine
{
    /// <summary>
    /// LibVLC「解码后音频接管器」——方案 C 的地基。
    ///
    /// 背景：LibVLC 默认会把解码后的音频直接送声卡（aout 直出），程序拿不到、也管不住，
    /// 于是出现「预览框也在出声」「非音源图层也在出声」这类问题。
    /// 做法：给 MediaPlayer 装上 audio callbacks（libvlc 会切到 amem 输出，不再自己出声），
    /// 解码后的 PCM 就完全落到我们手里，再由 AudioBus 决定"谁出声、进不进声卡、进不进推流"。
    ///
    /// 实现要点（均已实测验证）：
    ///  1) LibVLCSharp 自带的 SetAudioCallbacks 不暴露 opaque 参数，无法区分是哪一路播放器，
    ///     所以这里直接 P/Invoke libvlc_audio_set_callbacks，把 opaque 传成 GCHandle 指针，
    ///     回调里即可精确还原对应的 Track（实测 data == opaque）。
    ///  2) 必须先 libvlc_audio_set_callbacks 再 libvlc_audio_set_format("S16N",48000,2)，
    ///     且都要在 Play() 之前调用。
    ///  3) 回调参数 count 是「每声道样本数（帧数）」，不是字节数 —— 数据长度 = count×2ch×2byte。
    /// </summary>
    internal static class VlcAudioTap
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void PlayCb(IntPtr data, IntPtr samples, uint count, long pts);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void PauseCb(IntPtr data, long pts);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ResumeCb(IntPtr data, long pts);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void FlushCb(IntPtr data, long pts);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void DrainCb(IntPtr data);

        [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
        private static extern void libvlc_audio_set_callbacks(IntPtr mp, PlayCb play, PauseCb pause,
            ResumeCb resume, FlushCb flush, DrainCb drain, IntPtr opaque);

        [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
        private static extern int libvlc_audio_set_format(IntPtr mp, IntPtr format, uint rate, uint channels);

        // 全局保活：委托与 GCHandle 绝不能被子系统回收，否则 native 会回调到野指针
        private static readonly List<Tap> _alive = new();
        private static readonly object _lock = new();
        private static readonly PropertyInfo? NativeRefProp = typeof(MediaPlayer).GetProperty(
            "NativeReference",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private sealed class Tap
        {
            public AudioBus.Track Track = null!;
            public GCHandle Self;
            public PlayCb? Play;
            public PauseCb? Pause;
            public ResumeCb? Resume;
            public FlushCb? Flush;
            public DrainCb? Drain;

            public void OnPlay(IntPtr data, IntPtr samples, uint count, long pts)
            {
                if (count == 0 || samples == IntPtr.Zero) return;
                try { Track.PushS16(samples, (int)count); } catch { }
            }

            public void OnPause(IntPtr data, long pts) { }
            public void OnResume(IntPtr data, long pts) { }
            public void OnFlush(IntPtr data, long pts) { try { Track.Clear(); } catch { } }
            public void OnDrain(IntPtr data) { }
        }

        /// <summary>
        /// 把某个播放器的解码音频接管到指定 Track。
        /// ⚠ 必须在 Play() 之前调用；重复调用同一播放器会覆盖之前的绑定。
        /// </summary>
        public static bool Attach(MediaPlayer? player, AudioBus.Track? track)
        {
            if (player == null || track == null) return false;
            try
            {
                IntPtr native = IntPtr.Zero;
                try { native = (IntPtr)(NativeRefProp?.GetValue(player) ?? IntPtr.Zero); } catch { }
                if (native == IntPtr.Zero) return false;

                var tap = new Tap { Track = track };
                tap.Self = GCHandle.Alloc(tap);
                tap.Play = tap.OnPlay;
                tap.Pause = tap.OnPause;
                tap.Resume = tap.OnResume;
                tap.Flush = tap.OnFlush;
                tap.Drain = tap.OnDrain;

                libvlc_audio_set_callbacks(native, tap.Play, tap.Pause, tap.Resume, tap.Flush, tap.Drain,
                    GCHandle.ToIntPtr(tap.Self));

                var fmt = Marshal.StringToHGlobalAnsi("S16N");
                try
                {
                    libvlc_audio_set_format(native, fmt, (uint)AudioBus.Rate, (uint)AudioBus.Channels);
                }
                finally { Marshal.FreeHGlobal(fmt); }

                lock (_lock) _alive.Add(tap);
                Trace.Log($"'{track.Key}' 已接管 LibVLC 音频（amem 回调）");
                return true;
            }
            catch (Exception ex)
            {
                Trace.Log($"接管音频失败（{track.Key}）：{ex.Message}");
                return false;
            }
        }
    }
}
