using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UI18n.Core.Update;

namespace UI18n.Core.Settings
{
    /// <summary>
    /// <c>prefs.json</c> 的读写（纯 BCL，双目标 net472 / net10.0）。
    /// <para>
    /// 契约（docs/10 §10）：
    /// <code>
    /// {"schema":"ui18n-prefs/1","language":"zh-cn","defaultMode":"translated",
    ///  "mods":{"Squad":{"enabled":false}},
    ///  "submission":{"consent":{"granted":false}}}
    /// </code>
    /// 读的时候额外兼容旧文档里的两种写法（<c>"schema":1</c>、<c>"consent":"granted"</c>）——
    /// 早期示例与任务书不一致，两种都认才不会让玩家手改的文件变成默认值。
    /// </para>
    /// <para>
    /// 三条硬约束（违反即返工）：
    /// <list type="number">
    /// <item><b>绝不抛</b>：解析失败返回默认值 + <c>error</c> 串，由调用方 Warn（红线 6）。</item>
    /// <item><b>未知字段原样写回</b>：前向兼容，升级一次不能把玩家的状态吃掉。</item>
    /// <item><b>原子写</b>：先写 <c>.tmp</c> 再 <c>File.Replace</c>，游戏被强杀也不会留下半个 JSON。</item>
    /// </list>
    /// </para>
    /// </summary>
    public static class PrefsFile
    {
        /// <summary>文件名（相对 <c>PluginData/UI18n/</c>）。</summary>
        public const string FileName = "prefs.json";

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        /// <summary>
        /// 读 prefs；文件不存在 / 解析失败 / 读取异常一律返回 <b>默认值</b>（全部启用），永不抛。
        /// </summary>
        public static Prefs Load(string path)
        {
            string error;
            return Load(path, out error);
        }

        /// <summary>
        /// 读 prefs，并给出错误串（<c>null</c> = 干净）。文件不存在<b>不算错误</b>
        /// （首次运行就会走这条路径），此时 <c>error</c> 为 null、返回默认值。
        /// </summary>
        public static Prefs Load(string path, out string error)
        {
            bool exists;
            return Load(path, out error, out exists);
        }

        /// <summary>读 prefs，并给出错误串与「文件是否存在」。</summary>
        public static Prefs Load(string path, out string error, out bool exists)
        {
            error = null;
            exists = false;

            if (string.IsNullOrEmpty(path))
            {
                return Prefs.CreateDefault();
            }

            try
            {
                if (!File.Exists(path))
                {
                    return Prefs.CreateDefault();
                }

                exists = true;
                string text = File.ReadAllText(path, Encoding.UTF8);
                Prefs prefs;
                string parseError;
                if (!TryParse(text, out prefs, out parseError))
                {
                    error = parseError;
                    return Prefs.CreateDefault();
                }

                return prefs;
            }
            catch (Exception ex)
            {
                // IO 层失败（权限 / 文件被占用 / 路径过长）也只降级，交给调用方 Warn。
                error = "io: " + ex.GetType().Name + ": " + ex.Message;
                return Prefs.CreateDefault();
            }
        }

        /// <summary>把 <c>PluginData/UI18n/</c> 目录与文件名拼起来（Runtime 与测试共用，避免各处硬编码）。</summary>
        public static string FilePathIn(string directory)
        {
            if (string.IsNullOrEmpty(directory))
            {
                return FileName;
            }

            return Path.Combine(directory, FileName);
        }

        /// <summary>
        /// 解析 JSON 文本。<b>结构错误</b>（不是对象、mods 不是对象）返回 false + 错误串；
        /// 单条记录坏掉只进 <see cref="Prefs.Warnings"/>，不整体失败。
        /// </summary>
        public static bool TryParse(string json, out Prefs prefs, out string error)
        {
            prefs = null;
            error = null;

            if (string.IsNullOrEmpty(json))
            {
                error = "empty-document";
                return false;
            }

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

            Prefs result = Prefs.CreateDefault();

            // schema：认 "ui18n-prefs/1" / "1" / 数字 1；未知字符串原样保留（前向兼容）。
            JsonValue schema = root.Get("schema");
            if (schema != null && !schema.IsNull)
            {
                long schemaNumber;
                string schemaText = schema.AsString();
                if (schemaText != null)
                {
                    result.Schema = schemaText.Length == 0 ? Prefs.SchemaId : schemaText;
                }
                else if (schema.TryGetLong(out schemaNumber) && schemaNumber == Prefs.SchemaNumber)
                {
                    result.Schema = Prefs.SchemaId;
                }
                else
                {
                    result.Schema = schema.ToString();
                }
            }

            JsonValue language = root.Get("language");
            if (language != null && !language.IsNull)
            {
                string value = language.AsString();
                if (value != null && value.Trim().Length > 0)
                {
                    result.Language = value.Trim();
                }
                else if (value == null)
                {
                    result.Warnings.Add("language: not-a-string, using " + Prefs.DefaultLanguage);
                }
            }

            JsonValue mode = root.Get("defaultMode");
            if (mode != null && !mode.IsNull)
            {
                string value = mode.AsString();
                if (value != null && value.Trim().Length > 0)
                {
                    result.DefaultMode = value.Trim();
                }
                else if (value == null)
                {
                    result.Warnings.Add("defaultMode: not-a-string, using " + Prefs.DefaultModeValue);
                }
            }

            JsonValue mods = root.Get("mods");
            if (mods != null && !mods.IsNull)
            {
                if (mods.Kind != JsonKind.Object)
                {
                    error = "mods-not-object";
                    return false;
                }

                ParseMods(mods, result);
            }

            JsonValue submission = root.Get("submission");
            if (submission != null && !submission.IsNull)
            {
                if (submission.Kind == JsonKind.Object)
                {
                    result.Submission = ParseSubmission(submission, result.Warnings);
                }
                else
                {
                    result.Warnings.Add("submission: not-an-object, ignored");
                }
            }

            // 顶层未知键：原样留住（前向兼容）。
            string[] names = root.MemberNames();
            Array.Sort(names, StringComparer.Ordinal);
            for (int i = 0; i < names.Length; i++)
            {
                string name = names[i];
                if (IsKnownTopLevel(name))
                {
                    continue;
                }

                result.Unknown[name] = root.Get(name).ToString();
            }

            prefs = result;
            return true;
        }

        private static bool IsKnownTopLevel(string name)
        {
            return string.Equals(name, "schema", StringComparison.Ordinal)
                || string.Equals(name, "language", StringComparison.Ordinal)
                || string.Equals(name, "defaultMode", StringComparison.Ordinal)
                || string.Equals(name, "mods", StringComparison.Ordinal)
                || string.Equals(name, "submission", StringComparison.Ordinal);
        }

        private static void ParseMods(JsonValue mods, Prefs result)
        {
            string[] ids = mods.MemberNames();
            Array.Sort(ids, StringComparer.Ordinal);
            for (int i = 0; i < ids.Length; i++)
            {
                string modId = ids[i];
                JsonValue entry = mods.Get(modId);
                if (modId == null || modId.Length == 0)
                {
                    result.Warnings.Add("mods: empty-mod-id, ignored");
                    continue;
                }

                if (entry == null || entry.Kind != JsonKind.Object)
                {
                    result.Warnings.Add("mods." + modId + ": not-an-object, ignored");
                    continue;
                }

                ModPreference pref = new ModPreference();
                JsonValue enabled = entry.Get("enabled");
                if (enabled != null && !enabled.IsNull)
                {
                    bool parsed;
                    if (TryParseBool(enabled, out parsed))
                    {
                        pref.Enabled = parsed;
                    }
                    else
                    {
                        result.Warnings.Add("mods." + modId + ".enabled: not-a-bool, assuming enabled");
                    }
                }

                string[] keys = entry.MemberNames();
                Array.Sort(keys, StringComparer.Ordinal);
                for (int k = 0; k < keys.Length; k++)
                {
                    if (string.Equals(keys[k], "enabled", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    pref.Unknown[keys[k]] = entry.Get(keys[k]).ToString();
                }

                result.SetEnabled(modId, pref.Enabled);
                ModPreference stored = result.Get(modId);
                if (stored != null)
                {
                    foreach (KeyValuePair<string, string> pair in pref.Unknown)
                    {
                        if (!stored.Unknown.ContainsKey(pair.Key))
                        {
                            stored.Unknown[pair.Key] = pair.Value;
                        }
                    }
                }
            }
        }

        private static SubmissionPrefs ParseSubmission(JsonValue submission, IList<string> warnings)
        {
            SubmissionPrefs state = new SubmissionPrefs();

            JsonValue consent = submission.Get("consent");
            if (consent != null && !consent.IsNull)
            {
                bool granted;
                if (TryParseConsent(consent, out granted))
                {
                    state.Granted = granted;
                }
                else
                {
                    warnings.Add("submission.consent: unrecognized shape, assuming not-granted");
                }
            }

            long version;
            if (submission.TryGetLong("policyVersion", out version) && version >= 0 && version <= int.MaxValue)
            {
                state.PolicyVersion = (int)version;
            }

            string submittedAt = submission.GetString("lastSubmittedAt", string.Empty);
            state.LastSubmittedAt = submittedAt ?? string.Empty;

            string[] names = submission.MemberNames();
            Array.Sort(names, StringComparer.Ordinal);
            for (int i = 0; i < names.Length; i++)
            {
                string name = names[i];
                if (string.Equals(name, "consent", StringComparison.Ordinal)
                    || string.Equals(name, "policyVersion", StringComparison.Ordinal)
                    || string.Equals(name, "lastSubmittedAt", StringComparison.Ordinal))
                {
                    continue;
                }

                state.Unknown[name] = submission.Get(name).ToString();
            }

            return state;
        }

        /// <summary>
        /// 同意状态兼容三种写法：<c>true</c>/<c>false</c>、
        /// <c>"granted"</c>/<c>"not-granted"</c>（docs/10 §10 的字符串写法）、
        /// <c>{"granted":true}</c>（任务书的对象写法）。
        /// </summary>
        private static bool TryParseConsent(JsonValue consent, out bool granted)
        {
            granted = false;

            if (consent.Kind == JsonKind.Bool)
            {
                granted = consent.AsBool();
                return true;
            }

            string text = consent.AsString();
            if (text != null)
            {
                if (IsAffirmative(text))
                {
                    granted = true;
                    return true;
                }

                granted = false;
                return true;
            }

            if (consent.Kind == JsonKind.Object)
            {
                JsonValue flag = consent.Get("granted");
                if (flag == null || flag.IsNull)
                {
                    granted = false;
                    return true;
                }

                return TryParseBool(flag, out granted);
            }

            return false;
        }

        private static bool IsAffirmative(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            string value = text.Trim();
            return string.Equals(value, "granted", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "1", StringComparison.Ordinal);
        }

        /// <summary>布尔容错：<c>true/false</c>、<c>"true"/"false"/"1"/"0"</c>、<c>1/0</c>。</summary>
        private static bool TryParseBool(JsonValue value, out bool result)
        {
            result = false;

            if (value.Kind == JsonKind.Bool)
            {
                result = value.AsBool();
                return true;
            }

            string text = value.AsString();
            if (text != null)
            {
                string trimmed = text.Trim();
                if (string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(trimmed, "1", StringComparison.Ordinal))
                {
                    result = true;
                    return true;
                }

                if (string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(trimmed, "0", StringComparison.Ordinal))
                {
                    result = false;
                    return true;
                }

                return false;
            }

            if (value.Kind == JsonKind.Number)
            {
                result = value.AsDouble() != 0d;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 序列化（固定键顺序 + mods 按名字 ordinal 排序 ⇒ 同样内容永远同样的字节，
        /// 便于人工 diff 与「写回后没有变化」的测试）。
        /// </summary>
        public static string ToJson(Prefs prefs)
        {
            Prefs state = prefs ?? Prefs.CreateDefault();
            StringBuilder sb = new StringBuilder(256);
            sb.Append('{');

            sb.Append("\n  \"schema\": ");
            WriteString(sb, string.IsNullOrEmpty(state.Schema) ? Prefs.SchemaId : state.Schema);

            sb.Append(",\n  \"language\": ");
            WriteString(sb, string.IsNullOrEmpty(state.Language) ? Prefs.DefaultLanguage : state.Language);

            sb.Append(",\n  \"defaultMode\": ");
            WriteString(sb, string.IsNullOrEmpty(state.DefaultMode) ? Prefs.DefaultModeValue : state.DefaultMode);

            sb.Append(",\n  \"mods\": {");
            string[] modIds = state.GetModIds();
            for (int i = 0; i < modIds.Length; i++)
            {
                ModPreference pref = state.Get(modIds[i]);
                if (i > 0)
                {
                    sb.Append(',');
                }

                sb.Append("\n    ");
                WriteString(sb, modIds[i]);
                sb.Append(": { \"enabled\": ");
                sb.Append(pref != null && pref.Enabled ? "true" : "false");
                if (pref != null && pref.Unknown.Count > 0)
                {
                    List<string> keys = new List<string>(pref.Unknown.Keys);
                    keys.Sort(StringComparer.Ordinal);
                    for (int k = 0; k < keys.Count; k++)
                    {
                        sb.Append(", ");
                        WriteString(sb, keys[k]);
                        sb.Append(": ");
                        sb.Append(pref.Unknown[keys[k]]);
                    }
                }

                sb.Append(" }");
            }

            if (modIds.Length > 0)
            {
                sb.Append("\n  ");
            }

            sb.Append('}');

            SubmissionPrefs submission = state.Submission ?? new SubmissionPrefs();
            sb.Append(",\n  \"submission\": {\n    \"consent\": { \"granted\": ");
            sb.Append(submission.Granted ? "true" : "false");
            sb.Append(" },\n    \"policyVersion\": ");
            sb.Append(submission.PolicyVersion.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\n    \"lastSubmittedAt\": ");
            WriteString(sb, submission.LastSubmittedAt ?? string.Empty);

            List<string> submissionKeys = new List<string>(submission.Unknown.Keys);
            submissionKeys.Sort(StringComparer.Ordinal);
            for (int i = 0; i < submissionKeys.Count; i++)
            {
                sb.Append(",\n    ");
                WriteString(sb, submissionKeys[i]);
                sb.Append(": ");
                sb.Append(submission.Unknown[submissionKeys[i]]);
            }

            sb.Append("\n  }");

            List<string> extraKeys = new List<string>(state.Unknown.Keys);
            extraKeys.Sort(StringComparer.Ordinal);
            for (int i = 0; i < extraKeys.Count; i++)
            {
                sb.Append(",\n  ");
                WriteString(sb, extraKeys[i]);
                sb.Append(": ");
                sb.Append(state.Unknown[extraKeys[i]]);
            }

            sb.Append("\n}\n");
            return sb.ToString();
        }

        private static void WriteString(StringBuilder sb, string value)
        {
            JsonWriter.WriteString(sb, value ?? string.Empty);
        }

        /// <summary>
        /// 原子写：先写 <c>prefs.json.tmp</c>，再 <c>File.Replace</c>。
        /// <c>File.Replace</c> 在部分文件系统（网络盘 / 只读介质）上会抛，
        /// 此时退回「删 + 移动」，两条路径失败都只返回错误串（不抛）。
        /// </summary>
        public static bool TrySave(string path, Prefs prefs, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(path))
            {
                error = "empty-path";
                return false;
            }

            string tempPath = path + ".tmp";
            try
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string text = ToJson(prefs);
                File.WriteAllText(tempPath, text, Utf8NoBom);

                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(tempPath, path, null);
                    }
                    catch (Exception)
                    {
                        File.Delete(path);
                        File.Move(tempPath, path);
                    }
                }
                else
                {
                    File.Move(tempPath, path);
                }

                return true;
            }
            catch (Exception ex)
            {
                error = "io: " + ex.GetType().Name + ": " + ex.Message;
                TryDelete(tempPath);
                return false;
            }
        }

        /// <summary>写 prefs；失败只吞掉（要错误串请用 <see cref="TrySave"/>）。</summary>
        public static void Save(string path, Prefs prefs)
        {
            string error;
            TrySave(path, prefs, out error);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // 清理失败无所谓：下次 Save 会覆盖 .tmp。
            }
        }
    }
}
