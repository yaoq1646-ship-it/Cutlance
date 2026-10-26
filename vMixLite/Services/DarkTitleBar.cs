using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Cutlance.Services
{
    /// <summary>
    /// 让 Windows 原生标题栏跟随深色主题（DWMWA_USE_IMMERSIVE_DARK_MODE）。
    ///
    /// 为什么需要：主题层只管 WPF 内容区，标题栏是 DWM 画的系统色。
    /// Windows 处于浅色个性化时，深色应用会顶着一根刺眼的白色标题栏，割裂感很强。
    ///
    /// 调用点：App.OnStartup 里用 EventManager.RegisterClassHandler 挂在 Window 类上，
    /// 这样**所有**窗口（含派生自定义窗）都在 Loaded 时自动生效，不必逐个改 XAML。
    ///
    /// 失败情形（老系统没有 dwmapi、被组策略禁、句柄还没建好）一律静默忽略 —— 只影响观感。
    /// </summary>
    public static class DarkTitleBar
    {
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;      // Win10 20H1+
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;  // Win10 1809 ~ 1909

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public static void Apply(Window? w)
        {
            if (w == null) return;
            try
            {
                var hwnd = new WindowInteropHelper(w).Handle;
                if (hwnd == IntPtr.Zero) return;

                int on = 1;
                // 新属性号失败就退回旧属性号（两者不共存，成功的那个才写进去）
                if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref on, sizeof(int));
            }
            catch
            {
                // 老系统无 dwmapi.dll 等情况：忽略
            }
        }
    }
}
