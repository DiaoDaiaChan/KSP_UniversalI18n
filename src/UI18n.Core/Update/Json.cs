using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace UI18n.Core.Update
{
    /// <summary>JSON 值的类型。</summary>
    public enum JsonKind
    {
        /// <summary><c>null</c>。</summary>
        Null = 0,
        /// <summary><c>true</c> / <c>false</c>。</summary>
        Bool = 1,
        /// <summary>数字（保留原始文本，见 <see cref="JsonValue.TryGetLong"/>）。</summary>
        Number = 2,
        /// <summary>字符串。</summary>
        String = 3,
        /// <summary>数组。</summary>
        Array = 4,
        /// <summary>对象。</summary>
        Object = 5
    }

    /// <summary>
    /// 一个只读 JSON 值（对象 / 数组 / 字符串 / 数字 / 布尔 / null）。
    /// <para>
    /// 为什么自己写：net472 没有 <c>System.Text.Json</c>，而本项目禁止第三方包（T1 契约）；
    /// 更新清单 <c>manifest.json</c> 是嵌套结构，已有的 <c>JsonLineReader</c> 只能读「单层平表行」。
    /// </para>
    /// <para>所有取值方法都不抛异常：类型不对就返回默认值 / false。</para>
    /// </summary>
    public sealed class JsonValue
    {
        private static readonly JsonValue NullValue = new JsonValue(JsonKind.Null);
        private static readonly JsonValue TrueValue = new JsonValue(true);
        private static readonly JsonValue FalseValue = new JsonValue(false);

        private readonly JsonKind _kind;
        private readonly bool _bool;
        private readonly double _number;
        private readonly string _numberText;
        private readonly string _string;
        private readonly List<JsonValue> _items;
        private readonly Dictionary<string, JsonValue> _members;

        private JsonValue(JsonKind kind)
        {
            _kind = kind;
        }

        private JsonValue(bool value)
        {
            _kind = JsonKind.Bool;
            _bool = value;
        }

        private JsonValue(double value, string text)
        {
            _kind = JsonKind.Number;
            _number = value;
            _numberText = text;
        }

        private JsonValue(string value)
        {
            _kind = JsonKind.String;
            _string = value;
        }

        private JsonValue(List<JsonValue> items)
        {
            _kind = JsonKind.Array;
            _items = items;
        }

        private JsonValue(Dictionary<string, JsonValue> members)
        {
            _kind = JsonKind.Object;
            _members = members;
        }

        /// <summary>JSON <c>null</c>。</summary>
        public static JsonValue Null { get { return NullValue; } }

        /// <summary>由布尔构造。</summary>
        public static JsonValue FromBool(bool value) { return value ? TrueValue : FalseValue; }

        /// <summary>由数字构造（<paramref name="rawText"/> 只用于整数判定，可传 null）。</summary>
        public static JsonValue FromNumber(double value, string rawText = null)
        {
            return new JsonValue(value, rawText ?? value.ToString("R", CultureInfo.InvariantCulture));
        }

        /// <summary>由字符串构造（<c>null</c> 得到 JSON <c>null</c>）。</summary>
        public static JsonValue FromString(string value)
        {
            return value == null ? NullValue : new JsonValue(value);
        }

        /// <summary>由数组构造。</summary>
        public static JsonValue FromArray(IEnumerable<JsonValue> items)
        {
            List<JsonValue> list = items == null ? new List<JsonValue>() : new List<JsonValue>(items);
            return new JsonValue(list);
        }

        /// <summary>由对象构造。</summary>
        public static JsonValue FromObject(IDictionary<string, JsonValue> members)
        {
            Dictionary<string, JsonValue> map = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
            if (members != null)
            {
                foreach (KeyValuePair<string, JsonValue> pair in members)
                {
                    map[pair.Key] = pair.Value;
                }
            }

            return new JsonValue(map);
        }

        /// <summary>值类型。</summary>
        public JsonKind Kind { get { return _kind; } }

        /// <summary>是否 JSON <c>null</c>。</summary>
        public bool IsNull { get { return _kind == JsonKind.Null; } }

        /// <summary>布尔值（非布尔返回 false）。</summary>
        public bool AsBool()
        {
            return _kind == JsonKind.Bool && _bool;
        }

        /// <summary>数字值（非数字返回 0）。</summary>
        public double AsDouble()
        {
            return _kind == JsonKind.Number ? _number : 0d;
        }

        /// <summary>字符串值（非字符串返回 <c>null</c>）。</summary>
        public string AsString()
        {
            return _kind == JsonKind.String ? _string : null;
        }

        /// <summary>当前值的大小：对象 = 成员数，数组 = 元素数，字符串 = 长度，其它 = 0。</summary>
        public int Count
        {
            get
            {
                switch (_kind)
                {
                    case JsonKind.Object:
                        return _members.Count;
                    case JsonKind.Array:
                        return _items.Count;
                    case JsonKind.String:
                        return _string.Length;
                    default:
                        return 0;
                }
            }
        }

        /// <summary>数组元素（非数组返回空列表，永不返回 <c>null</c>）。</summary>
        public IList<JsonValue> Items
        {
            get
            {
                if (_kind != JsonKind.Array)
                {
                    return new JsonValue[0];
                }

                return _items;
            }
        }

        /// <summary>取对象成员；不是对象或成员不存在返回 <c>null</c>。</summary>
        public JsonValue Get(string name)
        {
            if (_kind != JsonKind.Object || name == null)
            {
                return null;
            }

            JsonValue value;
            return _members.TryGetValue(name, out value) ? value : null;
        }

        /// <summary>取成员并当字符串用；不是字符串时返回 <paramref name="fallback"/>。</summary>
        public string GetString(string name, string fallback)
        {
            JsonValue value = Get(name);
            string s = value == null ? null : value.AsString();
            return s ?? fallback;
        }

        /// <summary>取成员并当整数用。</summary>
        public bool TryGetLong(string name, out long value)
        {
            value = 0;
            JsonValue member = Get(name);
            return member != null && member.TryGetLong(out value);
        }

        /// <summary>
        /// 当整数取值：只有「原始文本里没有小数点/指数」的数字才算整数（<c>3.0</c> 不算）。
        /// 非数字、越界、带小数一律返回 false。
        /// </summary>
        public bool TryGetLong(out long value)
        {
            value = 0;
            if (_kind != JsonKind.Number)
            {
                return false;
            }

            string text = _numberText;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '.' || c == 'e' || c == 'E')
                {
                    return false;
                }
            }

            return long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>取对象成员名（按插入顺序不可保证；非对象返回空数组）。</summary>
        public string[] MemberNames()
        {
            if (_kind != JsonKind.Object)
            {
                return new string[0];
            }

            string[] names = new string[_members.Count];
            _members.Keys.CopyTo(names, 0);
            return names;
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            Write(sb);
            return sb.ToString();
        }

        private void Write(StringBuilder sb)
        {
            switch (_kind)
            {
                case JsonKind.Null:
                    sb.Append("null");
                    break;
                case JsonKind.Bool:
                    sb.Append(_bool ? "true" : "false");
                    break;
                case JsonKind.Number:
                    sb.Append(_numberText);
                    break;
                case JsonKind.String:
                    JsonWriter.WriteString(sb, _string);
                    break;
                case JsonKind.Array:
                    sb.Append('[');
                    for (int i = 0; i < _items.Count; i++)
                    {
                        if (i > 0)
                        {
                            sb.Append(',');
                        }

                        _items[i].Write(sb);
                    }

                    sb.Append(']');
                    break;
                case JsonKind.Object:
                    sb.Append('{');
                    bool first = true;
                    foreach (KeyValuePair<string, JsonValue> pair in _members)
                    {
                        if (!first)
                        {
                            sb.Append(',');
                        }

                        first = false;
                        JsonWriter.WriteString(sb, pair.Key);
                        sb.Append(':');
                        pair.Value.Write(sb);
                    }

                    sb.Append('}');
                    break;
            }
        }
    }

    /// <summary>把字符串写成 JSON 字面量（转义 <c>"</c> <c>\\</c> 与所有 &lt; 0x20 的字符）。</summary>
    public static class JsonWriter
    {
        /// <summary>写一个 JSON 字符串（含两端的引号）。</summary>
        public static void WriteString(StringBuilder sb, string value)
        {
            if (sb == null)
            {
                return;
            }

            if (value == null)
            {
                sb.Append("null");
                return;
            }

            sb.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '\b':
                        sb.Append("\\b");
                        break;
                    case '\f':
                        sb.Append("\\f");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (c < ' ')
                        {
                            sb.Append("\\u");
                            sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }

                        break;
                }
            }

            sb.Append('"');
        }

        /// <summary>把一个字符串转义成 JSON 字面量（含两端引号）。</summary>
        public static string Escape(string value)
        {
            StringBuilder sb = new StringBuilder((value == null ? 4 : value.Length) + 2);
            WriteString(sb, value);
            return sb.ToString();
        }
    }
}
