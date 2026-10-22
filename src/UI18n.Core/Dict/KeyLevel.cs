namespace UI18n.Core.Dict
{
    /// <summary>
    /// 键的层级（00 §4.1）。字典条目<b>不含</b>显式的层级字段，层级由结构推断：
    /// 有 <c>ctx</c> 字段 → <see cref="Ctx"/>；否则有 <c>mod</c> 字段 → <see cref="Site"/>；都没有 → <see cref="Global"/>。
    /// 数值越小优先级越高，供查找时排序用。
    /// </summary>
    public enum KeyLevel
    {
        /// <summary>没命中。</summary>
        Miss = 0,

        /// <summary>ctx 层（有上下文，最精确）。</summary>
        Ctx = 1,

        /// <summary>site 层（按程序集限定）。</summary>
        Site = 2,

        /// <summary>global 层（跨 mod 复用）。</summary>
        Global = 3
    }
}
