using System;
using System.IO;
using SynthPoc.Output;
using ProdAudio = Cutlance.Services.Audio;   // 混音总线（与 MonitorBridge / SynthWindow 同一个别名写法）

namespace Cutlance.Services
{
    /// <summary>一次录制会话的参数（2026-10-25 两套录制合并）。</summary>
    public sealed class RecordSessionOptions
    {
        public int Width = 1920;
        public int Height = 1080;
        public double Fps = 30;
        public int BitrateKbps = 6000;

        /// <summary>每段时长（小时）；0 = 不分段。</summary>
        public double SegmentHours;

        /// <summary>存放目录。空 = 「我的视频\Cutlance」。</summary>
        public string Dir = "";

        /// <summary>文件名前缀，后面自动补 _yyyyMMdd_HHmmss。</summary>
        public string Prefix = "record";

        /// <summary>容器扩展名（含点）。空 = .mkv（分段最稳）。</summary>
        public string Extension = ".mkv";

        /// <summary>是否把这台机器的混音总线接进来（AudioBus 的 TCP 回环）。</summary>
        public bool WithAudio = true;
    }

    /// <summary>
    /// 一次录制会话（2026-10-25 把两套录制合并成这一份）。
    ///
    /// ── 为什么要有这个类 ──
    ///   原来「产品输出」和「POC 切换台」各写一份"起 ffmpeg + 开音频管道 + 挂看门狗 + 停止清理"，
    ///   默认值还是两套（分段时长、目录、码率各记各的）—— 同一台机器两处录出来不一样，
    ///   这正是 10-20 审计里的 B3。现在只留这一份，两边都调它。
    ///
    /// ── 为什么产品那条「推流 + 录制」不直接用它 ──
    ///   产品那条是一个 ffmpeg 进程同时推流和录制（`FfmpegOutput` 单例），
    ///   推流必须和录制共用一个编码器、`-c:v` 要各写一遍（铁律 54），拆开就得跑两个进程、
    ///   编码两次。所以「推流+录制」仍走原路，这里只管**纯录制**；
    ///   参数（目录 / 分段 / 码率 / 容器）从 StreamService 取，口径是一份。
    ///
    /// ── 停止顺序不能乱 ──
    ///   编码器 → 看门狗 → 总线 → 音频管道。
    ///   看门狗要在总线 Dispose **之后**停（它要读最后一段做段尾清理），
    ///   但要在编码器停之后（编码器还在写就别去量文件）。
    /// </summary>
    public sealed class RecordSession : IDisposable
    {
        private OutputBus? _bus;
        private FfmpegEncoder? _enc;
        private RecordWatchdog? _watch;
        private bool _audioPipe;

        /// <summary>实际写到哪个文件（分段时这是第一段的模板路径）。</summary>
        public string Target { get; private set; } = "";

        /// <summary>实际存放目录。</summary>
        public string Dir { get; private set; } = "";

        /// <summary>ffmpeg 命令行（排障用，可写进日志）。</summary>
        public string ArgsText => _enc?.ArgsText ?? "";

        /// <summary>当前是否在录（Stop 之后变 false）。</summary>
        public bool IsActive => _enc != null;

        /// <summary>
        /// 开录。失败返回 null 并给 error（调用方负责提示，别在这儿弹框 ——
        /// 导播过程中弹模态框等于挡住播出画面）。
        /// </summary>
        public static RecordSession? Start(RecordSessionOptions o, out string? error)
        {
            error = null;
            var s = new RecordSession();
            try
            {
                if (!FfmpegOutput.IsAvailable)
                {
                    error = "找不到 ffmpeg.exe（安装目录或程序目录里都没有）";
                    return null;
                }

                string dir = string.IsNullOrWhiteSpace(o.Dir)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Cutlance")
                    : o.Dir;
                Directory.CreateDirectory(dir);
                s.Dir = dir;

                string ext = string.IsNullOrWhiteSpace(o.Extension) ? ".mkv" : o.Extension;
                if (!ext.StartsWith(".")) ext = "." + ext;
                s.Target = Path.Combine(dir, $"{o.Prefix}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}");

                // 分段：只按时间切（ffmpeg 8.x 没有 segment_size），由 RecordLimits 换算
                var limits = new RecordLimits { MaxHours = Math.Max(0, o.SegmentHours) };
                double segSec = limits.SegmentSecondsFor(o.BitrateKbps, 128);

                // 音频：产品混音总线先开 TCP 监听，ffmpeg 以客户端连进来取混好的 PCM。
                // 顺序不能反 —— ffmpeg 起不来会连不上，接口就一直是断的。
                string? audioUrl = null;
                if (o.WithAudio)
                {
                    var aud = ProdAudio.AudioBus.Instance;
                    aud.EnsureStarted();
                    aud.StartPipe();
                    audioUrl = string.IsNullOrEmpty(aud.InputUrl) ? null : aud.InputUrl;
                    s._audioPipe = true;
                }

                s._bus = new OutputBus();
                s._enc = s._bus.Add(FfmpegOutput.FfmpegPath, o.Width, o.Height, o.Fps, s.Target,
                                    o.BitrateKbps, 0.30, audioUrl, segSec);
                s._enc.Fatal += msg => ErrorLog.Info($"[录制] ffmpeg 报错：{msg}");

                s._watch = new RecordWatchdog(s._bus, limits, o.Fps);
                s._watch.Start();

                ErrorLog.Info($"[录制] 开始 {s.Target}  每段 {DescribeSeg(o.SegmentHours)}  " +
                              $"帧率 {o.Fps:0.##}  音频 {(audioUrl != null ? "开" : "关")}");
                ErrorLog.Info("[录制] ffmpeg 参数：" + s._enc.ArgsText);
                return s;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                try { s.Stop(); } catch { }
                ErrorLog.Info("[录制] 启动失败：" + ex);
                return null;
            }
        }

        /// <summary>把一帧推给编码器（合成线程直接调，内部已吞异常：不能因为录制挂了拖停合成）。</summary>
        public void Push(IntPtr ptr, int stride)
        {
            if (_bus == null || ptr == IntPtr.Zero) return;
            try { _bus.Push(ptr, stride); } catch { }
        }

        /// <summary>停止并收尾。可重复调用。</summary>
        public void Stop()
        {
            // 先把 ffmpeg 命令行留下的错误信息捞出来（关掉之后就读不到了）
            if (_enc != null)
            {
                try
                {
                    string st = _enc.Stderr;
                    if (!string.IsNullOrWhiteSpace(st))
                        ErrorLog.Info("[录制] ffmpeg 输出：\n" + st.Substring(0, Math.Min(1500, st.Length)));
                }
                catch { }
                try { _enc.Dispose(); } catch { }
                _enc = null;
            }

            if (_watch != null) { try { _watch.Dispose(); } catch { } _watch = null; }
            if (_bus != null) { try { _bus.Dispose(); } catch { } _bus = null; }

            if (_audioPipe)
            {
                try { ProdAudio.AudioBus.Instance.StopPipe(); } catch { }
                _audioPipe = false;
            }
        }

        public void Dispose() => Stop();

        private static string DescribeSeg(double hours)
            => hours <= 0 ? "不分段" : $"每段 {hours:0.##} 小时";
    }
}
