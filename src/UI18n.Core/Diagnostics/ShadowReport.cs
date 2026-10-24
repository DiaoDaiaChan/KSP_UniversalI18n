using System;
using System.Collections.Generic;
using System.Globalization;
using UI18n.Core.Capture;
using UI18n.Core.Hashing;

namespace UI18n.Core.Diagnostics
{
    /// <summary>
    /// task-31 的影子计数报告：把「统一出口（<c>GUIStyle.Internal_Draw</c> / <c>Internal_Draw2</c>）看到的文本」
    /// 与「现有 13 个 IMGUI hook 看到的文本」做差集，回答「能不能把 13 个收敛成 2 个」。
    /// <para>
    /// 三个输入各司其职：
    /// <list type="bullet">
    /// <item><c>styleDraw</c>：统一出口看到的所有文本（去重计次）。</item>
    /// <item><c>translatable</c>：其中<b>字典里有译文</b>的那些（= 真的会被翻、但现有 hook 可能漏掉的）。</item>
    /// <item><c>legacy</c>：现有 13 个 hook 看到的所有文本。</item>
    /// </list>
    /// 于是 <see cref="StyleDrawOnlyDistinct"/> 是「统一出口独有」的全部串，
    /// <see cref="StyleDrawOnlyTranslatable"/> 是其中<b>真正该翻而现在没翻</b>的串 —— 收敛决策看后者，
    /// 不是看前者（前者里绝大多数是数字/标识符，本来就不翻）。
    /// </para>
    /// <para>本类只读快照，不做统计写入；构造一次算一次（每次 dump 建一个新实例即可）。</para>
    /// </summary>
    public sealed class ShadowReport
    {
        /// <summary>日志里单条文本的截断长度。</summary>
        public const int LogTextLimit = 100;

        private readonly TextSinkTracker _styleDraw;
        private readonly TextSinkTracker _translatable;
        private readonly TextSinkTracker _legacy;
        private readonly List<TextSinkEntry> _styleDrawOnly;
        private readonly List<TextSinkEntry> _styleDrawOnlyTranslatable;

        /// <summary>建一份报告（三个 tracker 都可为 null，视为空）。</summary>
        public ShadowReport(TextSinkTracker styleDraw, TextSinkTracker translatable, TextSinkTracker legacy)
            : this(styleDraw, translatable, legacy, "GUIStyle.Internal_Draw")
        {
        }

        /// <summary>同上，并指定 <paramref name="sinkName"/>（写进 JSONL 的 <c>sink</c> 字段）。</summary>
        public ShadowReport(TextSinkTracker styleDraw, TextSinkTracker translatable, TextSinkTracker legacy, string sinkName)
        {
            _styleDraw = styleDraw;
            _translatable = translatable;
            _legacy = legacy;
            SinkName = string.IsNullOrEmpty(sinkName) ? "GUIStyle.Internal_Draw" : sinkName;

            _styleDrawOnly = new List<TextSinkEntry>();
            _styleDrawOnlyTranslatable = new List<TextSinkEntry>();

            if (styleDraw == null)
            {
                return;
            }

            string[] keys = styleDraw.Keys();
            for (int i = 0; i < keys.Length; i++)
            {
                string text = keys[i];
                if (legacy != null && legacy.Contains(text))
                {
                    continue;
                }

                var entry = new TextSinkEntry(text, styleDraw.CountOf(text));
                _styleDrawOnly.Add(entry);
                if (translatable != null && translatable.Contains(text))
                {
                    _styleDrawOnlyTranslatable.Add(entry);
                }
            }

            _styleDrawOnly.Sort(CompareEntries);
            _styleDrawOnlyTranslatable.Sort(CompareEntries);
        }

        /// <summary>统一出口的调用次数（每次绘制都算，含重复）。</summary>
        public long StyleDrawCalls
        {
            get { return _styleDraw == null ? 0 : _styleDraw.Calls; }
        }

        /// <summary>统一出口看到的不同文本条数。</summary>
        public int StyleDrawDistinct
        {
            get { return _styleDraw == null ? 0 : _styleDraw.Distinct; }
        }

        /// <summary>统一出口看到、且字典里有译文的不同文本条数。</summary>
        public int TranslatableDistinct
        {
            get { return _translatable == null ? 0 : _translatable.Distinct; }
        }

        /// <summary>现有 13 个 hook 看到的不同文本条数。</summary>
        public int LegacyDistinct
        {
            get { return _legacy == null ? 0 : _legacy.Distinct; }
        }

        /// <summary>「统一出口独有」的不同文本条数（现有 13 个 hook 一条都没看到的）。</summary>
        public int StyleDrawOnlyDistinct
        {
            get { return _styleDrawOnly.Count; }
        }

        /// <summary>「统一出口独有且字典里有译文」的条数 —— 收敛决策的真正依据。</summary>
        public int StyleDrawOnlyTranslatable
        {
            get { return _styleDrawOnlyTranslatable.Count; }
        }

        /// <summary>写进 JSONL 的 sink 名。</summary>
        public string SinkName { get; private set; }

        /// <summary>统一出口独有、且字典里有译文的条目（次数降序）前 <paramref name="n"/> 条。</summary>
        public TextSinkEntry[] TopStyleDrawOnlyTranslatable(int n)
        {
            return Take(_styleDrawOnlyTranslatable, n);
        }

        /// <summary>统一出口独有的全部条目（次数降序）前 <paramref name="n"/> 条。</summary>
        public TextSinkEntry[] TopStyleDrawOnly(int n)
        {
            return Take(_styleDrawOnly, n);
        }

        /// <summary>
        /// 一行汇总，格式固定（可 grep）：
        /// <c>shadow: styleDraw calls=N distinct=M, legacy distinct=K, styleDraw-only distinct=X (translatable=Y, translatable total=T)</c>。
        /// </summary>
        public string Describe()
        {
            return "shadow: styleDraw calls=" + StyleDrawCalls.ToString(CultureInfo.InvariantCulture)
                   + " distinct=" + StyleDrawDistinct.ToString(CultureInfo.InvariantCulture)
                   + ", legacy distinct=" + LegacyDistinct.ToString(CultureInfo.InvariantCulture)
                   + ", styleDraw-only distinct=" + StyleDrawOnlyDistinct.ToString(CultureInfo.InvariantCulture)
                   + " (translatable=" + StyleDrawOnlyTranslatable.ToString(CultureInfo.InvariantCulture)
                   + ", translatable total=" + TranslatableDistinct.ToString(CultureInfo.InvariantCulture) + ")";
        }

        /// <summary>
        /// 日志行：第 1 行是 <see cref="Describe"/>，随后先列「独有且可翻」的 <paramref name="top"/> 条，
        /// 再列「独有但不可翻」的 <paramref name="top"/> 条（后者的意义是证明它们确实不需要翻）。
        /// 每条单行、文本截断到 <see cref="LogTextLimit"/> 字符、换行转义成 <c>\n</c>。
        /// </summary>
        public string[] LogLines(int top)
        {
            var lines = new List<string>();
            lines.Add(Describe());

            TextSinkEntry[] hot = TopStyleDrawOnlyTranslatable(top);
            for (int i = 0; i < hot.Length; i++)
            {
                lines.Add("shadow: styleDraw-only translatable: " + hot[i].Count.ToString(CultureInfo.InvariantCulture)
                          + "x \"" + Clip(hot[i].Text) + "\"");
            }

            TextSinkEntry[] cold = TopStyleDrawOnly(top);
            int shown = 0;
            for (int i = 0; i < cold.Length && shown < top; i++)
            {
                if (_translatable != null && _translatable.Contains(cold[i].Text))
                {
                    continue; // 已经在上面列过
                }

                lines.Add("shadow: styleDraw-only: " + cold[i].Count.ToString(CultureInfo.InvariantCulture)
                          + "x \"" + Clip(cold[i].Text) + "\"");
                shown++;
            }

            return lines.ToArray();
        }

        /// <summary>
        /// JSONL 行（供 <c>PluginData/UI18n/shadow-&lt;ts&gt;.jsonl</c>）：
        /// <c>{"h":"…","n":"…","sink":"…","count":N,"translatable":true|false}</c>，
        /// <c>h</c> 是 <see cref="Fnv1a.Hash(string)"/> 的 16 位小写 hex（与翻译包同一算法，可直接查重）。
        /// 先写可翻的，再写其余；每条一行。
        /// </summary>
        public string[] JsonLines(int top)
        {
            var lines = new List<string>();
            TextSinkEntry[] hot = TopStyleDrawOnlyTranslatable(top);
            for (int i = 0; i < hot.Length; i++)
            {
                lines.Add(ToJsonLine(hot[i], true));
            }

            TextSinkEntry[] cold = TopStyleDrawOnly(top);
            int shown = 0;
            for (int i = 0; i < cold.Length && shown < top; i++)
            {
                if (_translatable != null && _translatable.Contains(cold[i].Text))
                {
                    continue;
                }

                lines.Add(ToJsonLine(cold[i], false));
                shown++;
            }

            return lines.ToArray();
        }

        private string ToJsonLine(TextSinkEntry entry, bool translatable)
        {
            return "{\"h\":\"" + Fnv1a.Hash(entry.Text).ToString("x16", CultureInfo.InvariantCulture)
                   + "\",\"n\":\"" + CaptureJson.Escape(entry.Text)
                   + "\",\"sink\":\"" + CaptureJson.Escape(SinkName)
                   + "\",\"count\":" + entry.Count.ToString(CultureInfo.InvariantCulture)
                   + ",\"translatable\":" + (translatable ? "true" : "false") + "}";
        }

        private static TextSinkEntry[] Take(List<TextSinkEntry> source, int n)
        {
            if (n <= 0 || source.Count == 0)
            {
                return new TextSinkEntry[0];
            }

            int take = n < source.Count ? n : source.Count;
            var result = new TextSinkEntry[take];
            for (int i = 0; i < take; i++)
            {
                result[i] = source[i];
            }

            return result;
        }

        private static int CompareEntries(TextSinkEntry a, TextSinkEntry b)
        {
            if (a.Count != b.Count)
            {
                return b.Count.CompareTo(a.Count);
            }

            return string.CompareOrdinal(a.Text, b.Text);
        }

        private static string Clip(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            string oneLine = text.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
            if (oneLine.Length <= LogTextLimit)
            {
                return oneLine;
            }

            return oneLine.Substring(0, LogTextLimit) + "...";
        }
    }
}
