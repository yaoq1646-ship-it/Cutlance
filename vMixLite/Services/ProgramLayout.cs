using System;
using System.Collections.Generic;
using System.Linq;
using Cutlance.Models;

namespace Cutlance.Services
{
    /// <summary>
    /// 输出框「槽位表」的切换规划（2026-10-22 重写）。
    ///
    /// ── 为什么要有这个类 ──
    /// 老板报的毛病：「在全部上输出后，再进行切换其他画面，输出框画面都会出错」。
    /// 老实现把「输出框」当成一个只增不减的图层栈：切换就是往栈上再 push 一层，
    /// 旧画面从不下线，只靠"新画面是满框、刚好盖住"碰巧不出问题 —— 一旦切成一组多画面，
    /// 旧画面就从缝里露出来（复现脚本 vmtest/verify_zorder.py 有实测数据）。
    ///
    /// ── 新模型 ──
    /// 输出框 = 一张**槽位表**：每个 Layer 是一个槽位（几何 + 绑定的输入 + Z）。
    ///   · 一次「整组上输出」= 一套布局 = 一个 GroupId
    ///   · **整组切**：新一套上线时旧一套整组下线；槽位**复用**，层数收敛，不再无限累积
    ///   · **单格切**：只改某一格绑定的输入，几何/层级/组归属完全不动
    ///
    /// 这个类只做**规划**（纯数据、可单测），不碰 UI、不碰合成器；
    /// 真正的增删改由 MainWindow 执行（要改 WPF 元素树，必须回主线程）。
    /// </summary>
    public static class ProgramLayout
    {
        /// <summary>输出图层口径里「铺满画布」的缩放值（图层宽 = 画布宽 × 0.5 × Scale）。</summary>
        public const double FullLayerScale = 2.0;

        /// <summary>预览槽口径（1 = 铺满）→ 输出图层口径（2 = 铺满）。</summary>
        public static double ToLayerScale(double slotScale) => slotScale * FullLayerScale;

        /// <summary>规划一次整组切换：算清楚复用哪几格、新建几格、删掉哪几格。</summary>
        public static GroupSwitchPlan PlanGroupSwitch(
            IReadOnlyList<Layer> current, IReadOnlyList<PreviewSlot> slots, int maxLayers, int newGroupId)
        {
            var plan = new GroupSwitchPlan { GroupId = newGroupId };

            var use = slots.Where(s => s != null && s.Source != null)
                           .Take(Math.Max(0, maxLayers))
                           .ToList();

            for (int i = 0; i < use.Count; i++)
            {
                plan.Cells.Add(new GroupCell
                {
                    // 现有槽位够就复用（改绑输入，不新建图层）；不够才新建
                    ReuseIndex = i < current.Count ? i : -1,
                    Slot = use[i],
                    Z = i + 1
                });
            }

            // 现有槽位比新布局多出来的，全部删掉（否则层数只增不减，迟早顶满上限）
            for (int i = use.Count; i < current.Count; i++)
                plan.DropIndexes.Add(i);

            return plan;
        }

        /// <summary>
        /// 规划一次单格切：只换某一格的内容。
        /// 返回 null 表示「不用动」——同源、或目标格不存在。
        /// </summary>
        public static Layer? PlanCellSwap(IReadOnlyList<Layer> current, int cellIndex, InputSource? src)
        {
            if (src == null) return null;
            if (cellIndex < 0 || cellIndex >= current.Count) return null;
            var target = current[cellIndex];
            if (ReferenceEquals(target.Source, src)) return null;
            return target;
        }
    }

    /// <summary>一次整组切换的施工计划（纯数据）。</summary>
    public sealed class GroupSwitchPlan
    {
        /// <summary>这一套布局的组号。</summary>
        public int GroupId { get; set; }

        /// <summary>目标槽位（顺序即 Z 序，从 1 开始）。</summary>
        public List<GroupCell> Cells { get; } = new();

        /// <summary>要从现有列表里删掉的下标（<see cref="Cells"/> 之外的尾巴）。</summary>
        public List<int> DropIndexes { get; } = new();
    }

    /// <summary>计划里的一个目标槽位。</summary>
    public sealed class GroupCell
    {
        /// <summary>复用现有图层的下标；-1 = 需要新建。</summary>
        public int ReuseIndex { get; set; } = -1;

        public PreviewSlot Slot { get; set; } = null!;

        /// <summary>该槽位的层级（1 = 最底，越大越靠上）。</summary>
        public int Z { get; set; }
    }
}
