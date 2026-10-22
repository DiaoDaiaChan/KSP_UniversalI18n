using System.Collections.Generic;

namespace UI18n.Runtime.Patches
{
    /// <summary>
    /// per-target 命中统计的注册表与输出（P2 新增；同时补上 00 §10 的「覆盖率报告」验收项）。
    /// <para>
    /// 输出格式（一行，方便 grep）：
    /// <c>[UI18n] patch stats: UnityEngine.UI.Text.set_text=1234 hit=980 | TMPro.TMP_Text.set_text=8421 hit=3100 kept-src=5321 | …</c>
    /// </para>
    /// <para>只读统计，绝不参与翻译决策；dump 由 <see cref="UI18nAddon.Update"/> 周期调用（有变化才打），
    /// 另外每个目标第一次被调用时会打一行 <c>first call: {目标}</c>，这样即使进程后来被杀也能证明该目标在本局活着。</para>
    /// <para>线程模型：只在主线程写（IMGUI / uGUI 都在主线程），dump 也在主线程，故不用锁。</para>
    /// </summary>
    internal static class PatchStats
    {
        private static readonly List<PatchCounter> Registered = new List<PatchCounter>();

        /// <summary>已注册的目标数（= 已装 patch 数，正常情况下 16）。</summary>
        internal static int Count
        {
            get { return Registered.Count; }
        }

        /// <summary>所有目标的总调用次数（UI18nAddon 用它判断「这 20 秒有没有新数据」）。</summary>
        internal static long TotalCalls
        {
            get
            {
                long total = 0;
                for (int i = 0; i < Registered.Count; i++)
                {
                    total += Registered[i].Calls;
                }

                return total;
            }
        }

        /// <summary>所有目标的总命中次数（诊断用）。</summary>
        internal static long TotalHits
        {
            get
            {
                long total = 0;
                for (int i = 0; i < Registered.Count; i++)
                {
                    total += Registered[i].Hits;
                }

                return total;
            }
        }

        /// <summary>登记一个目标并返回它的计数器（同一个对象会同时出现在 dump 里，顺序 = 调用顺序）。</summary>
        internal static PatchCounter Create(string target)
        {
            var counter = new PatchCounter(target);
            Registered.Add(counter);
            return counter;
        }

        /// <summary>清空登记表（重启一套统计时用；<see cref="Counters"/> 里的静态字段由下一次 BuildSpecs 重新赋值）。</summary>
        internal static void Reset()
        {
            Registered.Clear();
        }

        /// <summary>
        /// 生成 <c>patch stats: …</c> 一行（不含 <c>[UI18n] </c> 前缀，由 <see cref="Log"/> 加）。
        /// 无注册目标时返回空串（调用方据此跳过）。
        /// </summary>
        internal static string Dump()
        {
            if (Registered.Count == 0)
            {
                return string.Empty;
            }

            var sb = new System.Text.StringBuilder("patch stats: ");
            for (int i = 0; i < Registered.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(" | ");
                }

                sb.Append(Registered[i].Describe());
            }

            return sb.ToString();
        }
    }
}
