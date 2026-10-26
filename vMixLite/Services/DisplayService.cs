using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Cutlance.Models;

namespace Cutlance.Services
{
    /// <summary>
    /// 显示器枚举 / 定位（模块7：HDMI 全屏镜像输出用，零外部依赖，纯 Win32 P/Invoke）。
    /// 负责：列出所有显示器（物理像素边界）、判断某窗口当前在哪块屏、把窗口精确铺满某块屏。
    /// </summary>
    public static class DisplayService
    {
        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private const uint MONITORINFOF_PRIMARY = 1;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_SHOWWINDOW = 0x0040;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfn, IntPtr dwData);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);
        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        /// <summary>枚举当前所有显示器（顺序与系统一致，Index 从 0 起）。</summary>
        public static List<MonitorInfo> GetMonitors()
        {
            var list = new List<MonitorInfo>();
            MonitorEnumProc cb = (IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data) =>
            {
                var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>(), szDevice = "" };
                if (GetMonitorInfo(hMon, ref mi))
                {
                    list.Add(new MonitorInfo
                    {
                        Index = list.Count,
                        DeviceName = mi.szDevice,
                        Left = mi.rcMonitor.Left,
                        Top = mi.rcMonitor.Top,
                        Right = mi.rcMonitor.Right,
                        Bottom = mi.rcMonitor.Bottom,
                        IsPrimary = (mi.dwFlags & MONITORINFOF_PRIMARY) != 0
                    });
                }
                return true;
            };
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
            return list;
        }

        /// <summary>返回承载指定窗口句柄的显示器索引（找不到时回退到 0）。</summary>
        public static int GetMonitorIndexForWindow(IntPtr hwnd)
        {
            var mons = GetMonitors();
            if (mons.Count == 0 || hwnd == IntPtr.Zero) return 0;
            var hmon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>(), szDevice = "" };
            if (GetMonitorInfo(hmon, ref mi))
                foreach (var m in mons)
                    if (m.DeviceName == mi.szDevice) return m.Index;
            return 0;
        }

        /// <summary>把窗口无边框精确铺满指定显示器（物理像素，绕开 DPI 换算误差）。</summary>
        public static void MoveWindowToMonitor(IntPtr hwnd, MonitorInfo m) =>
            SetWindowPos(hwnd, IntPtr.Zero, m.Left, m.Top, m.Width, m.Height, SWP_NOZORDER | SWP_SHOWWINDOW);
    }
}
