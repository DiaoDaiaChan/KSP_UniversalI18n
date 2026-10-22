namespace UI18n.Runtime.Patches
{
    /// <summary>
    /// 单个 patch 目标的计数器（P2 新增）。三个数分别回答三个问题：
    /// <list type="bullet">
    /// <item><c>Calls</c> —— 这个目标本局被调用多少次（= 这个 hook 目标在真实玩局里有多热）。</item>
    /// <item><c>Hits</c> —— 其中有多少次真的把文本换成了译文（命中字典且成功应用）。</item>
    /// <item><c>KeptSource</c> —— 命中了但因为字体显示不了而<b>保留原文</b>的次数（TMP 可判定降级 / uGUI 无 CJK 字体时才会计数）。</item>
    /// </list>
    /// <para>刻意是 class + 可变字段：前缀方法里每次调用只做一次字段自增（约 1~2 ns），不做字典查找
    /// （05 §4 的未命中预算 17.1 ns 不允许在这里加哈希表查询）。实例由 <see cref="PatchStats.Create"/> 创建、
    /// 和 <see cref="Counters"/> 里的静态字段是同一个对象，所以 dump 顺序 = 注册表顺序，0 命中的目标也会出现。</para>
    /// </summary>
    internal sealed class PatchCounter
    {
        /// <summary>日志里显示的目标名（短名，例如 <c>GUI.Label</c> / <c>UnityEngine.UI.Text.set_text</c>）。</summary>
        internal readonly string Target;

        /// <summary>前缀被调用次数（含语言/字典短路之前的调用：这是目标方法本身的热度）。</summary>
        internal long Calls;

        /// <summary>真正把文本换成译文的次数。</summary>
        internal long Hits;

        /// <summary>命中译文但因字体无法显示而保留原文的次数。</summary>
        internal long KeptSource;

        internal PatchCounter(string target)
        {
            Target = target;
        }

        /// <summary>一行统计片段：<c>GUI.Label=1234 hit=980</c>；<c>kept-src</c> 只在非零时出现。</summary>
        internal string Describe()
        {
            string s = Target + "=" + Calls + " hit=" + Hits;
            if (KeptSource > 0)
            {
                s += " kept-src=" + KeptSource;
            }

            return s;
        }
    }
}
