using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UI18n.Core.Update;

namespace UI18n.Core.Submission
{
    /// <summary>
    /// 投稿 bundle 的 <c>actor</c> 段（docs/10 §6.1）。
    /// <para>
    /// <c>installId</c> 是客户端随机生成的 uuid4（<see cref="InstallId"/>），只用来在服务端做本地去重，
    /// <b>不是</b>硬件指纹：不许塞机器名、用户名、路径、MAC、硬盘序列号（§6.2/§6.4）。
    /// </para>
    /// </summary>
    public sealed class SubmissionActor
    {
        /// <summary>actor 类型的唯一合法值。</summary>
        public const string KindPlayer = "player";

        /// <summary>actor 类型（目前只有 <c>player</c>）。</summary>
        public string Kind { get; set; } = KindPlayer;

        /// <summary>客户端安装标识（32 位小写 hex，见 <see cref="InstallId"/>）。</summary>
        public string InstallId { get; set; } = string.Empty;

        /// <summary>UI18n 版本（例如 <c>1.6.0</c>）。</summary>
        public string Ui18nVersion { get; set; } = string.Empty;

        /// <summary>KSP 版本（例如 <c>1.12.5</c>）。</summary>
        public string KspVersion { get; set; } = string.Empty;

        /// <summary>深拷贝。</summary>
        public SubmissionActor Clone()
        {
            SubmissionActor copy = new SubmissionActor();
            copy.Kind = Kind;
            copy.InstallId = InstallId;
            copy.Ui18nVersion = Ui18nVersion;
            copy.KspVersion = KspVersion;
            return copy;
        }
    }

    /// <summary>投稿 bundle 的 <c>consent</c> 段（服务端硬要求 <c>granted is True</c>，不认字符串 "true"）。</summary>
    public sealed class SubmissionConsent
    {
        /// <summary>是否已同意（导出时恒为 true；不同意就不导出）。</summary>
        public bool Granted { get; set; }

        /// <summary>同意依据的政策版本（当前 <see cref="SubmissionPolicy.PolicyVersion"/>）。</summary>
        public int PolicyVersion { get; set; }

        /// <summary>同意时间（ISO 8601，空串 = 未记录）。</summary>
        public string GrantedAt { get; set; } = string.Empty;

        /// <summary>深拷贝。</summary>
        public SubmissionConsent Clone()
        {
            SubmissionConsent copy = new SubmissionConsent();
            copy.Granted = Granted;
            copy.PolicyVersion = PolicyVersion;
            copy.GrantedAt = GrantedAt;
            return copy;
        }
    }

    /// <summary>投稿 bundle 的 <c>mods[]</c> 一行：声明「这些 modId 的原文我投了」。</summary>
    public sealed class SubmissionMod
    {
        /// <summary>modId（必须能安全当文件名：字母数字与 <c>.</c> <c>_</c> <c>-</c>，不以点开头）。</summary>
        public string ModId { get; set; } = string.Empty;

        /// <summary>包文件名（可选；只在能唯一确定时写）。</summary>
        public string FileName { get; set; } = string.Empty;

        /// <summary>mod 版本（可选；运行时通常不知道，留空不写）。</summary>
        public string Version { get; set; } = string.Empty;

        /// <summary>深拷贝。</summary>
        public SubmissionMod Clone()
        {
            SubmissionMod copy = new SubmissionMod();
            copy.ModId = ModId;
            copy.FileName = FileName;
            copy.Version = Version;
            return copy;
        }
    }

    /// <summary>投稿 bundle 的 <c>entries[]</c> 一行（只允许：原文、mod、可选调用点、可选计数）。</summary>
    public sealed class SubmissionEntry
    {
        /// <summary>原文的 FNV-1a 64 十六进制（16 位小写）。</summary>
        public string H { get; set; } = string.Empty;

        /// <summary>可译原文（<b>渲染后</b>的英文；中文一律不进 bundle，见 §6.3）。</summary>
        public string S { get; set; } = string.Empty;

        /// <summary>归属 modId（必须出现在 <see cref="SubmissionBundle.Mods"/> 里）。</summary>
        public string Mod { get; set; } = string.Empty;

        /// <summary>通道名（可选；当前固定 <c>capture</c>）。</summary>
        public string Channel { get; set; } = string.Empty;

        /// <summary>渲染出口（可选，例如 <c>TMPro.TMP_Text.set_text</c>）。</summary>
        public string Sink { get; set; } = string.Empty;

        /// <summary>调用点（可选，例如 <c>FAR.Debug.Draw</c>）。</summary>
        public string Caller { get; set; } = string.Empty;

        /// <summary>本会话出现次数（可选；&gt;0 才写）。</summary>
        public int Count { get; set; }

        /// <summary>深拷贝。</summary>
        public SubmissionEntry Clone()
        {
            SubmissionEntry copy = new SubmissionEntry();
            copy.H = H;
            copy.S = S;
            copy.Mod = Mod;
            copy.Channel = Channel;
            copy.Sink = Sink;
            copy.Caller = Caller;
            copy.Count = Count;
            return copy;
        }
    }

    /// <summary>脱敏审计计数（docs/10 §6.1 的四个键，键名与顺序冻结）。</summary>
    public sealed class SubmissionRedactions
    {
        /// <summary>判据同 <c>CaptureClassify</c> 的 path（路径样文本）。</summary>
        public int Paths { get; set; }

        /// <summary>命中玩家私有标识（存档名 / 飞船名 / Kerbal 名）后**只做了占位替换、正文保留**的条数。</summary>
        public int UserNames { get; set; }

        /// <summary>
        /// 命中玩家私有标识、替换后整串再无可见字母（只剩占位符 / 数字 / 标点）而**整条丢弃**的条数。
        /// <para>
        /// 与 <see cref="UserNames"/> 是两类数（Lead 裁决：审计要能分清「替换过」和「丢掉了」）。
        /// 序列化时**只在 &gt; 0 时写出**：没有私有值的绝大多数 bundle 字节因此完全不变，
        /// 已落盘的投稿与既有 canonical 真值不受影响；服务端缺键按 0 记（tools/i18n_tools/submission.py 的 _check_redactions）。
        /// </para>
        /// </summary>
        public int UserNamesDropped { get; set; }

        /// <summary>绝对路径（盘符 / UNC / 家目录）被丢弃的条数。</summary>
        public int AbsolutePaths { get; set; }

        /// <summary>URL 被丢弃的条数。</summary>
        public int Urls { get; set; }

        /// <summary>深拷贝。</summary>
        public SubmissionRedactions Clone()
        {
            SubmissionRedactions copy = new SubmissionRedactions();
            copy.Paths = Paths;
            copy.UserNames = UserNames;
            copy.AbsolutePaths = AbsolutePaths;
            copy.Urls = Urls;
            copy.UserNamesDropped = UserNamesDropped;
            return copy;
        }

        /// <summary>日志用一行摘要。</summary>
        public string Describe()
        {
            string line = "paths=" + Paths.ToString(CultureInfo.InvariantCulture)
                + ", userNames=" + UserNames.ToString(CultureInfo.InvariantCulture)
                + ", absolutePaths=" + AbsolutePaths.ToString(CultureInfo.InvariantCulture)
                + ", urls=" + Urls.ToString(CultureInfo.InvariantCulture);
            if (UserNamesDropped > 0)
            {
                line += ", userNamesDropped=" + UserNamesDropped.ToString(CultureInfo.InvariantCulture);
            }

            return line;
        }
    }

    /// <summary>
    /// 投稿 bundle（<c>ui18n-submission/1</c>，契约见 docs/10 §6.1，<b>已冻结</b>）。
    /// <para>
    /// 本类的核心职责是**逐字节可复现的 canonical 序列化**：服务端按
    /// <c>json.dumps(body, sort_keys=True, separators=(",", ":"), ensure_ascii=False)</c>
    /// 重算 sha256 并取前 16 位当 <c>id</c>，所以 C# 侧必须写出**完全一样**的字节，
    /// 否则玩家导出的包会被 <c>api submit accept</c> 以 <c>submission-id-mismatch</c> 拒收。
    /// 对拍真值见 <c>tests/UI18n.Core.Tests/SubmissionBundleTests.cs</c>。
    /// </para>
    /// <para>
    /// 三条硬语义：
    /// <list type="number">
    /// <item>键名按 <b>ordinal</b> 排序（都是 ASCII，与 python 的码点排序等价）；对象内无空格。</item>
    /// <item>非 ASCII 原样输出（<c>ensure_ascii=False</c>），控制字符走 <c>\u00XX</c> 小写。</item>
    /// <item>本类<b>不做 IO、不抛异常、不依赖 UnityEngine</b>（Core 契约）。</item>
    /// </list>
    /// </para>
    /// </summary>
    public sealed class SubmissionBundle
    {
        /// <summary>schema 字符串（逐字比对，不许模糊匹配）。</summary>
        public const string SchemaId = "ui18n-submission/1";

        /// <summary>一行原文的最大长度（§6.1）。</summary>
        public const int MaxTextLength = 8000;

        /// <summary>bundle id 的十六进制长度（sha256 前 16 位）。</summary>
        public const int IdLength = 16;

        private static readonly Regex IdRegex =
            new Regex("^[0-9a-f]{16}$", RegexOptions.CultureInvariant);

        private static readonly Regex HashRegex =
            new Regex("^[0-9a-f]{16}$", RegexOptions.CultureInvariant);

        private static readonly Regex ModIdRegex =
            new Regex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant);

        /// <summary>schema 标识（写文件恒等于 <see cref="SchemaId"/>）。</summary>
        public string Schema { get; set; } = SchemaId;

        /// <summary>bundle id（由 <see cref="ComputeId"/> 计算；解析时保留文件里的值以便对拍）。</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>导出时间（ISO 8601，带时区偏移）。</summary>
        public string CreatedAt { get; set; } = string.Empty;

        /// <summary>译文语言（例如 <c>zh-cn</c>）。</summary>
        public string Language { get; set; } = string.Empty;

        /// <summary>actor 段。</summary>
        public SubmissionActor Actor { get; set; } = new SubmissionActor();

        /// <summary>consent 段。</summary>
        public SubmissionConsent Consent { get; set; } = new SubmissionConsent();

        /// <summary>mods[] 段。</summary>
        public List<SubmissionMod> Mods { get; set; } = new List<SubmissionMod>();

        /// <summary>entries[] 段。</summary>
        public List<SubmissionEntry> Entries { get; set; } = new List<SubmissionEntry>();

        /// <summary>脱敏审计计数。</summary>
        public SubmissionRedactions Redactions { get; set; } = new SubmissionRedactions();

        /// <summary>玩家可选备注（导出恒为空串，但字段本身要写，服务端视作可选）。</summary>
        public string Note { get; set; } = string.Empty;

        // ------------------------------------------------------------------
        // id / canonical
        // ------------------------------------------------------------------

        /// <summary>
        /// 重算 bundle id：canonical body（去 id）→ sha256 → 前 16 位小写 hex。
        /// </summary>
        public string ComputeId()
        {
            string body = ToCanonicalJson(false);
            byte[] utf8 = Encoding.UTF8.GetBytes(body);
            byte[] digest;
            using (SHA256 sha = SHA256.Create())
            {
                digest = sha.ComputeHash(utf8);
            }

            StringBuilder hex = new StringBuilder(IdLength);
            for (int i = 0; i < IdLength / 2; i++)
            {
                hex.Append(digest[i].ToString("x2", CultureInfo.InvariantCulture));
            }

            return hex.ToString();
        }

        /// <summary>
        /// 写出 canonical JSON（单行、无空格、键名 ordinal 升序）。
        /// </summary>
        /// <param name="includeId">true = 含 <c>id</c>（落盘用）；false = 去 id（算 sha256 用）。</param>
        public string ToCanonicalJson(bool includeId)
        {
            StringBuilder sb = new StringBuilder(4096);
            sb.Append('{');

            WriteName(sb, "actor", true);
            WriteActor(sb, Actor);

            WriteName(sb, "consent", false);
            WriteConsent(sb, Consent);

            WriteName(sb, "createdAt", false);
            WriteText(sb, CreatedAt);

            WriteName(sb, "entries", false);
            WriteEntries(sb, Entries);

            if (includeId)
            {
                WriteName(sb, "id", false);
                // 写「对象当前的 id」而不是现场重算：否则被篡改的 id 永远写不出去，
                // 落盘 → 读回的自检（TryParse 会重算比对）就变成永远通过，契约漏洞藏在这里。
                // 只有在调用方忘了赋值时才回落成重算值（导出器本来就会先赋 Id）。
                WriteText(sb, string.IsNullOrEmpty(Id) ? ComputeId() : Id);
            }

            WriteName(sb, "language", false);
            WriteText(sb, Language);

            WriteName(sb, "mods", false);
            WriteMods(sb, Mods);

            WriteName(sb, "note", false);
            WriteText(sb, Note);

            WriteName(sb, "redactions", false);
            WriteRedactions(sb, Redactions);

            WriteName(sb, "schema", false);
            WriteText(sb, Schema);

            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>落盘用的完整内容（canonical + id + 行尾换行）。</summary>
        public string ToFileJson()
        {
            return ToCanonicalJson(true) + "\n";
        }

        /// <summary>
        /// 确定性化：mods 按 modId 排序去重、entries 按 (mod, h) 排序并按 (mod, h) 合并计数。
        /// <para>
        /// 为什么必须做：capture 行的顺序取决于玩家这一局的 UI 遍历顺序，
        /// 不定序的话同一份输入导两次会得到两个 id，服务端去重就失效了（§6.2 幂等）。
        /// </para>
        /// </summary>
        public void SortDeterministic()
        {
            if (Mods == null)
            {
                Mods = new List<SubmissionMod>();
            }

            List<SubmissionMod> mods = new List<SubmissionMod>(Mods.Count);
            for (int i = 0; i < Mods.Count; i++)
            {
                SubmissionMod mod = Mods[i];
                if (mod == null || string.IsNullOrEmpty(mod.ModId))
                {
                    continue;
                }

                mods.Add(mod);
            }

            mods.Sort(delegate(SubmissionMod left, SubmissionMod right)
            {
                return string.CompareOrdinal(left.ModId, right.ModId);
            });

            List<SubmissionMod> uniqueMods = new List<SubmissionMod>(mods.Count);
            for (int i = 0; i < mods.Count; i++)
            {
                if (uniqueMods.Count > 0
                    && string.Equals(uniqueMods[uniqueMods.Count - 1].ModId, mods[i].ModId, StringComparison.Ordinal))
                {
                    continue;
                }

                uniqueMods.Add(mods[i]);
            }

            Mods = uniqueMods;

            if (Entries == null)
            {
                Entries = new List<SubmissionEntry>();
                return;
            }

            List<SubmissionEntry> entries = new List<SubmissionEntry>(Entries.Count);
            for (int i = 0; i < Entries.Count; i++)
            {
                if (Entries[i] != null)
                {
                    entries.Add(Entries[i]);
                }
            }

            entries.Sort(delegate(SubmissionEntry left, SubmissionEntry right)
            {
                int byMod = string.CompareOrdinal(left.Mod, right.Mod);
                return byMod != 0 ? byMod : string.CompareOrdinal(left.H, right.H);
            });

            List<SubmissionEntry> merged = new List<SubmissionEntry>(entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                SubmissionEntry current = entries[i];
                if (merged.Count > 0)
                {
                    SubmissionEntry last = merged[merged.Count - 1];
                    if (string.Equals(last.Mod, current.Mod, StringComparison.Ordinal)
                        && string.Equals(last.H, current.H, StringComparison.Ordinal))
                    {
                        long sum = (long)last.Count + current.Count;
                        last.Count = sum > int.MaxValue ? int.MaxValue : (int)sum;
                        if (string.IsNullOrEmpty(last.Sink) && !string.IsNullOrEmpty(current.Sink))
                        {
                            last.Sink = current.Sink;
                        }

                        if (string.IsNullOrEmpty(last.Caller) && !string.IsNullOrEmpty(current.Caller))
                        {
                            last.Caller = current.Caller;
                        }

                        if (string.IsNullOrEmpty(last.Channel) && !string.IsNullOrEmpty(current.Channel))
                        {
                            last.Channel = current.Channel;
                        }

                        continue;
                    }
                }

                merged.Add(current);
            }

            Entries = merged;
        }

        // ------------------------------------------------------------------
        // 写出细节（python json.dumps 等价）
        // ------------------------------------------------------------------

        private static void WriteName(StringBuilder sb, string name, bool first)
        {
            if (!first)
            {
                sb.Append(',');
            }

            sb.Append('"').Append(name).Append("\":");
        }

        /// <summary>
        /// 写一个 JSON 字符串字面量：非 ASCII 原样、控制字符 <c>\u00XX</c> 小写、孤立代理转 U+FFFD。
        /// <para>
        /// 为什么不用 <c>JsonWriter.WriteString</c> 就完事：那个函数会把孤立代理原样写出来，
        /// 落盘后就不是合法 UTF-8（python 侧 <c>.encode("utf-8")</c> 直接抛），
        /// 所以这里先做一次代理对清洗。其余转义规则与 python 完全一致。
        /// </para>
        /// </summary>
        private static void WriteText(StringBuilder sb, string value)
        {
            JsonWriter.WriteString(sb, Sanitize(value));
        }

        /// <summary>把孤立代理替换成 U+FFFD（句柄：capture 端可能从游戏里收到半个代理对）。</summary>
        public static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value ?? string.Empty;
            }

            bool clean = true;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                    {
                        i++;
                        continue;
                    }

                    clean = false;
                    break;
                }

                if (char.IsLowSurrogate(c))
                {
                    clean = false;
                    break;
                }
            }

            if (clean)
            {
                return value;
            }

            StringBuilder sb = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    sb.Append(c).Append(value[i + 1]);
                    i++;
                    continue;
                }

                if (char.IsSurrogate(c))
                {
                    sb.Append('\uFFFD');
                    continue;
                }

                sb.Append(c);
            }

            return sb.ToString();
        }

        private static void WriteActor(StringBuilder sb, SubmissionActor actor)
        {
            SubmissionActor value = actor ?? new SubmissionActor();
            sb.Append('{');
            WriteName(sb, "installId", true);
            WriteText(sb, value.InstallId);
            WriteName(sb, "kind", false);
            WriteText(sb, value.Kind);
            WriteName(sb, "kspVersion", false);
            WriteText(sb, value.KspVersion);
            WriteName(sb, "ui18nVersion", false);
            WriteText(sb, value.Ui18nVersion);
            sb.Append('}');
        }

        private static void WriteConsent(StringBuilder sb, SubmissionConsent consent)
        {
            SubmissionConsent value = consent ?? new SubmissionConsent();
            sb.Append('{');
            WriteName(sb, "granted", true);
            sb.Append(value.Granted ? "true" : "false");
            WriteName(sb, "grantedAt", false);
            WriteText(sb, value.GrantedAt);
            WriteName(sb, "policyVersion", false);
            sb.Append(value.PolicyVersion.ToString(CultureInfo.InvariantCulture));
            sb.Append('}');
        }

        private static void WriteRedactions(StringBuilder sb, SubmissionRedactions redactions)
        {
            SubmissionRedactions value = redactions ?? new SubmissionRedactions();
            sb.Append('{');
            WriteName(sb, "absolutePaths", true);
            sb.Append(value.AbsolutePaths.ToString(CultureInfo.InvariantCulture));
            WriteName(sb, "paths", false);
            sb.Append(value.Paths.ToString(CultureInfo.InvariantCulture));
            WriteName(sb, "urls", false);
            sb.Append(value.Urls.ToString(CultureInfo.InvariantCulture));
            WriteName(sb, "userNames", false);
            sb.Append(value.UserNames.ToString(CultureInfo.InvariantCulture));

            // 第五个键：只在 >0 时写（键名 ordinal 排在 userNames 之后，与服务端 sort_keys 一致）。
            // 不写 = 服务端按 0 记；这样「没有任何私有值」的 bundle 字节与 v1.7.0 之前完全一致。
            if (value.UserNamesDropped > 0)
            {
                WriteName(sb, "userNamesDropped", false);
                sb.Append(value.UserNamesDropped.ToString(CultureInfo.InvariantCulture));
            }

            sb.Append('}');
        }

        private static void WriteMods(StringBuilder sb, List<SubmissionMod> mods)
        {
            sb.Append('[');
            if (mods != null)
            {
                for (int i = 0; i < mods.Count; i++)
                {
                    SubmissionMod mod = mods[i];
                    if (mod == null)
                    {
                        continue;
                    }

                    if (sb[sb.Length - 1] != '[')
                    {
                        sb.Append(',');
                    }

                    sb.Append('{');
                    bool first = true;
                    if (!string.IsNullOrEmpty(mod.FileName))
                    {
                        WriteName(sb, "fileName", first);
                        WriteText(sb, mod.FileName);
                        first = false;
                    }

                    WriteName(sb, "modId", first);
                    WriteText(sb, mod.ModId);
                    if (!string.IsNullOrEmpty(mod.Version))
                    {
                        WriteName(sb, "version", false);
                        WriteText(sb, mod.Version);
                    }

                    sb.Append('}');
                }
            }

            sb.Append(']');
        }

        private static void WriteEntries(StringBuilder sb, List<SubmissionEntry> entries)
        {
            sb.Append('[');
            if (entries != null)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    SubmissionEntry entry = entries[i];
                    if (entry == null)
                    {
                        continue;
                    }

                    if (sb[sb.Length - 1] != '[')
                    {
                        sb.Append(',');
                    }

                    sb.Append('{');
                    bool first = true;
                    if (!string.IsNullOrEmpty(entry.Caller))
                    {
                        WriteName(sb, "caller", first);
                        WriteText(sb, entry.Caller);
                        first = false;
                    }

                    if (!string.IsNullOrEmpty(entry.Channel))
                    {
                        WriteName(sb, "channel", first);
                        WriteText(sb, entry.Channel);
                        first = false;
                    }

                    if (entry.Count > 0)
                    {
                        WriteName(sb, "count", first);
                        sb.Append(entry.Count.ToString(CultureInfo.InvariantCulture));
                        first = false;
                    }

                    WriteName(sb, "h", first);
                    WriteText(sb, entry.H);
                    WriteName(sb, "mod", false);
                    WriteText(sb, entry.Mod);
                    WriteName(sb, "s", false);
                    WriteText(sb, entry.S);
                    if (!string.IsNullOrEmpty(entry.Sink))
                    {
                        WriteName(sb, "sink", false);
                        WriteText(sb, entry.Sink);
                    }

                    sb.Append('}');
                }
            }

            sb.Append(']');
        }

        // ------------------------------------------------------------------
        // 校验（与服务端 submission.validate 同症状）
        // ------------------------------------------------------------------

        /// <summary>16 位小写 hex（bundle id 与行 h 共用同一条判据）。</summary>
        public static bool IsHex16(string value)
        {
            return !string.IsNullOrEmpty(value) && HashRegex.IsMatch(value);
        }

        /// <summary>bundle id 形状（16 位小写 hex）。</summary>
        public static bool IsBundleId(string value)
        {
            return !string.IsNullOrEmpty(value) && IdRegex.IsMatch(value);
        }

        /// <summary>modId 形状（服务端 <c>_MOD_ID_RE</c>：首字符字母数字，之后字母数字与 <c>.</c> <c>_</c> <c>-</c>，≤128）。</summary>
        public static bool IsValidModId(string value)
        {
            return !string.IsNullOrEmpty(value) && ModIdRegex.IsMatch(value);
        }

        /// <summary>
        /// 结构 + 语义校验（不含 id 与内容的匹配）。错误串与服务端一致，便于两边日志对读。
        /// </summary>
        public static bool TryValidate(SubmissionBundle bundle, out string error)
        {
            error = string.Empty;
            if (bundle == null)
            {
                error = "submission-bundle-null";
                return false;
            }

            if (!string.Equals(bundle.Schema ?? string.Empty, SchemaId, StringComparison.Ordinal))
            {
                error = "submission-schema-invalid";
                return false;
            }

            if (string.IsNullOrEmpty(bundle.CreatedAt) || string.IsNullOrEmpty(bundle.Language))
            {
                error = "submission-field-invalid:createdAt-or-language";
                return false;
            }

            SubmissionActor actor = bundle.Actor ?? new SubmissionActor();
            if (string.IsNullOrEmpty(actor.InstallId))
            {
                error = "submission-field-invalid:actor.installId";
                return false;
            }

            SubmissionConsent consent = bundle.Consent ?? new SubmissionConsent();
            if (!consent.Granted)
            {
                error = "submission-consent-missing";
                return false;
            }

            if (consent.PolicyVersion < 0)
            {
                error = "submission-field-invalid:consent.policyVersion";
                return false;
            }

            List<SubmissionMod> mods = bundle.Mods ?? new List<SubmissionMod>();
            HashSet<string> declared = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < mods.Count; i++)
            {
                SubmissionMod mod = mods[i];
                if (mod == null || !IsValidModId(mod.ModId))
                {
                    error = "submission-field-invalid:mods[" + i.ToString(CultureInfo.InvariantCulture) + "].modId";
                    return false;
                }

                declared.Add(mod.ModId);
            }

            List<SubmissionEntry> entries = bundle.Entries;
            if (entries == null || entries.Count == 0)
            {
                error = "submission-empty-entries";
                return false;
            }

            for (int i = 0; i < entries.Count; i++)
            {
                SubmissionEntry entry = entries[i];
                if (entry == null)
                {
                    error = "submission-field-invalid:entries[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                    return false;
                }

                if (!IsHex16(entry.H))
                {
                    error = "submission-field-invalid:entries[" + i.ToString(CultureInfo.InvariantCulture) + "].h";
                    return false;
                }

                if (string.IsNullOrEmpty(entry.S))
                {
                    error = "submission-field-invalid:entries[" + i.ToString(CultureInfo.InvariantCulture) + "].s-empty";
                    return false;
                }

                if (entry.S.Length > MaxTextLength)
                {
                    error = "submission-text-too-long:" + entry.H;
                    return false;
                }

                if (string.IsNullOrEmpty(entry.Mod))
                {
                    error = "submission-field-invalid:entries[" + i.ToString(CultureInfo.InvariantCulture) + "].mod-empty";
                    return false;
                }

                if (!declared.Contains(entry.Mod))
                {
                    error = "submission-mod-undeclared:" + entry.Mod;
                    return false;
                }

                if (entry.Count < 0)
                {
                    error = "submission-field-invalid:entries[" + i.ToString(CultureInfo.InvariantCulture) + "].count";
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 解析并做完整校验（含「id 必须等于重算值」）。供自检与将来的 <c>api validate</c> 对拍。
        /// </summary>
        public static bool TryParse(string json, out SubmissionBundle bundle, out string error)
        {
            bundle = null;
            error = string.Empty;

            JsonValue root;
            string jsonError;
            if (!Json.TryParse(json, out root, out jsonError))
            {
                error = "invalid-json: " + jsonError;
                return false;
            }

            if (root == null || root.Kind != JsonKind.Object)
            {
                error = "root-not-object";
                return false;
            }

            SubmissionBundle parsed = new SubmissionBundle();
            parsed.Schema = root.GetString("schema", string.Empty) ?? string.Empty;
            parsed.Id = root.GetString("id", string.Empty) ?? string.Empty;
            parsed.CreatedAt = root.GetString("createdAt", string.Empty) ?? string.Empty;
            parsed.Language = root.GetString("language", string.Empty) ?? string.Empty;
            parsed.Note = root.GetString("note", string.Empty) ?? string.Empty;

            JsonValue actor = root.Get("actor");
            if (actor != null && actor.Kind == JsonKind.Object)
            {
                parsed.Actor.Kind = actor.GetString("kind", SubmissionActor.KindPlayer) ?? SubmissionActor.KindPlayer;
                parsed.Actor.InstallId = actor.GetString("installId", string.Empty) ?? string.Empty;
                parsed.Actor.Ui18nVersion = actor.GetString("ui18nVersion", string.Empty) ?? string.Empty;
                parsed.Actor.KspVersion = actor.GetString("kspVersion", string.Empty) ?? string.Empty;
            }

            JsonValue consent = root.Get("consent");
            if (consent != null && consent.Kind == JsonKind.Object)
            {
                JsonValue granted = consent.Get("granted");
                parsed.Consent.Granted = granted != null && granted.Kind == JsonKind.Bool && granted.AsBool();
                long policyVersion;
                if (consent.TryGetLong("policyVersion", out policyVersion) && policyVersion >= 0 && policyVersion <= int.MaxValue)
                {
                    parsed.Consent.PolicyVersion = (int)policyVersion;
                }

                parsed.Consent.GrantedAt = consent.GetString("grantedAt", string.Empty) ?? string.Empty;
            }

            JsonValue redactions = root.Get("redactions");
            if (redactions != null && redactions.Kind == JsonKind.Object)
            {
                parsed.Redactions.Paths = ReadInt(redactions, "paths");
                parsed.Redactions.UserNames = ReadInt(redactions, "userNames");
                parsed.Redactions.AbsolutePaths = ReadInt(redactions, "absolutePaths");
                parsed.Redactions.Urls = ReadInt(redactions, "urls");
                parsed.Redactions.UserNamesDropped = ReadInt(redactions, "userNamesDropped");
            }

            JsonValue mods = root.Get("mods");
            if (mods != null && !mods.IsNull)
            {
                if (mods.Kind != JsonKind.Array)
                {
                    error = "mods-not-array";
                    return false;
                }

                for (int i = 0; i < mods.Count; i++)
                {
                    JsonValue item = mods.Items[i];
                    if (item == null || item.Kind != JsonKind.Object)
                    {
                        error = "bad-mod-entry:" + i.ToString(CultureInfo.InvariantCulture);
                        return false;
                    }

                    SubmissionMod mod = new SubmissionMod();
                    mod.ModId = item.GetString("modId", string.Empty) ?? string.Empty;
                    mod.FileName = item.GetString("fileName", string.Empty) ?? string.Empty;
                    mod.Version = item.GetString("version", string.Empty) ?? string.Empty;
                    parsed.Mods.Add(mod);
                }
            }

            JsonValue entries = root.Get("entries");
            if (entries != null && !entries.IsNull)
            {
                if (entries.Kind != JsonKind.Array)
                {
                    error = "entries-not-array";
                    return false;
                }

                for (int i = 0; i < entries.Count; i++)
                {
                    JsonValue item = entries.Items[i];
                    if (item == null || item.Kind != JsonKind.Object)
                    {
                        error = "bad-entry:" + i.ToString(CultureInfo.InvariantCulture);
                        return false;
                    }

                    SubmissionEntry entry = new SubmissionEntry();
                    entry.H = item.GetString("h", string.Empty) ?? string.Empty;
                    entry.S = item.GetString("s", string.Empty) ?? string.Empty;
                    entry.Mod = item.GetString("mod", string.Empty) ?? string.Empty;
                    entry.Channel = item.GetString("channel", string.Empty) ?? string.Empty;
                    entry.Sink = item.GetString("sink", string.Empty) ?? string.Empty;
                    entry.Caller = item.GetString("caller", string.Empty) ?? string.Empty;
                    long count;
                    if (item.TryGetLong("count", out count) && count > 0 && count <= int.MaxValue)
                    {
                        entry.Count = (int)count;
                    }

                    parsed.Entries.Add(entry);
                }
            }

            string structuralError;
            if (!TryValidate(parsed, out structuralError))
            {
                error = structuralError;
                return false;
            }

            if (!IsBundleId(parsed.Id))
            {
                error = "submission-field-invalid:id";
                return false;
            }

            string computed = parsed.ComputeId();
            if (!string.Equals(computed, parsed.Id, StringComparison.Ordinal))
            {
                error = "submission-id-mismatch: declared=" + parsed.Id + ", computed=" + computed;
                return false;
            }

            bundle = parsed;
            return true;
        }

        private static int ReadInt(JsonValue parent, string name)
        {
            long value;
            if (parent != null && parent.TryGetLong(name, out value) && value > 0 && value <= int.MaxValue)
            {
                return (int)value;
            }

            return 0;
        }
    }
}
