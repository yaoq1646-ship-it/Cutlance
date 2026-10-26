using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LibVLCSharp.Shared;
using Cutlance.Models;

namespace Cutlance.Services
{
    /// <summary>
    /// 模块D：网络流输入（RTSP / SRT / RTMP / HTTP(HLS) / RIST / UDP）。
    ///
    /// ── 这一层负责什么 ──────────────────────────────────────────────
    /// 把「用户粘进来的一串 URL」翻译成：
    ///   ① 协议判定（rtsp? srt? 还是认不出来）
    ///   ② URL 结构拆解（给面板显示，让人确认自己没粘错）
    ///   ③ ffmpeg 输入参数（超时 / 承载方式 / SRT 的 latency·mode·passphrase）
    ///   ④ VLC 直连选项（选了直连链路时用）
    ///   ⑤ 拉流预检（真连一次，报出分辨率/编码/码率 —— 而不是让用户对着黑屏猜）
    ///
    /// ── 本机实测的协议可用性 ────────────────────────────────────────
    ///   ffmpeg 8.1.1：rtsp / srt / rist / rtmp(s) / http(s) / udp 齐全
    ///                 （buildconf 含 --enable-libsrt 与 --enable-librist）
    ///   VLC 3.0.21  ：liblive555(RTSP) / libaccess_srt / librist / libhttp(s) / librtp / libudp 齐全
    ///   ⚠ NDI：**两边都没有**。这不是「没配好」，是根本没编进去 ——
    ///      VLC 3.0 与标准 ffmpeg 都不含 NDI 模块。所以这里直接判定为「不支持」，
    ///      并给出可操作的解释，而不是让用户添加一个注定黑屏的输入。
    /// </summary>
    public static class NetworkStreamService
    {
        // ==================== ① 协议判定 ====================

        /// <summary>
        /// 按 URL 前缀判定协议。认不出来时返回 <see cref="NetworkProtocol.Auto"/>，
        /// 由调用方决定是提示用户手选、还是按裸地址再猜一次。
        /// </summary>
        public static NetworkProtocol DetectProtocol(string? url)
        {
            var u = Normalize(url);
            if (u.Length == 0) return NetworkProtocol.Auto;

            if (u.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) ||
                u.StartsWith("rtspu://", StringComparison.OrdinalIgnoreCase) ||
                u.StartsWith("rtsps://", StringComparison.OrdinalIgnoreCase))
                return NetworkProtocol.Rtsp;

            if (u.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase) ||
                u.StartsWith("rtmps://", StringComparison.OrdinalIgnoreCase) ||
                u.StartsWith("rtmpe://", StringComparison.OrdinalIgnoreCase))
                return NetworkProtocol.Rtmp;

            if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return NetworkProtocol.Http;

            if (u.StartsWith("srt://", StringComparison.OrdinalIgnoreCase)) return NetworkProtocol.Srt;
            if (u.StartsWith("rist://", StringComparison.OrdinalIgnoreCase)) return NetworkProtocol.Rist;

            if (u.StartsWith("udp://", StringComparison.OrdinalIgnoreCase) ||
                u.StartsWith("rtp://", StringComparison.OrdinalIgnoreCase) ||
                u.StartsWith("udp4://", StringComparison.OrdinalIgnoreCase))
                return NetworkProtocol.Udp;

            if (u.StartsWith("ndi://", StringComparison.OrdinalIgnoreCase)) return NetworkProtocol.Ndi;

            return NetworkProtocol.Auto;
        }

        /// <summary>协议 → 面板显示名。</summary>
        public static string ProtocolText(NetworkProtocol p) => p switch
        {
            NetworkProtocol.Rtsp => "RTSP",
            NetworkProtocol.Rtmp => "RTMP",
            NetworkProtocol.Http => "HTTP / HLS",
            NetworkProtocol.Srt => "SRT",
            NetworkProtocol.Rist => "RIST",
            NetworkProtocol.Udp => "UDP / RTP",
            NetworkProtocol.Ndi => "NDI",
            _ => "未识别"
        };

        /// <summary>把这个协议「能不能用」说清楚。能用返回空串。</summary>
        public static string UnsupportedReason(NetworkProtocol p) => p switch
        {
            NetworkProtocol.Ndi =>
                "NDI 当前版本不支持。原因不是配置问题：NDI 是 NewTek/Vizrt 的私有协议，"
                + "本机播放链路的两个组件（VLC 3.0 与 ffmpeg 8.1.1）都没有编入 NDI 模块。"
                + "要接 NDI 源，常见做法是在推流端把 NDI 转成 SRT（如 NDI Tools / OBS 中转），"
                + "再把那个 SRT 地址粘到这里。",
            NetworkProtocol.Auto =>
                "认不出这串地址属于哪种协议。请检查是否漏了前缀（rtsp://、srt://、rtmp://、http://）。"
                + "如果源就是「IP:端口」的裸地址，可在下方「协议」下拉里手动指定。",
            _ => ""
        };

        // ==================== ② URL 规范化与结构拆解 ====================

        /// <summary>
        /// 清洗粘进来的 URL：去掉首尾空白、成对的引号，以及换行符
        /// （从聊天软件/文档里复制时常带上这些东西，直接喂给 ffmpeg 会报「协议未知」）。
        /// </summary>
        public static string Normalize(string? url)
        {
            var s = (url ?? "").Trim();
            // 逐行取第一条 —— 粘贴框允许多行，单条解析时只认第一行
            int nl = s.IndexOfAny(new[] { '\r', '\n' });
            if (nl >= 0) s = s.Substring(0, nl).Trim();

            if (s.Length >= 2)
            {
                char a = s[0], b = s[s.Length - 1];
                if ((a == '"' && b == '"') || (a == '\'' && b == '\'') ||
                    (a == '“' && b == '”') || (a == '‘' && b == '’'))
                    s = s.Substring(1, s.Length - 2).Trim();
            }
            return s;
        }

        /// <summary>URL 结构拆解结果（纯粹给界面显示用，不参与播放）。</summary>
        public sealed class UrlInfo
        {
            public NetworkProtocol Protocol { get; set; } = NetworkProtocol.Auto;
            public string ProtocolText { get; set; } = "";
            public string Scheme { get; set; } = "";
            public string UserInfo { get; set; } = "";     // user:pass（界面只显示有/无，不显示明文）
            public bool HasCredentials => UserInfo.Length > 0;
            public string Host { get; set; } = "";
            public int Port { get; set; }                   // 0 = 用协议默认端口
            public string Path { get; set; } = "";
            public string Query { get; set; } = "";
            public bool IsMulticast { get; set; }           // udp://@239.x.x.x 这类
            public string Error { get; set; } = "";
            public bool Ok => Error.Length == 0;

            /// <summary>「主机:端口」显示串（端口为 0 时不显示端口）。</summary>
            public string HostPort => Port > 0 ? Host + ":" + Port : Host;
        }

        /// <summary>
        /// 宽松解析 URL。
        ///
        /// 为什么不用 <see cref="Uri"/>：srt:// rist:// udp://@239.255.77.1 这些都不是标准 scheme，
        /// Uri 会把 udp 的 "@" 当成 userinfo、把非标准 scheme 的 Port 丢成 -1，
        /// 拿来显示会给出错误信息 —— 比不显示更糟。所以自己按 URL 通用语法切。
        /// </summary>
        public static UrlInfo Parse(string? url)
        {
            var info = new UrlInfo();
            var u = Normalize(url);
            if (u.Length == 0) { info.Error = "地址为空"; return info; }

            int sep = u.IndexOf("://", StringComparison.Ordinal);
            if (sep <= 0)
            {
                info.Error = "缺少协议前缀（形如 rtsp://…）";
                return info;
            }

            info.Scheme = u.Substring(0, sep).ToLowerInvariant();
            var rest = u.Substring(sep + 3);
            info.Protocol = DetectProtocol(u);
            info.ProtocolText = ProtocolText(info.Protocol);

            // 切 query
            int q = rest.IndexOf('?');
            if (q >= 0)
            {
                info.Query = rest.Substring(q + 1);
                rest = rest.Substring(0, q);
            }

            // 切 path
            int slash = rest.IndexOf('/');
            string hostPart;
            if (slash >= 0)
            {
                info.Path = rest.Substring(slash + 1);
                hostPart = rest.Substring(0, slash);
            }
            else hostPart = rest;

            // userinfo@host:port
            int at = hostPart.LastIndexOf('@');
            if (at >= 0)
            {
                info.UserInfo = hostPart.Substring(0, at);
                // udp://@239.255.77.1:31000 —— 这里的 @ 是 ffmpeg 的「绑定到组播」标记，
                // 前面并没有用户名密码，别把它显示成「已带鉴权」。
                if (info.UserInfo.Length == 0) info.IsMulticast = true;
                hostPart = hostPart.Substring(at + 1);
            }

            // IPv6：[::1]:9000
            if (hostPart.StartsWith("[", StringComparison.Ordinal))
            {
                int close = hostPart.IndexOf(']');
                if (close > 0)
                {
                    info.Host = hostPart.Substring(1, close - 1);
                    var tail = hostPart.Substring(close + 1);
                    if (tail.StartsWith(":", StringComparison.Ordinal))
                        info.Port = ParsePortOrZero(tail.Substring(1));
                }
                else info.Host = hostPart;
            }
            else
            {
                // 从右往左找最后一个 ':'，其后全是数字才算端口
                int colon = hostPart.LastIndexOf(':');
                if (colon > 0 && colon < hostPart.Length - 1)
                {
                    var maybe = hostPart.Substring(colon + 1);
                    if (maybe.All(char.IsDigit))
                    {
                        info.Port = ParsePortOrZero(maybe);
                        info.Host = hostPart.Substring(0, colon);
                    }
                    else info.Host = hostPart;
                }
                else info.Host = hostPart;
            }

            if (info.Host.Length == 0) info.Error = "地址里没有主机名/IP";
            else if (info.Protocol == NetworkProtocol.Ndi) info.Error = UnsupportedReason(NetworkProtocol.Ndi);
            else if (info.Protocol == NetworkProtocol.Auto)
                info.Error = UnsupportedReason(NetworkProtocol.Auto);

            return info;
        }

        private static int ParsePortOrZero(string s)
            => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p > 0 && p <= 65535
                ? p : 0;

        // ==================== ③ ffmpeg 输入参数 ====================

        /// <summary>
        /// 生成喂给 ffmpeg 的「输入侧参数」（不含 -i 与地址本身，便于预检/播放两处共用）。
        ///
        /// 各协议的坑：
        ///   · RTSP：默认它会先试 UDP，失败的机器上要等一个超时才回落 TCP —— 所以允许强制。
        ///           `-timeout` 单位是**微秒**（写成 5000 会被当成 5 毫秒，秒断）。
        ///   · HTTP：ffmpeg 自带重连（-reconnect 系列），比进程级重连快得多，一定打开 ——
        ///           HLS 直播源掉一片分片是常事，让 ffmpeg 自己补比重起进程划算。
        ///   · SRT/RIST：参数走 URL query，由 <see cref="BuildUrl"/> 拼进去，这里不加。
        ///   · UDP：必须显式声明 -f mpegts，否则 ffmpeg 会去猜封装格式，猜错就报「找不到流」。
        /// </summary>
        public static string BuildInputArgs(InputSource src)
        {
            var sb = new StringBuilder();
            var proto = src.EffectiveProtocol;
            long us = Math.Max(500, src.NetTimeoutMs) * 1000L;      // 毫秒 → 微秒

            switch (proto)
            {
                case NetworkProtocol.Rtsp:
                    switch (src.NetRtspTransport)
                    {
                        case RtspTransport.Tcp: sb.Append("-rtsp_transport tcp "); break;
                        case RtspTransport.Udp: sb.Append("-rtsp_transport udp "); break;
                        // Auto：不加，让 ffmpeg 自己协商（它会先试 UDP 再回落 TCP）
                    }
                    sb.Append("-timeout ").Append(us).Append(' ');
                    break;

                case NetworkProtocol.Rtmp:
                    sb.Append("-rw_timeout ").Append(us).Append(' ');
                    break;

                case NetworkProtocol.Http:
                    sb.Append("-rw_timeout ").Append(us).Append(' ');
                    // 让 ffmpeg 自己扛瞬时抖动，别动不动就整个进程重来
                    sb.Append("-reconnect 1 -reconnect_streamed 1 -reconnect_on_network_error 1 ");
                    sb.Append("-reconnect_delay_max 5 ");
                    break;

                case NetworkProtocol.Udp:
                    sb.Append("-f mpegts ");
                    break;

                case NetworkProtocol.Srt:
                case NetworkProtocol.Rist:
                    // 参数全在 URL query 里（见 BuildUrl），这里给个读超时兜底
                    break;
            }
            return sb.ToString();
        }

        /// <summary>
        /// 实际交给 ffmpeg/VLC 的地址。
        ///
        /// SRT 的参数必须写进 query（ffmpeg 不接受把它当命令行选项），
        /// 而用户粘的地址本身可能已经带了参数（很多推流端会给出 ?mode=listener 之类），
        /// 所以这里做的是**合并**而不是覆盖 —— 用户写的优先。
        /// </summary>
        public static string BuildUrl(InputSource src)
        {
            var url = Normalize(src.Location);
            if (url.Length == 0) return url;

            if (src.EffectiveProtocol == NetworkProtocol.Srt)
            {
                var add = new List<KeyValuePair<string, string>>
                {
                    new("mode", src.NetSrtMode == SrtMode.Listener ? "listener" : "caller"),
                    new("latency", Math.Max(20, src.NetSrtLatency).ToString(CultureInfo.InvariantCulture)),
                    // SRT 的 timeout 也是微秒
                    new("timeout", (Math.Max(500, src.NetTimeoutMs) * 1000L).ToString(CultureInfo.InvariantCulture))
                };
                if (!string.IsNullOrWhiteSpace(src.NetSrtPassphrase))
                    add.Add(new KeyValuePair<string, string>("passphrase", src.NetSrtPassphrase.Trim()));

                url = MergeQuery(url, add);
            }
            return url;
        }

        /// <summary>把键值对合并进 URL 的 query；已存在的键保留原值（用户显式写的优先）。</summary>
        private static string MergeQuery(string url, List<KeyValuePair<string, string>> add)
        {
            int q = url.IndexOf('?');
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (q >= 0)
                foreach (var kv in url.Substring(q + 1).Split('&'))
                {
                    int eq = kv.IndexOf('=');
                    existing.Add((eq >= 0 ? kv.Substring(0, eq) : kv).Trim());
                }

            var sb = new StringBuilder(url);
            foreach (var kv in add)
            {
                if (existing.Contains(kv.Key)) continue;
                sb.Append(sb.ToString().Contains('?') ? '&' : '?');
                sb.Append(kv.Key).Append('=').Append(kv.Value);
                existing.Add(kv.Key);
            }
            return sb.ToString();
        }

        /// <summary>给 VLC 直连用的 Media 选项（选「VLC 直连」链路时套在 Media 上）。</summary>
        public static void ApplyVlcOptions(Media m, InputSource src)
        {
            switch (src.EffectiveProtocol)
            {
                // RTSP 强制 TCP 承载。⚠ VLC 的选项名是 :rtsp-tcp（不是 ffmpeg 的 -rtsp_transport tcp），
                // 两边语法完全不同，别互相照搬。
                case NetworkProtocol.Rtsp:
                    if (src.NetRtspTransport == RtspTransport.Tcp) m.AddOption(":rtsp-tcp");
                    break;

                // SRT 的参数在 URL query 里（BuildUrl 已拼好），VLC 的 srt 模块直接认，
                // 不需要额外选项 —— 这里刻意留空而不是"顺手加一个"，加了反而可能冲突。
                case NetworkProtocol.Srt:
                    break;
            }
        }

        // ==================== ④ 拉流预检 ====================

        /// <summary>预检结果（面板上那行「测试连接」显示的东西）。</summary>
        public sealed class ProbeResult
        {
            public bool Ok { get; set; }
            public string Summary { get; set; } = "";      // 成功时的一句话（分辨率/编码/码率）
            public string Error { get; set; } = "";        // 失败时的说明
            public int Width { get; set; }
            public int Height { get; set; }
            public double Fps { get; set; }
            public string VideoCodec { get; set; } = "";
            public string AudioCodec { get; set; } = "";
            public long BitRate { get; set; }
            public int ElapsedMs { get; set; }
            /// <summary>是否隔行（ffprobe 的 field_order）—— 网络流同样可能送来 1080i。</summary>
            public bool Interlaced { get; set; }
        }

        /// <summary>ffprobe 路径（与 ffmpeg 同目录）。找不到返回空串。</summary>
        public static string FfprobePath
        {
            get
            {
                var f = FfmpegOutput.FfmpegPath;
                if (string.IsNullOrWhiteSpace(f)) return "";
                var dir = Path.GetDirectoryName(f);
                if (string.IsNullOrWhiteSpace(dir)) return "";
                var p = Path.Combine(dir, "ffprobe.exe");
                return File.Exists(p) ? p : "";
            }
        }

        /// <summary>
        /// 真连一次，把「这根流里到底有什么」读出来。
        ///
        /// 为什么值得做：网络流最常见的失败是「地址对但连不上」和「连上了但没有画面」，
        /// 两者的界面表现完全一样（黑屏）。预检把差异提前暴露成一句人话，
        /// 用户不用对着黑屏猜是地址错了、源没开、还是被防火墙挡了。
        /// </summary>
        public static async Task<ProbeResult> ProbeAsync(InputSource src, CancellationToken ct = default)
        {
            var res = new ProbeResult();
            var sw = Stopwatch.StartNew();

            var ffprobe = FfprobePath;
            if (ffprobe.Length == 0)
            {
                res.Error = "找不到 ffprobe.exe（预检需要它，与 ffmpeg.exe 同目录）";
                return res;
            }

            var url = BuildUrl(src);
            if (url.Length == 0) { res.Error = "地址为空"; return res; }

            // 探测预算：别让它无限期挂着读流。3 秒/3MB 足够拿到首个关键帧与流信息。
            var args = "-hide_banner -v error -print_format json -show_streams -show_format "
                     + "-analyzeduration 3000000 -probesize 3000000 "
                     + BuildInputArgs(src)
                     + "-i \"" + url + "\"";

            var psi = new ProcessStartInfo(ffprobe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // ffmpeg 系工具往管道写 UTF-8，不指定会按 GBK 解出乱码（项目里踩过多次）
                StandardErrorEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8
            };

            try
            {
                using var p = Process.Start(psi);
                if (p == null) { res.Error = "无法启动 ffprobe"; return res; }

                var stdoutTask = p.StandardOutput.ReadToEndAsync();
                var stderrTask = p.StandardError.ReadToEndAsync();

                // 总超时 = 用户设的连接超时 + 3 秒探测预算
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(Math.Max(1000, src.NetTimeoutMs) + 3000);

                try { await p.WaitForExitAsync(cts.Token); }
                catch (OperationCanceledException)
                {
                    try { p.Kill(true); } catch { }
                    res.ElapsedMs = (int)sw.ElapsedMilliseconds;
                    res.Error = ct.IsCancellationRequested
                        ? "已取消"
                        : $"连接超时（{Math.Max(1000, src.NetTimeoutMs) + 3000} ms 内没有响应）。"
                          + "请检查：地址与端口是否正确、源是否在线、防火墙是否放行。";
                    return res;
                }

                string json = "", err = "";
                try { json = await stdoutTask; } catch { }
                try { err = await stderrTask; } catch { }

                res.ElapsedMs = (int)sw.ElapsedMilliseconds;

                if (string.IsNullOrWhiteSpace(json))
                {
                    res.Error = string.IsNullOrWhiteSpace(err)
                        ? "没有读到任何流信息（对端没有响应或地址不可达）"
                        : FirstLines(err, 3);
                    return res;
                }

                ParseProbeJson(json, res);
                if (res.Width <= 0 && res.Height <= 0 && res.VideoCodec.Length == 0)
                {
                    res.Error = "连上了，但没有解析出视频流 —— 该地址可能只是音频流，或需要鉴权 / 非直播地址。";
                    return res;
                }

                res.Ok = true;
                var parts = new List<string>();
                if (res.Width > 0) parts.Add($"{res.Width}×{res.Height}");
                if (res.Fps > 0) parts.Add($"{res.Fps:0.##} fps");
                if (res.VideoCodec.Length > 0) parts.Add(res.VideoCodec.ToUpperInvariant());
                if (res.AudioCodec.Length > 0) parts.Add("音频 " + res.AudioCodec.ToUpperInvariant());
                if (res.BitRate > 0) parts.Add($"{res.BitRate / 1_000_000.0:0.##} Mbps");
                if (res.Interlaced) parts.Add("隔行");
                parts.Add($"耗时 {res.ElapsedMs} ms");
                res.Summary = string.Join(" · ", parts);
                return res;
            }
            catch (Exception ex)
            {
                res.Error = "预检异常：" + ex.Message;
                return res;
            }
        }

        private static void ParseProbeJson(string json, ProbeResult res)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
                    foreach (var s in streams.EnumerateArray())
                    {
                        var type = GetStr(s, "codec_type");
                        if (type == "video" && res.Width <= 0)
                        {
                            res.Width = GetInt(s, "width");
                            res.Height = GetInt(s, "height");
                            res.VideoCodec = GetStr(s, "codec_name");

                            var fr = GetStr(s, "avg_frame_rate");
                            res.Fps = ParseFraction(fr);
                            if (res.Fps <= 0) res.Fps = ParseFraction(GetStr(s, "r_frame_rate"));

                            // field_order: tt/bb/tb/bt = 隔行；progressive = 逐行
                            var fo = GetStr(s, "field_order");
                            res.Interlaced = fo.StartsWith("tt", StringComparison.OrdinalIgnoreCase)
                                          || fo.StartsWith("bb", StringComparison.OrdinalIgnoreCase)
                                          || fo.StartsWith("tb", StringComparison.OrdinalIgnoreCase)
                                          || fo.StartsWith("bt", StringComparison.OrdinalIgnoreCase);
                        }
                        else if (type == "audio" && res.AudioCodec.Length == 0)
                        {
                            res.AudioCodec = GetStr(s, "codec_name");
                        }
                    }

                if (root.TryGetProperty("format", out var fmt))
                {
                    var br = GetStr(fmt, "bit_rate");
                    if (long.TryParse(br, out var b)) res.BitRate = b;
                }
            }
            catch { /* 解析失败就当作没读到，由调用方的兜底分支处理 */ }
        }

        private static string GetStr(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? "" : "";

        private static int GetInt(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
                ? v.GetInt32() : 0;

        /// <summary>把 ffprobe 的 "25/1" 这种分数字符串转成 double。</summary>
        private static double ParseFraction(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            var parts = s.Split('/');
            if (parts.Length == 2 &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && b > 0)
                return a / b;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        }

        private static string FirstLines(string s, int n)
        {
            var lines = s.Split('\n')
                         .Select(x => x.Trim())
                         .Where(x => x.Length > 0)
                         .Take(n)
                         .ToList();
            return lines.Count == 0 ? "" : string.Join(" / ", lines);
        }

        // ==================== ⑤ 输入命名 ====================

        /// <summary>
        /// 生成输入显示名。原则：一眼能看出「哪一路、什么协议、走哪条链路」。
        /// 地址太长（带一长串 query）时截断，但保留主机与端口 —— 那才是辨认一路流的依据。
        /// </summary>
        public static string BuildName(InputSource src, string linkText)
        {
            var info = Parse(src.Location);
            var where = info.Ok ? info.HostPort : Normalize(src.Location);
            if (where.Length > 42) where = where.Substring(0, 42) + "…";

            var tags = new List<string> { ProtocolText(src.EffectiveProtocol), linkText };
            if (src.NeedDeinterlace) tags.Add("去隔行");
            if (src.CamLowLatency) tags.Add("低延迟");

            var path = info.Path;
            if (path.Length > 0)
            {
                var shortPath = path.Length > 18 ? "…" + path.Substring(path.Length - 18) : path;
                where += "/" + shortPath;
            }
            return $"网络流：{where} · {string.Join(" · ", tags)}";
        }
    }
}
