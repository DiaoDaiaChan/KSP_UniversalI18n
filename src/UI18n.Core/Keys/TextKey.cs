using System;
using System.Globalization;
using UI18n.Core.Hashing;

namespace UI18n.Core.Keys
{
    /// <summary>
    /// 三层键的字符串格式（决策 D5，00 §4.1）。查找顺序 ctx → site → global。
    /// <list type="bullet">
    /// <item>ctx    = <c>{asm}|{type}|{member}|{ordinal}|{hash:x16}</c>　精度最高，覆盖最差（mod 升级漂移）</item>
    /// <item>site   = <c>{asm}|{hash:x16}</c>　默认层</item>
    /// <item>global = <c>{hash:x16}</c>　跨 mod 复用的通用词</item>
    /// </list>
    /// <para>
    /// hash 一律 16 位<b>小写</b> hex（<c>ToString("x16")</c>）；任何一段为 null 时按空串处理，永不抛。
    /// 同一输入恒定产出同一字符串（幂等），可用于持久化。
    /// </para>
    /// </summary>
    public static class TextKey
    {
        /// <summary>键分隔符。</summary>
        public const char Separator = '|';

        /// <summary>把哈希格式化为 16 位小写 hex（三层键共用的唯一格式化入口）。</summary>
        public static string Hex(ulong hash)
        {
            return hash.ToString("x16", CultureInfo.InvariantCulture);
        }

        /// <summary>ctx 键：<c>{asm}|{type}|{member}|{ordinal}|{hash:x16}</c>。</summary>
        public static string Ctx(string asm, string typeFullName, string member, int ordinal, ulong hash)
        {
            return string.Concat(
                asm ?? string.Empty,
                "|",
                typeFullName ?? string.Empty,
                "|",
                member ?? string.Empty,
                "|",
                ordinal.ToString(CultureInfo.InvariantCulture),
                "|",
                Hex(hash));
        }

        /// <summary>ctx 键（按原文现算哈希）。</summary>
        public static string Ctx(string asm, string typeFullName, string member, int ordinal, string text)
        {
            return Ctx(asm, typeFullName, member, ordinal, Fnv1a.Hash(text));
        }

        /// <summary>site 键：<c>{asm}|{hash:x16}</c>。</summary>
        public static string Site(string asm, ulong hash)
        {
            return string.Concat(asm ?? string.Empty, "|", Hex(hash));
        }

        /// <summary>site 键（按原文现算哈希）。</summary>
        public static string Site(string asm, string text)
        {
            return Site(asm, Fnv1a.Hash(text));
        }

        /// <summary>global 键：<c>{hash:x16}</c>。</summary>
        public static string Global(ulong hash)
        {
            return Hex(hash);
        }

        /// <summary>global 键（按原文现算哈希）。</summary>
        public static string Global(string text)
        {
            return Hex(Fnv1a.Hash(text));
        }
    }
}
