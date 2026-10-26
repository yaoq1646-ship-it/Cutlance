using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace SynthPoc.Engine
{
    /// <summary>
    /// P3 切换台：输入池 + 预监(PVW) / 节目(PGM) 双栏 + 硬切 + 溶解转场 + 音频跟随。
    ///
    /// 与老链路（vMixLite 的 TransitionEngine）的根本差别：
    ///   老链路切源 = 停 A 的播放器、开 B 的播放器，中间有 200~600ms 的空窗（黑屏/卡顿）；
    ///   这里所有输入**一直在解码**，切源只是改「合成时读哪一路」，
    ///   所以硬切是零黑屏的，转场是两路同时出帧按比例混合 —— 毫秒级、不重开解码器。
    ///
    /// 「音频跟随节目」：
    ///   切到哪一路就只出那一路的声音。硬切时画面瞬间跳、声音做 150ms 交叉淡化，
    ///   否则切断波形会产生「咔」的一声（现场扩声里这个很明显）。
    ///   AUTO 转场时音频淡化时长跟画面转场时长一致，听感才同步。
    /// </summary>
    internal sealed class Switcher
    {
        private readonly object _gate = new();

        private readonly List<VmemInput> _inputs = new();
        private readonly List<AudioBus.Track?> _tracks = new();
        private readonly List<string> _events = new();

        private int _program;
        private int _preview;

        // 转场（AUTO）：从 from 溶解到 to
        private bool _inTransition;
        private int _to;
        private long _tStart;
        private double _tDurTicks;
        private double _mix;

        // 音频交叉淡化（独立时钟：硬切时画面瞬时，声音仍要 150ms 过去）
        private int _afFrom = -1, _afTo = -1;
        private long _afStart;
        private double _afDurTicks;

        public long Cuts { get; private set; }
        public long Autos { get; private set; }
        public int TransitionFrames { get; private set; }

        /// <summary>节目那一路的音量（单路出声，所以默认 1.0；多路混音模式另有算法）。</summary>
        public float AudioGain = 1f;

        /// <summary>true = 只出节目那一路的声音（vMix 的「音频跟随节目」）。</summary>
        public bool FollowProgram = true;

        /// <summary>硬切时声音交叉淡化的时长（秒）。0.15 是人耳听不出断点、又不会互相串音的长度。</summary>
        public double CutAudioFadeSeconds = 0.15;

        /// <summary>
        /// P4：把「某一路该出多大声」这件事交给上层去做（产品里挂的是主程序自己的音频总线）。
        /// 参数 = (哪一路, 线性音量 0~1, 是否静音)。设了它就不再走内建的 AudioBus.Track ——
        /// 产品里已经有一套带声卡输出/推流供流的总线，切换台不该再起第二套。
        /// </summary>
        public Action<VmemInput, float, bool>? SetFeedAudio;

        public event Action<string>? Switched;

        public int Count { get { lock (_gate) return _inputs.Count; } }
        public int Program { get { lock (_gate) return _program; } }
        public int Preview { get { lock (_gate) return _preview; } }

        /// <summary>
        /// 转场目标（不在转场时等于当前节目）。
        /// 合成器要靠它把「正在淡入的那一路」画成 PgmB —— 转场期间 Program 还停在旧的那一路。
        /// </summary>
        public int To { get { lock (_gate) return _inTransition ? _to : _program; } }
        public bool InTransition { get { lock (_gate) return _inTransition; } }
        public double Mix { get { lock (_gate) return _mix; } }
        public IReadOnlyList<string> Events { get { lock (_gate) return _events.ToArray(); } }

        public void Add(VmemInput feed, AudioBus.Track? track)
        {
            lock (_gate)
            {
                _inputs.Add(feed);
                _tracks.Add(track);
            }
        }

        /// <summary>把某一路设成节目（初始化用，不走转场、不记事件）。</summary>
        public void SetProgram(int idx)
        {
            lock (_gate)
            {
                if (idx < 0 || idx >= _inputs.Count) return;
                _program = idx;
                _inTransition = false;
                _mix = 0;
                _afFrom = -1;
                _afTo = idx;
                _afStart = 0;
                _afDurTicks = 0;
            }
        }

        public void SetPreview(int idx)
        {
            lock (_gate)
            {
                if (idx >= 0 && idx < _inputs.Count) _preview = idx;
            }
        }

        /// <summary>硬切到第 idx 路（1 起编号在 UI 层，这里 0 起）。</summary>
        public void Cut(int idx)
        {
            string? msg = null;
            lock (_gate)
            {
                if (idx < 0 || idx >= _inputs.Count) return;
                if (_inTransition) { _inTransition = false; _mix = 0; }   // 转场中再切：立即落在目标上

                int from = _program;
                _program = idx;
                _preview = idx;
                Cuts++;
                StartAudioFade(from, idx, CutAudioFadeSeconds);
                msg = $"CUT  #{from + 1} → #{idx + 1}";
                _events.Add(msg);
            }
            Switched?.Invoke(msg!);
        }

        /// <summary>用溶解转场切到第 idx 路。seconds ≤ 0 时退化成硬切。</summary>
        public void Auto(int idx, double seconds)
        {
            if (seconds <= 0.001) { Cut(idx); return; }

            string? msg = null;
            lock (_gate)
            {
                if (idx < 0 || idx >= _inputs.Count) return;
                if (_inTransition) { _program = _to; _inTransition = false; _mix = 0; }

                int from = _program;
                if (from == idx) { _mix = 0; return; }   // 已经在节目上，转场没有意义

                _to = idx;
                _preview = idx;        // 转场一开始，预监就跟到目标上（操作员看到的就是"正在切过去的"）
                _tStart = Stopwatch.GetTimestamp();
                _tDurTicks = Stopwatch.Frequency * seconds;
                _inTransition = true;
                _mix = 0;
                Autos++;
                StartAudioFade(from, idx, seconds);
                msg = $"AUTO #{from + 1} → #{idx + 1}（{seconds:0.0}s 溶解）";
                _events.Add(msg);
            }
            Switched?.Invoke(msg!);
        }

        private void StartAudioFade(int from, int to, double seconds)
        {
            _afFrom = from;
            _afTo = to;
            _afStart = Stopwatch.GetTimestamp();
            _afDurTicks = Stopwatch.Frequency * Math.Max(0, seconds);
        }

        /// <summary>
        /// 每帧调用（合成线程）：推进转场进度、推进音频淡化。
        /// 必须与 Compose 同线程、按帧率节拍调，否则转场时长会跟帧率漂。
        /// </summary>
        public void Tick()
        {
            long now = Stopwatch.GetTimestamp();

            lock (_gate)
            {
                if (_inTransition)
                {
                    double p = _tDurTicks <= 0 ? 1 : (now - _tStart) / _tDurTicks;
                    if (p >= 1)
                    {
                        _program = _to;
                        _inTransition = false;
                        _mix = 0;
                    }
                    else
                    {
                        _mix = p;
                        TransitionFrames++;
                    }
                }
            }

            ApplyAudio(now);
        }

        private void ApplyAudio(long now)
        {
            List<VmemInput> feeds;
            List<AudioBus.Track?> tracks;
            int afFrom, afTo;
            double dur;
            var ext = SetFeedAudio;

            lock (_gate)
            {
                if (_tracks.Count == 0 && ext == null) return;
                feeds = new List<VmemInput>(_inputs);
                tracks = new List<AudioBus.Track?>(_tracks);
                afFrom = _afFrom;
                afTo = _afTo;
                dur = _afDurTicks;
            }

            if (!FollowProgram)
            {
                // 多路混音模式（--mix-audio）：所有路等分音量，跟上一切
                float share = Math.Max(0.02f, AudioGain / Math.Max(1, feeds.Count));
                for (int i = 0; i < feeds.Count; i++)
                {
                    if (ext != null) { ext(feeds[i], share, false); continue; }
                    var t = i < tracks.Count ? tracks[i] : null;
                    if (t == null) continue;
                    t.Volume = share;
                    t.Mute = false;
                }
                return;
            }

            double p = 1.0;
            if (dur > 0)
            {
                p = (now - _afStart) / dur;
                if (p < 0) p = 0; else if (p > 1) p = 1;
            }

            for (int i = 0; i < feeds.Count; i++)
            {
                float g = 0f;
                if (i == afTo) g = (float)(p * AudioGain);
                else if (i == afFrom && p < 1) g = (float)((1 - p) * AudioGain);

                // 归零就静音：混音线程会 Skip 掉这路的缓冲，免得切回来时放出几百毫秒前的旧声音
                bool mute = g <= 0.0008f;

                if (ext != null) { ext(feeds[i], g, mute); continue; }

                var t = i < tracks.Count ? tracks[i] : null;
                if (t == null) continue;
                t.Volume = g;
                t.Mute = mute;
            }
        }

        /// <summary>合成器要的两路 + 混合比例。</summary>
        public void GetFrame(out VmemInput? a, out VmemInput? b, out double mix)
        {
            lock (_gate)
            {
                if (_inputs.Count == 0) { a = null; b = null; mix = 0; return; }

                a = _inputs[Math.Clamp(_program, 0, _inputs.Count - 1)];
                if (_inTransition && _to >= 0 && _to < _inputs.Count)
                {
                    b = _inputs[_to];
                    mix = _mix;
                }
                else
                {
                    b = null;
                    mix = 0;
                }
            }
        }

        /// <summary>给预监缩略图用：拿预览那一路当前的帧。</summary>
        public VmemInput? PreviewFeed
        {
            get { lock (_gate) return _inputs.Count == 0 ? null : _inputs[Math.Clamp(_preview, 0, _inputs.Count - 1)]; }
        }

        public VmemInput? ProgramFeed
        {
            get { lock (_gate) return _inputs.Count == 0 ? null : _inputs[Math.Clamp(_program, 0, _inputs.Count - 1)]; }
        }

        public VmemInput? FeedAt(int i)
        {
            lock (_gate) return (i >= 0 && i < _inputs.Count) ? _inputs[i] : null;
        }
    }
}
