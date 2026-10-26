using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cutlance.Models;

namespace Cutlance.Services
{
    /// <summary>一次热插拔事件的结论：多了哪些设备、少了哪些设备。</summary>
    public sealed class DeviceChangeInfo
    {
        public List<string> Added { get; set; } = new();
        public List<string> Removed { get; set; } = new();
        public List<CaptureDevice> Devices { get; set; } = new();

        public bool HasChange => Added.Count > 0 || Removed.Count > 0;
    }

    /// <summary>
    /// 模块E B-3：采集设备热插拔监听。
    ///
    /// ── 为什么用 WM_DEVICECHANGE 而不是定时轮询 ──────────────────────────
    /// 轮询要周期性地跑一遍 `ffmpeg -list_devices`（每次一个进程、约 1 秒），
    /// 几分钟一次地空转很浪费。WM_DEVICECHANGE 是系统主动投递的消息，
    /// 真正的「插上了/拔掉了」才会来，零空闲开销、且是实时的。
    ///
    /// ── 为什么要延迟 1.5 秒再枚举 ────────────────────────────────────────
    /// 设备被系统认到（WM_DEVICECHANGE 到达）与它在 DirectShow 里可枚举，
    /// 中间有驱动初始化的时间差。立刻去枚举会扑空，所以等一下再问。
    ///
    /// ── 为什么要快照比对 ──────────────────────────────────────────────────
    /// 系统消息只说「有设备变了」，不说变了哪个（DBT_DEVNODES_CHANGED 尤其如此，
    /// 键鼠、显示器、U 盘插拔都会触发）。所以每次重新枚举一遍，与上次快照比对，
    /// 才能得出「采集设备多了 GATHER CAM3」这种对导播有用的结论。
    /// </summary>
    public static class DeviceWatcher
    {
        public const int WM_DEVICECHANGE = 0x0219;
        public const int DBT_DEVICEARRIVAL = 0x8000;
        public const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
        public const int DBT_DEVNODES_CHANGED = 0x0007;

        /// <summary>采集设备增减事件（已在后台线程，订阅方需自行切回 UI 线程）。</summary>
        public static event Action<DeviceChangeInfo>? Changed;

        private static readonly object _lock = new();
        private static List<string> _last = new();
        private static DateTime _lastAt = DateTime.MinValue;

        /// <summary>
        /// 建立初始快照。程序启动时调一次，这样第一次插拔才不会把现有设备全报成"新增"。
        /// </summary>
        public static void Seed(IEnumerable<string>? names)
        {
            lock (_lock) _last = names?.ToList() ?? new List<string>();
        }

        /// <summary>
        /// 由主窗口的窗口消息钩子调用（只认设备到达/移除两类）。
        /// 带 1.2 秒防抖：一次物理插入常常连来好几条消息。
        /// </summary>
        public static void OnDeviceChange(int wParam)
        {
            if (wParam != DBT_DEVICEARRIVAL && wParam != DBT_DEVICEREMOVECOMPLETE) return;

            lock (_lock)
            {
                if ((DateTime.Now - _lastAt).TotalSeconds < 1.2) return;
                _lastAt = DateTime.Now;
            }

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(1500);      // 等设备在 DirectShow 里注册完

                    var en = await CaptureDeviceService.EnumerateDevicesAsync();
                    var devices = en.Devices;
                    var names = devices.Where(d => d.IsVideo).Select(d => d.Name).ToList();

                    List<string> added, removed;
                    lock (_lock)
                    {
                        added = names.Where(n => !_last.Contains(n)).ToList();
                        removed = _last.Where(n => !names.Contains(n)).ToList();
                        _last = names;
                    }

                    if (added.Count == 0 && removed.Count == 0) return;

                    Changed?.Invoke(new DeviceChangeInfo
                    {
                        Added = added,
                        Removed = removed,
                        Devices = devices
                    });
                }
                catch { }
            });
        }
    }
}
