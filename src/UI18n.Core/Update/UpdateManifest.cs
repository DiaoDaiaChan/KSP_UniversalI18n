using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

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

        private UpdateManifest(
            string version,
            ReleaseVersion parsedVersion,
            string builtAt,
            string language,
            string zipName,
            long zipBytes,
            string zipSha256,
            IList<ManifestFile> files)
        {
            Version = version;
            ParsedVersion = parsedVersion;
            BuiltAt = builtAt;
            Language = language;
            ZipName = zipName;
            ZipBytes = zipBytes;
            ZipSha256 = zipSha256;
            Files = files;

            _byModRelative = new Dictionary<string, ManifestFile>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < files.Count; i++)
            {
                _byModRelative[files[i].ModRelativePath] = files[i];
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
            return "manifest: version=" + Version
                + ", zip=" + ZipName + " (" + Sha256Hex.DescribeBytes(ZipBytes) + ", sha256 " + ZipSha256 + ")"
                + ", files=" + FileCount.ToString(CultureInfo.InvariantCulture);
        }

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

            manifest = new UpdateManifest(
                version,
                parsedVersion,
                root.GetString("builtAt", string.Empty),
                root.GetString("language", string.Empty),
                zipName,
                zipBytes,
                Sha256Hex.Normalize(zipSha),
                files);
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
