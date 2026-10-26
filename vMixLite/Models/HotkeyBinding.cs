using System.Collections.Generic;

namespace Cutlance.Models
{
    /// <summary>
    /// 动作目录里的一项 —— 可以被绑一个按键的「导播动作」。
    /// 目录是**静态**的（写在 Services/HotkeyMap.cs 里），这里只是它的数据形状。
    /// </summary>
    public sealed class HotkeyAction
    {
        public HotkeyAction() { }

        public HotkeyAction(string id, string group, string title, string hint = "")
        {
            Id = id; Group = group; Title = title; Hint = hint;
        }

        /// <summary>动作 ID（唯一、稳定，写进配置；改 ID 等于让用户已存的绑定失效，别轻易改）。</summary>
        public string Id { get; set; } = "";

        /// <summary>分组名（设置窗口里按它分节显示）。</summary>
        public string Group { get; set; } = "";

        /// <summary>动作名（显示给用户，如「CUT（硬切）」）。</summary>
        public string Title { get; set; } = "";

        /// <summary>一句话说明（可空）。</summary>
        public string Hint { get; set; } = "";
    }

    /// <summary>
    /// 一条按键绑定：某个动作 → 某个按键组合串。
    ///
    /// 组合串格式（由 HotkeyMap.Combo 统一生成，比较时按字符串全等）：
    ///   · 无修饰键 → <c>"F5"</c> / <c>"Numpad1"</c> / <c>"S"</c> / <c>"OemComma"</c>
    ///   · 有修饰键 → <c>"Ctrl+Shift+S"</c>（固定顺序 Ctrl → Alt → Shift → Win）
    /// 键名取 WPF <c>Key</c> 枚举名（钩子路径用 KeyInterop.KeyFromVirtualKey 换算过来，两边同一套名字）。
    /// </summary>
    public sealed class HotkeyBinding
    {
        public HotkeyBinding() { }

        public HotkeyBinding(string action, string combo)
        {
            Action = action; Combo = combo;
        }

        /// <summary>动作 ID（对应 HotkeyAction.Id）。</summary>
        public string Action { get; set; } = "";

        /// <summary>按键组合串；空串 = 该动作**已显式清除绑定**（不是"用默认"）。</summary>
        public string Combo { get; set; } = "";
    }

    /// <summary>hotkeys.json 的落盘结构。</summary>
    public sealed class HotkeyFile
    {
        /// <summary>
        /// 是否「全局生效」：程序在后台（主窗没有焦点）时也响应外设按键。
        /// 关掉它就退回"只有主窗前台时才响应"。
        /// </summary>
        public bool Global { get; set; } = true;

        public List<HotkeyBinding> Bindings { get; set; } = new();
    }
}
