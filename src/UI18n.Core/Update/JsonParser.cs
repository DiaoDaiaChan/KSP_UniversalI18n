using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace UI18n.Core.Update
{
    /// <summary>
    /// 极小的 JSON 解析器（递归下降），只服务于更新清单 <c>manifest.json</c> 与
    /// <c>update-state.json</c>。
    /// <para>
    /// 为什么不用 <c>System.Text.Json</c>：net472 目标没有它，而本项目禁止第三方包（T1 契约）。
    /// 为什么不用 <c>JsonLineReader</c>：那个只读「单层平表行」，读不了嵌套对象。
    /// </para>
    /// <para>
    /// 安全取向：**任何异常输入都返回 false + 原因串，绝不抛异常**；限制嵌套深度与文档长度，
    /// 防止恶意清单把游戏线程拖死或栈溢出。
    /// </para>
    /// </summary>
    public static class Json
    {
        /// <summary>允许的最大嵌套深度（数组/对象各算一层）。</summary>
        public const int MaxDepth = 64;

        /// <summary>允许的最大文档长度（字符）。清单实测 18 KB，4 MB 已经很宽松。</summary>
        public const int MaxDocumentLength = 4 * 1024 * 1024;

        /// <summary>
        /// 解析一段 JSON 文本。
        /// </summary>
        /// <param name="text">JSON 文本（允许首尾空白，允许 UTF-8 BOM 字符）。</param>
        /// <param name="value">成功时的根值。</param>
        /// <param name="error">失败原因（形如 <c>unexpected-char at 12</c>）。</param>
        /// <returns>成功返回 true；任何非法输入返回 false（不抛异常）。</returns>
        public static bool TryParse(string text, out JsonValue value, out string error)
        {
            value = null;
            error = null;

            if (text == null)
            {
                error = "empty-document";
                return false;
            }

            if (text.Length > MaxDocumentLength)
            {
                error = "document-too-long (" + text.Length.ToString(CultureInfo.InvariantCulture) + " chars)";
                return false;
            }

            Parser parser = new Parser(text);
            JsonValue root;
            if (!parser.ParseDocument(out root))
            {
                error = parser.Error;
                return false;
            }

            value = root;
            return true;
        }

        private sealed class Parser
        {
            private readonly string _text;
            private int _pos;
            private string _error;

            internal Parser(string text)
            {
                _text = text;
                _pos = 0;
                _error = null;
            }

            internal string Error
            {
                get { return _error == null ? "unknown-error" : _error; }
            }

            internal bool ParseDocument(out JsonValue value)
            {
                value = null;

                // 允许 UTF-8 BOM（有些服务器会把它写进文件）
                if (_text.Length > 0 && _text[0] == '\uFEFF')
                {
                    _pos = 1;
                }

                SkipWhitespace();

                JsonValue root;
                if (!ParseValue(0, out root))
                {
                    return false;
                }

                SkipWhitespace();
                if (_pos != _text.Length)
                {
                    return Fail("trailing-content");
                }

                value = root;
                return true;
            }

            private bool ParseValue(int depth, out JsonValue value)
            {
                value = null;

                if (depth > MaxDepth)
                {
                    return Fail("too-deep");
                }

                if (_pos >= _text.Length)
                {
                    return Fail("unexpected-end");
                }

                char c = _text[_pos];
                switch (c)
                {
                    case '{':
                        return ParseObject(depth, out value);
                    case '[':
                        return ParseArray(depth, out value);
                    case '"':
                        string s;
                        if (!ParseString(out s))
                        {
                            return false;
                        }

                        value = JsonValue.FromString(s);
                        return true;
                    case 't':
                        return ParseLiteral("true", JsonValue.FromBool(true), out value);
                    case 'f':
                        return ParseLiteral("false", JsonValue.FromBool(false), out value);
                    case 'n':
                        return ParseLiteral("null", JsonValue.Null, out value);
                    default:
                        if (c == '-' || (c >= '0' && c <= '9'))
                        {
                            return ParseNumber(out value);
                        }

                        return Fail("unexpected-char");
                }
            }

            private bool ParseObject(int depth, out JsonValue value)
            {
                value = null;
                _pos++; // '{'

                Dictionary<string, JsonValue> members = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
                SkipWhitespace();

                if (_pos < _text.Length && _text[_pos] == '}')
                {
                    _pos++;
                    value = JsonValue.FromObject(members);
                    return true;
                }

                while (true)
                {
                    SkipWhitespace();

                    if (_pos >= _text.Length || _text[_pos] != '"')
                    {
                        return Fail("expected-member-name");
                    }

                    string name;
                    if (!ParseString(out name))
                    {
                        return false;
                    }

                    if (members.ContainsKey(name))
                    {
                        return Fail("duplicate-key: " + name);
                    }

                    SkipWhitespace();
                    if (_pos >= _text.Length || _text[_pos] != ':')
                    {
                        return Fail("expected-colon");
                    }

                    _pos++;
                    SkipWhitespace();

                    JsonValue member;
                    if (!ParseValue(depth + 1, out member))
                    {
                        return false;
                    }

                    members[name] = member;
                    SkipWhitespace();

                    if (_pos >= _text.Length)
                    {
                        return Fail("unterminated-object");
                    }

                    char c = _text[_pos];
                    if (c == ',')
                    {
                        _pos++;
                        continue;
                    }

                    if (c == '}')
                    {
                        _pos++;
                        value = JsonValue.FromObject(members);
                        return true;
                    }

                    return Fail("expected-comma-or-brace");
                }
            }

            private bool ParseArray(int depth, out JsonValue value)
            {
                value = null;
                _pos++; // '['

                List<JsonValue> items = new List<JsonValue>();
                SkipWhitespace();

                if (_pos < _text.Length && _text[_pos] == ']')
                {
                    _pos++;
                    value = JsonValue.FromArray(items);
                    return true;
                }

                while (true)
                {
                    SkipWhitespace();

                    JsonValue item;
                    if (!ParseValue(depth + 1, out item))
                    {
                        return false;
                    }

                    items.Add(item);
                    SkipWhitespace();

                    if (_pos >= _text.Length)
                    {
                        return Fail("unterminated-array");
                    }

                    char c = _text[_pos];
                    if (c == ',')
                    {
                        _pos++;
                        continue;
                    }

                    if (c == ']')
                    {
                        _pos++;
                        value = JsonValue.FromArray(items);
                        return true;
                    }

                    return Fail("expected-comma-or-bracket");
                }
            }

            private bool ParseString(out string value)
            {
                value = null;
                _pos++; // '"'

                StringBuilder sb = new StringBuilder();
                while (true)
                {
                    if (_pos >= _text.Length)
                    {
                        return Fail("unterminated-string");
                    }

                    char c = _text[_pos];
                    if (c == '"')
                    {
                        _pos++;
                        value = sb.ToString();
                        return true;
                    }

                    if (c == '\\')
                    {
                        _pos++;
                        if (_pos >= _text.Length)
                        {
                            return Fail("unterminated-escape");
                        }

                        char e = _text[_pos];
                        switch (e)
                        {
                            case '"':
                                sb.Append('"');
                                _pos++;
                                continue;
                            case '\\':
                                sb.Append('\\');
                                _pos++;
                                continue;
                            case '/':
                                sb.Append('/');
                                _pos++;
                                continue;
                            case 'b':
                                sb.Append('\b');
                                _pos++;
                                continue;
                            case 'f':
                                sb.Append('\f');
                                _pos++;
                                continue;
                            case 'n':
                                sb.Append('\n');
                                _pos++;
                                continue;
                            case 'r':
                                sb.Append('\r');
                                _pos++;
                                continue;
                            case 't':
                                sb.Append('\t');
                                _pos++;
                                continue;
                            case 'u':
                                int code;
                                if (!ParseHex4(out code))
                                {
                                    return false;
                                }

                                sb.Append((char)code);
                                continue;
                            default:
                                return Fail("bad-escape");
                        }
                    }

                    if (c < ' ')
                    {
                        return Fail("control-char-in-string");
                    }

                    sb.Append(c);
                    _pos++;
                }
            }

            private bool ParseHex4(out int value)
            {
                value = 0;
                _pos++; // 'u'

                if (_pos + 4 > _text.Length)
                {
                    return Fail("short-unicode-escape");
                }

                int acc = 0;
                for (int i = 0; i < 4; i++)
                {
                    char c = _text[_pos + i];
                    int digit;
                    if (c >= '0' && c <= '9')
                    {
                        digit = c - '0';
                    }
                    else if (c >= 'a' && c <= 'f')
                    {
                        digit = c - 'a' + 10;
                    }
                    else if (c >= 'A' && c <= 'F')
                    {
                        digit = c - 'A' + 10;
                    }
                    else
                    {
                        return Fail("bad-unicode-escape");
                    }

                    acc = (acc * 16) + digit;
                }

                _pos += 4;
                value = acc;
                return true;
            }

            private bool ParseNumber(out JsonValue value)
            {
                value = null;
                int start = _pos;

                if (_pos < _text.Length && _text[_pos] == '-')
                {
                    _pos++;
                }

                int digits = 0;
                while (_pos < _text.Length && _text[_pos] >= '0' && _text[_pos] <= '9')
                {
                    _pos++;
                    digits++;
                }

                if (digits == 0)
                {
                    return Fail("bad-number");
                }

                if (_pos < _text.Length && _text[_pos] == '.')
                {
                    _pos++;
                    int frac = 0;
                    while (_pos < _text.Length && _text[_pos] >= '0' && _text[_pos] <= '9')
                    {
                        _pos++;
                        frac++;
                    }

                    if (frac == 0)
                    {
                        return Fail("bad-number");
                    }
                }

                if (_pos < _text.Length && (_text[_pos] == 'e' || _text[_pos] == 'E'))
                {
                    _pos++;
                    if (_pos < _text.Length && (_text[_pos] == '+' || _text[_pos] == '-'))
                    {
                        _pos++;
                    }

                    int exp = 0;
                    while (_pos < _text.Length && _text[_pos] >= '0' && _text[_pos] <= '9')
                    {
                        _pos++;
                        exp++;
                    }

                    if (exp == 0)
                    {
                        return Fail("bad-number");
                    }
                }

                string raw = _text.Substring(start, _pos - start);
                double parsed;
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                {
                    return Fail("bad-number");
                }

                value = JsonValue.FromNumber(parsed, raw);
                return true;
            }

            private bool ParseLiteral(string literal, JsonValue value, out JsonValue parsed)
            {
                parsed = null;

                if (_pos + literal.Length > _text.Length)
                {
                    return Fail("bad-literal");
                }

                for (int i = 0; i < literal.Length; i++)
                {
                    if (_text[_pos + i] != literal[i])
                    {
                        return Fail("bad-literal");
                    }
                }

                _pos += literal.Length;
                parsed = value;
                return true;
            }

            private void SkipWhitespace()
            {
                while (_pos < _text.Length)
                {
                    char c = _text[_pos];
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                    {
                        _pos++;
                        continue;
                    }

                    break;
                }
            }

            private bool Fail(string message)
            {
                if (_error == null)
                {
                    _error = message + " at " + _pos.ToString(CultureInfo.InvariantCulture);
                }

                return false;
            }
        }
    }
}
