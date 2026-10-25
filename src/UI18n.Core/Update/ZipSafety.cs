using System;
using System.Collections.Generic;
using System.Text;

namespace UI18n.Core.Update
{
    /// <summary>
    /// zip 条目路径的**唯一**安全闸门：zip-slip（<c>..</c>）、绝对路径、盘符、UNC、控制字符全部拒绝，
    /// 并且只把 <c>Packs/&lt;lang&gt;/&lt;file&gt;.jsonl</c> 认定为「可以安装的语言包」。
    /// <para>
    /// 为什么单独一个类：这条逻辑要被单测逐个用例钉死，而解压/写盘在 Runtime。
    /// 语义上它是**纯字符串**判断——不碰文件系统、不抛异常、失败一律给原因码。
    /// </para>
    /// <para>
    /// zip 里的条目名来自 <c>build-release.ps1</c>，形如 <c>GameData/UI18n/Packs/zh-cn/Squad.jsonl</c>；
    /// 也接受已经去掉 <c>GameData/UI18n/</c> 前缀的 <c>Packs/zh-cn/Squad.jsonl</c>（两种写法都验过）。
    /// </para>
    /// </summary>
    public static class ZipSafety
    {
        /// <summary>发布包内 mod 根目录前缀（带结尾斜杠）。</summary>
        public const string ModRootPrefix = "GameData/UI18n/";

        /// <summary>语言包目录段名。</summary>
        public const string PacksSegment = "Packs";

        /// <summary>语言包扩展名（比较时忽略大小写）。</summary>
        public const string PackExtension = ".jsonl";

        /// <summary>以下划线开头的条目名视为「开发期产物」，一律不安装（例如 <c>_smoke.jsonl</c>）。</summary>
        public const char ReservedPrefix = '_';

        /// <summary>空路径。</summary>
        public const string ReasonEmpty = "empty";
        /// <summary>以 <c>/</c> 开头（绝对路径）。</summary>
        public const string ReasonAbsolute = "absolute";
        /// <summary>带盘符（<c>C:</c>）。</summary>
        public const string ReasonDrive = "drive-letter";
        /// <summary>UNC（<c>//server/share</c>）。</summary>
        public const string ReasonUnc = "unc";
        /// <summary>含 <c>..</c> 段（zip-slip）。</summary>
        public const string ReasonParent = "parent-dir";
        /// <summary>含控制字符（<c>\0</c>、换行等）。</summary>
        public const string ReasonControlChar = "control-char";
        /// <summary>含空段（<c>a//b</c>、结尾斜杠）。</summary>
        public const string ReasonEmptySegment = "empty-segment";
        /// <summary>不是 <c>Packs/&lt;lang&gt;/&lt;file&gt;.jsonl</c> 形态。</summary>
        public const string ReasonNotPack = "not-a-pack-file";
        /// <summary>语言目录名非法。</summary>
        public const string ReasonBadLanguage = "bad-language";
        /// <summary>包文件名非法。</summary>
        public const string ReasonBadFileName = "bad-file-name";
        /// <summary>以 <c>_</c> 开头的保留名（<c>_smoke.jsonl</c> 这类联调产物）。</summary>
        public const string ReasonReservedName = "reserved-name";

        /// <summary>语言目录名长度上限。</summary>
        public const int MaxLanguageLength = 16;

        /// <summary>包文件名长度上限（含扩展名）。</summary>
        public const int MaxFileNameLength = 128;

        /// <summary>条目路径是否安全（能做 zip-slip 校验通过）。失败原因用 <see cref="TryNormalize"/> 取。</summary>
        public static bool IsSafeRelativePath(string entry)
        {
            string normalized;
            string reason;
            return TryNormalize(entry, out normalized, out reason);
        }

        /// <summary>
        /// 把 zip 条目名归一化成 <c>/</c> 分隔的相对路径（去掉 <c>./</c> 段）。
        /// 失败返回 false 并给出原因码（<b>不抛</b>）。
        /// </summary>
        public static bool TryNormalize(string entry, out string normalized, out string reason)
        {
            normalized = null;
            reason = null;

            if (entry == null || entry.Length == 0)
            {
                reason = ReasonEmpty;
                return false;
            }

            for (int i = 0; i < entry.Length; i++)
            {
                char c = entry[i];
                if (c < ' ' || c == (char)0x7F)
                {
                    reason = ReasonControlChar;
                    return false;
                }
            }

            string s = entry.Replace('\\', '/');

            if (s.Length >= 2 && s[0] == '/' && s[1] == '/')
            {
                reason = ReasonUnc;
                return false;
            }

            if (s[0] == '/')
            {
                reason = ReasonAbsolute;
                return false;
            }

            if (s.Length >= 2 && IsAsciiLetter(s[0]) && s[1] == ':')
            {
                reason = ReasonDrive;
                return false;
            }

            string[] parts = s.Split('/');
            List<string> kept = new List<string>(parts.Length);
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (part.Length == 0)
                {
                    reason = ReasonEmptySegment;
                    return false;
                }

                if (part == ".")
                {
                    continue;
                }

                if (part == "..")
                {
                    reason = ReasonParent;
                    return false;
                }

                kept.Add(part);
            }

            if (kept.Count == 0)
            {
                reason = ReasonEmpty;
                return false;
            }

            StringBuilder sb = new StringBuilder(entry.Length);
            for (int i = 0; i < kept.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append('/');
                }

                sb.Append(kept[i]);
            }

            normalized = sb.ToString();
            return true;
        }

        /// <summary>
        /// 归一化并去掉可选的 <c>GameData/UI18n/</c> 前缀，得到「相对 mod 根目录」的路径
        /// （例如 <c>Packs/zh-cn/Squad.jsonl</c>）。失败给原因码。
        /// </summary>
        public static bool TryMapToModRelative(string entry, out string modRelative, out string reason)
        {
            modRelative = null;

            string normalized;
            if (!TryNormalize(entry, out normalized, out reason))
            {
                return false;
            }

            if (normalized.StartsWith(ModRootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(ModRootPrefix.Length);
            }

            if (normalized.Length == 0)
            {
                reason = ReasonEmpty;
                return false;
            }

            modRelative = normalized;
            return true;
        }

        /// <summary>
        /// 判断「相对 mod 根目录」的路径能否安装：必须是 <c>Packs/&lt;lang&gt;/&lt;file&gt;.jsonl</c>（正好三层）。
        /// 成功给出语言目录名与文件名。
        /// </summary>
        public static bool TryGetPackTarget(string modRelative, out string language, out string fileName, out string reason)
        {
            language = null;
            fileName = null;
            reason = null;

            if (modRelative == null || modRelative.Length == 0)
            {
                reason = ReasonEmpty;
                return false;
            }

            string[] parts = modRelative.Split('/');
            if (parts.Length != 3 || !string.Equals(parts[0], PacksSegment, StringComparison.OrdinalIgnoreCase))
            {
                reason = ReasonNotPack;
                return false;
            }

            if (!IsValidLanguageTag(parts[1]))
            {
                reason = ReasonBadLanguage;
                return false;
            }

            if (!IsValidPackFileName(parts[2]))
            {
                reason = ReasonBadFileName;
                return false;
            }

            if (parts[2][0] == ReservedPrefix)
            {
                reason = ReasonReservedName;
                return false;
            }

            language = parts[1];
            fileName = parts[2];
            return true;
        }

        /// <summary>
        /// 语言目录名是否合法：2~16 个字符，首字符是字母，其余是字母/数字/连字符，不能以连字符结尾、不能有连续连字符。
        /// 例：<c>zh-cn</c>、<c>en-us</c>、<c>zh</c>；非法：<c>cn</c> 之后带空格、<c>..</c>、<c>zh_cn</c>。
        /// </summary>
        public static bool IsValidLanguageTag(string tag)
        {
            if (tag == null)
            {
                return false;
            }

            if (tag.Length < 2 || tag.Length > MaxLanguageLength)
            {
                return false;
            }

            if (!IsAsciiLetter(tag[0]))
            {
                return false;
            }

            if (tag[tag.Length - 1] == '-')
            {
                return false;
            }

            bool previousDash = false;
            for (int i = 1; i < tag.Length; i++)
            {
                char c = tag[i];
                bool isDash = c == '-';
                if (isDash)
                {
                    if (previousDash)
                    {
                        return false;
                    }
                }
                else if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')))
                {
                    return false;
                }

                previousDash = isDash;
            }

            return true;
        }

        /// <summary>
        /// 包文件名是否合法：以 <c>.jsonl</c> 结尾（忽略大小写），主干 1~120 字符，
        /// 只含字母/数字/<c>_ -</c>，不以 <c>.</c> 开头、不含 <c>..</c>（<c>_smoke.jsonl</c> 语法合法，由保留名规则单独拒）。
        /// </summary>
        public static bool IsValidPackFileName(string name)
        {
            if (name == null || name.Length <= PackExtension.Length || name.Length > MaxFileNameLength)
            {
                return false;
            }

            if (!name.EndsWith(PackExtension, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (name[0] == '.')
            {
                return false;
            }

            if (name.IndexOf("..", StringComparison.Ordinal) >= 0)
            {
                return false;
            }

            string stem = name.Substring(0, name.Length - PackExtension.Length);
            if (stem.Length == 0)
            {
                return false;
            }

            for (int i = 0; i < stem.Length; i++)
            {
                char c = stem[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                          || c == '_' || c == '-' || c == '.';
                if (!ok)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsAsciiLetter(char c)
        {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
        }
    }
}
