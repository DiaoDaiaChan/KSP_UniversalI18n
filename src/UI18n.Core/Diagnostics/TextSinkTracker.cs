using System;
using System.Collections.Generic;

namespace UI18n.Core.Diagnostics
{
    /// <summary>
    /// 一个「文本出口」（sink）看到过的文本集合：按文本去重并计次，带上限与溢出计数。
    /// <para>
    /// 用途是 task-31 的 IMGUI 收敛实验：分别统计「现有 13 个 hook 看到的文本」与
    /// 「<c>GUIStyle.Internal_Draw</c> 这个统一出口看到的文本」，再求差集，用数据回答
    /// 「能不能把 13 个 hook 收敛成 2 个」。本类只做统计，<b>不参与翻译决策</b>。
    /// </para>
    /// <para>
    /// 键是<b>归一化后</b>的文本（<see cref="UI18n.Core.Text.TextNormalizer.Normalize"/> 的产物），
    /// 与字典的哈希口径一致：同一条目在两种空白写法下不会被算成两条。
    /// </para>
    /// <para>
    /// 上限 <see cref="MaxDistinct"/> 只限制<b>不同文本</b>的条数；触顶后新文本计入 <see cref="Overflow"/>、
    /// 已有文本照常 <c>+1</c>。这样即使某个 sink 看到海量文本（例如把所有数字串都画出来），内存也不会失控。
    /// </para>
    /// <para>线程模型：非线程安全，只在 Unity 主线程使用（与 Translator 一致）。</para>
    /// </summary>
    public sealed class TextSinkTracker
    {
        /// <summary>默认的不同文本上限。</summary>
        public const int DefaultMaxDistinct = 20000;

        private readonly Dictionary<string, int> _counts;
        private readonly int _maxDistinct;

        /// <summary>用默认上限（<see cref="DefaultMaxDistinct"/>）建一个空 tracker。</summary>
        public TextSinkTracker()
            : this(DefaultMaxDistinct)
        {
        }

        /// <summary>指定不同文本上限；小于 0 按 0 处理（等于什么都不记，只累计 <see cref="Calls"/>）。</summary>
        public TextSinkTracker(int maxDistinct)
        {
            _maxDistinct = maxDistinct < 0 ? 0 : maxDistinct;
            _counts = new Dictionary<string, int>(StringComparer.Ordinal);
        }

        /// <summary>不同文本上限。</summary>
        public int MaxDistinct
        {
            get { return _maxDistinct; }
        }

        /// <summary><see cref="Note"/> 被调用的总次数（= 该 sink 看到文本的次数，含重复）。</summary>
        public long Calls { get; private set; }

        /// <summary>不同文本条数（受 <see cref="MaxDistinct"/> 限制）。</summary>
        public int Distinct
        {
            get { return _counts.Count; }
        }

        /// <summary>因为触顶而没能记下来的不同文本条数。</summary>
        public long Overflow { get; private set; }

        /// <summary>是否曾经触顶。</summary>
        public bool Capped
        {
            get { return Overflow > 0; }
        }

        /// <summary>清空统计（保留上限设置）。</summary>
        public void Reset()
        {
            _counts.Clear();
            Calls = 0;
            Overflow = 0;
        }

        /// <summary>
        /// 记一次「看到 <paramref name="text"/>」。返回 true 表示这是一条<b>新</b>文本。
        /// 空白文本（null / 空串 / 归一化后为空）直接忽略并返回 false —— 它们没有翻译价值。
        /// </summary>
        public bool Note(string text)
        {
            if (string.IsNullOrEmpty(text) || IsWhitespaceOnly(text))
            {
                return false;
            }

            Calls++;

            int count;
            if (_counts.TryGetValue(text, out count))
            {
                _counts[text] = count + 1;
                return false;
            }

            if (_counts.Count >= _maxDistinct)
            {
                Overflow++;
                return false;
            }

            _counts[text] = 1;
            return true;
        }

        /// <summary>该文本出现过几次（未记录过返回 0）。</summary>
        public int CountOf(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            int count;
            return _counts.TryGetValue(text, out count) ? count : 0;
        }

        /// <summary>该文本是否被记过。</summary>
        public bool Contains(string text)
        {
            return !string.IsNullOrEmpty(text) && _counts.ContainsKey(text);
        }

        /// <summary>快照：当前所有不同文本（顺序不保证，供差集计算用）。</summary>
        public string[] Keys()
        {
            var keys = new string[_counts.Count];
            int i = 0;
            foreach (KeyValuePair<string, int> pair in _counts)
            {
                keys[i] = pair.Key;
                i++;
            }

            return keys;
        }

        /// <summary>
        /// 出现次数最多的前 <paramref name="n"/> 条（次数降序，同次数按文本 Ordinal 升序，保证输出稳定）。
        /// <paramref name="n"/> ≤ 0 返回空数组。
        /// </summary>
        public TextSinkEntry[] TopByCount(int n)
        {
            if (n <= 0 || _counts.Count == 0)
            {
                return new TextSinkEntry[0];
            }

            var entries = new List<TextSinkEntry>(_counts.Count);
            foreach (KeyValuePair<string, int> pair in _counts)
            {
                entries.Add(new TextSinkEntry(pair.Key, pair.Value));
            }

            entries.Sort(CompareEntries);

            int take = n < entries.Count ? n : entries.Count;
            var result = new TextSinkEntry[take];
            for (int i = 0; i < take; i++)
            {
                result[i] = entries[i];
            }

            return result;
        }

        /// <summary>
        /// 纯空白判定。热路径上的开销是「首字符不是空白就立刻返回 false」这一次比较
        /// （Runtime 传进来的文本已经归一化，纯空白根本到不了这里；这只是让公开 API 自洽）。
        /// </summary>
        private static bool IsWhitespaceOnly(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (!char.IsWhiteSpace(text[i]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>次数降序、同次数按文本 Ordinal 升序。</summary>
        private static int CompareEntries(TextSinkEntry a, TextSinkEntry b)
        {
            if (a.Count != b.Count)
            {
                return b.Count.CompareTo(a.Count);
            }

            return string.CompareOrdinal(a.Text, b.Text);
        }
    }

    /// <summary>一条 sink 统计：文本 + 出现次数（只读）。</summary>
    public sealed class TextSinkEntry
    {
        /// <summary>建一条统计。</summary>
        public TextSinkEntry(string text, int count)
        {
            Text = text ?? string.Empty;
            Count = count;
        }

        /// <summary>归一化后的文本。</summary>
        public string Text { get; private set; }

        /// <summary>出现次数。</summary>
        public int Count { get; private set; }

        /// <inheritdoc />
        public override string ToString()
        {
            return Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "x " + Text;
        }
    }
}
