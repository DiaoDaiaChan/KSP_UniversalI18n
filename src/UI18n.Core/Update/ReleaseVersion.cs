using System;
using System.Globalization;

namespace UI18n.Core.Update
{
    /// <summary>
    /// 「发布版本」= <c>major.minor[.patch[.revision]]</c> 的纯数字版本号，<b>忽略</b> <c>+build</c> 与 <c>-prerelease</c> 后缀。
    /// <para>
    /// 为什么要自己写一个：<c>System.Version</c> 不接受 <c>1.1.0+29ffa5c</c> 这种串（.NET SDK 会把 commit 追加进
    /// <c>AssemblyInformationalVersion</c>，见 README §10.8），而 <c>Version.Parse</c> 遇到就抛。
    /// 这里只做「够用且可判定」的比较：三段数字逐段比，后缀只用于日志展示。
    /// </para>
    /// <para>
    /// 契约：<see cref="TryParse"/> 永不抛、失败时 <c>version = <see cref="None"/></c>（<see cref="IsValid"/> 为 false）。
    /// 接受 <c>1.2</c> / <c>1.2.3</c> / <c>1.2.3.4</c>（第 4 段存在时忽略）、<c>1.2.3+sha</c>、<c>1.2.3-beta.1</c>。
    /// </para>
    /// </summary>
    public struct ReleaseVersion : IComparable<ReleaseVersion>, IEquatable<ReleaseVersion>
    {
        /// <summary>「没有版本」的哨兵值（<see cref="IsValid"/> = false）。</summary>
        public static readonly ReleaseVersion None = default(ReleaseVersion);

        private readonly int _major;
        private readonly int _minor;
        private readonly int _patch;
        private readonly bool _valid;

        private ReleaseVersion(int major, int minor, int patch)
        {
            _major = major;
            _minor = minor;
            _patch = patch;
            _valid = true;
        }

        /// <summary>主版本号。</summary>
        public int Major { get { return _major; } }

        /// <summary>次版本号。</summary>
        public int Minor { get { return _minor; } }

        /// <summary>修订号。</summary>
        public int Patch { get { return _patch; } }

        /// <summary>是否是成功解析出来的版本（<see cref="None"/> 为 false）。</summary>
        public bool IsValid { get { return _valid; } }

        /// <summary>由三段数字直接构造。</summary>
        public static ReleaseVersion Create(int major, int minor, int patch)
        {
            return new ReleaseVersion(major, minor, patch);
        }

        /// <summary>
        /// 解析版本串。<b>失败不抛</b>，返回 false 并给出 <see cref="None"/>。
        /// 允许前后空白、允许 <c>+</c>/<c>-</c> 之后的后缀（后缀被丢弃）、允许 2~4 段数字。
        /// </summary>
        public static bool TryParse(string text, out ReleaseVersion version)
        {
            version = None;

            if (text == null)
            {
                return false;
            }

            string s = text.Trim();
            if (s.Length == 0)
            {
                return false;
            }

            // 丢掉 build metadata（+sha）与 prerelease（-beta），只看数字前缀
            int cut = s.Length;
            int plus = s.IndexOf('+');
            if (plus >= 0 && plus < cut)
            {
                cut = plus;
            }

            int minus = s.IndexOf('-');
            if (minus >= 0 && minus < cut)
            {
                cut = minus;
            }

            s = s.Substring(0, cut).Trim();
            if (s.Length == 0)
            {
                return false;
            }

            string[] parts = s.Split('.');
            if (parts.Length < 2 || parts.Length > 4)
            {
                return false;
            }

            int[] numbers = new int[3];
            for (int i = 0; i < parts.Length; i++)
            {
                int value;
                if (!TryParseComponent(parts[i], out value))
                {
                    return false;
                }

                if (i < 3)
                {
                    numbers[i] = value;
                }
            }

            version = new ReleaseVersion(numbers[0], numbers[1], numbers[2]);
            return true;
        }

        /// <summary>解析失败时返回 <see cref="None"/>（<b>不抛</b>）。</summary>
        public static ReleaseVersion ParseOrNone(string text)
        {
            ReleaseVersion version;
            TryParse(text, out version);
            return version;
        }

        private static bool TryParseComponent(string part, out int value)
        {
            value = 0;

            if (part == null || part.Length == 0)
            {
                return false;
            }

            for (int i = 0; i < part.Length; i++)
            {
                char c = part[i];
                if (c < '0' || c > '9')
                {
                    return false;
                }
            }

            return int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>三段数字逐段比较；<see cref="None"/> 视为 0.0.0。</summary>
        public int CompareTo(ReleaseVersion other)
        {
            int c = _major.CompareTo(other._major);
            if (c != 0)
            {
                return c;
            }

            c = _minor.CompareTo(other._minor);
            if (c != 0)
            {
                return c;
            }

            return _patch.CompareTo(other._patch);
        }

        /// <summary>三段数字相等即相等（与 <see cref="CompareTo"/> 一致）。</summary>
        public bool Equals(ReleaseVersion other)
        {
            return _major == other._major && _minor == other._minor && _patch == other._patch;
        }

        /// <inheritdoc/>
        public override bool Equals(object obj)
        {
            return obj is ReleaseVersion && Equals((ReleaseVersion)obj);
        }

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = _major * 397;
                hash = (hash ^ _minor) * 397;
                return hash ^ _patch;
            }
        }

        /// <summary>是否比 <paramref name="other"/> 新。</summary>
        public bool IsNewerThan(ReleaseVersion other)
        {
            return CompareTo(other) > 0;
        }

        /// <summary>形如 <c>1.2.3</c>；<see cref="None"/> 输出 <c>0.0.0</c>。</summary>
        public override string ToString()
        {
            return _major.ToString(CultureInfo.InvariantCulture) + "."
                   + _minor.ToString(CultureInfo.InvariantCulture) + "."
                   + _patch.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>小于比较（按三段数字）。</summary>
        public static bool operator <(ReleaseVersion a, ReleaseVersion b) { return a.CompareTo(b) < 0; }

        /// <summary>大于比较（按三段数字）。</summary>
        public static bool operator >(ReleaseVersion a, ReleaseVersion b) { return a.CompareTo(b) > 0; }

        /// <summary>相等比较。</summary>
        public static bool operator ==(ReleaseVersion a, ReleaseVersion b) { return a.Equals(b); }

        /// <summary>不等比较。</summary>
        public static bool operator !=(ReleaseVersion a, ReleaseVersion b) { return !a.Equals(b); }
    }
}
