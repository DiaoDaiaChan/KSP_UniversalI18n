using System;
using System.Text;
using System.Text.RegularExpressions;

namespace UI18n.Core.Text
{
    /// <summary>
    /// 数字骨架（pattern 通道）的唯一实现 —— 与 <c>tools/i18n_tools/skeleton.py</c> 逐字同口径，
    /// 靠 <c>tests/UI18n.Core.Tests/Data/skeleton-golden.jsonl</c>（331 条）钉死。
    /// <para>
    /// <b>为什么需要它</b>（v1.3，29 局实测）：捕获回来的模板档（<c>Mass Flow: 2.20917 ton/s</c>、
    /// <c>Vessels: 51</c>）每条原文都不一样，精确匹配永远不命中；把数字换成占位符 <c>#</c> 后，
    /// 56,632 条原文塌成 526 个骨架，一条骨架译文覆盖它的全部实例。所以这里是<b>查表键</b>的生产者，
    /// 不是模糊匹配：匹配侧任何「剥装饰符 / 分段 / 词窗口」的技巧只救 0.2%，本类<b>不做</b>那些。
    /// </para>
    /// <para>
    /// <b>规则（与 python 侧必须完全一致，改这里就要改 skeleton.py 并重跑两侧 selftest）</b>：
    /// <list type="number">
    /// <item>数字段 = <c>\d+(?:[.,]\d+)?</c>：连续数字，后面允许<b>最多一次</b> <c>.</c> 或 <c>,</c>
    /// 紧跟至少一位数字（只吃一层分隔符：<c>1.2.3</c> 得到 <c>1.2</c> 与 <c>3</c>，骨架 <c>#.#</c>）。</item>
    /// <item>每个数字段替换成<b>一个</b> <c>#</c>；译文侧同样用 <c>#</c> 表示「这里原样填回捕获到的数字」。</item>
    /// <item><b>TMP 富文本标签内部不骨化</b>：<see cref="TagPattern"/> 匹配到的整段原样拷贝，
    /// 所以 <c>&lt;color=#FF00AA&gt;12 ms&lt;/color&gt;</c> 的骨架是
    /// <c>&lt;color=#FF00AA&gt;# ms&lt;/color&gt;</c>（色值里的 <c>#</c> 不是占位符）；未知尖括号
    /// （<c>a &lt; b</c>）不吞数字，照常骨化。</item>
    /// <item>数字段 0 个（不是模板）或多于 <see cref="MaxSlots"/> 个（长日志 / 序列化数据）→ 返回 false。</item>
    /// <item>其余字符一个不动：空白、<c>&lt;&lt;1&gt;&gt;</c>、大小写、引号全原样保留 —— 骨架只换数字。</item>
    /// </list>
    /// </para>
    /// <para>线程：纯静态、无状态、无分配以外副作用，任何线程都可调用（运行期只在主线程用）。</para>
    /// </summary>
    public static class TextSkeleton
    {
        /// <summary>一条骨架最多几个数字槽位。超过就放弃（填回成本与误伤风险都不值）。</summary>
        public const int MaxSlots = 8;

        /// <summary>
        /// TMP 标签白名单（与 <c>CaptureClassify.TmpTagPattern</c>、
        /// <c>tools/i18n_tools/resolve_keyrefs.py:60</c> 的 <c>_TMP_TAG_RE</c> 逐字相同）。
        /// 只有带 <c>=值</c> 的形式（以及裸闭标签）才匹配：<c>&lt;quad width=2&gt;</c> 这种带空格的写法<b>不</b>匹配，
        /// 于是它里面的 <c>2</c> 照样被骨化 —— 这与 python 侧实测值一致，别「顺手修」。
        /// </summary>
        public const string TagPattern = "</?(?:b|i|u|s|color|size|material|quad|sprite|link)(?:=[^>]*)?>";

        private static readonly Regex TagRegex = new Regex(TagPattern, RegexOptions.CultureInvariant);

        private static readonly string[] NoNumbers = new string[0];

        /// <summary>
        /// 原文 → 骨架 + 数字段（按出现顺序）。
        /// <para>
        /// 返回 false 时 <paramref name="skeleton"/> 为 <c>null</c>、<paramref name="numbers"/> 为空数组：
        /// 文本为空 / 里没有数字（不是模板）/ 数字段多于 <see cref="MaxSlots"/> 个。
        /// </para>
        /// <para>
        /// 实现是<b>一次线性扫描 + 单次 StringBuilder</b>：不先做正则替换再 split（那样每条要分配 2~3 个数组）。
        /// 只在缓存未命中的首次路径调用，不在热路径上。
        /// </para>
        /// </summary>
        public static bool TryBuild(string text, out string skeleton, out string[] numbers)
        {
            skeleton = null;
            numbers = NoNumbers;

            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            StringBuilder sb = new StringBuilder(text.Length);
            string[] slots = new string[MaxSlots];
            int count = 0;
            int pos = 0;
            int length = text.Length;

            while (pos < length)
            {
                char ch = text[pos];

                if (ch == '<')
                {
                    Match tag = TagRegex.Match(text, pos);
                    if (tag.Success && tag.Index == pos)
                    {
                        // 标签整段原样拷贝：里面的数字与 '#' 都是字面量
                        sb.Append(tag.Value);
                        pos += tag.Length;
                        continue;
                    }
                }

                if (ch >= '0' && ch <= '9')
                {
                    int end = ScanNumber(text, pos);
                    if (count >= MaxSlots)
                    {
                        return false;                       // 9 段以上：长日志 / 序列化数据，放弃
                    }

                    slots[count++] = text.Substring(pos, end - pos);
                    sb.Append('#');
                    pos = end;
                    continue;
                }

                sb.Append(ch);
                pos++;
            }

            if (count == 0)
            {
                return false;                               // 没有数字：不是模板
            }

            skeleton = sb.ToString();
            if (count == MaxSlots)
            {
                numbers = slots;
            }
            else
            {
                string[] trimmed = new string[count];
                Array.Copy(slots, trimmed, count);
                numbers = trimmed;
            }

            return true;
        }

        /// <summary>
        /// 骨架 + 数字段 → 还原成原文（译文填回也走这条逻辑：把译文里的 <c>#</c> 按顺序换成数字）。
        /// <para>
        /// <b><c>#</c> 不都是占位符</b>：TMP 标签内部的 <c>#</c>（<c>&lt;color=#FF00AA&gt;</c> 的色值分隔）
        /// 是字面量，只有标签<b>外面</b>的 <c>#</c> 才吃一个数字段 —— 与 <see cref="TryBuild"/> 互逆。
        /// </para>
        /// <para>
        /// 槽位不匹配（占位符比数字多、数字没吃完、参数为 null）返回 <c>null</c>：调用方据此按未命中处理，
        /// <b>绝不能</b>把带 <c>#</c> 的半成品显示给玩家。
        /// </para>
        /// </summary>
        public static string FillBack(string skeleton, string[] numbers)
        {
            if (skeleton == null || numbers == null)
            {
                return null;
            }

            StringBuilder sb = new StringBuilder(skeleton.Length + 8);
            int used = 0;
            int pos = 0;
            int length = skeleton.Length;

            while (pos < length)
            {
                char ch = skeleton[pos];

                if (ch == '<')
                {
                    Match tag = TagRegex.Match(skeleton, pos);
                    if (tag.Success && tag.Index == pos)
                    {
                        sb.Append(tag.Value);
                        pos += tag.Length;
                        continue;
                    }
                }

                if (ch == '#')
                {
                    if (used >= numbers.Length)
                    {
                        return null;                        // 占位符比数字多
                    }

                    string number = numbers[used++];
                    if (number == null)
                    {
                        return null;                        // 半个数字段：宁可不翻
                    }

                    sb.Append(number);
                    pos++;
                    continue;
                }

                sb.Append(ch);
                pos++;
            }

            if (used != numbers.Length)
            {
                return null;                                // 数字没吃完：骨架与数字不配套
            }

            return sb.ToString();
        }

        /// <summary>
        /// 骨架里占位符 <c>#</c> 的个数（TMP 标签内部的不算）—— 门禁与填回的共同口径。
        /// 传 <c>null</c> 返回 <c>-1</c>（不是 0：0 表示「这条骨架没有槽位」，两者要能区分）。
        /// </summary>
        public static int CountPlaceholders(string skeleton)
        {
            if (skeleton == null)
            {
                return -1;
            }

            int count = 0;
            int pos = 0;
            int length = skeleton.Length;

            while (pos < length)
            {
                if (skeleton[pos] == '<')
                {
                    Match tag = TagRegex.Match(skeleton, pos);
                    if (tag.Success && tag.Index == pos)
                    {
                        pos += tag.Length;
                        continue;
                    }
                }

                if (skeleton[pos] == '#')
                {
                    count++;
                }

                pos++;
            }

            return count;
        }

        /// <summary>文本里有没有 ASCII 数字（捕获侧判模板档用的快速判据；不做正则）。</summary>
        public static bool HasDigits(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c >= '0' && c <= '9')
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 从 <paramref name="start"/> 起扫一个数字段，返回它的结束位置（不含）。
        /// 语义严格等于 python 的 <c>\d+(?:[.,]\d+)?</c>：只在<b>调用方已确认首字符是 ASCII 数字</b>时调用；
        /// 位数的判定用 <see cref="char.IsDigit(char)"/>（Unicode Nd，与 python <c>\d</c> 同集合）。
        /// </summary>
        private static int ScanNumber(string text, int start)
        {
            int length = text.Length;
            int pos = start;

            while (pos < length && char.IsDigit(text[pos]))
            {
                pos++;
            }

            if (pos < length && (text[pos] == '.' || text[pos] == ','))
            {
                int digitStart = pos + 1;
                int end = digitStart;
                while (end < length && char.IsDigit(text[end]))
                {
                    end++;
                }

                if (end > digitStart)
                {
                    pos = end;                              // 只吃一层分隔符：1.2.3 -> "1.2" + "3"
                }
            }

            return pos;
        }
    }
}
