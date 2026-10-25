using System;
using System.Globalization;
using System.IO;

namespace UI18n.Core.Update
{
    /// <summary>
    /// 下载产物的完整性校验：字节数 + sha256。
    /// <para>
    /// 语义刻意做成「先比字节数、再比 sha256、再比文件清单」三步都过才算通过：
    /// 字节数能一次拦住截断的下载（便宜），sha256 才是真正的完整性判据，
    /// 文件清单用于抓「zip 本身完好但内容是旧包 / 被换过」的情况。
    /// </para>
    /// <para>所有方法返回 false + 原因码，**永不抛异常**（IO 异常也吞成原因码）。</para>
    /// </summary>
    public static class UpdateVerify
    {
        /// <summary>通过。</summary>
        public const string ReasonNone = "";

        /// <summary>字节数不符。</summary>
        public const string ReasonSizeMismatch = "size-mismatch";

        /// <summary>sha256 不符。</summary>
        public const string ReasonSha256Mismatch = "sha256-mismatch";

        /// <summary>文件不存在 / 读不出来。</summary>
        public const string ReasonUnreadable = "unreadable";

        /// <summary>没有清单可比对。</summary>
        public const string ReasonNoManifest = "no-manifest";

        /// <summary>校验内存中的字节。</summary>
        public static bool CheckBytes(byte[] data, long expectedBytes, string expectedSha256, out string reason)
        {
            reason = ReasonNone;

            if (data == null)
            {
                reason = ReasonUnreadable;
                return false;
            }

            if (expectedBytes >= 0 && data.LongLength != expectedBytes)
            {
                reason = ReasonSizeMismatch + " (expected " + expectedBytes.ToString(CultureInfo.InvariantCulture)
                    + " B, got " + data.LongLength.ToString(CultureInfo.InvariantCulture) + " B)";
                return false;
            }

            if (!Sha256Hex.IsHex64(expectedSha256))
            {
                reason = ReasonSha256Mismatch + " (expected value is not a sha256 hex: "
                    + (expectedSha256 == null ? "(null)" : expectedSha256) + ")";
                return false;
            }

            string actual = Sha256Hex.OfBytes(data);
            if (!Sha256Hex.EqualsIgnoreCase(actual, expectedSha256))
            {
                reason = ReasonSha256Mismatch + " (expected " + Sha256Hex.Normalize(expectedSha256)
                    + ", got " + actual + ")";
                return false;
            }

            return true;
        }

        /// <summary>读文件并校验（文件读不出来当 <see cref="ReasonUnreadable"/>）。</summary>
        public static bool CheckFile(string path, long expectedBytes, string expectedSha256, out string reason)
        {
            reason = ReasonNone;

            if (string.IsNullOrEmpty(path))
            {
                reason = ReasonUnreadable + " (empty path)";
                return false;
            }

            byte[] data;
            try
            {
                data = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                reason = ReasonUnreadable + " (" + ex.GetType().Name + ": " + ex.Message + ")";
                return false;
            }

            return CheckBytes(data, expectedBytes, expectedSha256, out reason);
        }

        /// <summary>校验 zip 本身（字节数 + sha256）。</summary>
        public static bool CheckZip(byte[] zip, UpdateManifest manifest, out string reason)
        {
            reason = ReasonNone;

            if (manifest == null)
            {
                reason = ReasonNoManifest;
                return false;
            }

            return CheckBytes(zip, manifest.ZipBytes, manifest.ZipSha256, out reason);
        }

        /// <summary>校验解压出来的一条文件是否与清单一致（内容字节 + sha256）。</summary>
        public static bool CheckEntry(byte[] content, ManifestFile file, out string reason)
        {
            reason = ReasonNone;

            if (file == null)
            {
                reason = ReasonNoManifest;
                return false;
            }

            return CheckBytes(content, file.Bytes, file.Sha256, out reason);
        }
    }
}
