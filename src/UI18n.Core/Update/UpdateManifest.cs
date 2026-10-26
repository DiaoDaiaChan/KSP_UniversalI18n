using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UI18n.Core.Dict;

namespace UI18n.Core.Update
{
    /// <summary>更新清单里的一条文件记录。</summary>
    public sealed class ManifestFile
    {
        internal ManifestFile(string path, string modRelativePath, long bytes, string sha256)
        {
            Path = path;
            ModRelativePath = modRelativePath;
            Bytes = bytes;
            Sha256 = sha256;
        }

        /// <summary>清单里的原始路径（已归一化成 <c>/</c> 分隔，保留 <c>GameData/UI18n/</c> 前缀）。</summary>
        public string Path { get; private set; }

        /// <summary>
        /// 相对 mod 根（即 <c>GameData/UI18n/</c>）的路径，例如 <c>Packs/zh-cn/RP-1.jsonl</c>。
        /// 未带 <c>GameData/UI18n/</c> 前缀的路径会原样作为相对路径。
        /// </summary>
        public string ModRelativePath { get; private set; }

        /// <summary>解压后的字节数。</summary>
        public long Bytes { get; private set; }

        /// <summary>解压后内容的 sha256（大写 64 位 hex）。</summary>
        public string Sha256 { get; private set; }

        /// <inheritdoc/>
        public override string ToString()
        {
            return ModRelativePath + " (" + Sha256Hex.DescribeBytes(Bytes) + ", sha256 " + Sha256 + ")";
        }
    }

    /// <summary>
    /// 更新清单 v2 里的一条 <b>按 mod</b> 的记录（<c>mods[]</c>，契约见 docs/10 §9）。
    /// <para>
    /// 为什么要按 mod 一条：v1 的整包 zip 只能"全装"，而玩家状态里是可以关掉某些 mod 的
    /// （<c>prefs.json</c>），装一个被关掉的包等于白下几 MB。v2 有了这张表，
    /// 安装阶段就能<see cref="PackInstallFilter"/>先过滤再解压校验。
    /// </para>
    /// <para>
    /// 向后兼容硬约束：<c>mods[]</c> 是<b>可选</b>的，只有 <c>files[]</c> 的旧清单必须继续能装
    /// （老版本 <c>build-update-package.ps1</c> 产出的 manifest 就是那种形状）。
    /// </para>
    /// </summary>
    public sealed class ManifestMod
    {
        internal ManifestMod(
            string modId,
            string displayName,
            IList<string> files,
            IList<string> modRelativePaths,
            long bytes,
            string sha256,
            long rows,
            string updatedAt)
        {
            ModId = modId;
            DisplayName = string.IsNullOrEmpty(displayName) ? modId : displayName;
            Files = files ?? new List<string>();
            ModRelativePaths = modRelativePaths ?? new List<string>();
            File = Files.Count > 0 ? Files[0] : string.Empty;
            ModRelativePath = ModRelativePaths.Count > 0 ? ModRelativePaths[0] : string.Empty;
            Bytes = bytes;
            Sha256 = sha256 ?? string.Empty;
            Rows = rows;
            UpdatedAt = updatedAt ?? string.Empty;
        }

        /// <summary>稳定键（包文件名派生，见 <see cref="PackFilter"/>）；一旦发布不许改名。</summary>
        public string ModId { get; private set; }

        /// <summary>给玩家看的名字（缺省回退 <see cref="ModId"/>）。</summary>
        public string DisplayName { get; private set; }

        /// <summary>
        /// 清单里的第一个原始路径（单文件形态就是那个 <c>file</c>；聚合形态是 <c>files[0]</c>）。
        /// 多文件请走 <see cref="Files"/> / <see cref="ModRelativePaths"/>。
        /// </summary>
        public string File { get; private set; }

        /// <summary>相对 mod 根的第一个路径，例如 <c>Packs/zh-cn/TUFX.il.jsonl</c>（聚合形态是 <c>files[0]</c> 解析出来的）。</summary>
        public string ModRelativePath { get; private set; }

        /// <summary>
        /// 该 mod 名下的<b>全部</b>原始路径（清单原样，已归一化 <c>/</c> 分隔）。
        /// <para>
        /// 为什么会有多条：同一个 mod 可能同时有 <c>Foo.jsonl</c> 与 <c>Foo.il.jsonl</c> 两个 channel
        /// 的包（仓库里 97 个包只有 82 个 modId）。清单 v2 允许两种写法：
        /// <c>{"modId":..,"file":"..","bytes":..,"sha256":..}</c>（单文件，老形状）
        /// 或 <c>{"modId":..,"files":["..",".."],"rows":..,"bytes":..}</c>（按 mod 聚合，packs-only 更新包用这个）。
        /// </para>
        /// </summary>
        public IList<string> Files { get; private set; }

        /// <summary>与 <see cref="Files"/> 一一对应的、相对 mod 根的路径。</summary>
        public IList<string> ModRelativePaths { get; private set; }

        /// <summary>该 mod 名下的包文件条数（≥1）。</summary>
        public int FileCount { get { return Files.Count; } }

        /// <summary>解压后的字节数（聚合形态是名下各包之和）。</summary>
        public long Bytes { get; private set; }

        /// <summary>
        /// 解压后内容的 sha256（大写 64 位 hex）。<b>聚合形态允许为空串</b>：多文件没法共用一个哈希，
        /// 那种清单的逐文件校验走顶层 <c>files[]</c>。
        /// </summary>
        public string Sha256 { get; private set; }

        /// <summary>是否登记了 sha256（聚合形态通常没有）。</summary>
        public bool HasSha256 { get { return !string.IsNullOrEmpty(Sha256); } }

        /// <summary>行数（0 = 清单没写；仅用于展示与报告，不参与校验）。</summary>
        public long Rows { get; private set; }

        /// <summary>该 mod 包的更新时间（清单没写则为空串）。</summary>
        public string UpdatedAt { get; private set; }

        /// <inheritdoc/>
        public override string ToString()
        {
            string tail = FileCount > 1
                ? " (+" + (FileCount - 1).ToString(CultureInfo.InvariantCulture) + " more)"
                : string.Empty;
            return ModId + " -> " + ModRelativePath + tail + " (" + Sha256Hex.DescribeBytes(Bytes) + ", rows="
                + Rows.ToString(CultureInfo.InvariantCulture) + ")";
        }
    }

    /// <summary>
    /// 更新清单 <c>manifest.json</c> 的只读视图。
    /// <para>
    /// 兼容 <c>dist/manifest.json</c>（docs/06 §6.7）：顶层 <c>version</c> / <c>zip{name,bytes,sha256}</c> /
    /// <c>files[]{path,bytes,sha256}</c>；<c>builtAt</c> / <c>kspVersion</c> / <c>language</c> 等字段忽略。
    /// </para>
    /// <para>
    /// 安全：<c>files[].path</c> 一律走 <see cref="ZipSafety.TryMapToModRelative"/> 做 zip-slip 校验，
    /// 含 <c>..</c> / 绝对路径 / 盘符的条目会让整份清单被拒绝（而不是被跳过）——清单是可信边界，
    /// 它坏掉就说明服务器或链路有问题，此时绝不能拿它去覆盖本地文件。
    /// </para>
    /// </summary>
    public sealed class UpdateManifest
    {
        private readonly Dictionary<string, ManifestFile> _byModRelative;
        private readonly Dictionary<string, ManifestMod> _byModId;
        private readonly Dictionary<string, string> _modIdByFile;

        private UpdateManifest(
            string version,
            ReleaseVersion parsedVersion,
            string builtAt,
            string language,
            string zipName,
            long zipBytes,
            string zipSha256,
            IList<ManifestFile> files,
            IList<ManifestMod> mods)
        {
            Version = version;
            ParsedVersion = parsedVersion;
            BuiltAt = builtAt;
            Language = language;
            ZipName = zipName;
            ZipBytes = zipBytes;
            ZipSha256 = zipSha256;
            Files = files;

            Mods = mods ?? new List<ManifestMod>();

            _byModRelative = new Dictionary<string, ManifestFile>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < files.Count; i++)
            {
                _byModRelative[files[i].ModRelativePath] = files[i];
            }

            _byModId = new Dictionary<string, ManifestMod>(StringComparer.OrdinalIgnoreCase);
            _modIdByFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Mods.Count; i++)
            {
                ManifestMod mod = Mods[i];
                _byModId[mod.ModId] = mod;
                for (int k = 0; k < mod.ModRelativePaths.Count; k++)
                {
                    // 一个 mod 可能有两个 channel 的包：每一条都要能反查到它，否则更新时会按文件名猜错归属。
                    _modIdByFile[mod.ModRelativePaths[k]] = mod.ModId;
                }
            }
        }

        /// <summary>清单声明的版本串（原样，例如 <c>1.1.0</c>）。</summary>
        public string Version { get; private set; }

        /// <summary>解析后的版本（<see cref="ReleaseVersion.IsValid"/> 恒为 true）。</summary>
        public ReleaseVersion ParsedVersion { get; private set; }

        /// <summary>构造时间（清单里有就带，没有则为空串）。</summary>
        public string BuiltAt { get; private set; }

        /// <summary>语言（清单里有就带，没有则为空串）。</summary>
        public string Language { get; private set; }

        /// <summary>zip 文件名（不含路径）。</summary>
        public string ZipName { get; private set; }

        /// <summary>zip 字节数。</summary>
        public long ZipBytes { get; private set; }

        /// <summary>zip 的 sha256（大写 64 位 hex）。</summary>
        public string ZipSha256 { get; private set; }

        /// <summary>清单里的文件列表（可能为空：旧清单不带 <c>files</c>）。</summary>
        public IList<ManifestFile> Files { get; private set; }

        /// <summary>文件条数。</summary>
        public int FileCount { get { return Files.Count; } }

        /// <summary>按 mod 的记录（v2；旧清单为空列表，永不返回 <c>null</c>）。</summary>
        public IList<ManifestMod> Mods { get; private set; }

        /// <summary>按 mod 的记录条数。</summary>
        public int ModCount { get { return Mods.Count; } }

        /// <summary>清单是否带 <c>mods[]</c>（false = v1 旧清单，安装阶段退回"全装"）。</summary>
        public bool HasMods { get { return Mods.Count > 0; } }

        /// <summary>按 modId 查一条记录（忽略大小写）；没有返回 <c>null</c>。</summary>
        public ManifestMod FindMod(string modId)
        {
            if (string.IsNullOrEmpty(modId))
            {
                return null;
            }

            ManifestMod mod;
            return _byModId.TryGetValue(modId, out mod) ? mod : null;
        }

        /// <summary>
        /// 由包文件的 mod 相对路径反查 modId（<c>mods[]</c> 里有才会命中）；没有返回 <c>null</c>。
        /// </summary>
        public string ModIdOfFile(string modRelativePath)
        {
            if (string.IsNullOrEmpty(modRelativePath))
            {
                return null;
            }

            string modId;
            return _modIdByFile.TryGetValue(modRelativePath, out modId) ? modId : null;
        }

        /// <summary>按 mod 相对路径查一条记录（忽略大小写）；没有返回 <c>null</c>。</summary>
        public ManifestFile Find(string modRelativePath)
        {
            if (string.IsNullOrEmpty(modRelativePath))
            {
                return null;
            }

            ManifestFile file;
            return _byModRelative.TryGetValue(modRelativePath, out file) ? file : null;
        }

        /// <summary>一行摘要（用于日志）。</summary>
        public string Describe()
        {
            string text = "manifest: version=" + Version
                + ", zip=" + ZipName + " (" + Sha256Hex.DescribeBytes(ZipBytes) + ", sha256 " + ZipSha256 + ")"
                + ", files=" + FileCount.ToString(CultureInfo.InvariantCulture);
            if (HasMods)
            {
                text += ", mods=" + ModCount.ToString(CultureInfo.InvariantCulture);
            }

            return text;
        }

        /// <summary>
        /// 本读取器认识的清单形状契约标记（清单顶层 <c>readerContract</c>）。
        /// <para>
        /// 为什么需要它：2026-10-26 的 1.6.0 事故 —— 出包脚本自 v1.2 起就按 <c>files[]</c> 聚合形态写
        /// <c>mods[]</c>，而当时的读取器只认单文件 <c>file</c>，于是游戏里每份清单都被
        /// <c>bad-mod-entry:0: missing-file</c> 整份拒掉，联网更新从 v1.2 到 v1.6 **一次都没生效**。
        /// 两边的断言全在各自语言里（PowerShell 读回自己写的 JSON、C# 读自己造的 fixture），
        /// 谁也不校验对方，所以没人发现。有了这个标记：
        /// 清单声明 <c>readerContract</c>，读取器只接受自己认识的那个值，不认识就报
        /// <c>unsupported-reader-contract</c>（比 missing-file 直白），
        /// 而出包脚本的 reader round-trip 门禁（<c>tools/manifest-roundtrip</c>）会用**这个读取器**
        /// 真解一遍刚产出的清单，两边只要有一边不认就出不了包。
        /// </para>
        /// <para>改契约（例如又加一种形状）= 提升这里的版本号，并同步 <c>build-update-package.ps1</c>。</para>
        /// </summary>
        public const string ReaderContract = "ui18n-manifest-reader/2";

        /// <summary>
        /// 解析清单。任何非法输入返回 false + 原因串（**永不抛异常**）。
        /// </summary>
        public static bool TryParse(string json, out UpdateManifest manifest, out string error)
        {
            manifest = null;
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

            // 形状契约协商（见 ReaderContract 的注释）。旧清单没有这个键 → 照旧解析，不因缺键拒绝。
            string declaredContract = root.GetString("readerContract", null);
            if (!string.IsNullOrEmpty(declaredContract) && !string.Equals(declaredContract, ReaderContract, StringComparison.Ordinal))
            {
                error = "unsupported-reader-contract: " + declaredContract + " (this build reads " + ReaderContract + ")";
                return false;
            }

            string version = root.GetString("version", null);
            ReleaseVersion parsedVersion;
            if (version == null || !ReleaseVersion.TryParse(version, out parsedVersion))
            {
                error = "bad-version: " + (version == null ? "(missing)" : version);
                return false;
            }

            JsonValue zip = root.Get("zip");
            if (zip == null || zip.Kind != JsonKind.Object)
            {
                error = "missing-zip";
                return false;
            }

            string zipName = zip.GetString("name", null);
            string zipNameError = CheckZipName(zipName);
            if (zipNameError != null)
            {
                error = zipNameError;
                return false;
            }

            long zipBytes;
            if (!zip.TryGetLong("bytes", out zipBytes) || zipBytes <= 0)
            {
                error = "bad-zip-bytes";
                return false;
            }

            string zipSha = zip.GetString("sha256", null);
            if (!Sha256Hex.IsHex64(zipSha))
            {
                error = "bad-zip-sha256";
                return false;
            }

            List<ManifestFile> files = new List<ManifestFile>();
            JsonValue filesNode = root.Get("files");
            if (filesNode != null)
            {
                if (filesNode.Kind != JsonKind.Array)
                {
                    error = "files-not-array";
                    return false;
                }

                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                IList<JsonValue> items = filesNode.Items;
                for (int i = 0; i < items.Count; i++)
                {
                    JsonValue item = items[i];
                    string entryError;
                    ManifestFile file = ParseFile(item, i, out entryError);
                    if (file == null)
                    {
                        error = entryError;
                        return false;
                    }

                    if (!seen.Add(file.ModRelativePath))
                    {
                        error = "duplicate-file-path: " + file.ModRelativePath;
                        return false;
                    }

                    files.Add(file);
                }
            }

            List<ManifestMod> mods;
            string modsError;
            if (!TryParseMods(root, out mods, out modsError))
            {
                error = modsError;
                return false;
            }

            manifest = new UpdateManifest(
                version,
                parsedVersion,
                root.GetString("builtAt", string.Empty),
                root.GetString("language", string.Empty),
                zipName,
                zipBytes,
                Sha256Hex.Normalize(zipSha),
                files,
                mods);
            return true;
        }

        private static ManifestFile ParseFile(JsonValue item, int index, out string error)
        {
            error = null;
            string prefix = "bad-file-entry:" + index.ToString(CultureInfo.InvariantCulture);

            if (item == null || item.Kind != JsonKind.Object)
            {
                error = prefix;
                return null;
            }

            string path = item.GetString("path", null);
            if (string.IsNullOrEmpty(path))
            {
                error = prefix + ": missing-path";
                return null;
            }

            string modRelative;
            string reason;
            if (!ZipSafety.TryMapToModRelative(path, out modRelative, out reason))
            {
                error = "unsafe-path:" + index.ToString(CultureInfo.InvariantCulture) + ":" + path + ":" + reason;
                return null;
            }

            if (string.IsNullOrEmpty(modRelative))
            {
                error = prefix + ": empty-path";
                return null;
            }

            long bytes;
            if (!item.TryGetLong("bytes", out bytes) || bytes < 0)
            {
                error = prefix + ": bad-bytes";
                return null;
            }

            string sha256 = item.GetString("sha256", null);
            if (!Sha256Hex.IsHex64(sha256))
            {
                error = prefix + ": bad-sha256";
                return null;
            }

            return new ManifestFile(path, modRelative, bytes, Sha256Hex.Normalize(sha256));
        }

        /// <summary>
        /// 解析可选的 <c>mods[]</c>（v2）。缺字段 = 空列表（旧清单，合法）；形状不对一律整份拒绝。
        /// </summary>
        private static bool TryParseMods(JsonValue root, out List<ManifestMod> mods, out string error)
        {
            mods = new List<ManifestMod>();
            error = null;

            JsonValue node = root.Get("mods");
            if (node == null || node.IsNull)
            {
                return true;
            }

            if (node.Kind != JsonKind.Array)
            {
                error = "mods-not-array";
                return false;
            }

            HashSet<string> seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IList<JsonValue> items = node.Items;
            for (int i = 0; i < items.Count; i++)
            {
                string entryError;
                ManifestMod mod = ParseMod(items[i], i, out entryError);
                if (mod == null)
                {
                    error = entryError;
                    return false;
                }

                if (!seenIds.Add(mod.ModId))
                {
                    error = "duplicate-mod-id: " + mod.ModId;
                    return false;
                }

                for (int k = 0; k < mod.ModRelativePaths.Count; k++)
                {
                    if (!seenFiles.Add(mod.ModRelativePaths[k]))
                    {
                        error = "duplicate-mod-file: " + mod.ModRelativePaths[k];
                        return false;
                    }
                }

                mods.Add(mod);
            }

            return true;
        }

        private static ManifestMod ParseMod(JsonValue item, int index, out string error)
        {
            error = null;
            string prefix = "bad-mod-entry:" + index.ToString(CultureInfo.InvariantCulture);

            if (item == null || item.Kind != JsonKind.Object)
            {
                error = prefix;
                return null;
            }

            string modId = item.GetString("modId", null);
            if (string.IsNullOrEmpty(modId))
            {
                error = prefix + ": missing-mod-id";
                return null;
            }

            if (!PackFilter.IsValidModId(modId))
            {
                error = prefix + ": bad-mod-id";
                return null;
            }

            // 两种合法形态：单文件 file（老形状）/ 按 mod 聚合的 files[]（packs-only 更新包）。
            // 同时给两套字段是歧义（到底信哪个？），一律拒绝，绝不猜。
            JsonValue singleNode = item.Get("file");
            JsonValue multiNode = item.Get("files");
            bool hasSingle = singleNode != null && !singleNode.IsNull;
            bool hasMulti = multiNode != null && !multiNode.IsNull;
            if (hasSingle && hasMulti)
            {
                error = prefix + ": both-file-and-files";
                return null;
            }

            List<string> files = new List<string>();
            List<string> modRelatives = new List<string>();
            if (hasMulti)
            {
                if (multiNode.Kind != JsonKind.Array)
                {
                    error = prefix + ": bad-files";
                    return null;
                }

                IList<JsonValue> items = multiNode.Items;
                if (items.Count == 0)
                {
                    error = prefix + ": empty-files";
                    return null;
                }

                HashSet<string> seenInMod = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < items.Count; i++)
                {
                    string raw = items[i] == null ? null : items[i].AsString();
                    if (string.IsNullOrEmpty(raw))
                    {
                        error = prefix + ": bad-files";
                        return null;
                    }

                    string modRelative;
                    string mapError;
                    if (!TryMapPackEntry(prefix, raw, out modRelative, out mapError))
                    {
                        error = mapError;
                        return null;
                    }

                    if (!seenInMod.Add(modRelative))
                    {
                        error = prefix + ": duplicate-file:" + modRelative;
                        return null;
                    }

                    files.Add(raw);
                    modRelatives.Add(modRelative);
                }
            }
            else
            {
                string file = singleNode == null ? null : singleNode.AsString();
                if (string.IsNullOrEmpty(file))
                {
                    error = prefix + ": missing-file";
                    return null;
                }

                string modRelative;
                string mapError;
                if (!TryMapPackEntry(prefix, file, out modRelative, out mapError))
                {
                    error = mapError;
                    return null;
                }

                files.Add(file);
                modRelatives.Add(modRelative);
            }

            long bytes;
            if (!item.TryGetLong("bytes", out bytes) || bytes < 0)
            {
                error = prefix + ": bad-bytes";
                return null;
            }

            // 单文件形态必须自带 sha256（安装期要逐文件校验）；聚合形态允许多文件共用一个"合计"条目，
            // 那种的逐文件哈希在上层 files[] 里，这里不强制。
            string sha256 = item.GetString("sha256", null);
            if (hasMulti)
            {
                if (!string.IsNullOrEmpty(sha256) && !Sha256Hex.IsHex64(sha256))
                {
                    error = prefix + ": bad-sha256";
                    return null;
                }
            }
            else if (!Sha256Hex.IsHex64(sha256))
            {
                error = prefix + ": bad-sha256";
                return null;
            }

            long rows = 0;
            JsonValue rowsNode = item.Get("rows");
            if (rowsNode != null && !rowsNode.IsNull)
            {
                if (!item.TryGetLong("rows", out rows) || rows < 0)
                {
                    error = prefix + ": bad-rows";
                    return null;
                }
            }

            return new ManifestMod(
                modId,
                item.GetString("displayName", null),
                files,
                modRelatives,
                bytes,
                string.IsNullOrEmpty(sha256) ? string.Empty : Sha256Hex.Normalize(sha256),
                rows,
                item.GetString("updatedAt", string.Empty));
        }

        /// <summary>
        /// 把清单里的一条路径映射成 mod 相对路径，并要求它确实是一个语言包（<c>Packs/&lt;lang&gt;/&lt;file&gt;.jsonl</c>）。
        /// <c>mods[]</c> 只登记包文件：DLL / cfg 不归属任何 mod，混进来要么是构建脚本算错了分组，
        /// 要么是有人在手改清单——两种都必须整份拒绝。
        /// </summary>
        private static bool TryMapPackEntry(string prefix, string path, out string modRelative, out string error)
        {
            error = null;
            modRelative = null;

            string reason;
            if (!ZipSafety.TryMapToModRelative(path, out modRelative, out reason))
            {
                error = prefix + ": unsafe-file:" + path + ":" + reason;
                return false;
            }

            string packLanguage;
            string packFileName;
            string packReason;
            if (!ZipSafety.TryGetPackTarget(modRelative, out packLanguage, out packFileName, out packReason))
            {
                error = prefix + ": not-a-pack-file";
                return false;
            }

            return true;
        }

        private static string CheckZipName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return "bad-zip-name: (missing)";
            }

            if (name.Length > 200)
            {
                return "bad-zip-name: too-long";
            }

            if (name == "." || name == "..")
            {
                return "bad-zip-name: " + name;
            }

            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (c == '/' || c == '\\')
                {
                    return "bad-zip-name: contains-separator";
                }

                if (c < ' ')
                {
                    return "bad-zip-name: control-char";
                }
            }

            return null;
        }
    }
}
