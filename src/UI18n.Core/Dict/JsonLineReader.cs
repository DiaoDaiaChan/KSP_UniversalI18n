using System;
using System.Collections.Generic;
using System.Text;

namespace UI18n.Core.Dict
{
    /// <summary>
    /// 极小的 JSONL 行解析器（net472 没有 System.Text.Json，且项目禁第三方包）。
    /// <para>
    /// <b>只做</b>字典行需要的那点 JSON：一个扁平对象，键值对是字符串 / 整数 / 字面量；
    /// 未知键跳过、嵌套对象与数组整体跳过、<c>\uXXXX</c>（含代理对两个转义自然拼接）、
    /// <c>\"</c> <c>\\</c> <c>\/</c> <c>\b</c> <c>\f</c> <c>\n</c> <c>\r</c> <c>\t</c> 转义、BOM、空行。
    /// <b>不做</b>浮点、注释、尾逗号、多文档。
    /// </para>
    /// <para>
    /// <b>JSON 字面量语义（T8 修的缺陷，别再改回去）</b>：
    /// 裸 <c>null</c> 一律解析成「该字段缺失」（不写进字段表），于是 <c>"t": null</c> 与 <c>t</c> 缺失
    /// 完全等价 —— 整行按坏行跳过并计入 ParseErrors，<b>绝不会</b>把字符串 <c>"null"</c> 当成译文
    /// （否则它还能通过 04 §6.3 的非空门禁，最后把 "null" 显示给玩家）。
    /// 裸 <c>true</c> / <c>false</c> 在当前 schema（h/n/s/t/mod/ctx，全是字符串或整数）里没有任何合法位置，
    /// 因此出现即整行非法 —— 同样计 ParseErrors，不会退化成字符串 "true"。
    /// 将来若 schema 真要加布尔字段，必须在这里显式转换，而不是让字面量漏成字符串。
    /// </para>
    /// <para>
    /// 结果三态：<see cref="Status.Ok"/> / <see cref="Status.Blank"/>（空行、null 行，静默跳过）/
    /// <see cref="Status.Error"/>（坏行，调用方计数跳过，<b>绝不抛</b>——红线 6）。
    /// </para>
    /// </summary>
    internal static class JsonLineReader
    {
        internal enum Status
        {
            Ok = 0,
            Blank = 1,
            Error = 2
        }

        /// <summary>JSON 字面量。<c>null</c> 表示字段缺失，<c>true</c>/<c>false</c> 在当前 schema 下非法。</summary>
        internal enum JsonLiteral
        {
            Null = 0,
            True = 1,
            False = 2
        }

        private const char Bom = '\uFEFF';

        /// <summary>
        /// 解析一行。返回 <see cref="Status.Ok"/> 时 <paramref name="entry"/> 非 null；
        /// 返回 Blank 或 Error 时 <paramref name="entry"/> 为 null。
        /// </summary>
        internal static Status ParseLine(string line, out DictEntry entry)
        {
            entry = null;
            if (line == null)
            {
                return Status.Blank;
            }

            int pos = 0;
            if (line.Length > 0 && line[0] == Bom)
            {
                pos = 1;
            }

            SkipWhitespace(line, ref pos);
            if (pos >= line.Length)
            {
                return Status.Blank;
            }

            if (line[pos] != '{')
            {
                return Status.Error;
            }

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!ParseObject(line, ref pos, map))
            {
                return Status.Error;
            }

            SkipWhitespace(line, ref pos);
            if (pos != line.Length)
            {
                return Status.Error;                       // 对象后面还有垃圾
            }

            string hashRaw;
            if (!map.TryGetValue("h", out hashRaw) || hashRaw == null)
            {
                return Status.Error;                       // h 缺失：无法定位
            }

            ulong hash;
            if (!TryParseHash(hashRaw, out hash))
            {
                return Status.Error;                       // h 非法
            }

            string translation;
            if (!map.TryGetValue("t", out translation) || string.IsNullOrEmpty(translation))
            {
                return Status.Error;                       // 04 §6.3 门禁 7：空译文一律拒绝
            }

            entry = new DictEntry(
                hash,
                Get(map, "n"),
                Get(map, "s"),
                translation,
                Get(map, "mod"),
                Get(map, "ctx"));

            return Status.Ok;
        }

        private static string Get(Dictionary<string, string> map, string key)
        {
            string v;
            return map.TryGetValue(key, out v) && v != null ? v : string.Empty;
        }

        /// <summary>
        /// h 字段解析。<b>优先级规则（这是与 python 生产端的格式契约，改动前先看 T8 任务书）</b>：
        /// <list type="number">
        /// <item><c>0x</c> / <c>0X</c> 前缀 → 无条件按 16 进制。</item>
        /// <item><b>长度恰为 16 且全部字符是 <c>[0-9a-fA-F]</c> → 按 16 进制</b>（FNV-1a 64 的标准表示，
        /// <c>Hash(...).ToString("x16")</c> 的产物）。<b>必须先于十进制尝试</b>：像
        /// <c>"8400081244540915"</c> 这种「16 位全数字」的 hex 串，若先按十进制读会得到一个完全不同的值
        /// （实测 pool.jsonl 12,792 行里有 12 行走这条路，例如 id=1156 R-79 Turbofan）。</item>
        /// <item>其他长度 → 先尝试十进制（JSON number 或十进制字符串），再退回 16 进制。</item>
        /// </list>
        /// 负号、空串、无法解析 → false（调用方计入 ParseErrors 并跳过该行，不抛）。
        /// </summary>
        private static bool TryParseHash(string raw, out ulong hash)
        {
            hash = 0UL;
            string s = raw.Trim();
            if (s.Length == 0 || s[0] == '-')
            {
                return false;
            }

            if (s.Length > 2 && s[0] == '0' && (s[1] == 'x' || s[1] == 'X'))
            {
                return ulong.TryParse(s.Substring(2), System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out hash);
            }

            if (s.Length == 16 && IsHexString(s))
            {
                return ulong.TryParse(s, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out hash);
            }

            if (ulong.TryParse(s, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out hash))
            {
                return true;
            }

            return ulong.TryParse(s, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out hash);
        }

        /// <summary>全部字符都是 <c>[0-9a-fA-F]</c>（调用方已保证非空）。</summary>
        private static bool IsHexString(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                if (HexDigit(s[i]) < 0)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>解析 <c>{ "k": v, ... }</c>，pos 指向 '{'；返回 false 表示语法错误。</summary>
        private static bool ParseObject(string line, ref int pos, Dictionary<string, string> map)
        {
            if (pos >= line.Length || line[pos] != '{')
            {
                return false;
            }

            pos++;                                          // '{'
            SkipWhitespace(line, ref pos);

            if (pos < line.Length && line[pos] == '}')
            {
                pos++;
                return true;
            }

            while (true)
            {
                SkipWhitespace(line, ref pos);
                if (pos >= line.Length || line[pos] != '"')
                {
                    return false;
                }

                string key = ParseString(line, ref pos);
                if (key == null)
                {
                    return false;
                }

                SkipWhitespace(line, ref pos);
                if (pos >= line.Length || line[pos] != ':')
                {
                    return false;
                }

                pos++;                                      // ':'
                SkipWhitespace(line, ref pos);
                if (pos >= line.Length)
                {
                    return false;
                }

                string value;
                char c = line[pos];
                if (c == '"')
                {
                    value = ParseString(line, ref pos);
                    if (value == null)
                    {
                        return false;
                    }
                }
                else if (c == '{' || c == '[')
                {
                    if (!SkipNested(line, ref pos))
                    {
                        return false;
                    }

                    value = null;                           // 嵌套结构不进字典
                }
                else if (c == '-' || (c >= '0' && c <= '9'))
                {
                    if (!ReadNumber(line, ref pos, out value))
                    {
                        return false;
                    }
                }
                else
                {
                    JsonLiteral literal;
                    if (!ReadLiteral(line, ref pos, out literal))
                    {
                        return false;
                    }

                    if (literal != JsonLiteral.Null)
                    {
                        // true / false：当前 schema 里没有任何布尔字段（见类注释），
                        // 出现它们是格式错误，整行按坏行处理（计 ParseErrors），绝不把 "true" 当译文。
                        return false;
                    }

                    value = null;                       // 裸 null = 该字段「缺失」，不写进 map
                }

                if (value != null)
                {
                    map[key] = value;                       // 重复键：后写的赢
                }

                SkipWhitespace(line, ref pos);
                if (pos >= line.Length)
                {
                    return false;
                }

                if (line[pos] == ',')
                {
                    pos++;
                    continue;
                }

                if (line[pos] == '}')
                {
                    pos++;
                    return true;
                }

                return false;
            }
        }

        /// <summary>解析 JSON 字符串，pos 指向开头的引号；成功返回解转义后的值且 pos 指向闭合引号之后。</summary>
        private static string ParseString(string line, ref int pos)
        {
            if (pos >= line.Length || line[pos] != '"')
            {
                return null;
            }

            pos++;
            var sb = new StringBuilder(32);

            while (pos < line.Length)
            {
                char c = line[pos++];

                if (c == '"')
                {
                    return sb.ToString();
                }

                if (c != '\\')
                {
                    if (c < 0x20)
                    {
                        return null;                        // JSON 字符串里不允许裸控制字符
                    }

                    sb.Append(c);
                    continue;
                }

                if (pos >= line.Length)
                {
                    return null;
                }

                char esc = line[pos++];
                switch (esc)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        {
                            if (pos + 4 > line.Length)
                            {
                                return null;
                            }

                            int code = 0;
                            for (int i = 0; i < 4; i++)
                            {
                                int digit = HexDigit(line[pos + i]);
                                if (digit < 0)
                                {
                                    return null;
                                }

                                code = (code << 4) | digit;
                            }

                            pos += 4;
                            sb.Append((char)code);          // \uD83D\uDE00 这类代理对两个转义相连即合法 UTF-16
                            break;
                        }

                    default:
                        return null;
                }
            }

            return null;                                    // 没有闭合引号
        }

        private static int HexDigit(char c)
        {
            if (c >= '0' && c <= '9')
            {
                return c - '0';
            }

            if (c >= 'a' && c <= 'f')
            {
                return c - 'a' + 10;
            }

            if (c >= 'A' && c <= 'F')
            {
                return c - 'A' + 10;
            }

            return -1;
        }

        private static bool ReadNumber(string line, ref int pos, out string token)
        {
            token = null;
            int start = pos;
            if (pos < line.Length && line[pos] == '-')
            {
                pos++;
            }

            int digits = 0;
            while (pos < line.Length && line[pos] >= '0' && line[pos] <= '9')
            {
                pos++;
                digits++;
            }

            if (digits == 0)
            {
                pos = start;
                return false;
            }

            token = line.Substring(start, pos - start);
            return true;
        }

        /// <summary>
        /// 读一个 JSON 字面量（<c>true</c> / <c>false</c> / <c>null</c>），pos 指向首字符。
        /// 返回 false 表示这里根本不是字面量（语法错）。
        /// </summary>
        private static bool ReadLiteral(string line, ref int pos, out JsonLiteral literal)
        {
            literal = JsonLiteral.Null;
            if (MatchLiteral(line, pos, "null"))
            {
                pos += 4;
                literal = JsonLiteral.Null;
                return true;
            }

            if (MatchLiteral(line, pos, "true"))
            {
                pos += 4;
                literal = JsonLiteral.True;
                return true;
            }

            if (MatchLiteral(line, pos, "false"))
            {
                pos += 5;
                literal = JsonLiteral.False;
                return true;
            }

            return false;
        }

        private static bool MatchLiteral(string line, int pos, string literal)
        {
            return pos + literal.Length <= line.Length
                   && string.CompareOrdinal(line, pos, literal, 0, literal.Length) == 0;
        }

        /// <summary>整体跳过一个嵌套对象 / 数组（含内部字符串），返回 false 表示括号不配对。</summary>
        private static bool SkipNested(string line, ref int pos)
        {
            int depth = 0;
            while (pos < line.Length)
            {
                char c = line[pos];
                if (c == '"')
                {
                    if (ParseString(line, ref pos) == null)
                    {
                        return false;
                    }

                    continue;
                }

                if (c == '{' || c == '[')
                {
                    depth++;
                    pos++;
                    continue;
                }

                if (c == '}' || c == ']')
                {
                    depth--;
                    pos++;
                    if (depth == 0)
                    {
                        return true;
                    }

                    if (depth < 0)
                    {
                        return false;
                    }

                    continue;
                }

                pos++;
            }

            return false;
        }

        private static void SkipWhitespace(string line, ref int pos)
        {
            while (pos < line.Length)
            {
                char c = line[pos];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                {
                    pos++;
                    continue;
                }

                break;
            }
        }
    }
}
