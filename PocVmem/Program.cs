using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using LibVLCSharp.Shared;

namespace PocVmem
{
    /// <summary>
    /// C 方案生死线验证：LibVLC 3.x 能不能把每一路解码后的帧交到我们内存里（vmem 回调），
    /// 以及同时开 N 路 1080p 时 CPU / 内存带宽的真实代价。
    ///
    /// 只做验证，不碰产品代码。跑完自己退出。
    ///   PocVmem.exe --n=8 --sec=15 [--hw] [--file=xxx.mp4]
    /// </summary>
    internal static class Program
    {
        private static LibVLC? _vlc;
        private static readonly List<Feed> Feeds = new();
        private static int W = 1920, H = 1080;

        private sealed class Feed
        {
            public MediaPlayer Player = null!;
            public Media Media = null!;
            public IntPtr Buffer;
            public long Frames;
            // 委托必须被强引用持有：被 GC 回收后 VLC 回调进来就是野指针，进程直接崩
            public MediaPlayer.LibVLCVideoLockCb? LockCb;
            public MediaPlayer.LibVLCVideoUnlockCb? UnlockCb;
            public MediaPlayer.LibVLCVideoDisplayCb? DisplayCb;
        }

        private static int Main(string[] args)
        {
            string file = @"E:\WorkBuddy\01-视频制作\vMixLite\验证截图\抠像测试素材_绿幕.mp4";
            int n = 8, seconds = 15;
            bool hw = false;
            foreach (var a in args)
            {
                if (a.StartsWith("--file=", StringComparison.Ordinal)) file = a.Substring(7);
                else if (a.StartsWith("--n=", StringComparison.Ordinal)) n = int.Parse(a.Substring(4));
                else if (a.StartsWith("--sec=", StringComparison.Ordinal)) seconds = int.Parse(a.Substring(6));
                else if (a == "--hw") hw = true;
            }

            if (!System.IO.File.Exists(file)) { Console.WriteLine("素材不存在: " + file); return 2; }

            Core.Initialize();
            var opts = new List<string> { "--no-video-title-show", "--no-audio", "--quiet" };
            opts.Add(hw ? "--avcodec-hw=any" : "--avcodec-hw=none");
            _vlc = new LibVLC(false, opts.ToArray());

            // 先探源分辨率：vmem 的格式必须和最终输出格式精确一致，猜错会花屏或直接不出帧
            using (var probe = new Media(_vlc, file, FromType.FromPath))
            {
                probe.Parse(MediaParseOptions.ParseLocal);
                foreach (var t in probe.Tracks)
                {
                    if (t.TrackType != TrackType.Video) continue;
                    W = (int)t.Data.Video.Width;
                    H = (int)t.Data.Video.Height;
                    break;
                }
            }

            Console.WriteLine($"素材 {System.IO.Path.GetFileName(file)}  {W}x{H}");
            Console.WriteLine($"路数 {n}   解码 {(hw ? "硬解(any)" : "软解(none)")}   时长 {seconds}s");
            Console.WriteLine("--------------------------------------------------------------");

            int stride = W * 4;
            for (int i = 0; i < n; i++)
            {
                var f = new Feed();
                f.Buffer = Marshal.AllocHGlobal(stride * H);
                f.Player = new MediaPlayer(_vlc);
                f.LockCb = (opaque, planes) =>
                {
                    // planes 是 void**：把第 0 平面指到我们自己的缓冲区，VLC 就往这儿解码
                    Marshal.WriteIntPtr(planes, 0, f.Buffer);
                    return IntPtr.Zero;
                };
                f.UnlockCb = (opaque, picture, planes) => { };
                f.DisplayCb = (opaque, picture) => { f.Frames++; };

                f.Player.SetVideoFormat("RV32", (uint)W, (uint)H, (uint)stride);
                f.Player.SetVideoCallbacks(f.LockCb, f.UnlockCb, f.DisplayCb);
                Feeds.Add(f);
            }

            foreach (var f in Feeds)
            {
                f.Media = new Media(_vlc, file, FromType.FromPath);
                f.Media.AddOption(":input-repeat=999");   // 素材短，循环放，避免中途 EndReached
                if (!f.Player.Play(f.Media))
                    Console.WriteLine("  ⚠ 有一路 Play 返回失败");
            }

            var swAll = Stopwatch.StartNew();
            var proc = Process.GetCurrentProcess();
            proc.Refresh();
            var cpu0 = proc.TotalProcessorTime;
            long last = 0;
            int cores = Environment.ProcessorCount;

            for (int t = 1; t <= seconds; t++)
            {
                Thread.Sleep(1000);
                proc.Refresh();
                long total = Feeds.Sum(f => f.Frames);
                long delta = total - last;
                last = total;
                var cpu = proc.TotalProcessorTime - cpu0;
                double pct = cpu.TotalMilliseconds / Math.Max(1, swAll.ElapsedMilliseconds) / cores * 100;
                Console.WriteLine($"t={t,2}s  帧/秒 {delta,5}   累计 {total,7}   CPU {pct,5:0.0}%   路均 {delta / (double)n,5:0.0} fps   内存 {proc.WorkingSet64 / 1048576,4} MB");
            }

            proc.Refresh();
            double cpuWall = (proc.TotalProcessorTime - cpu0).TotalSeconds / swAll.Elapsed.TotalSeconds;
            long grand = Feeds.Sum(f => f.Frames);
            double fpsAll = grand / swAll.Elapsed.TotalSeconds;
            double mbps = W * (double)H * 4 * fpsAll / 1048576.0;

            Console.WriteLine("--------------------------------------------------------------");
            Console.WriteLine($"总计 {grand} 帧 / {swAll.Elapsed.TotalSeconds:0.0}s  →  合计 {fpsAll:0.0} fps，路均 {fpsAll / n:0.0} fps");
            Console.WriteLine($"CPU 占用 {cpuWall:0.00} 核（{cpuWall / cores * 100:0.0}% 整机）");
            Console.WriteLine($"vmem 出帧带宽 ≈ {mbps:0} MB/s");
            Console.WriteLine($"每路帧分布: {string.Join(", ", Feeds.Select(f => f.Frames))}");

            foreach (var f in Feeds)
            {
                try { f.Player.Stop(); } catch { }
                try { f.Media.Dispose(); } catch { }
                try { f.Player.Dispose(); } catch { }
                try { Marshal.FreeHGlobal(f.Buffer); } catch { }
            }
            try { _vlc?.Dispose(); } catch { }
            Console.WriteLine("DONE");
            return 0;
        }
    }
}
