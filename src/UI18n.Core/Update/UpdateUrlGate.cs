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
        public static bool IsAllowedBaseUrl(string url, out string reason)
        {
            reason = null;

            if (url == null || url.Trim().Length == 0)
            {
                reason = ReasonEmpty;
                return false;
            }

            Uri uri;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out uri))
            {
                reason = ReasonBadUrl;
                return false;
            }

            if (string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(uri.Host))
                {
                    reason = ReasonBadUrl;
                    return false;
                }

                return true;
            }

            if (string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase))
            {
                if (!uri.IsLoopback)
                {
                    reason = ReasonHost;
                    return false;
                }

                return true;
            }

            reason = ReasonScheme;
            return false;
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
