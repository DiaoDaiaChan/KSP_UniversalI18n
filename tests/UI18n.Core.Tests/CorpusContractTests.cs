using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using UI18n.Core.Dict;
using UI18n.Core.Hashing;
using UI18n.Core.Text;
using Xunit;

namespace UI18n.Core.Tests
{
    /// <summary>
    /// 真实语料契约：语料里的 <c>h</c> 字段必须等于 <c>text</c> 的 UTF-8 FNV-1a 64。
    /// 这是整个项目跨语言的数据契约（python 侧 tools/i18n_tools/fnv.py 生成，C# 侧查询），
    /// 几条手写向量挡不住「某种字符在某条路径上被换成了别的编码」，所以还要拿真实语料跑。
    /// <para>
    /// 两级数据源（T9 起）：
    /// </para>
    /// <list type="number">
    /// <item>入库夹具 <c>tests/UI18n.Core.Tests/Data/pool-sample.jsonl</c>（从 packs/pool.jsonl 确定性抽取的 696 行子集，
    /// 随仓库提交、**必须存在**；缺失时用例显式失败，不跳过）。默认跑这一份。</item>
    /// <item>全量语料 <c>packs/pool.jsonl</c>（退化路径 <c>_scratch/corpus/pool.jsonl</c>）：3 MB 生成物**不入库**。
    /// 它在就额外跑一遍全量并在输出里报行数；不在就只跑样本，并打印一行
    /// <c>full corpus not present, sample only</c> —— 绝不静默跳过。</item>
    /// </list>
    /// <para>
    /// 夹具抽取规则（确定性，可复现）：取 pool 中 0-based 行号 <c>idx % 20 == 0</c> 的 640 行，
    /// 再并上全部 12 条「h 为 16 位全数字」行（T8 修的 hex/十进制歧义那 12 行）、全部 19 条含
    /// × ’ é è — “ ” 的行、含占位符/富文本的行（每 50 个取 1）、len &gt; 500 的长文本行（每 50 个取 1）、
    /// 原始 JSON 里含字面反斜杠-u 的行（每 200 个取 1），按原行序去重写出，行内容逐字节保留 pool 原 JSON。
    /// </para>
    /// </summary>
    public class CorpusContractTests
    {
        /// <summary>入库夹具的体积上限（task-9 验收：&lt;= 400 KB）。</summary>
        private const int SampleBudgetBytes = 400 * 1024;

        private readonly ITestOutputHelper _output;

        public CorpusContractTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>入库夹具路径（csproj 里 CopyToOutputDirectory=PreserveNewest 复制到输出目录的 Data 下）。</summary>
        private static string SamplePath()
        {
            return Path.Combine(AppContext.BaseDirectory, "Data", "pool-sample.jsonl");
        }

        /// <summary>全量语料路径；不存在返回 null（不失败 —— 全量是可选数据，样本是必跑数据）。</summary>
        private static string FindFullCorpus()
        {
            DirectoryInfo dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "UI18n.sln")))
            {
                dir = dir.Parent;
            }

            if (dir == null)
            {
                return null;
            }

            string[] candidates =
            {
                Path.Combine(dir.FullName, "packs", "pool.jsonl"),
                Path.Combine(dir.FullName, "_scratch", "corpus", "pool.jsonl"),
            };

            for (int i = 0; i < candidates.Length; i++)
            {
                if (File.Exists(candidates[i]))
                {
                    return candidates[i];
                }
            }

            return null;
        }

        private sealed class PoolRow
        {
            /// <summary>语料里 h 字段的原始字符串（16 位小写 hex）。</summary>
            public string RawHash;

            /// <summary>按 hex 解析出来的哈希值（语料契约里的真值）。</summary>
            public ulong Hash;

            /// <summary>text 字段原文。</summary>
            public string Text;
        }

        private static List<PoolRow> LoadRows(string path, out int lineCount)
        {
            var rows = new List<PoolRow>(13000);
            int lines = 0;

            foreach (string line in File.ReadLines(path, Encoding.UTF8))
            {
                if (line.Length == 0)
                {
                    continue;
                }

                lines++;
                using (JsonDocument doc = JsonDocument.Parse(line))
                {
                    JsonElement root = doc.RootElement;
                    string rawHash = root.GetProperty("h").GetString();
                    string text = root.GetProperty("text").GetString();

                    var row = new PoolRow();
                    row.RawHash = rawHash;
                    row.Hash = ulong.Parse(rawHash, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    row.Text = text ?? string.Empty;
                    rows.Add(row);
                }
            }

            lineCount = lines;
            return rows;
        }

        private static bool IsAllAsciiDigits(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] < '0' || s[i] > '9')
                {
                    return false;
                }
            }

            return s.Length > 0;
        }

        private static bool HasNonAscii(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] > 0x7f)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasPlaceholderOrMarkup(string s)
        {
            return s.IndexOf("<<", StringComparison.Ordinal) >= 0
                || s.IndexOf("{0}", StringComparison.Ordinal) >= 0
                || s.IndexOf("%s", StringComparison.Ordinal) >= 0
                || s.IndexOf("<color", StringComparison.Ordinal) >= 0
                || s.IndexOf("<b>", StringComparison.Ordinal) >= 0
                || s.IndexOf("<i>", StringComparison.Ordinal) >= 0
                || s.IndexOf('\n') >= 0
                || s.IndexOf("\\n", StringComparison.Ordinal) >= 0;
        }

        [Fact]
        public void SampleFixture_ExistsInsideBudgetAndCoversTheHardCases()
        {
            string path = SamplePath();
            Assert.True(File.Exists(path), "缺少入库夹具（应从 packs/pool.jsonl 确定性抽取并提交进仓库）：" + path);

            var info = new FileInfo(path);
            Assert.True(
                info.Length > 0 && info.Length <= SampleBudgetBytes,
                "夹具体积越界（应在 (0, 400 KB] 内），实际 " + info.Length.ToString(CultureInfo.InvariantCulture) + " 字节");

            int lineCount;
            List<PoolRow> rows = LoadRows(path, out lineCount);

            // 样本必须足够大才有代表性，但又必须小到能入库
            Assert.True(lineCount >= 600, "夹具行数过少：" + lineCount.ToString(CultureInfo.InvariantCulture));

            int allDigitHex = 0;
            int nonAscii = 0;
            int placeholder = 0;
            int longText = 0;
            foreach (PoolRow row in rows)
            {
                if (row.RawHash.Length == 16 && IsAllAsciiDigits(row.RawHash))
                {
                    allDigitHex++;
                }

                if (HasNonAscii(row.Text))
                {
                    nonAscii++;
                }

                if (HasPlaceholderOrMarkup(row.Text))
                {
                    placeholder++;
                }

                if (row.Text.Length > 500)
                {
                    longText++;
                }
            }

            // 这四类「难啃的」行是抽样时强制包含的，少一类就等于夹具退化
            Assert.True(allDigitHex >= 12, "夹具丢了 T8 缺陷那 12 条 16 位全数字 hex 行，实际 " + allDigitHex.ToString(CultureInfo.InvariantCulture));
            Assert.True(nonAscii >= 10, "夹具里非 ASCII 文本行太少，实际 " + nonAscii.ToString(CultureInfo.InvariantCulture));
            Assert.True(placeholder >= 10, "夹具里占位符/富文本行太少，实际 " + placeholder.ToString(CultureInfo.InvariantCulture));
            Assert.True(longText >= 3, "夹具里长文本行太少，实际 " + longText.ToString(CultureInfo.InvariantCulture));

            _output.WriteLine("sample fixture: " + lineCount.ToString(CultureInfo.InvariantCulture) + " rows, "
                + info.Length.ToString(CultureInfo.InvariantCulture) + " bytes, alldigit16="
                + allDigitHex.ToString(CultureInfo.InvariantCulture) + ", nonascii="
                + nonAscii.ToString(CultureInfo.InvariantCulture) + ", placeholder="
                + placeholder.ToString(CultureInfo.InvariantCulture) + ", long="
                + longText.ToString(CultureInfo.InvariantCulture));
        }

        [Fact]
        public void EverySampleRow_HashEqualsFnv1aOfItsUtf8Text()
        {
            int lineCount;
            List<PoolRow> rows = LoadRows(SamplePath(), out lineCount);
            Assert.True(lineCount >= 600, "夹具行数过少：" + lineCount.ToString(CultureInfo.InvariantCulture));

            int mismatch = 0;
            string firstMismatch = null;
            foreach (PoolRow row in rows)
            {
                ulong actual = Fnv1a.Hash(row.Text);
                if (actual != row.Hash)
                {
                    if (mismatch == 0)
                    {
                        firstMismatch = "h=" + row.Hash.ToString("x16", CultureInfo.InvariantCulture)
                            + " 实际=" + actual.ToString("x16", CultureInfo.InvariantCulture)
                            + " text=" + row.Text;
                    }

                    mismatch++;
                }
            }

            Assert.True(mismatch == 0, "有 " + mismatch.ToString(CultureInfo.InvariantCulture) + " 行哈希不一致，首个：" + firstMismatch);
        }

        [Fact]
        public void SampleRows_NormalizeIsIdempotentAndLeavesTheCorpusUntouched()
        {
            int lineCount;
            List<PoolRow> rows = LoadRows(SamplePath(), out lineCount);
            int changed = 0;
            int bad = 0;
            string sample = null;

            foreach (PoolRow row in rows)
            {
                string once = TextNormalizer.Normalize(row.Text);
                string twice = TextNormalizer.Normalize(once);

                bool ok = string.Equals(once, twice, StringComparison.Ordinal)
                    && once.IndexOf('\n') < 0
                    && once.IndexOf('\r') < 0
                    && once.IndexOf('\t') < 0
                    && string.Equals(once, once.Trim(), StringComparison.Ordinal);

                if (!ok)
                {
                    bad++;
                    if (sample == null)
                    {
                        sample = row.Text + "  =>  " + once;
                    }
                }

                if (!string.Equals(once, row.Text, StringComparison.Ordinal))
                {
                    changed++;
                }
            }

            // 采集池里的 text 已是归一化形态（h 也是按它算的），所以归一化器对语料必须是「不动点」：
            // 只要有一行被改动，就说明 C# 侧 04 §4.1 五步与语料生成端不一致 —— 那批文本的 hash 会对不上。
            Assert.True(changed == 0, "有 " + changed.ToString(CultureInfo.InvariantCulture) + " 行归一化结果与语料不同：C# 归一化器与生成端不一致");
            Assert.True(bad == 0, "有 " + bad.ToString(CultureInfo.InvariantCulture) + " 行归一化后不幂等/仍含换行，样例：" + sample);
        }

        /// <summary>
        /// 样本回灌解析器：把每一行重新序列化成字典行（h 用十进制，绕开 hex/十进制歧义），
        /// 再逐条按 (hash, source) 查回自己的译文。任何转义处理错误都会表现为 ParseErrors 或未命中。
        /// </summary>
        [Fact]
        public void SampleRows_RoundTripThroughJsonlParserAndLookup()
        {
            int lineCount;
            List<PoolRow> rows = LoadRows(SamplePath(), out lineCount);
            var lines = new List<string>(rows.Count);
            var expected = new Dictionary<ulong, string>(rows.Count);

            foreach (PoolRow row in rows)
            {
                ulong hash = Fnv1a.Hash(row.Text);
                string translation = "Z" + hash.ToString("x16", CultureInfo.InvariantCulture);
                expected[hash] = translation;
                lines.Add(
                    "{\"h\":" + hash.ToString(CultureInfo.InvariantCulture)
                    + ",\"s\":" + JsonSerializer.Serialize(row.Text)
                    + ",\"t\":\"" + translation + "\"}");
            }

            DictPack pack = DictPack.LoadJsonLines(lines);

            Assert.Equal(0, pack.ParseErrors);
            Assert.Equal(lines.Count, pack.Entries);

            int missed = 0;
            string sample = null;
            foreach (PoolRow row in rows)
            {
                ulong hash = Fnv1a.Hash(row.Text);
                string translation;

                if (!pack.TryGet(hash, row.Text, out translation) || !string.Equals(translation, expected[hash], StringComparison.Ordinal))
                {
                    missed++;
                    if (sample == null)
                    {
                        sample = row.Text;
                    }
                }
            }

            Assert.True(missed == 0, "有 " + missed.ToString(CultureInfo.InvariantCulture) + " 行查不回自己，样例：" + sample);
            Assert.Equal(0, pack.Misses);
        }

        /// <summary>
        /// T8 修复 #1 的真实数据证据：h 字段是「16 位、全数字」的 hex 串时，解析器必须按 **hex** 解析
        /// （而不是被十进制 TryParse 抢走）。这一批正是 pool.jsonl 里踩中歧义的 12 行，
        /// 例如 h=8400081244540915 的真值是 0x8400081244540915 = 9516546049249841429。
        /// </summary>
        [Fact]
        public void AllDigitHexRowsInSample_ParseToTheirTrueHash()
        {
            int lineCount;
            List<PoolRow> rows = LoadRows(SamplePath(), out lineCount);

            var digits = new List<PoolRow>();
            foreach (PoolRow row in rows)
            {
                if (row.RawHash.Length == 16 && IsAllAsciiDigits(row.RawHash))
                {
                    digits.Add(row);
                }
            }

            Assert.True(digits.Count >= 12, "样本里 16 位全数字 hex 行不足（应含 T8 缺陷的 12 条真实行），实际 " + digits.Count.ToString(CultureInfo.InvariantCulture));

            var lines = new List<string>(digits.Count);
            foreach (PoolRow row in digits)
            {
                lines.Add("{\"h\":\"" + row.RawHash + "\",\"s\":" + JsonSerializer.Serialize(row.Text) + ",\"t\":\"Z\"}");
            }

            DictPack pack = DictPack.LoadJsonLines(lines);
            Assert.Equal(0, pack.ParseErrors);
            Assert.Equal(digits.Count, pack.Entries);

            var byHash = new Dictionary<ulong, DictEntry>();
            foreach (DictEntry entry in pack.GetEntries())
            {
                byHash[entry.Hash] = entry;
            }

            int verified = 0;
            foreach (PoolRow row in digits)
            {
                // 这一批的定义就是「hex 解析值 != 十进制解析值」，所以它们是歧义的真实受害者
                ulong decimalValue = ulong.Parse(row.RawHash, NumberStyles.None, CultureInfo.InvariantCulture);
                Assert.NotEqual(decimalValue, row.Hash);
                Assert.Equal(Fnv1a.Hash(row.Text), row.Hash);

                Assert.True(byHash.ContainsKey(row.Hash), "h=" + row.RawHash + " 没有按 hex 解析入库（T8 修复回退？）");
                Assert.Equal(row.Hash, byHash[row.Hash].Hash);

                string translation;
                Assert.True(pack.TryGet(row.Hash, row.Text, out translation), "按真实 hash 查不到 h=" + row.RawHash);
                Assert.Equal("Z", translation);
                verified++;
            }

            Assert.Equal(digits.Count, verified);
            _output.WriteLine("all-digit-hex rows verified: " + verified.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// 全量语料（可选数据）：在就跑并报行数，不在就只跑样本并打印
        /// <c>full corpus not present, sample only</c>。两种情况用例都通过，但**绝不静默** ——
        /// 少验了什么必须能从测试输出里看见（--logger "console;verbosity=detailed" 可见）。
        /// </summary>
        [Fact]
        public void FullCorpus_WhenPresentAlsoMatchesContract()
        {
            string full = FindFullCorpus();
            if (full == null)
            {
                const string message = "full corpus not present, sample only";
                _output.WriteLine(message);
                Console.WriteLine(message);
                return;
            }

            int lineCount;
            List<PoolRow> rows = LoadRows(full, out lineCount);
            Assert.True(lineCount > 0, "全量语料为空：" + full);

            int mismatch = 0;
            string firstMismatch = null;
            foreach (PoolRow row in rows)
            {
                ulong actual = Fnv1a.Hash(row.Text);
                if (actual != row.Hash)
                {
                    if (mismatch == 0)
                    {
                        firstMismatch = "h=" + row.Hash.ToString("x16", CultureInfo.InvariantCulture)
                            + " 实际=" + actual.ToString("x16", CultureInfo.InvariantCulture)
                            + " text=" + row.Text;
                    }

                    mismatch++;
                }
            }

            string summary = "full corpus present: " + lineCount.ToString(CultureInfo.InvariantCulture)
                + " rows verified against Fnv1a.Hash (" + full + ")";
            _output.WriteLine(summary);
            Console.WriteLine(summary);

            Assert.True(mismatch == 0, "全量语料有 " + mismatch.ToString(CultureInfo.InvariantCulture) + " 行哈希不一致，首个：" + firstMismatch);
        }
    }
}
