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
        Global = 3,

        /// <summary>
        /// 骨架层（v1.3 pattern 通道）：<b>只由</b> <see cref="DictPack.TryGetPattern"/> 产出，
        /// 精确查表永远不会返回它。<b>不参与桶内排序</b> —— 骨架条目走独立索引，
        /// 这个值只用于命中层级统计（<c>hit sample: … (pattern)</c>）与日志。
        /// </summary>
        Pattern = 4
    }
}
