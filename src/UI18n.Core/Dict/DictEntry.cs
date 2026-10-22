using System;

namespace UI18n.Core.Dict
{
    /// <summary>
    /// 字典条目，字段与 JSONL 源的 6 个固定字段一一对应（h / n / s / t / mod / ctx）。
    /// <para>
    /// <c>IsKeep</c>：译文等于源文（Kerbin、MechJeb、RD-107 之类不可译项，04 §6.3 门禁 5）。
    /// 这种条目<b>视为无译文</b>：查表时算 miss（但单独计入 <c>Keeps</c>，便于和 parse error 区分开统计）。
    /// </para>
    /// </summary>
    public sealed class DictEntry
    {
        /// <summary>构造条目。字符串参数传 null 等价于空串。层级的推断规则见 <see cref="KeyLevel"/>。</summary>
        public DictEntry(ulong hash, string normalized, string source, string translation, string mod, string ctx)
        {
            Hash = hash;
            Normalized = normalized ?? string.Empty;
            Source = source ?? string.Empty;
            Translation = translation ?? string.Empty;
            Mod = mod ?? string.Empty;
            Context = ctx ?? string.Empty;

            if (Context.Length > 0)
            {
                Level = KeyLevel.Ctx;
            }
            else if (Mod.Length > 0)
            {
                Level = KeyLevel.Site;
            }
            else
            {
                Level = KeyLevel.Global;
            }

            string keepBasis = Source.Length > 0 ? Source : Normalized;
            IsKeep = keepBasis.Length > 0 && Translation.Length > 0
                     && string.Equals(Translation, keepBasis, StringComparison.Ordinal);
        }

        /// <summary>源文本哈希（FNV-1a 64，UTF-8；等于 <c>n</c> 的哈希）。</summary>
        public ulong Hash { get; private set; }

        /// <summary>归一化文本（<c>n</c>）：Normalizer 的输出。</summary>
        public string Normalized { get; private set; }

        /// <summary>源文原文（<c>s</c>）：比对冲突与过期检测用。</summary>
        public string Source { get; private set; }

        /// <summary>译文（<c>t</c>）：真正要显示的东西。</summary>
        public string Translation { get; private set; }

        /// <summary>来源 mod id（<c>mod</c>）：用于推断 site 层。</summary>
        public string Mod { get; private set; }

        /// <summary>上下文（<c>ctx</c>）：约定为 <c>{asm}|{type}|{member}|{ordinal}</c>，可选。</summary>
        public string Context { get; private set; }

        /// <summary>推断出的层级。</summary>
        public KeyLevel Level { get; private set; }

        /// <summary>译文是否等于源文（= 不可译项，查表时按 miss 处理）。</summary>
        public bool IsKeep { get; private set; }

        /// <inheritdoc />
        public override string ToString()
        {
            return Level + " " + Hash.ToString("x16") + " => " + Translation;
        }
    }
}
