using System.Globalization;
using System.Text;

namespace UI18n.Core.Capture
{
    /// <summary>
    /// 缺命中记录的 JSONL 序列化（每行一个对象，字段顺序固定，LF 结尾，无 BOM）。
    /// <para>
    /// <b>为什么手写而不引第三方</b>：Core 零第三方依赖、net472 无 System.Text.Json（与 DictPack 同一个理由）。
    /// 字段只有 7 个且全部是字符串/整数，手写比引包便宜。
    /// </para>
    /// <para>
    /// 字段（顺序固定）：<c>h</c> 16 位小写 hex；<c>n</c> 归一化文本；<c>s</c> 原文；<c>sink</c> patch 短名；
    /// <c>caller</c> 真实调用者；<c>count</c> 未命中次数；<c>firstSeen</c> ISO8601 UTC；
    /// <c>template</c> 布尔（含数字）；<c>reason</c>（<c>path</c>/<c>progress</c>/<c>counter</c>/空串）；
    /// <c>bucket</c>（<c>candidate</c>/<c>template</c>/<c>never</c>）。
    /// </para>
    /// <para>
    /// task-34：后三个字段是<b>追加</b>的，老字段的名字、顺序、语义一个字没改 —— 已有解析器
    /// （data-engineer 的 capture_ingest）不受影响，新字段按需读。
    /// </para>
    /// <para>
    /// v1.5（task-59）：正文被截断时在最末尾追加 <c>"truncated": true</c>；<b>不截断时整行字节与旧版逐字节一致</b>
    /// （不写 <c>"truncated": false</c>）—— 让「没截断」这条路径的产物与老版本可混排、可比对。
    /// </para>
    /// </summary>
    public static class CaptureJson
    {
        /// <summary>一行（<b>不含</b>结尾换行）。</summary>
        public static string ToJsonLine(CaptureRecord record)
        {
            if (record == null)
            {
                return string.Empty;
            }

            return ToJsonLine(record.Hash, record.Source, record.Normalized, record.Sink, record.Caller, record.Count,
                              record.FirstSeen, record.Template, record.Reason, record.Bucket, record.Truncated);
        }

        /// <summary>
        /// 一行（<b>不含</b>结尾换行）。<c>template</c>/<c>reason</c>/<c>bucket</c> 由
        /// <paramref name="normalized"/> 现算（<see cref="CaptureClassify"/>）。
        /// </summary>
        public static string ToJsonLine(ulong hash, string source, string normalized, string sink, string caller, int count, string firstSeen)
        {
            CaptureClassification classification = CaptureClassification.Of(
                string.IsNullOrEmpty(normalized) ? (source ?? string.Empty) : normalized);
            return ToJsonLine(hash, source, normalized, sink, caller, count, firstSeen,
                              classification.Template, classification.Reason, classification.Bucket);
        }

        /// <summary>一行（<b>不含</b>结尾换行），分类字段由调用方给定。</summary>
        public static string ToJsonLine(ulong hash, string source, string normalized, string sink, string caller, int count,
                                        string firstSeen, bool template, string reason, string bucket)
        {
            return ToJsonLine(hash, source, normalized, sink, caller, count, firstSeen, template, reason, bucket, false);
        }

        /// <summary>
        /// 一行（<b>不含</b>结尾换行），分类字段与截断标记都由调用方给定。
        /// <paramref name="truncated"/> 为 true 时在行尾追加 <c>,"truncated":true</c>；false 时不写该字段。
        /// </summary>
        public static string ToJsonLine(ulong hash, string source, string normalized, string sink, string caller, int count,
                                        string firstSeen, bool template, string reason, string bucket, bool truncated)
        {
            StringBuilder sb = new StringBuilder(288);
            sb.Append('{');
            AppendField(sb, "h", hash.ToString("x16", CultureInfo.InvariantCulture), true);
            AppendField(sb, "n", normalized ?? string.Empty, false);
            AppendField(sb, "s", source ?? string.Empty, false);
            AppendField(sb, "sink", sink ?? string.Empty, false);
            AppendField(sb, "caller", caller ?? string.Empty, false);
            sb.Append(",\"count\":");
            sb.Append(count.ToString(CultureInfo.InvariantCulture));
            AppendField(sb, "firstSeen", firstSeen ?? string.Empty, false);
            sb.Append(",\"template\":").Append(template ? "true" : "false");
            AppendField(sb, "reason", reason ?? string.Empty, false);
            AppendField(sb, "bucket", string.IsNullOrEmpty(bucket) ? CaptureClassify.BucketCandidate : bucket, false);
            if (truncated)
            {
                sb.Append(",\"truncated\":true");
            }

            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>JSON 字符串转义（<c>"</c> <c>\</c> 与所有 &lt; 0x20 的控制字符）。</summary>
        public static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            StringBuilder sb = new StringBuilder(value.Length + 8);
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

            return sb.ToString();
        }

        private static void AppendField(StringBuilder sb, string name, string value, bool first)
        {
            if (!first)
            {
                sb.Append(',');
            }

            sb.Append('"').Append(name).Append("\":\"").Append(Escape(value)).Append('"');
        }
    }
}
