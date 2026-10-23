using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UI18n.Core.Dict
{
    /// <summary>
    /// 包目录解析状态。Runtime 用它决定日志级别：<see cref="Found"/> 不吵，其余 Warn 一次
    /// （红线 6：失败只 log 不抛，但必须能被计数）。
    /// </summary>
    public enum PackDirStatus
    {
        /// <summary>命中语言目录。目录里可能一个 *.jsonl 都没有（看 <see cref="PackResolution.HasPacks"/>）。</summary>
        Found = 0,

        /// <summary>Packs 根目录不存在（字典没装 / 装错位置）。</summary>
        PacksRootMissing = 1,

        /// <summary>根存在，但没有该语言的目录；语言串为空 / 空白时也是这个状态（不猜）。</summary>
        LanguageDirMissing = 2,
    }

    /// <summary>
    /// 一次包目录解析的全部结果（不可变）。诊断字段（<see cref="AvailableLanguageDirs"/>、
    /// <see cref="FlatFileCount"/>）直接进日志，Runtime 不再自己拼文案。
    /// <para>
    /// 字段名以 T19 任务书为准（Status / SelectedLanguage / Directory / Files / Candidates）；
    /// 提案 PROPOSAL-PackLocator.md 里的旧名 <see cref="ChosenDir"/> / <see cref="PackFiles"/>
    /// 保留为别名，语义完全等价。
    /// </para>
    /// <para>
    /// 数组属性按约定视为只读：不要改它们（改了自己负责）。要改就重新 <see cref="PackLocator.Resolve"/>。
    /// </para>
    /// </summary>
    public sealed class PackResolution
    {
        internal PackResolution(
            string packsRoot,
            string language,
            string normalizedLanguage,
            string[] candidates,
            PackDirStatus status,
            string selectedLanguage,
            string directory,
            string[] files,
            string[] availableLanguageDirs,
            int flatFileCount)
        {
            PacksRoot = packsRoot;
            Language = language;
            NormalizedLanguage = normalizedLanguage ?? string.Empty;
            Candidates = candidates ?? PackLocator.EmptyStrings;
            Status = status;
            SelectedLanguage = selectedLanguage;
            Directory = directory;
            Files = files ?? PackLocator.EmptyStrings;
            AvailableLanguageDirs = availableLanguageDirs ?? PackLocator.EmptyStrings;
            FlatFileCount = flatFileCount;
        }

        /// <summary>调用方传入的根目录（原样回显，便于日志对账）。</summary>
        public string PacksRoot { get; private set; }

        /// <summary>调用方传入的语言串（原样，可能是 null / 空白）。</summary>
        public string Language { get; private set; }

        /// <summary>归一化后的语言（小写、下划线换连字符、去首尾空白）；无语言时为空串（永不为 null）。</summary>
        public string NormalizedLanguage { get; private set; }

        /// <summary>候选目录名，按优先级排列（例如 zh-cn 得到 ["zh-cn","zh"]）；无语言时为空数组。</summary>
        public string[] Candidates { get; private set; }

        /// <summary>解析状态。</summary>
        public PackDirStatus Status { get; private set; }

        /// <summary>
        /// 命中的目录名，取磁盘上的真实大小写（例如磁盘上是 ZH-CN 就返回 ZH-CN）；
        /// 未命中为 null。
        /// </summary>
        public string SelectedLanguage { get; private set; }

        /// <summary>命中目录的路径（传入根 + 磁盘真实目录名的拼接）；未命中为 null。</summary>
        public string Directory { get; private set; }

        /// <summary>
        /// 命中目录下的 *.jsonl 绝对路径，按文件名 ordinal 升序；未命中为空数组。
        /// 不含 0 字节文件、不含非 .jsonl 后缀、不递归子目录。
        /// </summary>
        public string[] Files { get; private set; }

        /// <summary>诊断用：根下实际存在的语言目录名，ordinal 升序，最多 8 个。根不存在时为空数组。</summary>
        public string[] AvailableLanguageDirs { get; private set; }

        /// <summary>
        /// 诊断用：根下被忽略的平铺 *.jsonl 个数（不管大小）。
        /// 缺陷②（deploy 把 pool.jsonl 平铺拷进 Packs 根）的常驻自检：大于 0 就该 Warn。
        /// </summary>
        public int FlatFileCount { get; private set; }

        /// <summary>命中目录里是否有可读的包文件。</summary>
        public bool HasPacks
        {
            get { return Files.Length > 0; }
        }

        /// <summary>提案里的旧名，等价于 <see cref="Directory"/>。</summary>
        public string ChosenDir
        {
            get { return Directory; }
        }

        /// <summary>提案里的旧名，等价于 <see cref="Files"/>。</summary>
        public string[] PackFiles
        {
            get { return Files; }
        }

        /// <summary>
        /// 一行诊断（给 Runtime 打日志用，确定性输出，可直接断言）。
        /// 形如：packs: status=Found language=zh-cn normalized=zh-cn candidates=[zh-cn,zh]
        /// dir=C:\KSP\GameData\UI18n\Packs\zh-cn files=2 flat=0 available=[zh-cn,zh]
        /// </summary>
        public string Describe()
        {
            StringBuilder sb = new StringBuilder(160);
            sb.Append("packs: status=").Append(Status.ToString());
            sb.Append(" language=").Append(Quoted(Language));
            sb.Append(" normalized=").Append(Quoted(NormalizedLanguage));
            sb.Append(" candidates=").Append(JoinList(Candidates));
            sb.Append(" dir=").Append(Quoted(Directory));
            sb.Append(" files=").Append(Files.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(" flat=").Append(FlatFileCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(" available=").Append(JoinList(AvailableLanguageDirs));
            return sb.ToString();
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return Describe();
        }

        private static string Quoted(string value)
        {
            return string.IsNullOrEmpty(value) ? "(none)" : value;
        }

        private static string JoinList(string[] items)
        {
            if (items == null || items.Length == 0)
            {
                return "[]";
            }

            StringBuilder sb = new StringBuilder(64);
            sb.Append('[');
            for (int i = 0; i < items.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                sb.Append(items[i]);
            }

            sb.Append(']');
            return sb.ToString();
        }
    }

    /// <summary>
    /// 包目录定位器（D35 契约：字典放在 <c>Packs/&lt;lang&gt;/&lt;modId&gt;.jsonl</c>）。
    /// <para>
    /// 存在的理由是一次真实事故：旧 Runtime 用 <c>TopDirectoryOnly</c> 平铺读 <c>Packs/*.jsonl</c>，
    /// 于是按契约放的包一条都读不到、字典恒空、patch 恒 0 —— 而这个 bug 只在装进游戏后才暴露，
    /// 因为它所在的层依赖游戏侧程序集，在本机根本没法单测。这段逻辑本身是**纯路径计算**，
    /// 搬进 Core 之后优先级规则、回退规则、平铺文件被忽略这三件事都能被单测穷举。
    /// </para>
    /// <para>
    /// 硬约束（违反即返工）：只用 BCL（System / System.IO），net472 与 net10.0 双目标都能编译，
    /// C# 9，不用 Span / 不用 System.IO.Enumeration / 不用 string.Contains(char)；
    /// 所有键比较走 <see cref="StringComparer.Ordinal"/>（文化敏感比较会踩土耳其 i 之类的地雷）；
    /// **任何输入都不抛异常**（Runtime 在 Startup.Instantly 阶段调用，抛异常等于把游戏带崩），
    /// 失败原因一律通过 <see cref="PackDirStatus"/> + 诊断字段返回，由调用方计数与打日志。
    /// </para>
    /// </summary>
    public static class PackLocator
    {
        /// <summary>共用的空数组（避免每次分配）。调用方不要往里写。</summary>
        internal static readonly string[] EmptyStrings = new string[0];

        private const string JsonlPattern = "*.jsonl";
        private const string JsonlExtension = ".jsonl";
        private const int MaxAvailableDirs = 8;

        /// <summary>
        /// 归一化语言串：Trim → 下划线换连字符 → ToLowerInvariant。
        /// null / 空串 / 纯空白一律返回 <see cref="string.Empty"/>（不返回 null，调用方少写一个分支）。
        /// </summary>
        public static string NormalizeLanguage(string language)
        {
            if (string.IsNullOrEmpty(language))
            {
                return string.Empty;
            }

            string trimmed = language.Trim();
            if (trimmed.Length == 0)
            {
                return string.Empty;
            }

            if (trimmed.IndexOf('_') >= 0)
            {
                trimmed = trimmed.Replace('_', '-');
            }

            return trimmed.ToLowerInvariant();
        }

        /// <summary>
        /// 候选目录名，按优先级：完整标签在前、主语言在后（<c>zh-cn</c> → <c>["zh-cn","zh"]</c>）。
        /// 多段标签只回退一次（<c>zh-cn-x-foo</c> → <c>["zh-cn-x-foo","zh"]</c>），不逐级剥；
        /// 无语言时返回空数组。
        /// </summary>
        public static IReadOnlyList<string> LanguageCandidates(string language)
        {
            return LanguageCandidatesCore(language);
        }

        /// <summary>
        /// **纯函数，不碰磁盘**：给定根下已存在的目录名，返回该用哪个（返回磁盘上的真实写法）。
        /// 逐个候选匹配：先 Ordinal 精确命中，再 OrdinalIgnoreCase 兜底；全不中返回 null。
        /// 逐候选而不是「先全精确再全忽略大小写」，是为了让更具体的候选优先于更宽松的匹配。
        /// </summary>
        public static string SelectLanguageDir(IReadOnlyList<string> existingDirNames, string language)
        {
            if (existingDirNames == null || existingDirNames.Count == 0)
            {
                return null;
            }

            string[] candidates = LanguageCandidatesCore(language);
            return MatchDirectory(existingDirNames, candidates);
        }

        /// <summary>
        /// I/O 包装版：扫 <paramref name="packsRoot"/> 下的子目录，按 <paramref name="candidates"/>
        /// 的顺序选一个。找不到根 → <see cref="PackDirStatus.PacksRootMissing"/>；
        /// 没有匹配目录 / 候选为空 → <see cref="PackDirStatus.LanguageDirMissing"/>。永不抛异常。
        /// </summary>
        public static PackDirStatus SelectLanguageDir(string packsRoot, IEnumerable<string> candidates, out string selectedDir)
        {
            selectedDir = null;

            string[] existingDirNames = TryGetSubDirectoryNames(packsRoot);
            if (existingDirNames == null)
            {
                return PackDirStatus.PacksRootMissing;
            }

            if (candidates == null)
            {
                return PackDirStatus.LanguageDirMissing;
            }

            List<string> wanted = new List<string>();
            foreach (string candidate in candidates)
            {
                if (!string.IsNullOrEmpty(candidate))
                {
                    wanted.Add(candidate);
                }
            }

            if (wanted.Count == 0)
            {
                return PackDirStatus.LanguageDirMissing;
            }

            string chosen = MatchDirectory(existingDirNames, wanted);
            if (chosen == null)
            {
                return PackDirStatus.LanguageDirMissing;
            }

            selectedDir = CombineRootAndName(packsRoot, chosen);
            return PackDirStatus.Found;
        }

        /// <summary>
        /// 列出某个语言目录下的 *.jsonl（<c>TopDirectoryOnly</c>，不递归），按文件名 ordinal 升序。
        /// 跳过 0 字节文件与读不到大小的文件；跳过非 .jsonl 后缀（防 Windows 短名通配误匹配）。
        /// 目录不存在 / 无权限 / 参数非法 → 空数组，永不抛异常。
        /// </summary>
        public static IReadOnlyList<string> ListPackFiles(string languageDir)
        {
            return ListPackFilesCore(languageDir);
        }

        /// <summary>便捷版：<c>Resolve(packsRoot, language).Files</c>。</summary>
        public static IReadOnlyList<string> ListPackFiles(string packsRoot, string language)
        {
            return Resolve(packsRoot, language).Files;
        }

        /// <summary>
        /// 一次解析：候选、状态、命中目录、包文件、诊断字段全给全。永不抛异常。
        /// </summary>
        public static PackResolution Resolve(string packsRoot, string language)
        {
            string normalized = NormalizeLanguage(language);
            string[] candidates = LanguageCandidatesCore(language);
            string[] existingDirNames = TryGetSubDirectoryNames(packsRoot);
            int flatFileCount = CountFlatJsonl(packsRoot);

            if (existingDirNames == null)
            {
                return new PackResolution(
                    packsRoot, language, normalized, candidates,
                    PackDirStatus.PacksRootMissing,
                    null, null, EmptyStrings, EmptyStrings, flatFileCount);
            }

            string[] available = SortAndCap(existingDirNames, MaxAvailableDirs);

            if (candidates.Length == 0)
            {
                return new PackResolution(
                    packsRoot, language, normalized, candidates,
                    PackDirStatus.LanguageDirMissing,
                    null, null, EmptyStrings, available, flatFileCount);
            }

            string chosen = MatchDirectory(existingDirNames, candidates);
            if (chosen == null)
            {
                return new PackResolution(
                    packsRoot, language, normalized, candidates,
                    PackDirStatus.LanguageDirMissing,
                    null, null, EmptyStrings, available, flatFileCount);
            }

            string dir = CombineRootAndName(packsRoot, chosen);
            string[] files = ListPackFilesCore(dir);
            return new PackResolution(
                packsRoot, language, normalized, candidates,
                PackDirStatus.Found,
                chosen, dir, files, available, flatFileCount);
        }

        // ---------------------------------------------------------------- 内部实现

        /// <summary>
        /// 候选数组（内部用数组，避免 IReadOnlyList 包装层）。顺序与去重规则见 <see cref="LanguageCandidates"/>。
        /// </summary>
        internal static string[] LanguageCandidatesCore(string language)
        {
            string normalized = NormalizeLanguage(language);
            if (normalized.Length == 0)
            {
                return EmptyStrings;
            }

            int dash = normalized.IndexOf('-');
            if (dash <= 0)
            {
                // 没有 '-'，或首字符就是 '-'：只有完整标签一个候选（不猜）。
                return new string[] { normalized };
            }

            string primary = normalized.Substring(0, dash);
            if (primary.Length == 0 || string.Equals(primary, normalized, StringComparison.Ordinal))
            {
                return new string[] { normalized };
            }

            return new string[] { normalized, primary };
        }

        /// <summary>逐候选匹配：先 Ordinal 精确，再 OrdinalIgnoreCase 兜底。返回磁盘上的真实写法。</summary>
        private static string MatchDirectory(IReadOnlyList<string> existingDirNames, IReadOnlyList<string> candidates)
        {
            if (existingDirNames == null || candidates == null)
            {
                return null;
            }

            for (int c = 0; c < candidates.Count; c++)
            {
                string candidate = candidates[c];
                if (string.IsNullOrEmpty(candidate))
                {
                    continue;
                }

                for (int i = 0; i < existingDirNames.Count; i++)
                {
                    string dir = existingDirNames[i];
                    if (dir != null && string.Equals(dir, candidate, StringComparison.Ordinal))
                    {
                        return dir;
                    }
                }

                for (int i = 0; i < existingDirNames.Count; i++)
                {
                    string dir = existingDirNames[i];
                    if (dir != null && string.Equals(dir, candidate, StringComparison.OrdinalIgnoreCase))
                    {
                        return dir;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// 根下的子目录名（只要名字）。根不存在 / 空参数 / 无权限 / 非法路径 → null（表示「根拿不到」）。
        /// 根存在但没有子目录 → 空数组（不是 null）。
        /// </summary>
        private static string[] TryGetSubDirectoryNames(string packsRoot)
        {
            if (string.IsNullOrEmpty(packsRoot))
            {
                return null;
            }

            string trimmed = packsRoot.Trim();
            if (trimmed.Length == 0)
            {
                return null;
            }

            string[] dirs;
            try
            {
                if (!Directory.Exists(trimmed))
                {
                    return null;
                }

                dirs = Directory.GetDirectories(trimmed);
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (NotSupportedException)
            {
                return null;
            }
            catch (System.Security.SecurityException)
            {
                return null;
            }

            if (dirs == null || dirs.Length == 0)
            {
                return EmptyStrings;
            }

            string[] names = new string[dirs.Length];
            for (int i = 0; i < dirs.Length; i++)
            {
                names[i] = SafeFileName(dirs[i]);
            }

            return names;
        }

        /// <summary>根下平铺的 *.jsonl 个数（只计数，永不读、永不进 Files）。</summary>
        private static int CountFlatJsonl(string packsRoot)
        {
            if (string.IsNullOrEmpty(packsRoot))
            {
                return 0;
            }

            string trimmed = packsRoot.Trim();
            if (trimmed.Length == 0)
            {
                return 0;
            }

            string[] files;
            try
            {
                if (!Directory.Exists(trimmed))
                {
                    return 0;
                }

                files = Directory.GetFiles(trimmed, JsonlPattern, SearchOption.TopDirectoryOnly);
            }
            catch (UnauthorizedAccessException)
            {
                return 0;
            }
            catch (IOException)
            {
                return 0;
            }
            catch (ArgumentException)
            {
                return 0;
            }
            catch (NotSupportedException)
            {
                return 0;
            }
            catch (System.Security.SecurityException)
            {
                return 0;
            }

            if (files == null || files.Length == 0)
            {
                return 0;
            }

            int count = 0;
            for (int i = 0; i < files.Length; i++)
            {
                if (files[i] != null && IsJsonlExtension(files[i]))
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>目录下的 *.jsonl（排序、过滤后的数组；内部版）。</summary>
        internal static string[] ListPackFilesCore(string languageDir)
        {
            if (string.IsNullOrEmpty(languageDir))
            {
                return EmptyStrings;
            }

            string trimmed = languageDir.Trim();
            if (trimmed.Length == 0)
            {
                return EmptyStrings;
            }

            string[] raw;
            try
            {
                if (!Directory.Exists(trimmed))
                {
                    return EmptyStrings;
                }

                raw = Directory.GetFiles(trimmed, JsonlPattern, SearchOption.TopDirectoryOnly);
            }
            catch (UnauthorizedAccessException)
            {
                return EmptyStrings;
            }
            catch (IOException)
            {
                return EmptyStrings;
            }
            catch (ArgumentException)
            {
                return EmptyStrings;
            }
            catch (NotSupportedException)
            {
                return EmptyStrings;
            }
            catch (System.Security.SecurityException)
            {
                return EmptyStrings;
            }

            if (raw == null || raw.Length == 0)
            {
                return EmptyStrings;
            }

            List<string> keep = new List<string>(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                string path = raw[i];
                if (path == null)
                {
                    continue;
                }

                if (!IsJsonlExtension(path))
                {
                    continue;
                }

                if (!IsNonEmptyFile(path))
                {
                    continue;
                }

                keep.Add(path);
            }

            if (keep.Count == 0)
            {
                return EmptyStrings;
            }

            // 顺序必须确定：DictPack 里后写覆盖先写，顺序不确定 = 行为不可复现。
            keep.Sort(CompareByFileNameOrdinal);
            return keep.ToArray();
        }

        private static int CompareByFileNameOrdinal(string left, string right)
        {
            int byName = string.CompareOrdinal(SafeFileName(left), SafeFileName(right));
            if (byName != 0)
            {
                return byName;
            }

            // 同目录内文件名不会重复，这里只是给排序一个全序（稳定性的保险）。
            return string.CompareOrdinal(left, right);
        }

        private static bool IsJsonlExtension(string path)
        {
            string ext;
            try
            {
                ext = Path.GetExtension(path);
            }
            catch (ArgumentException)
            {
                return false;
            }

            return string.Equals(ext, JsonlExtension, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNonEmptyFile(string path)
        {
            try
            {
                return new FileInfo(path).Length > 0;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (NotSupportedException)
            {
                return false;
            }
            catch (System.Security.SecurityException)
            {
                return false;
            }
        }

        private static string[] SortAndCap(string[] items, int max)
        {
            if (items == null || items.Length == 0)
            {
                return EmptyStrings;
            }

            string[] copy = (string[])items.Clone();
            Array.Sort(copy, StringComparer.Ordinal);
            if (copy.Length <= max)
            {
                return copy;
            }

            string[] capped = new string[max];
            Array.Copy(copy, capped, max);
            return capped;
        }

        private static string SafeFileName(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFileName(path);
            }
            catch (ArgumentException)
            {
                return path;
            }
        }

        private static string CombineRootAndName(string packsRoot, string dirName)
        {
            try
            {
                return Path.Combine(packsRoot, dirName);
            }
            catch (ArgumentException)
            {
                // 根里有非法字符：退化成字符串拼接，仍然不抛（调用方看 Status 决定怎么打日志）。
                return packsRoot + Path.DirectorySeparatorChar + dirName;
            }
        }
    }
}
