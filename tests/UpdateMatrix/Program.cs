using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using UI18n.Core.Update;
using CoreZip = UI18n.Core.Update.ZipArchive;
using SysZip = System.IO.Compression.ZipArchive;

namespace UpdCheck
{
    internal static class Program
    {
        private static int _checks;
        private static int _failures;

        private static void Check(bool ok, string what)
        {
            _checks++;
            if (!ok)
            {
                _failures++;
                Console.WriteLine("FAIL: " + what);
            }
            else
            {
                Console.WriteLine("ok   : " + what);
            }
        }

        private static int Main(string[] args)
        {
            if (args.Length >= 4 && args[0] == "matrix")
            {
                return Matrix.Run(args[1], args[2], args[3]);
            }

            if (args.Length >= 3 && args[0] == "zipcheck")
            {
                return ZipCheck(args[1], args[2]);
            }

            Console.WriteLine("usage: updcheck zipcheck <zip> <manifest.json>");
            Console.WriteLine("       updcheck matrix  <workRoot> <baseUrl> <fixturesRoot>");
            return 2;
        }

        private static int ZipCheck(string zipPath, string manifestPath)
        {
            Console.WriteLine("== zipcheck ==");
            byte[] zip = File.ReadAllBytes(zipPath);
            string manifestJson = File.ReadAllText(manifestPath, Encoding.UTF8);

            UpdateManifest manifest;
            string error;
            Check(UpdateManifest.TryParse(manifestJson, out manifest, out error), "manifest parses (" + (error ?? "ok") + ")");
            if (manifest == null)
            {
                return 1;
            }

            Console.WriteLine("     " + manifest.Describe());

            Check(UpdateVerify.CheckZip(zip, manifest, out error), "zip bytes+sha256 match manifest (" + error + ")");

            CoreZip archive;
            Check(ZipArchiveReader.TryRead(zip, out archive, out error), "own zip reader parses the release zip (" + error + ")");
            if (archive == null)
            {
                return 1;
            }

            Console.WriteLine("     entries=" + archive.Count.ToString(CultureInfo.InvariantCulture));

            // System.IO.Compression 作为参照物
            Dictionary<string, byte[]> reference = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            using (SysZip netZip = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in netZip.Entries)
                {
                    if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    using (Stream stream = entry.Open())
                    using (MemoryStream buffer = new MemoryStream())
                    {
                        stream.CopyTo(buffer);
                        reference[entry.FullName] = buffer.ToArray();
                    }
                }
            }

            Check(reference.Count == archive.Count, "entry count matches System.IO.Compression (" + reference.Count + " vs " + archive.Count + ")");

            int mismatch = 0;
            int notFound = 0;
            long bytesTotal = 0;
            foreach (ZipEntry entry in archive.Entries)
            {
                byte[] mine;
                if (!archive.TryExtract(entry, out mine, out error))
                {
                    mismatch++;
                    Console.WriteLine("FAIL: extract " + entry.Name + ": " + error);
                    continue;
                }

                bytesTotal += mine.LongLength;

                byte[] theirs;
                if (!reference.TryGetValue(entry.Name, out theirs))
                {
                    notFound++;
                    Console.WriteLine("FAIL: entry not in reference: " + entry.Name);
                    continue;
                }

                if (mine.Length != theirs.Length)
                {
                    mismatch++;
                    Console.WriteLine("FAIL: length differs: " + entry.Name);
                    continue;
                }

                for (int i = 0; i < mine.Length; i++)
                {
                    if (mine[i] != theirs[i])
                    {
                        mismatch++;
                        Console.WriteLine("FAIL: byte differs at " + i.ToString(CultureInfo.InvariantCulture) + " in " + entry.Name);
                        break;
                    }
                }
            }

            Check(mismatch == 0, "all " + archive.Count + " entries byte-identical to System.IO.Compression");
            Check(notFound == 0, "no entry missing from reference");
            Console.WriteLine("     extracted bytes=" + bytesTotal.ToString("N0", CultureInfo.InvariantCulture));

            // 逐文件对 manifest（manifest 路径带 GameData/UI18n/ 前缀）
            int manifestChecked = 0;
            int manifestBad = 0;
            foreach (ManifestFile file in manifest.Files)
            {
                byte[] content;
                if (!archive.TryExtract("GameData/UI18n/" + file.ModRelativePath, out content, out error))
                {
                    manifestBad++;
                    Console.WriteLine("FAIL: manifest file not in zip: " + file.ModRelativePath + " (" + error + ")");
                    continue;
                }

                if (!UpdateVerify.CheckEntry(content, file, out error))
                {
                    manifestBad++;
                    Console.WriteLine("FAIL: manifest mismatch " + file.ModRelativePath + ": " + error);
                    continue;
                }

                manifestChecked++;
            }

            Check(manifestBad == 0, "every manifest file matches the zip content (" + manifestChecked + "/" + manifest.FileCount + ")");

            // 负向：故意改一个字节必须被 sha256 拦住
            byte[] tampered = (byte[])zip.Clone();
            tampered[tampered.Length / 2] ^= 0xFF;
            string tamperReason;
            Check(!UpdateVerify.CheckZip(tampered, manifest, out tamperReason), "tampered zip rejected (" + tamperReason + ")");

            // 负向：截断的 zip 必须解析失败而不是崩
            byte[] truncated = new byte[zip.Length / 2];
            Buffer.BlockCopy(zip, 0, truncated, 0, truncated.Length);
            CoreZip broken;
            string brokenError;
            Check(!ZipArchiveReader.TryRead(truncated, out broken, out brokenError), "truncated zip rejected (" + brokenError + ")");

            // 负向：垃圾字节
            byte[] junk = new byte[128];
            for (int i = 0; i < junk.Length; i++)
            {
                junk[i] = (byte)i;
            }

            Check(!ZipArchiveReader.TryRead(junk, out broken, out brokenError), "junk rejected (" + brokenError + ")");

            Console.WriteLine();
            Console.WriteLine("checks=" + _checks + " failures=" + _failures);
            return _failures == 0 ? 0 : 1;
        }
    }
}
