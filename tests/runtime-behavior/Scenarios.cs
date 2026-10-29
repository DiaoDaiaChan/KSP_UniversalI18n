using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UI18n.Core.Config;
using UI18n.Core.Dict;
using UI18n.Core.Hashing;
using UI18n.Core.Text;
using UnityEngine;

namespace UI18n.Runtime.Behavior
{
    /// <summary>
    /// 九个端到端场景：真 <c>src/UI18n.Runtime</c> 代码 + 真 <c>UI18n.Core</c> + 磁盘上的真 JSONL。
    /// 每个场景都在自己的临时 KSP 根下造包，互不影响（<see cref="Env.Begin"/> 会改
    /// <c>KSPUtil.ApplicationRootPath</c>，那是全局静态字段，所以场景必须串行跑）。
    /// <para>
    /// 断言全部走 <see cref="Check"/>，失败只抛 <see cref="CheckFailed"/>：场景之间不共享任何可变状态
    /// （<c>Translator</c> 每次 <c>Initialize</c> 都把缓存/样本/捕获器清空）。
    /// </para>
    /// </summary>
    internal static class Scenarios
    {
        /// <summary>S1：包目录 → <c>ConfigLoader.LoadPacks</c> → <c>Translator</c> 命中、重复命中、未命中。</summary>
        internal static void PacksLoadAndHit(string gameRoot)
        {
            Env.Begin(gameRoot);
            Env.WritePack("Alpha.jsonl",
                Env.Row("Hello", "你好", "Alpha"),
                Env.Row("Fuel Amount", "燃料量", "Alpha"));

            var config = new UI18nConfig();
            DictPack pack = ConfigLoader.LoadPacks(config, "zh-cn");
            Check.Eq("条目数", 2L, pack.Entries);
            Check.Eq("坏行数", 0L, pack.ParseErrors);

            Translator.Initialize(config, pack);
            Check.True("has-any-translation", Translator.HasAnyTranslation);
            Check.Eq("命中", "你好", Translator.Translate("Hello", "Alpha.Patch"));
            Check.Eq("重复命中（走缓存）", "你好", Translator.Translate("Hello"));
            Check.Eq("distinct hits", 1L, Translator.DistinctHits);
            Check.Eq("样本数", 1L, Translator.SampleCount);
            Check.False("样本未截断", Translator.SampleCapped);
            Check.Eq("命中的原文进缓存", 1L, Translator.CachedTexts);
            Check.Null("真未命中", Translator.Translate("Fuel"));
            Check.Eq("未命中也进缓存（存空串，重复未命中零分配）", 2L, Translator.CachedTexts);
            Check.Null("空串", Translator.Translate(string.Empty));
            Check.Null("null", Translator.Translate(null));
            Check.Eq("未命中不进 distinct", 1L, Translator.DistinctHits);
            Check.Eq("未命中不进样本", 1L, Translator.SampleCount);
        }

        /// <summary>
        /// S2：同一个 h 三条行（global / site / ctx）—— 验「没有位置信息绝不碰 ctx 级」这条契约
        /// 在真 JSONL 上成立（<c>DictPack.TryGet</c> 的注释说得很清楚，这里把它钉在数据上）。
        /// </summary>
        internal static void KeyLevels(string gameRoot)
        {
            Env.Begin(gameRoot);
            const string CtxId = "Assembly-CSharp|Foo|Bar|1";
            Env.WritePack("Alpha.jsonl",
                Env.Row("Run", "跑"),
                Env.Row("Run", "执行", "Alpha"),
                Env.Row("Run", "跑步", null, CtxId));

            var config = new UI18nConfig();
            DictPack pack = ConfigLoader.LoadPacks(config, "zh-cn");
            Check.Eq("条目数", 3L, pack.Entries);

            ulong hash = Fnv1a.Hash("Run");
            string translation;
            KeyLevel level;

            Check.True("无 ctx 的查询命中 site 级", pack.TryGet(hash, "Run", out translation, out level));
            Check.Eq("无 ctx 拿 site 译文", "执行", translation);
            Check.True("无 ctx 层级是 Site", level == KeyLevel.Site, "got " + level);

            Check.True("带 ctx 的查询命中 ctx 级", pack.TryGet(hash, "Run", CtxId, out translation, out level));
            Check.Eq("带 ctx 拿 ctx 译文", "跑步", translation);
            Check.True("带 ctx 层级是 Ctx", level == KeyLevel.Ctx, "got " + level);

            Check.True("别处的 ctx 落到 site", pack.TryGet(hash, "Run", "Other|Type|Member|2", out translation, out level));
            Check.Eq("别处的 ctx 拿 site 译文", "执行", translation);
            Check.True("别处的 ctx 层级是 Site", level == KeyLevel.Site, "got " + level);

            Translator.Initialize(config, pack);
            Check.Eq("运行时（无位置）翻译", "执行", Translator.Translate("Run"));
        }

        /// <summary>
        /// S3：骨架行只进骨架索引、精确索引看不到它；运行时未命中 → 骨化 → 命中 → 数字填回。
        /// 骨架不写死在测试里，而是用产品自己的 <see cref="TextSkeleton.TryBuild"/> 生成（改规则时这里跟着走）。
        /// </summary>
        internal static void SkeletonFillBack(string gameRoot)
        {
            Env.Begin(gameRoot);
            string skeleton;
            string[] numbers;
            Check.True("TryBuild 建出骨架", TextSkeleton.TryBuild("Crew: 3", out skeleton, out numbers));
            Check.Eq("骨架形态", "Crew: #", skeleton);
            Check.Eq("数字段数", 1L, numbers.Length);

            Env.WritePack("Alpha.jsonl", Env.Row(skeleton, "乘员：#", "Alpha", null, true));

            var config = new UI18nConfig();
            DictPack pack = ConfigLoader.LoadPacks(config, "zh-cn");
            Check.Eq("骨架行数", 1L, pack.PatternEntries);

            string ignored;
            Check.False("骨架行不在精确索引里", pack.TryGet(Fnv1a.Hash(skeleton), skeleton, out ignored));

            Translator.Initialize(config, pack);
            Check.Eq("模板填回", "乘员：7", Translator.Translate("Crew: 7"));
            Check.Eq("另一条模板", "乘员：42", Translator.Translate("Crew: 42"));
            Check.Null("非模板（没有数字）", Translator.Translate("Crew: many"));
            Check.Eq("两次填回只算一次 distinct", 2L, Translator.DistinctHits);
        }

        /// <summary>S4：keep 行（t == s）＝ 明确「不要翻」；运行时返回 null，且**不**进缺命中捕获。</summary>
        internal static void KeepRows(string gameRoot)
        {
            Env.Begin(gameRoot);
            Env.WritePack("Alpha.jsonl", Env.Row("Kerbin", "Kerbin", "Alpha"));

            var config = new UI18nConfig();
            DictPack pack = ConfigLoader.LoadPacks(config, "zh-cn");
            string translation;
            Check.False("keep 行算未命中", pack.TryGet(Fnv1a.Hash("Kerbin"), "Kerbin", out translation));
            Check.Eq("keep 计数", 1L, pack.Keeps);

            var capture = new UI18n.Core.Capture.CaptureStore();
            Translator.Initialize(config, pack);
            Translator.AttachCapture(capture);
            Check.Null("keep 行不被翻译", Translator.Translate("Kerbin", "Alpha.Patch"));
            Check.Eq("keep 行进不了捕获", 0L, capture.Distinct);
            Check.Eq("keep 不进样本", 0L, Translator.SampleCount);
        }

        /// <summary>
        /// S5：per-mod 策略表（M5-a）。NATIVE 的包在**加载期**就被滤掉（它们条目全 0 是因为游戏自己
        /// 有中文，不是因为漏翻）；策略表损坏时必须退化成「全都加载」，绝不能连累翻译。
        /// </summary>
        internal static void NativePolicy(string gameRoot)
        {
            Env.Begin(gameRoot);
            Env.WritePack("Alpha.jsonl", Env.Row("Hello", "你好", "Alpha"));
            Env.WritePack("Beta.jsonl", Env.Row("World", "世界", "Beta"));
            Env.WritePolicies(
                "{\"schema\":\"ui18n-policies/1\",\"lang\":\"zh-cn\","
                + "\"mods\":{\"Alpha\":{\"policy\":\"NATIVE\",\"locKeys\":3,\"source\":\"pool\"}}}");

            string error;
            ModPolicies policies = ModPolicies.LoadFromDirectory(Env.LangDir(), out error);
            Check.Eq("策略表解析错误", ModPolicies.ErrorNone, error);
            Check.Eq("策略表条目数", 1L, policies.Count);
            Check.True("Alpha 是 NATIVE", policies.IsNative("Alpha"));
            Check.False("Beta 不是 NATIVE", policies.IsNative("Beta"));

            var config = new UI18nConfig();
            DictPack pack = ConfigLoader.LoadPacks(config, "zh-cn");
            Check.Eq("NATIVE 包被跳过", 1L, pack.Entries);
            Translator.Initialize(config, pack);
            Check.Null("NATIVE 包里的串不翻译", Translator.Translate("Hello"));
            Check.Eq("非 NATIVE 包照常翻译", "世界", Translator.Translate("World"));

            Env.WritePolicies("{ this is not json");
            DictPack fallback = ConfigLoader.LoadPacks(new UI18nConfig(), "zh-cn");
            Check.Eq("坏策略表 → 全量加载", 2L, fallback.Entries);
        }

        /// <summary>
        /// S6：缺命中捕获（task-30）。未命中 → 建 FilePath → Flush → 文件一行、字段齐；
        /// 同一串第二次进来只 CountMiss（零分配路径），不再写第二行。
        /// </summary>
        internal static void CaptureMisses(string gameRoot)
        {
            Env.Begin(gameRoot);
            Env.WritePack("Alpha.jsonl", Env.Row("Hello", "你好", "Alpha"));

            var config = new UI18nConfig();
            config.CaptureMisses = true;
            config.CaptureMaxEntries = 10;
            DictPack pack = ConfigLoader.LoadPacks(config, "zh-cn");

            Translator.Initialize(config, pack);
            MissCapture.Configure(config);
            Check.NotNull("捕获器已建", MissCapture.Store);
            Translator.AttachCapture(MissCapture.Store);

            Check.Null("未命中", Translator.Translate("Unknown Widget Label", "Alpha.Patch"));
            Check.Eq("捕获到一条", 1L, MissCapture.Store.Distinct);
            Check.Eq("还待写一条", 1L, MissCapture.Store.Pending);

            MissCapture.Flush("behavior-selftest");
            string file = MissCapture.FilePath;
            Check.NotNull("落盘路径已定", file);
            Check.True("文件存在", File.Exists(file), file == null ? "null" : file);
            string[] lines = File.ReadAllLines(file);
            Check.Eq("文件一行", 1L, lines.Length);
            Check.Contains("行里有原文", lines[0], "\"s\":\"Unknown Widget Label\"");
            Check.Contains("行里有归一化文本", lines[0], "\"n\":\"Unknown Widget Label\"");
            Check.Contains("行里有 sink", lines[0], "\"sink\":\"Alpha.Patch\"");
            Check.Contains("行里有人类可读时间", lines[0], "\"firstSeen\":\"");
            Check.Contains("行里有 hash", lines[0], "\"h\":\"");

            Check.Null("第二次同串仍未命中", Translator.Translate("Unknown Widget Label", "Alpha.Patch"));
            Check.Eq("重复未命中只计数", 1L, MissCapture.Store.Distinct);
            Check.Eq("重复未命中不新增待写", 0L, MissCapture.Store.Pending);
            Check.Eq("文件还是一条", 1L, File.ReadAllLines(file).Length);

            MissCapture.Shutdown();
        }

        /// <summary>S7：<c>Enabled=false</c> ＝ 全短路（不翻译、不缓存、不捕获），重新打开后立刻恢复。</summary>
        internal static void Disabled(string gameRoot)
        {
            Env.Begin(gameRoot);
            Env.WritePack("Alpha.jsonl", Env.Row("Hello", "你好", "Alpha"));

            var config = new UI18nConfig();
            config.Enabled = false;
            DictPack pack = ConfigLoader.LoadPacks(config, "zh-cn");
            var capture = new UI18n.Core.Capture.CaptureStore();

            Translator.Initialize(config, pack);
            Translator.AttachCapture(capture);
            Check.Null("关掉后不翻译", Translator.Translate("Hello", "Alpha.Patch"));
            Check.Eq("关掉后不缓存", 0L, Translator.CachedTexts);
            Check.Eq("关掉后不捕获", 0L, capture.Distinct);

            config.Enabled = true;
            Check.Eq("打开后立刻可用", "你好", Translator.Translate("Hello", "Alpha.Patch"));
            Check.Eq("打开后正常缓存", 1L, Translator.CachedTexts);
        }

        /// <summary>S8：<c>MaxEntries</c> 是硬上限（包文件按名排序，超出的整段不再吃）。</summary>
        internal static void MaxEntries(string gameRoot)
        {
            Env.Begin(gameRoot);
            Env.WritePack("A.jsonl", Env.Row("Hello", "你好", "A"));
            Env.WritePack("B.jsonl", Env.Row("World", "世界", "B"));

            var config = new UI18nConfig();
            config.MaxEntries = 1;
            DictPack pack = ConfigLoader.LoadPacks(config, "zh-cn");
            Check.Eq("截断到 1 条", 1L, pack.Entries);
            Check.True("先读 A.jsonl", pack.TryGet(Fnv1a.Hash("Hello"), "Hello", out _));
            Check.False("B.jsonl 没进包", pack.TryGet(Fnv1a.Hash("World"), "World", out _));
        }

        /// <summary>S9：包目录不存在 ＝ 空包（不抛、不崩），<see cref="Translator"/> 全短路。</summary>
        internal static void NoPacks(string gameRoot)
        {
            Env.Begin(gameRoot);
            var config = new UI18nConfig();
            DictPack pack = ConfigLoader.LoadPacks(config, "zh-cn");
            Check.Eq("空包", 0L, pack.Entries);
            Translator.Initialize(config, pack);
            Check.False("没有可翻译的东西", Translator.HasAnyTranslation);
            Check.Null("空包查询", Translator.Translate("Hello"));
        }
    }

    /// <summary>
    /// 场景用的磁盘/语句构造器：把「KSP 根 → GameData/UI18n/Packs/&lt;lang&gt;」建出来，
    /// 并把字典行按产品的 JSONL schema（h/n/s/t/mod/ctx/p）拼出来。
    /// <para>
    /// <c>h</c> 用产品自己的 <see cref="Fnv1a.Hash"/> 算，<c>s</c> 全部是 ASCII —— 于是
    /// <c>n == s</c> 成立（NFKC 对 ASCII 是恒等），断言里不需要再算一遍归一化。
    /// </para>
    /// </summary>
    internal static class Env
    {
        private const string Language = "zh-cn";

        /// <summary>把 KSP 根指到本场景目录（全局静态字段，所以场景只能串行）。</summary>
        internal static void Begin(string gameRoot)
        {
            Directory.CreateDirectory(gameRoot);
            KSPUtil.ApplicationRootPath = gameRoot;
        }

        internal static string LangDir()
        {
            string dir = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "UI18n", "Packs", Language);
            Directory.CreateDirectory(dir);
            return dir;
        }

        internal static void WritePack(string fileName, params string[] rows)
        {
            File.WriteAllLines(Path.Combine(LangDir(), fileName), rows, new UTF8Encoding(false));
        }

        internal static void WritePolicies(string json)
        {
            File.WriteAllText(Path.Combine(LangDir(), ModPolicies.FileName), json, new UTF8Encoding(false));
        }

        /// <summary>拼一行字典 JSONL（字段顺序固定，便于人眼对拍）。</summary>
        internal static string Row(string source, string translation, string mod, string ctx, bool pattern)
        {
            var sb = new StringBuilder(160);
            sb.Append('{');
            sb.Append("\"h\":\"").Append(Fnv1a.Hash(source).ToString("x16", CultureInfo.InvariantCulture)).Append('"');
            sb.Append(",\"n\":").Append(Quote(source));
            sb.Append(",\"s\":").Append(Quote(source));
            sb.Append(",\"t\":").Append(Quote(translation));
            if (!string.IsNullOrEmpty(mod))
            {
                sb.Append(",\"mod\":").Append(Quote(mod));
            }

            if (!string.IsNullOrEmpty(ctx))
            {
                sb.Append(",\"ctx\":").Append(Quote(ctx));
            }

            if (pattern)
            {
                sb.Append(",\"p\":1");
            }

            sb.Append('}');
            return sb.ToString();
        }

        internal static string Row(string source, string translation)
        {
            return Row(source, translation, null, null, false);
        }

        internal static string Row(string source, string translation, string mod)
        {
            return Row(source, translation, mod, null, false);
        }

        internal static string Row(string source, string translation, string mod, string ctx)
        {
            return Row(source, translation, mod, ctx, false);
        }

        private static string Quote(string text)
        {
            var sb = new StringBuilder(text.Length + 2);
            sb.Append('"');
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"' || c == '\\')
                {
                    sb.Append('\\').Append(c);
                }
                else
                {
                    sb.Append(c);
                }
            }

            sb.Append('"');
            return sb.ToString();
        }
    }
}
