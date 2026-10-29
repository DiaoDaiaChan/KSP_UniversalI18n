using System;
using System.IO;
using System.Security.Cryptography;
using UI18n.Core.Update;

namespace UI18n.Core.Config
{
    /// <summary>
    /// 随包字体（<c>FontMode = Bundled</c>）的清单条目：一个 AssetBundle 文件 + 一条许可记录。
    /// 字段与 <c>packs/fonts/fonts.json</c>（工具 <c>i18n_tools.fontbundle</c> 生成）一一对应。
    /// </summary>
    public sealed class FontBundleManifest
    {
        /// <summary>清单 schema（<see cref="FontBundle.SchemaId"/>）。</summary>
        public string Schema;

        /// <summary>AssetBundle 文件名（只允许纯文件名，禁止路径分隔符）。</summary>
        public string FileName;

        /// <summary>文件内容的 sha256（64 位小写十六进制）。</summary>
        public string Sha256;

        /// <summary>文件字节数。</summary>
        public long Bytes;

        /// <summary>许可证 SPDX 标识（例：<c>OFL-1.1</c>）。</summary>
        public string License;

        /// <summary>随包的许可证文件名（例：<c>OFL-JetBrainsMono.txt</c>）。</summary>
        public string LicenseFile;

        /// <summary>字体族名（例：<c>Noto Sans SC</c>），只用于日志。</summary>
        public string FontName;

        /// <summary>上游来源（URL 或仓库路径），只用于溯源。</summary>
        public string Source;

        /// <summary>子集化说明（保留了哪些码位区间），只用于溯源。</summary>
        public string Subset;

        /// <summary>浅拷贝（清单是纯数据，验证流程会写回，故给调用方一份独立副本）。</summary>
        public FontBundleManifest Clone()
        {
            var copy = new FontBundleManifest();
            copy.Schema = Schema;
            copy.FileName = FileName;
            copy.Sha256 = Sha256;
            copy.Bytes = Bytes;
            copy.License = License;
            copy.LicenseFile = LicenseFile;
            copy.FontName = FontName;
            copy.Source = Source;
            copy.Subset = Subset;
            return copy;
        }
    }

    /// <summary>一次随包字体校验的结果。<b>永不抛异常</b>，失败只给原因码。</summary>
    public sealed class FontBundleCheck
    {
        /// <summary>文件存在、大小与 sha256 都对上（<paramref name="requireLicense"/> 时许可证也在）。</summary>
        public bool Ok;

        /// <summary>失败原因码（见 <see cref="FontBundle"/> 的 Reason 常量）；成功为 <see cref="FontBundle.ReasonNone"/>。</summary>
        public string Reason;

        /// <summary>人读细节（哪个字段/哪个路径不对），只进日志。</summary>
        public string Detail;

        /// <summary>解析出的清单；清单缺失/损坏时为 null。</summary>
        public FontBundleManifest Manifest;

        /// <summary>被校验的文件绝对路径。</summary>
        public string FilePath;

        /// <summary>磁盘上实际的字节数。</summary>
        public long ActualBytes;

        /// <summary>磁盘上实际的 sha256（读不出时为 null）。</summary>
        public string ActualSha256;

        /// <summary>许可证文件绝对路径（清单未给或不要求时为 null）。</summary>
        public string LicensePath;

        /// <summary>一句话日志摘要。</summary>
        public string Describe()
        {
            if (Ok)
            {
                return "font bundle ok: " + (Manifest != null && Manifest.FileName != null ? Manifest.FileName : "?")
                       + ", " + ActualBytes + " bytes, sha256=" + ShortSha(ActualSha256)
                       + (LicensePath != null ? ", license=" + Manifest.License : string.Empty);
            }

            return "font bundle rejected (" + (Reason ?? FontBundle.ReasonNoManifest)
                   + "): " + (Detail ?? string.Empty);
        }

        private static string ShortSha(string sha)
        {
            if (string.IsNullOrEmpty(sha))
            {
                return "?";
            }

            return sha.Length > 12 ? sha.Substring(0, 12) : sha;
        }
    }

    /// <summary>
    /// 随包字体的清单解析与完整性校验（<b>纯 BCL</b>，故可单测；Runtime 只负责载入 AssetBundle）。
    /// <para>
    /// 为什么要有这一层：<c>FontMode = Bundled</c> 载入的是**二进制** AssetBundle，一旦这个文件被换掉
    /// （发布包被中间人改、硬盘上的文件被别的工具覆盖、手动替换试字体），Unity 侧只会给出难懂的
    /// 载入失败或更糟——载入成功但字形不对。sha256 对不上就**不载入**，直接回落到 OsFont 链，
    /// 玩家最多看到系统字体，不会看到方框或崩溃。
    /// </para>
    /// <para>
    /// 红线（与配置口径一致）：<b>配置/数据非法只降级不阻断</b>。清单缺失、字段写错、验不过，
    /// 全部返回 <see cref="FontBundleCheck.Ok"/> = false 并给原因码，调用方据此回落，绝不抛。
    /// </para>
    /// </summary>
    public static class FontBundle
    {
        /// <summary>清单 schema 标识。</summary>
        public const string SchemaId = "ui18n-font-bundle/1";

        /// <summary>清单文件名（与字体文件同目录）。</summary>
        public const string ManifestFileName = "fonts.json";

        /// <summary>体积预算：随包字体不超过 6 MB（发布包每 MB 都要用户下载）。</summary>
        public const long SizeBudget = 6L * 1024 * 1024;

        /// <summary>成功。</summary>
        public const string ReasonNone = "";

        /// <summary>同目录没有清单文件。</summary>
        public const string ReasonNoManifest = "no-manifest";

        /// <summary>清单不是合法 JSON，或字段缺失/取值非法。</summary>
        public const string ReasonBadManifest = "bad-manifest";

        /// <summary>清单里的文件名不安全（含路径分隔符、<c>..</c>、盘符）。</summary>
        public const string ReasonUnsafeName = "unsafe-name";

        /// <summary>字体文件不在盘上。</summary>
        public const string ReasonMissing = "missing";

        /// <summary>超过 <see cref="SizeBudget"/>。</summary>
        public const string ReasonTooLarge = "too-large";

        /// <summary>字节数与清单不符。</summary>
        public const string ReasonSizeMismatch = "size-mismatch";

        /// <summary>sha256 与清单不符（文件被换过）。</summary>
        public const string ReasonSha256Mismatch = "sha256-mismatch";

        /// <summary>要求许可证但清单没登记，或登记的文件不存在。</summary>
        public const string ReasonNoLicense = "no-license";

        /// <summary>文件名是否安全：非空、≤ 200 字符、不是 <c>.</c>/<c>..</c>、不含 <c>/</c>、<c>\</c>、<c>:</c> 与非法字符。</summary>
        public static bool IsSafeFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName) || fileName.Length > 200)
            {
                return false;
            }

            if (fileName == "." || fileName == "..")
            {
                return false;
            }

            if (fileName.IndexOf('/') >= 0 || fileName.IndexOf('\\') >= 0 || fileName.IndexOf(':') >= 0)
            {
                return false;
            }

            return fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }

        /// <summary>
        /// 解析清单 JSON。成功返回清单对象（<paramref name="error"/> = null），失败返回 null 并给出原因。
        /// 必填字段：schema（必须是 <see cref="SchemaId"/>）、file、sha256（64 位十六进制）、bytes（&gt; 0）。
        /// </summary>
        public static FontBundleManifest ParseManifest(string json, out string error)
        {
            error = null;

            if (string.IsNullOrEmpty(json))
            {
                error = ReasonBadManifest + ": empty json";
                return null;
            }

            JsonValue root;
            string jsonError;
            if (!Json.TryParse(json, out root, out jsonError))
            {
                error = ReasonBadManifest + ": " + jsonError;
                return null;
            }

            // 允许两种形态：单条清单（顶层就是字段）或 {"fonts": [...]}（首个条目，方便以后放多套字体）。
            JsonValue item = root;
            if (root != null && root.Kind == JsonKind.Object)
            {
                JsonValue fonts = root.Get("fonts");
                if (fonts != null && fonts.Kind == JsonKind.Array && fonts.Count > 0)
                {
                    item = fonts.Items[0];
                }
            }

            if (item == null || item.Kind != JsonKind.Object)
            {
                error = ReasonBadManifest + ": 顶层既不是对象也没有非空的 fonts 数组";
                return null;
            }

            var manifest = new FontBundleManifest();
            manifest.Schema = item.GetString("schema", null);
            manifest.FileName = FirstNonEmpty(item.GetString("file", null), item.GetString("fileName", null));
            manifest.Sha256 = Sha256Hex.Normalize(item.GetString("sha256", null));
            manifest.License = FirstNonEmpty(item.GetString("license", null), item.GetString("licenseSpdx", null));
            manifest.LicenseFile = FirstNonEmpty(item.GetString("licenseFile", null), item.GetString("license_file", null));
            manifest.FontName = item.GetString("fontName", null);
            manifest.Source = item.GetString("source", null);
            manifest.Subset = item.GetString("subset", null);

            long bytes;
            manifest.Bytes = item.TryGetLong("bytes", out bytes) ? bytes : -1;

            if (manifest.Schema != SchemaId)
            {
                error = ReasonBadManifest + ": schema=\"" + (manifest.Schema ?? "(missing)") + "\"，期望 " + SchemaId;
                return null;
            }

            // 空名 / 带路径 / 带盘符一律归到 unsafe-name：调用方只需要知道「这个清单不能用来拼路径」。
            if (!IsSafeFileName(manifest.FileName))
            {
                error = ReasonUnsafeName + ": file=\"" + manifest.FileName + "\"";
                return null;
            }

            if (!Sha256Hex.IsHex64(manifest.Sha256))
            {
                error = ReasonBadManifest + ": sha256=\"" + (manifest.Sha256 ?? "(missing)") + "\"，期望 64 位十六进制";
                return null;
            }

            if (manifest.Bytes <= 0)
            {
                error = ReasonBadManifest + ": bytes=" + manifest.Bytes + "，期望正数";
                return null;
            }

            return manifest;
        }

        /// <summary>从目录读 <see cref="ManifestFileName"/>。读不到返回 null（原因写进 <paramref name="error"/>）。</summary>
        public static FontBundleManifest LoadManifest(string directory, out string error)
        {
            error = null;

            if (string.IsNullOrEmpty(directory))
            {
                error = ReasonNoManifest + ": directory is empty";
                return null;
            }

            string path;
            string text;
            try
            {
                path = Path.Combine(directory, ManifestFileName);
                if (!File.Exists(path))
                {
                    error = ReasonNoManifest + ": " + path;
                    return null;
                }

                text = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                error = ReasonNoManifest + ": " + ex.GetType().Name + ": " + ex.Message;
                return null;
            }

            return ParseManifest(text, out error);
        }

        /// <summary>
        /// 校验目录里的随包字体：清单 → 文件名安全 → 存在 → 体积预算 → 字节数 → sha256（→ 许可证）。
        /// </summary>
        /// <param name="directory">字体文件与清单所在目录。</param>
        /// <param name="requireLicense">
        /// true 时许可证必须登记且文件存在（发布/CI 口径）；false 时只警告不拒绝（运行时口径：
        /// 许可证缺失是发布流程问题，不该让玩家看到「字体不可用」）。
        /// </param>
        public static FontBundleCheck Check(string directory, bool requireLicense)
        {
            var check = new FontBundleCheck();
            check.Ok = false;
            check.Reason = ReasonNone;

            string error;
            FontBundleManifest manifest = LoadManifest(directory, out error);
            if (manifest == null)
            {
                check.Reason = ClassifyManifestError(error);
                check.Detail = error;
                return check;
            }

            check.Manifest = manifest;

            string path;
            try
            {
                path = Path.Combine(directory, manifest.FileName);
            }
            catch (Exception ex)
            {
                check.Reason = ReasonUnsafeName;
                check.Detail = ex.GetType().Name + ": " + ex.Message;
                return check;
            }

            check.FilePath = path;

            long length = -1;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    check.Reason = ReasonMissing;
                    check.Detail = "字库文件不在盘上：" + path;
                    return check;
                }

                length = info.Length;
                check.ActualBytes = length;
            }
            catch (Exception ex)
            {
                check.Reason = ReasonMissing;
                check.Detail = ex.GetType().Name + ": " + ex.Message;
                return check;
            }

            if (length > SizeBudget)
            {
                check.Reason = ReasonTooLarge;
                check.Detail = length + " bytes > 预算 " + SizeBudget;
                return check;
            }

            if (length != manifest.Bytes)
            {
                check.Reason = ReasonSizeMismatch;
                check.Detail = "磁盘 " + length + " bytes，清单 " + manifest.Bytes;
                return check;
            }

            string actual = Sha256OfFile(path);
            check.ActualSha256 = actual;
            if (actual == null)
            {
                check.Reason = ReasonMissing;
                check.Detail = "读不出内容（权限或 IO 错误）：" + path;
                return check;
            }

            if (!Sha256Hex.EqualsIgnoreCase(actual, manifest.Sha256))
            {
                check.Reason = ReasonSha256Mismatch;
                check.Detail = "磁盘 " + actual + "，清单 " + manifest.Sha256;
                return check;
            }

            if (requireLicense)
            {
                if (string.IsNullOrEmpty(manifest.License) || string.IsNullOrEmpty(manifest.LicenseFile))
                {
                    check.Reason = ReasonNoLicense;
                    check.Detail = "清单未登记 license/licenseFile";
                    return check;
                }

                if (!IsSafeFileName(manifest.LicenseFile))
                {
                    check.Reason = ReasonUnsafeName;
                    check.Detail = "licenseFile=\"" + manifest.LicenseFile + "\"";
                    return check;
                }

                string licensePath = Path.Combine(directory, manifest.LicenseFile);
                check.LicensePath = licensePath;
                if (!File.Exists(licensePath))
                {
                    check.Reason = ReasonNoLicense;
                    check.Detail = "许可证文件不存在：" + licensePath;
                    return check;
                }
            }
            else if (!string.IsNullOrEmpty(manifest.LicenseFile) && IsSafeFileName(manifest.LicenseFile))
            {
                string licensePath = Path.Combine(directory, manifest.LicenseFile);
                check.LicensePath = File.Exists(licensePath) ? licensePath : null;
            }

            check.Ok = true;
            return check;
        }

        /// <summary>把 <see cref="LoadManifest"/> 的错误串归到稳定的原因码上（调用方只认码，不解析文本）。</summary>
        private static string ClassifyManifestError(string error)
        {
            if (error == null)
            {
                return ReasonNoManifest;
            }

            if (error.StartsWith(ReasonUnsafeName, StringComparison.Ordinal))
            {
                return ReasonUnsafeName;
            }

            if (error.StartsWith(ReasonBadManifest, StringComparison.Ordinal))
            {
                return ReasonBadManifest;
            }

            return ReasonNoManifest;
        }

        /// <summary>文件读 + sha256（流式，不整份读进内存；读失败返回 null，永不抛）。</summary>
        public static string Sha256OfFile(string path)
        {
            try
            {
                using (FileStream stream = File.OpenRead(path))
                using (SHA256 sha = SHA256.Create())
                {
                    return Sha256Hex.ToHex(sha.ComputeHash(stream));
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string FirstNonEmpty(string a, string b)
        {
            if (!string.IsNullOrEmpty(a))
            {
                return a;
            }

            return b;
        }
    }
}
