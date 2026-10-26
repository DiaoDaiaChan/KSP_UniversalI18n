using System;
using System.Collections.Generic;
using System.Globalization;
using UI18n.Core.Update;

namespace UI18n.Core.Submission
{
    /// <summary>
    /// 生成投稿 bundle 的输入参数（docs/10 §6）。所有「环境事实」都由运行时填进来，
    /// 这一层<b>不碰任何游戏 API</b>，所以可以在 net10.0 的单元测试里逐字节验证 canonical id。
    /// </summary>
    public sealed class SubmissionBuildOptions
    {
        /// <summary>客户端安装标识（32 位小写 hex）。<b>不是</b>硬件指纹，见 <see cref="InstallId"/>。</summary>
        public string InstallId { get; set; } = string.Empty;

        /// <summary>UI18n 版本串（<c>AssemblyInformationalVersion</c>，形如 <c>1.6.0+abc1234</c>）。</summary>
        public string Ui18nVersion { get; set; } = string.Empty;

        /// <summary>
        /// KSP 版本串（形如 <c>1.12.5</c>），由调用方从 Assembly-CSharp 的
        /// <c>Versioning.version_major</c> / <c>.version_minor</c> / <c>.Revision</c> 拼出
        /// （<c>UI18n.Core.Diagnostics.KspVersionFormat.FromParts</c>）。
        /// <para>
        /// <b>不要用 <c>UnityEngine.Application.version</c></b>：那是 Unity 的
        /// <c>PlayerSettings.bundleVersion</c>，实测本机 KSP 1.12.5 的安装里它仍写着 1.12.4
        /// （Squad 出 1.12.5 时没 bump），照抄会让投稿记录与服务端 meta.json 都记错版本。
        /// </para>
        /// </summary>
        public string KspVersion { get; set; } = string.Empty;

        /// <summary>游戏语言（<c>zh-cn</c>）。</summary>
        public string Language { get; set; } = "zh-cn";

        /// <summary><c>createdAt</c>（<c>yyyy-MM-ddTHH:mm:sszzz</c>）；空串时取当前本地时间。</summary>
        public string CreatedAt { get; set; } = string.Empty;

        /// <summary><c>consent.grantedAt</c>；空串时沿用 <c>CreatedAt</c>。</summary>
        public string ConsentGrantedAt { get; set; } = string.Empty;

        /// <summary>同意条款版本（<c>consent.policyVersion</c>）。</summary>
        public int PolicyVersion { get; set; } = SubmissionPolicy.PolicyVersion;

        /// <summary>给作者看的备注（可选，界面上暂不收集）。</summary>
        public string Note { get; set; } = string.Empty;

        /// <summary>§6.3 第三步：是否放开「只出现一次且长度 &gt; 120」的串。</summary>
        public bool AllowLongSingletons { get; set; }

        /// <summary>当前已装载的 modId 全集（用于 caller 第一段归属，含 pseudo-mod）。</summary>
        public IList<string> ModIds { get; set; } = new List<string>();

        /// <summary>modId → 包文件名（<c>mods[].fileName</c>）；查不到时回落 <c>&lt;modId&gt;.jsonl</c>。</summary>
        public IDictionary<string, string> ModFileNames { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>玩家私有标识（存档名 / 飞行器名 / Kerbal 名 / 玩家目录名），§6.3 第二步替换掉。</summary>
        public IList<SubmissionPrivateValue> PrivateValues { get; set; } = new List<SubmissionPrivateValue>();

        /// <summary>默认值的独立副本（同 <c>CaptureOptions.Default</c> 的约定：不共享可变实例）。</summary>
        public static SubmissionBuildOptions Default
        {
            get { return new SubmissionBuildOptions(); }
        }
    }

    /// <summary>
    /// <see cref="SubmissionBuilder.Build"/> 的结果：<see cref="Ok"/> 为 false 时 <see cref="Error"/> 说明原因，
    /// 面板只负责把原因翻成人话，不猜。
    /// </summary>
    public sealed class SubmissionBuildResult
    {
        /// <summary>bundle（失败时为 null）。</summary>
        public SubmissionBundle Bundle { get; internal set; }

        /// <summary><c>ok</c> / <c>no-lines</c> / <c>no-entries</c> / <c>invalid</c> / <c>validate-failed</c>。</summary>
        public string Outcome { get; internal set; } = SubmissionBuilder.OutcomeNoLines;

        /// <summary>失败原因（成功时为空串）。</summary>
        public string Error { get; internal set; } = string.Empty;

        /// <summary>读到的捕获行数（含坏行）。</summary>
        public int Input { get; internal set; }

        /// <summary>坏行（JSON 解析失败 / h 非法 / 正文为空）条数。</summary>
        public int SkippedInvalid { get; internal set; }

        /// <summary>因 <c>truncated:true</c> 跳过的条数（截断的正文不投）。</summary>
        public int SkippedTruncated { get; internal set; }

        /// <summary>三步脱敏明细（失败时可能为 null）。</summary>
        public SubmissionFilterResult Filter { get; internal set; }

        /// <summary><c>mods[]</c> 条数。</summary>
        public int Mods { get; internal set; }

        /// <summary><c>entries[]</c> 条数（已按 (mod,h) 合并）。</summary>
        public int Entries { get; internal set; }

        /// <summary>是否拿到了合法 bundle。</summary>
        public bool Ok
        {
            get { return Bundle != null; }
        }
    }

    /// <summary>
    /// 把捕获行（<c>PluginData/UI18n/capture-*.jsonl</c>）变成投稿 bundle（docs/10 §6）。
    /// <para>
    /// 纯函数：输入是文本行 + 参数，输出是 bundle（或失败原因）。文件读写在
    /// <c>UI18n.Runtime.Submission.SubmissionExporter</c>，那层才碰 KSP 与磁盘。
    /// </para>
    /// <para>
    /// <b>取 <c>n</c> 而不是 <c>s</c></b>：捕获行的 <c>s</c> 是渲染后文本（我们自己翻过的串在那里是中文），
    /// <c>n</c> 才是归一化原文；两者都空的行直接跳过。already-cjk 判据仍然对最终文本生效
    /// （有的 mod 原生就是中文，那时 <c>n</c> 也是中文）。
    /// </para>
    /// </summary>
    public static class SubmissionBuilder
    {
        /// <summary>成功。</summary>
        public const string OutcomeOk = "ok";

        /// <summary>没有任何捕获行（本会话没抓到东西，或捕获功能关闭）。</summary>
        public const string OutcomeNoLines = "no-lines";

        /// <summary>行读到了，但三步脱敏之后一条都不剩。</summary>
        public const string OutcomeNoEntries = "no-entries";

        /// <summary>参数非法（安装标识 / 语言 / 版本为空等）。</summary>
        public const string OutcomeInvalid = "invalid";

        /// <summary>bundle 没通过 <see cref="SubmissionBundle.TryValidate"/>。</summary>
        public const string OutcomeValidateFailed = "validate-failed";

        /// <summary>投稿行的 <c>channel</c> 值（服务端按它归并 candidates）。</summary>
        public const string DefaultChannel = "capture";

        /// <summary>把捕获行构造成一份 bundle。</summary>
        /// <param name="jsonLines">捕获行（<c>CaptureJson.ToJsonLine</c> 的产物），允许含空行与坏行。</param>
        /// <param name="options">环境事实与开关。</param>
        public static SubmissionBuildResult Build(IEnumerable<string> jsonLines, SubmissionBuildOptions options)
        {
            SubmissionBuildResult result = new SubmissionBuildResult();

            if (options == null)
            {
                return Fail(result, OutcomeInvalid, "options-null");
            }

            string installId = InstallId.Ensure(options.InstallId);
            if (!InstallId.IsValid(installId))
            {
                return Fail(result, OutcomeInvalid, "install-id-invalid");
            }

            string language = Trim(options.Language);
            if (language.Length == 0)
            {
                return Fail(result, OutcomeInvalid, "language-empty");
            }

            string createdAt = Trim(options.CreatedAt);
            if (createdAt.Length == 0)
            {
                createdAt = FormatNow();
            }

            string grantedAt = Trim(options.ConsentGrantedAt);
            if (grantedAt.Length == 0)
            {
                grantedAt = createdAt;
            }

            ModResolver resolver = new ModResolver(options.ModIds);

            List<SubmissionEntry> raw = new List<SubmissionEntry>(64);
            if (jsonLines != null)
            {
                foreach (string line in jsonLines)
                {
                    if (line == null || line.Trim().Length == 0)
                    {
                        continue;
                    }

                    result.Input++;

                    bool truncated;
                    SubmissionEntry entry = ReadEntry(line, resolver, out truncated);
                    if (truncated)
                    {
                        result.SkippedTruncated++;
                        continue;
                    }

                    if (entry == null)
                    {
                        result.SkippedInvalid++;
                        continue;
                    }

                    raw.Add(entry);
                }
            }

            if (raw.Count == 0)
            {
                result.Outcome = result.Input == 0 ? OutcomeNoLines : OutcomeNoEntries;
                return result;
            }

            SubmissionFilter filter = new SubmissionFilter(options.PrivateValues);
            SubmissionFilterResult filtered = filter.Filter(raw, options.AllowLongSingletons);
            result.Filter = filtered;

            if (filtered.Entries.Count == 0)
            {
                result.Outcome = OutcomeNoEntries;
                result.SkippedInvalid = result.SkippedInvalid;
                return result;
            }

            SubmissionBundle bundle = new SubmissionBundle();
            bundle.Schema = SubmissionBundle.SchemaId;
            bundle.CreatedAt = createdAt;
            bundle.Language = language;
            bundle.Actor = new SubmissionActor();
            bundle.Actor.Kind = SubmissionActor.KindPlayer;
            bundle.Actor.InstallId = installId;
            bundle.Actor.Ui18nVersion = Trim(options.Ui18nVersion);
            bundle.Actor.KspVersion = Trim(options.KspVersion);
            bundle.Consent = new SubmissionConsent();
            bundle.Consent.Granted = true;
            bundle.Consent.PolicyVersion = options.PolicyVersion;
            bundle.Consent.GrantedAt = grantedAt;
            bundle.Redactions = filtered.Redactions != null ? filtered.Redactions : new SubmissionRedactions();
            bundle.Note = Trim(options.Note);

            Dictionary<string, bool> seenMods = new Dictionary<string, bool>(StringComparer.Ordinal);
            for (int i = 0; i < filtered.Entries.Count; i++)
            {
                SubmissionEntry entry = filtered.Entries[i];
                bundle.Entries.Add(entry);
                if (entry.Mod.Length > 0)
                {
                    seenMods[entry.Mod] = true;
                }
            }

            List<string> modIds = new List<string>(seenMods.Keys);
            modIds.Sort(StringComparer.Ordinal);
            for (int i = 0; i < modIds.Count; i++)
            {
                string modId = modIds[i];
                string fileName;
                if (options.ModFileNames == null || !options.ModFileNames.TryGetValue(modId, out fileName) || string.IsNullOrEmpty(fileName))
                {
                    fileName = modId + ".jsonl";
                }

                SubmissionMod mod = new SubmissionMod();
                mod.ModId = modId;
                mod.FileName = fileName;
                bundle.Mods.Add(mod);
            }

            bundle.SortDeterministic();
            bundle.Id = bundle.ComputeId();

            string error;
            if (!SubmissionBundle.TryValidate(bundle, out error))
            {
                result.Bundle = null;
                result.Outcome = OutcomeValidateFailed;
                result.Error = error;
                return result;
            }

            result.Bundle = bundle;
            result.Outcome = OutcomeOk;
            result.Mods = bundle.Mods.Count;
            result.Entries = bundle.Entries.Count;
            return result;
        }

        /// <summary>把本地时间格式化成契约要求的 <c>yyyy-MM-ddTHH:mm:ss+08:00</c>。</summary>
        public static string FormatNow()
        {
            return FormatTimestamp(DateTimeOffset.Now);
        }

        /// <summary>同 <see cref="FormatNow"/>，但时间点由调用方给定（测试用，保证可复现）。</summary>
        public static string FormatTimestamp(DateTimeOffset value)
        {
            return value.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
        }

        private static SubmissionBuildResult Fail(SubmissionBuildResult result, string outcome, string error)
        {
            result.Outcome = outcome;
            result.Error = error ?? string.Empty;
            return result;
        }

        /// <summary>把一行捕获 JSON 读成投稿条目；坏行返回 null 并把 <paramref name="truncated"/> 置位表示「超长被截断」。</summary>
        private static SubmissionEntry ReadEntry(string line, ModResolver resolver, out bool truncated)
        {
            truncated = false;

            JsonValue root;
            string error;
            if (!Json.TryParse(line, out root, out error) || root == null || root.Kind != JsonKind.Object)
            {
                return null;
            }

            JsonValue truncatedValue = root.Get("truncated");
            if (truncatedValue != null && !truncatedValue.IsNull && truncatedValue.AsBool())
            {
                truncated = true;
                return null;
            }

            string hash = Trim(root.GetString("h", string.Empty));
            if (!SubmissionBundle.IsHex16(hash))
            {
                return null;
            }

            // n 是归一化原文（h 就是对它算的哈希）；旧行 / 极端情况下回落 s（渲染后文本）。
            string text = Trim(root.GetString("n", string.Empty));
            if (text.Length == 0)
            {
                text = Trim(root.GetString("s", string.Empty));
            }

            if (text.Length == 0)
            {
                return null;
            }

            string caller = Trim(root.GetString("caller", string.Empty));
            string sink = Trim(root.GetString("sink", string.Empty));

            long count;
            if (!root.TryGetLong("count", out count) || count < 1)
            {
                count = 1;
            }

            if (count > int.MaxValue)
            {
                count = int.MaxValue;
            }

            SubmissionEntry entry = new SubmissionEntry();
            entry.H = hash;
            entry.S = text;
            entry.Mod = resolver != null ? resolver.Resolve(caller) : ModResolver.Unattributed;
            entry.Channel = DefaultChannel;
            entry.Sink = sink;
            entry.Caller = caller;
            entry.Count = (int)count;
            return entry;
        }

        private static string Trim(string value)
        {
            return value == null ? string.Empty : value.Trim();
        }
    }
}
