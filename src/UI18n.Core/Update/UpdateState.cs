using System;
using System.Globalization;
using System.Text;

namespace UI18n.Core.Update
{
    /// <summary>
    /// 更新检查的本地状态（<c>PluginData/UI18n/update-state.json</c>）。
    /// <para>
    /// 只存三样东西：上次**成功**检查的 UTC 时间、那次看到的远端版本、以及结果码。
    /// 用途是 <see cref="IsCheckDue"/>：避免每次启动都打一次网络请求。
    /// </para>
    /// <para>任何损坏的状态文件都退化成「从没检查过」，**绝不阻止启动、绝不抛异常**。</para>
    /// </summary>
    public sealed class UpdateState
    {
        /// <summary>结果码：有更新可用。</summary>
        public const string ResultUpdateAvailable = "update-available";

        /// <summary>结果码：已是最新。</summary>
        public const string ResultUpToDate = "up-to-date";

        /// <summary>结果码：检查出过问题（时间不推进到「下次再等 N 天」用，这里只是记录）。</summary>
        public const string ResultFailed = "failed";

        /// <summary>新建一个「从没检查过」的状态。</summary>
        public UpdateState()
        {
            LastCheckUtc = DateTime.MinValue;
            LastVersion = string.Empty;
            LastResult = string.Empty;
        }

        /// <summary>新建并带值。</summary>
        public UpdateState(DateTime lastCheckUtc, string lastVersion, string lastResult)
        {
            LastCheckUtc = lastCheckUtc.Kind == DateTimeKind.Utc ? lastCheckUtc : lastCheckUtc.ToUniversalTime();
            LastVersion = lastVersion ?? string.Empty;
            LastResult = lastResult ?? string.Empty;
        }

        /// <summary>上次成功检查的 UTC 时间；<see cref="DateTime.MinValue"/> 表示从没检查过。</summary>
        public DateTime LastCheckUtc { get; set; }

        /// <summary>上次看到的远端版本（原样字符串，可能为空）。</summary>
        public string LastVersion { get; set; }

        /// <summary>上次结果码（见 <c>Result*</c> 常量，可能为空）。</summary>
        public string LastResult { get; set; }

        /// <summary>「从没检查过」的状态（每次返回新实例，避免共享可变对象）。</summary>
        public static UpdateState Empty { get { return new UpdateState(); } }

        /// <summary>是否从没成功检查过。</summary>
        public bool HasNeverChecked { get { return LastCheckUtc == DateTime.MinValue; } }

        /// <summary>
        /// 现在是否该检查了。<paramref name="everyDays"/> &lt;= 0 表示每次启动都查；
        /// 时钟回拨（<paramref name="nowUtc"/> 早于记录值）也当作该查。
        /// </summary>
        public bool IsCheckDue(DateTime nowUtc, int everyDays)
        {
            if (everyDays <= 0)
            {
                return true;
            }

            if (LastCheckUtc == DateTime.MinValue)
            {
                return true;
            }

            if (nowUtc.Kind != DateTimeKind.Utc)
            {
                nowUtc = nowUtc.ToUniversalTime();
            }

            TimeSpan elapsed = nowUtc - LastCheckUtc;
            if (elapsed < TimeSpan.Zero)
            {
                return true;
            }

            return elapsed.TotalDays >= everyDays;
        }

        /// <summary>记录一次成功检查，返回新状态（不改自身）。</summary>
        public UpdateState WithCheck(DateTime whenUtc, string version, string result)
        {
            return new UpdateState(whenUtc, version, result);
        }

        /// <summary>序列化成一行 JSON。</summary>
        public string ToJson()
        {
            StringBuilder sb = new StringBuilder(160);
            sb.Append('{');
            sb.Append("\"lastCheckUtc\":");
            if (LastCheckUtc == DateTime.MinValue)
            {
                sb.Append("null");
            }
            else
            {
                JsonWriter.WriteString(sb, LastCheckUtc.ToUniversalTime()
                    .ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            }

            sb.Append(",\"lastVersion\":");
            JsonWriter.WriteString(sb, LastVersion ?? string.Empty);
            sb.Append(",\"lastResult\":");
            JsonWriter.WriteString(sb, LastResult ?? string.Empty);
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// 解析状态文件。任何非法输入返回 false + 原因串；调用方应退化成
        /// <see cref="Empty"/> 并继续（坏状态文件不是故障）。
        /// </summary>
        public static bool TryParse(string json, out UpdateState state, out string error)
        {
            state = null;
            error = null;

            JsonValue root;
            string jsonError;
            if (!Json.TryParse(json, out root, out jsonError))
            {
                error = "invalid-json: " + jsonError;
                return false;
            }

            if (root.Kind != JsonKind.Object)
            {
                error = "root-not-object";
                return false;
            }

            DateTime when = DateTime.MinValue;
            JsonValue whenNode = root.Get("lastCheckUtc");
            string whenText = whenNode == null ? null : whenNode.AsString();
            if (!string.IsNullOrEmpty(whenText))
            {
                DateTime parsed;
                if (!DateTime.TryParse(
                        whenText,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                        out parsed))
                {
                    error = "bad-lastCheckUtc: " + whenText;
                    return false;
                }

                when = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            }

            state = new UpdateState(when, root.GetString("lastVersion", string.Empty), root.GetString("lastResult", string.Empty));
            return true;
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return "update-state: lastCheckUtc="
                + (LastCheckUtc == DateTime.MinValue
                    ? "(never)"
                    : LastCheckUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))
                + ", lastVersion=" + (string.IsNullOrEmpty(LastVersion) ? "(none)" : LastVersion)
                + ", lastResult=" + (string.IsNullOrEmpty(LastResult) ? "(none)" : LastResult);
        }
    }
}
