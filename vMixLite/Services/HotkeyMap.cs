using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows.Input;
using Cutlance.Models;

namespace Cutlance.Services
{
    /// <summary>
    /// 快捷键 / 外设导播键盘的「动作目录 + 按键映射」（全局设置，跨工程，不进 .cutlance）。
    ///
    /// ── 为什么要有它 ────────────────────────────────────────────────
    /// 以前快捷键是硬编码在 MainWindow.Window_KeyDown 里的（Ctrl+S / F5 / F11…），
    /// 一个键都改不了。而外设导播键盘（或 USB 小键盘、脚踏开关）在 Windows 里**就是标准键盘**，
    /// 它只会往外发它固化的那几个按键码 —— 想让「它发的键」对上「程序要的动作」，
    /// 就必须有一张可编辑的映射表。本类就是那张表。
    ///
    /// ── 三条约定 ──────────────────────────────────────────────────
    ///  1) 动作目录（<see cref="Actions"/>）是**静态**的，写在代码里；
    ///     配置只存「动作 ID → 按键串」，目录里没有的 ID（旧配置残留）一律忽略，绝不崩。
    ///  2) 内置默认绑定（<see cref="Defaults"/>）= 原来那套硬编码快捷键，
    ///     所以配置文件丢了 / 读坏了，操作习惯照旧。
    ///  3) 配置里出现过的动作以配置为准 —— 空串表示「用户显式清除了绑定」，
    ///     不会退回默认（否则用户永远清不掉 Ctrl+S）。
    ///
    /// 配置文件：%APPDATA%\Cutlance\hotkeys.json
    /// </summary>
    public static class HotkeyMap
    {
        // ================= 动作目录（设置窗口按 Group 分节显示） =================

        /// <summary>可以被绑定的全部动作。顺序即设置窗口里的显示顺序。</summary>
        public static readonly List<HotkeyAction> Actions = new()
        {
            // —— 导播切换（导播键盘最核心的一组）——
            new("trans.cut",    "导播切换", "CUT —— 硬切（预览立刻进输出）"),
            new("trans.auto",   "导播切换", "AUTO —— 按当前转场切（淡出 / 交叉溶解）"),
            new("trans.ftb",    "导播切换", "FTB —— 淡黑 / 恢复（切换式）"),
            new("layer.add",    "导播切换", "把预览加入输出图层", "原 Ctrl+L"),
            new("layer.clear",  "导播切换", "清空输出图层"),

            // —— 输出布局预设（P3，2026-10-22）——
            // 序号 = 布局下拉里的顺序（第 1 项 = Ctrl+Alt+1）。
            // 默认给 Ctrl+Alt+数字：带修饰键 → 打字时不会误触，且窗口不在前台也能用（导播时必备）。
            new("layout.1", "输出布局", "切到第 1 套输出布局", "对应布局下拉的第 1 项"),
            new("layout.2", "输出布局", "切到第 2 套输出布局"),
            new("layout.3", "输出布局", "切到第 3 套输出布局"),
            new("layout.4", "输出布局", "切到第 4 套输出布局"),
            new("layout.5", "输出布局", "切到第 5 套输出布局"),
            new("layout.6", "输出布局", "切到第 6 套输出布局"),
            new("layout.7", "输出布局", "切到第 7 套输出布局"),
            new("layout.8", "输出布局", "切到第 8 套输出布局"),
            new("layout.undo", "输出布局", "撤回上一次布局切换", "回到切之前那套画面"),

            // —— 输入切换 ——
            new("src.1", "输入切换", "切到输入 1", "没有第 N 路时什么都不做"),
            new("src.2", "输入切换", "切到输入 2"),
            new("src.3", "输入切换", "切到输入 3"),
            new("src.4", "输入切换", "切到输入 4"),
            new("src.5", "输入切换", "切到输入 5"),
            new("src.6", "输入切换", "切到输入 6"),
            new("src.7", "输入切换", "切到输入 7"),
            new("src.8", "输入切换", "切到输入 8"),
            new("src.next", "输入切换", "预览切到下一路输入", "按左侧列表顺序循环"),
            new("src.prev", "输入切换", "预览切到上一路输入"),

            // —— 文档翻页（老板 2026-10-19：表格 / PPT 进预览后按 PageUp / PageDown 翻页）——
            new("doc.next", "文档翻页", "下一条 / 下一页（字幕表优先，没字幕表才翻表格 / PPT）", "默认 PageDown"),
            new("doc.prev", "文档翻页", "上一条 / 上一页（字幕表优先）", "默认 PageUp"),
            new("docinput.next", "文档翻页", "下一页 —— 强制翻表格 / PPT，跳过字幕表", "默认 Ctrl+PageDown"),
            new("docinput.prev", "文档翻页", "上一页 —— 强制翻表格 / PPT，跳过字幕表", "默认 Ctrl+PageUp"),

            // —— 叠加通道（OVERLAY 1-8）——
            new("ov.1", "叠加通道", "叠加通道 1 —— 显示 / 隐藏"),
            new("ov.2", "叠加通道", "叠加通道 2 —— 显示 / 隐藏"),
            new("ov.3", "叠加通道", "叠加通道 3 —— 显示 / 隐藏"),
            new("ov.4", "叠加通道", "叠加通道 4 —— 显示 / 隐藏"),
            new("ov.5", "叠加通道", "叠加通道 5 —— 显示 / 隐藏"),
            new("ov.6", "叠加通道", "叠加通道 6 —— 显示 / 隐藏"),
            new("ov.7", "叠加通道", "叠加通道 7 —— 显示 / 隐藏"),
            new("ov.8", "叠加通道", "叠加通道 8 —— 显示 / 隐藏"),

            // —— 输出图层的显隐 ——
            new("ly.1", "图层显隐", "图层 1 —— 显示 / 隐藏"),
            new("ly.2", "图层显隐", "图层 2 —— 显示 / 隐藏"),
            new("ly.3", "图层显隐", "图层 3 —— 显示 / 隐藏"),
            new("ly.4", "图层显隐", "图层 4 —— 显示 / 隐藏"),
            new("ly.5", "图层显隐", "图层 5 —— 显示 / 隐藏"),
            new("ly.6", "图层显隐", "图层 6 —— 显示 / 隐藏"),
            new("ly.7", "图层显隐", "图层 7 —— 显示 / 隐藏"),
            new("ly.8", "图层显隐", "图层 8 —— 显示 / 隐藏"),

            // —— 输出 ——
            new("out.start", "推流 / 录制", "开始推流 / 录制", "等同「流 → 开始推流 / 录制」"),
            new("out.stop",  "推流 / 录制", "停止推流 / 录制"),

            // —— 倒计时 / 播放列表 ——
            new("cd.toggle", "倒计时 / 播放列表", "倒计时：开始 / 暂停继续", "原 F5"),
            new("cd.window", "倒计时 / 播放列表", "打开倒计时 / 触发器窗口", "原 F6"),
            new("pl.start",  "倒计时 / 播放列表", "播放列表：从第 1 项开始", "原 F7"),
            new("pl.stop",   "倒计时 / 播放列表", "播放列表：停止", "原 F8"),
            new("pl.next",   "倒计时 / 播放列表", "播放列表：下一项"),
            new("pl.prev",   "倒计时 / 播放列表", "播放列表：上一项"),

            // —— 窗口 / 设置 ——
            new("win.fullscreen", "窗口 / 设置", "全屏 / 还原", "原 F11"),
            new("view.preview",   "窗口 / 设置", "显示 / 隐藏预览监视器"),
            new("view.program",   "窗口 / 设置", "显示 / 隐藏输出监视器"),
            new("app.hotkeys",    "窗口 / 设置", "打开「导播键盘 / 快捷键」设置", "原 F9"),

            // —— 工程 / 素材 ——
            new("proj.new",    "工程 / 素材", "新建工程", "原 Ctrl+N（会弹确认）"),
            new("proj.open",   "工程 / 素材", "打开工程…", "原 Ctrl+Shift+O"),
            new("proj.save",   "工程 / 素材", "保存工程", "原 Ctrl+S"),
            new("proj.saveAs", "工程 / 素材", "工程另存为…（选目录）", "原 Ctrl+Shift+S"),
            new("in.file",     "工程 / 素材", "添加视频文件…", "原 Ctrl+O"),
            new("in.text",     "工程 / 素材", "新建文字叠加", "原 Ctrl+T"),
            new("in.image",    "工程 / 素材", "新建图片叠加…", "原 Ctrl+I"),
        };

        /// <summary>
        /// 内置默认绑定 = 改造前那套硬编码快捷键。
        /// 刻意**不给**「切输入 1-8 / CUT / AUTO / 叠加 1-8」设默认：那些键（数字、字母）
        /// 在打字时会频繁出现，默认绑上等于把用户的键盘抢走 —— 要用就自己到设置窗里绑。
        /// </summary>
        public static readonly Dictionary<string, string> Defaults = new(StringComparer.OrdinalIgnoreCase)
        {
            ["proj.new"]    = "Ctrl+N",
            ["proj.open"]   = "Ctrl+Shift+O",
            ["proj.save"]   = "Ctrl+S",
            ["proj.saveAs"] = "Ctrl+Shift+S",
            ["in.file"]     = "Ctrl+O",
            ["in.text"]     = "Ctrl+T",
            ["in.image"]    = "Ctrl+I",
            ["layer.add"]   = "Ctrl+L",
            ["layout.1"]    = "Ctrl+Alt+1",
            ["layout.2"]    = "Ctrl+Alt+2",
            ["layout.3"]    = "Ctrl+Alt+3",
            ["layout.4"]    = "Ctrl+Alt+4",
            ["layout.5"]    = "Ctrl+Alt+5",
            ["layout.6"]    = "Ctrl+Alt+6",
            ["layout.7"]    = "Ctrl+Alt+7",
            ["layout.8"]    = "Ctrl+Alt+8",
            ["layout.undo"] = "Ctrl+Alt+0",
            ["doc.next"]    = "PageDown",
            ["doc.prev"]    = "PageUp",
            ["docinput.next"] = "Ctrl+PageDown",
            ["docinput.prev"] = "Ctrl+PageUp",
            ["cd.toggle"]   = "F5",
            ["cd.window"]   = "F6",
            ["pl.start"]    = "F7",
            ["pl.stop"]     = "F8",
            ["app.hotkeys"] = "F9",
            ["win.fullscreen"] = "F11",
        };

        // ================= 运行时映射 =================

        private static readonly object _lock = new();
        private static Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, string> _rev = new(StringComparer.OrdinalIgnoreCase);   // combo → actionId
        private static bool _loaded;

        /// <summary>是否「全局生效」（程序在后台也响应外设按键）。</summary>
        public static bool GlobalEnabled { get; set; } = true;

        /// <summary>
        /// 处于「按键捕获 / 设置窗打开」状态。
        ///
        /// 全局键盘钩子必须看这个标志并**完全放行**，两个理由：
        ///  1) 设置窗自己要用键盘事件来录用户按下的键，钩子抢走就录不到；
        ///  2) 导播在设置窗里点按钮、翻列表时敲的键，不该把主界面的导播动作触发一遍。
        /// </summary>
        public static bool CaptureMode { get; set; }

        private static string Dir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cutlance");

        private static string ConfigPath => Path.Combine(Dir, "hotkeys.json");

        private static readonly JsonSerializerOptions Opts = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>懒加载（首次访问映射时读一次盘）。可以重复调用。</summary>
        public static void EnsureLoaded()
        {
            lock (_lock)
            {
                if (_loaded) return;
                _loaded = true;

                _map = new Dictionary<string, string>(Defaults, StringComparer.OrdinalIgnoreCase);
                GlobalEnabled = true;

                try
                {
                    if (File.Exists(ConfigPath))
                    {
                        var f = JsonSerializer.Deserialize<HotkeyFile>(File.ReadAllText(ConfigPath));
                        if (f != null)
                        {
                            GlobalEnabled = f.Global;
                            foreach (var b in f.Bindings ?? new List<HotkeyBinding>())
                            {
                                if (string.IsNullOrWhiteSpace(b.Action)) continue;
                                // 目录里已不存在的动作（旧版本残留）→ 忽略，别让它污染反查表
                                if (!Actions.Any(a => string.Equals(a.Id, b.Action, StringComparison.OrdinalIgnoreCase)))
                                    continue;
                                _map[b.Action] = Normalize(b.Combo ?? "");
                            }
                        }
                    }
                }
                catch
                {
                    // 配置读坏 → 用默认绑定继续跑，绝不因为一个 json 让程序起不来
                }

                RebuildReverse();
            }
        }

        private static void RebuildReverse()
        {
            var rev = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in Actions)
            {
                var c = Get(a.Id);
                if (!string.IsNullOrEmpty(c)) rev[c] = a.Id;   // 撞键时后登记的赢（设置窗会提示冲突）
            }
            _rev = rev;
        }

        /// <summary>取某动作当前的按键串（空 = 未绑定）。</summary>
        public static string Get(string actionId)
        {
            EnsureLoaded();
            return _map.TryGetValue(actionId, out var c) ? c : "";
        }

        /// <summary>设置某动作的按键串（空串 = 清除绑定）。不落盘，需自行调 <see cref="Save"/>。</summary>
        public static void Set(string actionId, string combo)
        {
            EnsureLoaded();
            lock (_lock)
            {
                _map[actionId] = combo ?? "";
                RebuildReverse();
            }
        }

        /// <summary>把所有动作恢复成内置默认。不落盘。</summary>
        public static void ResetToDefaults()
        {
            EnsureLoaded();
            lock (_lock)
            {
                _map = new Dictionary<string, string>(Defaults, StringComparer.OrdinalIgnoreCase);
                RebuildReverse();
            }
        }

        /// <summary>按键串 → 动作 ID（没绑返回 null）。这是触发路径上的热函数，走反向字典。</summary>
        public static string? ActionOfCombo(string combo)
        {
            if (string.IsNullOrEmpty(combo)) return null;
            EnsureLoaded();
            if (_rev.TryGetValue(combo, out var id)) return id;
            // 兜底：手改配置文件写成 "1" 而不是 "D1" 也能认
            var n = Normalize(combo);
            if (!string.Equals(n, combo, StringComparison.Ordinal) && _rev.TryGetValue(n, out var id2)) return id2;
            return null;
        }

        /// <summary>某个按键串已被谁占用（用于设置窗的冲突提示）。没被占返回 null。</summary>
        public static string? OwnerOfCombo(string combo, string exceptActionId)
        {
            EnsureLoaded();
            foreach (var a in Actions)
            {
                if (string.Equals(a.Id, exceptActionId, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(Get(a.Id)) &&
                    string.Equals(Get(a.Id), combo, StringComparison.OrdinalIgnoreCase)) return a.Id;
            }
            return null;
        }

        /// <summary>动作 ID → 动作名（找不到返回 ID 本身）。</summary>
        public static string TitleOf(string actionId) =>
            Actions.FirstOrDefault(a => string.Equals(a.Id, actionId, StringComparison.OrdinalIgnoreCase))?.Title ?? actionId;

        public static bool Save(out string err)
        {
            err = "";
            EnsureLoaded();
            try
            {
                Directory.CreateDirectory(Dir);
                var f = new HotkeyFile
                {
                    Global = GlobalEnabled,
                    Bindings = Actions.Select(a => new HotkeyBinding(a.Id, Get(a.Id))).ToList()
                };
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(f, Opts), new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                err = ex.Message;
                return false;
            }
        }

        // ================= 按键串工具 =================

        /// <summary>WPF 的「按键 + 修饰键」→ 组合串（本类唯一认可的字符串格式）。</summary>
        public static string Combo(Key key, ModifierKeys mods)
        {
            var k = KeyName(key);
            if (string.IsNullOrEmpty(k)) return "";

            var parts = new List<string>(4);
            if ((mods & ModifierKeys.Control) != 0) parts.Add("Ctrl");
            if ((mods & ModifierKeys.Alt) != 0) parts.Add("Alt");
            if ((mods & ModifierKeys.Shift) != 0) parts.Add("Shift");
            if ((mods & ModifierKeys.Windows) != 0) parts.Add("Win");
            parts.Add(k);
            return string.Join("+", parts);
        }

        /// <summary>WPF Key → 键名（顺带把几个「修饰键本身」挡掉，它们不能单独成绑定）。</summary>
        public static string KeyName(Key key)
        {
            if (key == Key.None || IsModifierKey(key)) return "";
            return key.ToString();
        }

        /// <summary>是不是「修饰键本身」（Ctrl / Shift / Alt / Win 的左右版）。</summary>
        public static bool IsModifierKey(Key key) => key
            is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin;

        /// <summary>组合串的显示美化：把 WPF 的键名翻成人话（D1→1、NumPad1→小键盘1、OemComma→,）。</summary>
        public static string Display(string combo)
        {
            if (string.IsNullOrEmpty(combo)) return "未绑定";
            var parts = combo.Split('+');
            parts[^1] = FriendlyKey(parts[^1]);
            return string.Join("+", parts);
        }

        /// <summary>单个键名 → 界面上的叫法。</summary>
        private static string FriendlyKey(string k)
        {
            if (string.IsNullOrEmpty(k)) return k;
            // 主键盘数字：WPF 里叫 D1..D0
            if (k.Length == 2 && (k[0] == 'D' || k[0] == 'd') && char.IsDigit(k[1])) return k[1].ToString();
            // 小键盘：NumPad1 → 小键盘 1（导播键盘/脚踏开关最常用的一类键）
            if (k.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase) && k.Length > 6)
                return "小键盘" + FriendlyKey(k.Substring(6));
            return k switch
            {
                "OemComma" => ",", "OemPeriod" => ".", "OemQuestion" => "/", "OemMinus" => "-",
                "OemPlus" => "=", "OemQuotes" => "'", "OemSemicolon" => ";", "OemPipe" => "\\",
                "OemOpenBrackets" => "[", "OemCloseBrackets" => "]", "OemTilde" => "`",
                "Space" => "空格", "Return" => "回车", "Back" => "退格", "Escape" => "Esc",
                "LeftCtrl" or "RightCtrl" => "Ctrl",
                "LeftShift" or "RightShift" => "Shift",
                "LeftAlt" or "RightAlt" => "Alt",
                "LWin" or "RWin" => "Win",
                _ => k
            };
        }

        /// <summary>
        /// 把用户可能手写的简写归一化成内部格式（配置文件是可以手改的）。
        /// 目前只处理「主键盘数字」这一条：写 <c>"1"</c> 与 <c>"D1"</c> 等价 ——
        /// 手改 json 的人几乎不可能知道 WPF 管数字 1 叫 D1。
        /// </summary>
        public static string Normalize(string combo)
        {
            if (string.IsNullOrEmpty(combo)) return combo ?? "";
            var parts = combo.Split('+');
            var last = parts[^1].Trim();
            if (last.Length == 1 && char.IsDigit(last[0])) parts[^1] = "D" + last;
            return string.Join("+", parts);
        }

        /// <summary>
        /// 这个组合串在**主窗没有焦点**（程序在后台）时，允不允许触发？
        ///
        /// 只有两类放行：
        ///   · 带修饰键的组合（Ctrl+1、Alt+CUT…）—— 别的软件里打字不可能误撞；
        ///   · 功能键 F1-F24 与**小键盘键** —— 这是导播键盘 / 脚踏开关最常用的键码，
        ///     也正是「全局生效」要服务的场景。
        /// 纯字母 / 数字 / 逗号 / 空格 这类单键**后台一律不响应**：
        /// 否则一边在 Word 里打字、导播台在后台连着切输入和 CUT，是灾难。
        /// （主窗在前台时不受此限，全部照常响应。）
        /// </summary>
        public static bool IsBackgroundSafe(string combo)
        {
            if (string.IsNullOrEmpty(combo)) return false;
            if (combo.Contains('+')) return true;                 // 带修饰键
            if (combo.StartsWith("Numpad", StringComparison.OrdinalIgnoreCase)) return true;

            if (combo.Length >= 2 && combo.Length <= 3 &&
                (combo[0] == 'F' || combo[0] == 'f') &&
                int.TryParse(combo.AsSpan(1), out var fn) && fn >= 1 && fn <= 24) return true;

            return false;
        }
    }
}
