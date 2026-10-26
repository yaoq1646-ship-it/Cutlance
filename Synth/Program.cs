using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;

namespace SynthPoc
{
    internal static class Program
    {
        // 合成节拍靠 Thread.Sleep 对齐；系统默认定时器粒度 15.6ms，不压到 1ms 就会稳定掉 5~8% 帧率
        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint TimeBeginPeriod(uint ms);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        private static extern uint TimeEndPeriod(uint ms);

        [STAThread]
        private static int Main(string[] args)
        {
            Trace.Reset();
            Trace.Log("Main 进入");
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                Trace.Log("!!! 未处理异常: " + e.ExceptionObject);

            var opt = new Options();
            foreach (var a in args)
            {
                string k = a, v = string.Empty;
                int eq = a.IndexOf('=');
                if (eq > 0) { k = a.Substring(0, eq); v = a.Substring(eq + 1); }

                switch (k.TrimStart('-').ToLowerInvariant())
                {
                    case "file": case "f": opt.File = v; break;
                    case "n": opt.Count = int.Parse(v, CultureInfo.InvariantCulture); break;
                    case "fps": opt.Fps = double.Parse(v, CultureInfo.InvariantCulture); break;
                    case "sec": opt.Seconds = double.Parse(v, CultureInfo.InvariantCulture); break;
                    case "size":
                        {
                            var p = v.Split('x', 'X');
                            if (p.Length == 2)
                            {
                                opt.Width = int.Parse(p[0], CultureInfo.InvariantCulture);
                                opt.Height = int.Parse(p[1], CultureInfo.InvariantCulture);
                            }
                            break;
                        }
                    case "dump": opt.DumpPath = v; break;
                    case "report": opt.ReportPath = v; break;
                    case "out": opt.AddOut(v); break;
                    case "out2": opt.AddOut(v); break;
                    case "bitrate": opt.BitrateKbps = int.Parse(v, CultureInfo.InvariantCulture); break;
                    case "fullscreen": opt.FullscreenMonitor = int.Parse(v, CultureInfo.InvariantCulture); break;
                    case "fs-notopmost": opt.FullscreenTopmost = false; break;
                    case "fs-rect":
                        {
                            var p = v.Split(',');
                            if (p.Length == 4)
                                opt.FsRect = new[]
                                {
                                    int.Parse(p[0], CultureInfo.InvariantCulture), int.Parse(p[1], CultureInfo.InvariantCulture),
                                    int.Parse(p[2], CultureInfo.InvariantCulture), int.Parse(p[3], CultureInfo.InvariantCulture)
                                };
                            break;
                        }
                    case "ffmpeg": opt.FfmpegPath = v; break;
                    case "minimize-after": opt.MinimizeAfter = double.Parse(v, CultureInfo.InvariantCulture); break;
                    case "freeze-ui": opt.FreezeUiAfter = double.Parse(v, CultureInfo.InvariantCulture); break;
                    case "no-audio": opt.WithAudio = false; break;
                    case "audio-volume": opt.AudioVolume = double.Parse(v, CultureInfo.InvariantCulture); break;
                    case "offscreen": opt.Offscreen = true; break;
                    case "noactivate": opt.NoActivate = true; break;
                    case "debug": opt.DebugOverlay = true; break;

                    // ── P3：预监/节目（PVW/PGM）双栏 ──────────────────────────
                    case "mode": opt.Mode = v.Trim().ToLowerInvariant(); break;
                    case "pgm": opt.StartProgram = int.Parse(v, CultureInfo.InvariantCulture) - 1; break;
                    case "pvw": opt.StartPreview = int.Parse(v, CultureInfo.InvariantCulture) - 1; break;
                    case "auto-dur": opt.AutoDuration = double.Parse(v, CultureInfo.InvariantCulture); break;
                    case "plan": opt.AddPlan(v); break;
                    case "mix-audio": opt.AudioFollowProgram = false; break;

                    // ── 录制上限（按大小 GB / 按时长 小时）────────────────────
                    case "rec-max-gb": opt.RecordLimits.MaxGigabytes = double.Parse(v, CultureInfo.InvariantCulture); break;
                    case "rec-max-hours": opt.RecordLimits.MaxHours = double.Parse(v, CultureInfo.InvariantCulture); break;
                    case "exit-on-rec-limit": opt.ExitWhenRecordStops = true; break;
                }
            }

            if (!System.IO.File.Exists(opt.File))
            {
                // 离屏跑测试时绝不能弹模态框：没人能点它，进程会一直卡在消息循环里
                if (opt.Offscreen || opt.NoActivate)
                {
                    Trace.Log("素材不存在（离屏模式不弹框）：" + opt.File);
                    return 2;
                }
                MessageBox.Show("素材不存在：\n" + opt.File, "SynthPoc");
                return 2;
            }

            Trace.Log($"参数: n={opt.Count} fps={opt.Fps} size={opt.Width}x{opt.Height} sec={opt.Seconds} off={opt.Offscreen} file={opt.File}");

            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            Trace.Log("构造窗口");
            var win = new MainWindow(opt);
            Trace.Log("Run");
            TimeBeginPeriod(1);
            try
            {
                int rc = app.Run(win);
                Trace.Log("Run 退出 rc=" + rc);
                return rc;
            }
            finally
            {
                TimeEndPeriod(1);
            }
        }
    }

    internal sealed class Options
    {
        // ⚠ 这是磁盘上的真实路径，目录仍叫 vMixLite（改名只改了软件名，没动文件夹）
        public string File = @"E:\WorkBuddy\01-视频制作\vMixLite\vMixLite\测试素材\竖屏_信箱.mp4";
        public int Count = 8;
        /// <summary>输出帧率。25 / 29.97 / 30 / 50 / 59.94 / 60 都要支持（29.97 是 NTSC，不能取整）。</summary>
        public double Fps = 25;
        public int Width = 1920;
        public int Height = 1080;
        public double Seconds = 0;          // 0 = 手动退出
        public double MinimizeAfter = -1;   // >0 = 到点后最小化，验证「窗口没了画面照出」

        /// <summary>验证用：到点后在 UI 线程上硬睡几秒，模拟界面卡死，检验其它输出是否照常。</summary>
        public double FreezeUiAfter = -1;

        /// <summary>P2.5：是否把各路声音混进输出（关闭 = 纯视频，用于对比测试）。</summary>
        public bool WithAudio = true;

        /// <summary>混音后总音量（1.0 = 满）。多路同时出声时用来防削波。</summary>
        public double AudioVolume = 1.0;
        public bool Offscreen;
        public bool NoActivate;
        public bool DebugOverlay;
        public string? DumpPath;
        public string? ReportPath;

        /// <summary>P2：输出通路。本地路径 = 录制 mp4；rtmp:// srt:// udp:// = 推流。可加多条（边录边推）。</summary>
        public readonly List<string> OutTargets = new();
        public int BitrateKbps = 6000;

        /// <summary>全屏输出到第几台显示器（0 开始）。-1 = 不开。</summary>
        public int FullscreenMonitor = -1;
        public bool FullscreenTopmost = true;

        /// <summary>全屏窗定位覆盖（x,y,w,h）。自动化验证时放到屏幕外，避免盖住老板的桌面。</summary>
        public int[]? FsRect;

        public string FfmpegPath = @"C:\FFMPEG\bin\ffmpeg.exe";

        // ── P3：PVW/PGM 双栏 ────────────────────────────────────────────────
        /// <summary>grid = 多画面拼接（P1/P2 回归用）；pgm = 预监/节目双栏（P3）。</summary>
        public string Mode = "grid";

        public int StartProgram = 1;
        public int StartPreview = 2;

        /// <summary>AUTO 转场时长（秒）。硬切 CUT 固定 0（画面瞬时，声音 150ms 交叉淡化防爆音）。</summary>
        public double AutoDuration = 1.0;

        /// <summary>true = 只出节目那一路的声音（P3 的默认口径，等同 vMix 的「音频跟随节目」）。</summary>
        public bool AudioFollowProgram = true;

        public readonly List<SwitchCue> Plan = new();

        public bool IsPgmMode => string.Equals(Mode, "pgm", StringComparison.OrdinalIgnoreCase);

        /// <summary>切换计划：at:action:channel，多句用 ; 或 , 分隔。例 "3:cut:2;7:auto:5"。at 单位秒。</summary>
        public void AddPlan(string text)
        {
            foreach (var part in text.Split(new[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var p = part.Split(':');
                if (p.Length < 3) continue;
                if (!double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double at)) continue;
                string act = p[1].Trim().ToLowerInvariant();
                if (!int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ch)) continue;
                Plan.Add(new SwitchCue { At = at, Action = act, Channel = ch - 1 });
            }
            Plan.Sort((a, b) => a.At.CompareTo(b.At));
        }

        public sealed class SwitchCue
        {
            public double At;
            public string Action = "cut";   // cut | auto
            public int Channel;             // 0 开始
            public bool Done;
        }

        // ── 录制上限 ────────────────────────────────────────────────────────
        public readonly SynthPoc.Output.RecordLimits RecordLimits = new();

        /// <summary>验证用：所有录制到上限停完就退出（免得测试脚本干等 --sec）。</summary>
        public bool ExitWhenRecordStops;

        public void AddOut(string target)
        {
            foreach (var t in target.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string s = t.Trim();
                if (s.Length > 0 && !OutTargets.Contains(s)) OutTargets.Add(s);
            }
        }
    }
}
