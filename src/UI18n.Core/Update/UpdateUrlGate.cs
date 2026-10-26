using System;

namespace UI18n.Core.Update
{
    /// <summary>
    /// 更新地址闸门（安全红线）：只允许 <c>https://</c>，以及**本机回环**上的 <c>http://</c>
    /// （<c>127.0.0.1</c> / <c>localhost</c> / <c>[::1]</c>，离线端到端验收用）。
    /// <para>
    /// 其它 scheme（<c>ftp:</c>、<c>file:</c>、<c>jar:</c>…）与「明文 http 指向外网主机」一律拒绝并给出原因码：
    /// 明文 HTTP 拉语言包等于允许中间人替换字典，而字典内容会被渲染进玩家界面。
    /// </para>
    /// <para>所有方法都不抛异常；失败返回 false + 原因码，由调用方 Warn 后**当作「不联网」**处理。</para>
    /// </summary>
    public static class UpdateUrlGate
    {
        /// <summary>清单文件名（相对 <c>&lt;base&gt;/&lt;channel&gt;/</c>）。</summary>
        public const string ManifestFileName = "manifest.json";

        /// <summary>空地址。</summary>
        public const string ReasonEmpty = "empty";
        /// <summary>不是绝对 URL。</summary>
        public const string ReasonBadUrl = "invalid-url";
        /// <summary>scheme 不在白名单（只允许 https / 本机 http）。</summary>
        public const string ReasonScheme = "scheme-not-allowed";
        /// <summary>明文 http 指向非本机主机。</summary>
        public const string ReasonHost = "http-host-not-loopback";
        /// <summary>通道名非法。</summary>
        public const string ReasonBadChannel = "invalid-channel";

        /// <summary>通道名长度上限。</summary>
        public const int MaxChannelLength = 32;

        /// <summary>是否是允许的更新地址（https 任意主机；http 仅回环）。</summary>
        /// <remarks>
        /// v1.5 起 <b>不再依赖 <see cref="Uri"/> 解析</b>：游戏跑在 Mono 上，同一个 https 地址
        /// （<c>https://raw.githubusercontent.com/DiaoDaiaChan/KSP_UniversalI18n/update</c>）在
        /// .NET 10 下 <c>Uri.TryCreate</c> 成功，在 Mono 下可能返回 false，表现为
        /// <c>update: disabled (invalid-url)</c> —— 地址明明合法却永久不联网，且原因码骗人。
        /// 这里改成「自己切 scheme + authority」，只做本闸门需要的判断（scheme 白名单、authority 非空、
        /// 明文 http 是否回环），不追求 RFC 完备；真正的 URL 合法性由 HTTP 层兜底。
        /// </remarks>
        public static bool IsAllowedBaseUrl(string url, out string reason)
        {
            reason = null;

            if (url == null || url.Trim().Length == 0)
            {
                reason = ReasonEmpty;
                return false;
            }

            string value = url.Trim();

            int schemeEnd = value.IndexOf(SchemeSeparator, StringComparison.Ordinal);
            if (schemeEnd <= 0)
            {
                reason = ReasonBadUrl;
                return false;
            }

            string scheme = value.Substring(0, schemeEnd);
            if (!IsAsciiToken(scheme))
            {
                reason = ReasonBadUrl;
                return false;
            }

            // scheme 白名单先判：这样 `file:///c:/x` 报 scheme-not-allowed（原因码要能指向真正的问题），
            // 而不是因为 authority 为空被报成 invalid-url。
            bool isHttps = string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase);
            bool isHttp = string.Equals(scheme, "http", StringComparison.OrdinalIgnoreCase);
            if (!isHttps && !isHttp)
            {
                reason = ReasonScheme;
                return false;
            }

            string authority = Authority(value, schemeEnd + SchemeSeparator.Length);
            if (authority.Length == 0)
            {
                // "https://" / "http:///x" —— 有 scheme 没主机：非法地址
                reason = ReasonBadUrl;
                return false;
            }

            if (isHttps)
            {
                return true;
            }

            if (isHttp)
            {
                string host = Host(authority);
                if (host.Length == 0)
                {
                    reason = ReasonBadUrl;
                    return false;
                }

                if (!IsLoopbackHost(host))
                {
                    reason = ReasonHost;
                    return false;
                }

                return true;
            }

            reason = ReasonScheme;
            return false;
        }

        private const string SchemeSeparator = "://";

        /// <summary>scheme 只允许 <c>[A-Za-z][A-Za-z0-9+.-]*</c>（不查表，避免把 <c>:` 之类当 scheme）。</summary>
        private static bool IsAsciiToken(string scheme)
        {
            for (int i = 0; i < scheme.Length; i++)
            {
                char c = scheme[i];
                bool alpha = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
                if (i == 0)
                {
                    if (!alpha)
                    {
                        return false;
                    }

                    continue;
                }

                bool ok = alpha || (c >= '0' && c <= '9') || c == '+' || c == '.' || c == '-';
                if (!ok)
                {
                    return false;
                }
            }

            return scheme.Length > 0;
        }

        /// <summary>取 <c>://</c> 之后到第一个 <c>/ ? #</c> 之前的部分（userinfo 一并返回）。</summary>
        private static string Authority(string value, int start)
        {
            int end = value.Length;
            for (int i = start; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '/' || c == '?' || c == '#')
                {
                    end = i;
                    break;
                }
            }

            return value.Substring(start, end - start);
        }

        /// <summary>从 authority 里取主机：去掉 userinfo 与端口，IPv6 保留方括号内的形式。</summary>
        private static string Host(string authority)
        {
            string hostPort = authority;
            int at = hostPort.LastIndexOf('@');
            if (at >= 0)
            {
                hostPort = hostPort.Substring(at + 1);
            }

            if (hostPort.Length > 0 && hostPort[0] == '[')
            {
                int close = hostPort.IndexOf(']');
                return close > 0 ? hostPort.Substring(1, close - 1) : hostPort;
            }

            int colon = hostPort.IndexOf(':');
            if (colon >= 0)
            {
                hostPort = hostPort.Substring(0, colon);
            }

            return hostPort;
        }

        /// <summary>回环主机：<c>localhost</c> / <c>127.0.0.1</c> / <c>::1</c>（大小写与结尾点都忽略）。</summary>
        private static bool IsLoopbackHost(string host)
        {
            string h = host.Trim();
            while (h.Length > 0 && h[h.Length - 1] == '.')
            {
                h = h.Substring(0, h.Length - 1);
            }

            return string.Equals(h, "localhost", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(h, "127.0.0.1", StringComparison.Ordinal)
                   || string.Equals(h, "::1", StringComparison.Ordinal);
        }

        /// <summary>去掉首尾空白与结尾的 <c>/</c>；空值返回空串（不抛）。</summary>
        public static string NormalizeBaseUrl(string url)
        {
            if (url == null)
            {
                return string.Empty;
            }

            string s = url.Trim();
            while (s.Length > 0 && s[s.Length - 1] == '/')
            {
                s = s.Substring(0, s.Length - 1);
            }

            return s;
        }

        /// <summary>
        /// 通道名是否合法：1~32 个 <c>[A-Za-z0-9._-]</c>，首字符是字母或数字，不含 <c>..</c>。
        /// 例：<c>stable</c>、<c>beta</c>、<c>v1.2</c>。
        /// </summary>
        public static bool IsValidChannel(string channel)
        {
            if (channel == null)
            {
                return false;
            }

            if (channel.Length == 0 || channel.Length > MaxChannelLength)
            {
                return false;
            }

            if (channel.IndexOf("..", StringComparison.Ordinal) >= 0)
            {
                return false;
            }

            for (int i = 0; i < channel.Length; i++)
            {
                char c = channel[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                          || c == '.' || c == '_' || c == '-';
                if (!ok)
                {
                    return false;
                }
            }

            char first = channel[0];
            bool firstOk = (first >= 'a' && first <= 'z') || (first >= 'A' && first <= 'Z') || (first >= '0' && first <= '9');
            return firstOk;
        }

        /// <summary>拼清单地址：<c>&lt;base&gt;/&lt;channel&gt;/manifest.json</c>（base 的结尾斜杠会被吃掉）。</summary>
        public static string BuildManifestUrl(string baseUrl, string channel)
        {
            return BuildAssetUrl(baseUrl, channel, ManifestFileName);
        }

        /// <summary>拼资源地址：<c>&lt;base&gt;/&lt;channel&gt;/&lt;fileName&gt;</c>。参数为空时返回空串（不抛）。</summary>
        public static string BuildAssetUrl(string baseUrl, string channel, string fileName)
        {
            string root = NormalizeBaseUrl(baseUrl);
            if (root.Length == 0 || string.IsNullOrEmpty(channel) || string.IsNullOrEmpty(fileName))
            {
                return string.Empty;
            }

            return root + "/" + channel + "/" + fileName;
        }
    }
}
