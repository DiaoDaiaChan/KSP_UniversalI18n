using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using UI18n.Core.Capture;
using UI18n.Core.Diagnostics;
using Xunit;

namespace UI18n.Core.Tests
{
    /// <summary>
    /// task-34：捕获质量三缺陷的回归用例。
    /// <list type="number">
    /// <item>caller 必须落在真实调用者上（不能是我们自己的 Harmony 补丁 <c>*_PatchN</c>）；</item>
    /// <item>已经是中文的串必须被 <c>already-cjk</c> 过滤掉；</item>
    /// <item>动态文本要打上 <c>template</c> / <c>reason</c> / <c>bucket</c> 三个标记，数据一条不丢。</item>
    /// </list>
    /// </summary>
    public class CaptureQualityTests
    {
        // ================= 缺陷 1：caller 帧跳过规则 =================

        /// <summary>round 8 的真实形态：栈顶是我们自己的补丁 + Harmony 生成的 <c>*_Patch1</c>，真调用者在更下面。</summary>
        [Fact]
        public void Caller_SkipsOurPatchAndHarmonyFrame_AndLandsOnModType()
        {
            var frames = new List<CaptureFrame>
            {
                new CaptureFrame("UI18n.Runtime.Patches.TextPatches", "UnityTextPrefix"),
                new CaptureFrame("UI18n.Runtime.Translator", "Translate"),
                new CaptureFrame("TMP_Text", "TMPro.TMP_Text.set_text_Patch1"),
                new CaptureFrame("UnityEngine.UI", "Text"),
                new CaptureFrame("RCSBuildAid", "RCSBuildAidEditor")
            };

            Assert.Equal("RCSBuildAid.RCSBuildAidEditor", CaptureCaller.PickCaller(frames));
        }

        /// <summary>Unity / TMPro / BCL 转发层一律跳过：它们对「文本来自哪个 mod」没有信息量。</summary>
        [Fact]
        public void Caller_SkipsUnityAndSystemFrames()
        {
            var frames = new List<CaptureFrame>
            {
                new CaptureFrame("UnityEngine", "GUI"),
                new CaptureFrame("UnityEngine.IMGUIModule", "GUILayout"),
                new CaptureFrame("TMPro", "TMP_Text"),
                new CaptureFrame("System.Reflection", "RuntimeMethodInfo"),
                new CaptureFrame("MonoMod.Utils", "DMD"),
                new CaptureFrame("Kerbalism", "KerbalismConfig")
            };

            Assert.Equal("Kerbalism.KerbalismConfig", CaptureCaller.PickCaller(frames));
        }

        /// <summary>全跳完 = <c>unknown</c>（绝不能把 <c>*_Patch1</c> 当调用者写进捕获文件）。</summary>
        [Fact]
        public void Caller_AllFramesSkipped_ReturnsUnknown()
        {
            var frames = new List<CaptureFrame>
            {
                new CaptureFrame("UI18n.Runtime.Patches.TextPatches", "UnityTextPrefix"),
                new CaptureFrame("HarmonyLib", "PatchProcessor"),
                new CaptureFrame("UnityEngine", "GUI")
            };

            Assert.Equal(CaptureCaller.Unknown, CaptureCaller.PickCaller(frames));
            Assert.Equal("unknown", CaptureCaller.PickCaller(frames));
            Assert.Equal(CaptureCaller.Unknown, CaptureCaller.PickCaller(null));
            Assert.Equal(CaptureCaller.Unknown, CaptureCaller.PickCaller(new List<CaptureFrame>()));
        }

        /// <summary>逐帧判据（含「前缀必须整段匹配」的边界：<c>UI18nSomething</c> 不是我们的命名空间）。</summary>
        [Theory]
        [InlineData("UI18n.Runtime.Translator", "Translate", true)]
        [InlineData("UI18n", "Main", true)]
        [InlineData("HarmonyLib", "PatchProcessor", true)]
        [InlineData("MonoMod.Utils", "DMD", true)]
        [InlineData("UnityEngine", "GUI", true)]
        [InlineData("UnityEngine.UI", "Text", true)]
        [InlineData("TMPro", "TMP_Text", true)]
        [InlineData("System.Reflection", "RuntimeMethodInfo", true)]
        [InlineData("UI18nSomethingElse", "Foo", false)]
        [InlineData("RCSBuildAid", "RCSBuildAidEditor", false)]
        [InlineData("ScreenMessages", "PostScreenMessage", false)]
        [InlineData("UnityEngineX", "Foo", false)]
        [InlineData("Foo", "Bar_Patch2", true)]
        [InlineData("", "Foo", true)]
        [InlineData("Foo", "", true)]
        public void Caller_ShouldSkipFrame_Matrix(string typeFullName, string methodName, bool expected)
        {
            Assert.Equal(expected, CaptureCaller.ShouldSkipFrame(typeFullName, methodName));
        }

        /// <summary>真栈上取一次：结果绝不能是补丁帧、也不能是我们自己的代码（不校验具体值，因为跑测试的宿主在变）。</summary>
        [Fact]
        public void Caller_FromStack_NeverReturnsPatchOrOurOwnFrames()
        {
            string caller = CaptureCaller.FromStack(0);

            Assert.False(string.IsNullOrEmpty(caller));
            Assert.DoesNotContain("_Patch", caller, StringComparison.Ordinal);
            Assert.False(caller.StartsWith("UI18n", StringComparison.Ordinal), "取到了我们自己的帧：" + caller);
        }

        // ================= 缺陷 2：already-cjk 过滤 =================

        /// <summary>round 8 实测混进来的 26+ 条中文，必须全部按 <c>already-cjk</c> 拒掉。</summary>
        [Theory]
        [InlineData("设置")]
        [InlineData("性能")]
        [InlineData("正在加载…")]
        [InlineData("正在加载资源")]
        [InlineData("正在加载经验系统")]
        [InlineData("删除全部信息")]
        [InlineData("扩展包加载完成！")]
        [InlineData("加载中 50%")]
        [InlineData("中文 mixed with English")]
        public void Filter_ChineseText_IsRejectedAsAlreadyCjk(string text)
        {
            string reason = CaptureFilter.RejectReason(text, text, null);

            Assert.Equal(CaptureFilter.ReasonAlreadyCjk, reason);
            Assert.False(CaptureFilter.ShouldCapture(text, text));
            Assert.True(CaptureFilter.IsAlreadyCjk(text));
        }

        /// <summary>英文/数字/路径不受这条规则影响（否则会把真正的候选也一起丢掉）。</summary>
        [Theory]
        [InlineData("Searching assets to load...")]
        [InlineData("Loading sound assets...")]
        [InlineData("Extracting patches")]
        [InlineData("Vessel mass")]
        [InlineData("Debug Toolbar")]
        public void Filter_EnglishText_IsNotAlreadyCjk(string text)
        {
            Assert.NotEqual(CaptureFilter.ReasonAlreadyCjk, CaptureFilter.RejectReason(text, text, null));
            Assert.False(CaptureFilter.IsAlreadyCjk(text));
        }

        /// <summary>归一化后的文本是英文、原文带中文时也要拒（两边都查）。</summary>
        [Fact]
        public void Filter_ChineseOnlyInSource_IsStillRejected()
        {
            string reason = CaptureFilter.RejectReason("Settings", "设置 Settings", null);

            Assert.Equal(CaptureFilter.ReasonAlreadyCjk, reason);
        }

        /// <summary>假名/全角标点同样属于「别人已经处理好」的 CJK 文本。</summary>
        [Theory]
        [InlineData("読み込み中")]
        [InlineData("ロード中")]
        [InlineData("。")]
        public void Filter_KanaAndCjkPunctuation_AreTreatedAsCjk(string text)
        {
            Assert.True(CaptureFilter.IsAlreadyCjk(text));
        }

        // ================= 缺陷 3：template / reason / bucket =================

        /// <summary>round 8 里那三类动态串：路径进 <c>never</c>，进度与计数进 <c>template</c>。</summary>
        [Theory]
        [InlineData("ReStockPlus/Parts/Control/RCS/restock-rcs-5way", true, "path", "never")]
        [InlineData("GameData/Squad/Parts/Command/mk1pod", true, "path", "never")]
        [InlineData("Loading sound asset 0/234", true, "progress", "template")]
        [InlineData("12 / 100", true, "progress", "template")]
        [InlineData("ModuleManager: 8448 patches applied", true, "counter", "template")]
        [InlineData("Pre patch init", false, "", "candidate")]
        [InlineData("Searching assets to load...", false, "", "candidate")]
        [InlineData("Extracting patches", false, "", "candidate")]
        public void Classify_AssignsTemplateReasonAndBucket(string text, bool template, string reason, string bucket)
        {
            bool actualTemplate;
            string actualReason;
            string actualBucket;
            CaptureClassify.Classify(text, out actualTemplate, out actualReason, out actualBucket);

            Assert.Equal(template, actualTemplate);
            Assert.Equal(reason, actualReason);
            Assert.Equal(bucket, actualBucket);
        }

        /// <summary>纯数字三段式不是路径（没有字母），是进度。</summary>
        [Fact]
        public void Classify_NumericSlashes_AreProgressNotPath()
        {
            Assert.False(CaptureClassify.IsPathLike("1/2/3"));
            Assert.True(CaptureClassify.IsProgressLike("1/2/3"));

            bool template;
            string reason;
            string bucket;
            CaptureClassify.Classify("1/2/3", out template, out reason, out bucket);
            Assert.True(template);
            Assert.Equal(CaptureClassify.ReasonProgress, reason);
        }

        /// <summary>记录对象自带分类，JSONL 行里三个新字段都在（且老字段不变）。</summary>
        [Fact]
        public void Record_And_Json_CarryClassificationFields()
        {
            var record = new CaptureRecord(0x1234UL, "ModuleManager: 8448 patches applied",
                                           "ModuleManager: 8448 patches applied", "TMPro.TMP_Text.set_text",
                                           "ModuleManager.ModuleManager", "2026-10-23T06:01:28Z");

            Assert.True(record.Template);
            Assert.Equal("counter", record.Reason);
            Assert.Equal(CaptureClassify.BucketTemplate, record.Bucket);

            string line = CaptureJson.ToJsonLine(record);
            using (JsonDocument doc = JsonDocument.Parse(line))
            {
                JsonElement root = doc.RootElement;
                Assert.Equal("0000000000001234", root.GetProperty("h").GetString());
                Assert.Equal("ModuleManager: 8448 patches applied", root.GetProperty("s").GetString());
                Assert.Equal("ModuleManager.ModuleManager", root.GetProperty("caller").GetString());
                Assert.True(root.GetProperty("template").GetBoolean());
                Assert.Equal("counter", root.GetProperty("reason").GetString());
                Assert.Equal("template", root.GetProperty("bucket").GetString());
            }

            Assert.DoesNotContain("\n", line);
        }

        /// <summary>静态文本的 JSONL 行：<c>template:false</c>、<c>reason</c> 空串、<c>bucket:candidate</c>。</summary>
        [Fact]
        public void Json_StaticText_IsCandidate()
        {
            string line = CaptureJson.ToJsonLine(1UL, "Vessel mass", "Vessel mass", "GUILayout.DoLabel",
                                                 "RCSBuildAid.RCSBuildAidEditor", 3, "2026-10-23T06:01:28Z");
            using (JsonDocument doc = JsonDocument.Parse(line))
            {
                JsonElement root = doc.RootElement;
                Assert.False(root.GetProperty("template").GetBoolean());
                Assert.Equal(string.Empty, root.GetProperty("reason").GetString());
                Assert.Equal("candidate", root.GetProperty("bucket").GetString());
            }
        }

        /// <summary>
        /// 捕获器按桶计数，<c>Describe()</c> 里能看到分布。
        /// v1.5（docs/08 D-T2）：桶只表示<b>优先级</b>，不表示要不要留 —— 连中文串（<c>already-cjk</c>）
        /// 也照样建记录，<c>Filtered</c> 恒为 0。
        /// </summary>
        [Fact]
        public void Store_CountsBuckets_AndRecordsEveryString()
        {
            var store = new CaptureStore();
            string seen = "2026-10-23T06:01:28Z";

            Assert.True(store.Record(1UL, "Vessel mass", "Vessel mass", "GUILayout.DoLabel", "A.B", seen));
            Assert.True(store.Record(2UL, "ModuleManager: 8448 patches applied", "ModuleManager: 8448 patches applied",
                                     "TMPro.TMP_Text.set_text", "A.B", seen));
            Assert.True(store.Record(3UL, "ReStockPlus/Parts/Control/RCS/x", "ReStockPlus/Parts/Control/RCS/x",
                                     "TMPro.TMP_Text.set_text", "A.B", seen));

            // v1.5 之前这条返回 false（CaptureFilter 判 already-cjk 直接拒收）；现在必须留档。
            Assert.True(store.Record(4UL, "设置", "设置", "TMPro.TMP_Text.set_text", "A.B", seen));

            Assert.Equal(4, store.Distinct);
            Assert.Equal(2, store.Candidates);   // "Vessel mass" + "设置"（CaptureClassify 不看语种）
            Assert.Equal(1, store.Templates);    // 带数字的 patch 计数器
            Assert.Equal(1, store.Never);         // 路径形态
            Assert.Equal(0L, store.Filtered);

            string describe = store.Describe();
            Assert.Contains("candidate=2", describe, StringComparison.Ordinal);
            Assert.Contains("template=1", describe, StringComparison.Ordinal);
            Assert.Contains("never=1", describe, StringComparison.Ordinal);
            Assert.DoesNotContain("filtered", describe, StringComparison.Ordinal);
        }

        /// <summary>重复未命中只累加计数，不会重复建桶（分桶是 distinct 口径）。</summary>
        [Fact]
        public void Store_RepeatedMiss_DoesNotDoubleCountBuckets()
        {
            var store = new CaptureStore();
            store.Record(7UL, "Pre patch init", "Pre patch init", "TMPro.TMP_Text.set_text", "A.B", "2026-10-23T06:01:28Z");
            store.CountMiss(7UL);
            store.CountMiss(7UL);

            Assert.Equal(1, store.Distinct);
            Assert.Equal(1, store.Candidates);
            Assert.Equal(3, store.ToArray()[0].Count);
        }

        // ================= prefab 三档口径 =================

        /// <summary>三档分别统计：只有第一档（在场景里且激活）算缺口。</summary>
        [Fact]
        public void PrefabGap_Tiers_SeparateActiveFromAssets()
        {
            var legacy = new TextSinkTracker();
            legacy.Note("Seen Through Setter");

            var active = new List<string> { "Seen Through Setter", "Visible Never Set", "Visible Never Set" };
            var inactive = new List<string> { "Hidden Never Set" };
            var assets = new List<string> { "II", "lorem ipsum dolor" };

            PrefabGapReport report = PrefabGapReport.AnalyzeTiers(active, inactive, assets, legacy, 20,
                                                                  delegate(string text) { return text == "Visible Never Set"; });

            Assert.Equal(3, report.ActiveComponents);
            Assert.Equal(2, report.ActiveNeverSetText);
            Assert.Equal(1, report.InactiveComponents);
            Assert.Equal(1, report.InactiveNeverSetText);
            Assert.Equal(2, report.AssetComponents);
            Assert.Equal(2, report.AssetNeverSetText);
            Assert.Equal(2, report.RecoverableFromSceneScan);
            Assert.Equal(3, report.Components);
            Assert.Equal(2, report.NeverSetText);

            string describe = report.Describe();
            Assert.Contains("active=3 (never=2, scene-scan recoverable=2)", describe, StringComparison.Ordinal);
            Assert.Contains("inactive=1 (never=1)", describe, StringComparison.Ordinal);
            Assert.Contains("assets=2 (never=2)", describe, StringComparison.Ordinal);
            Assert.Equal(3, report.TierLines().Length);
            Assert.Single(report.Samples);
            Assert.Equal("Visible Never Set", report.Samples[0]);
        }

        /// <summary>旧口径（<c>Analyze</c>）保持原样：全部当第一档，输出旧格式。</summary>
        [Fact]
        public void PrefabGap_LegacyAnalyze_KeepsOldFormat()
        {
            PrefabGapReport report = PrefabGapReport.Analyze(new List<string> { "A never seen", "B never seen" }, null, 5);

            Assert.Equal(2, report.Components);
            Assert.Equal(2, report.NeverSetText);
            Assert.Equal("prefab gap: components=2, never-through-set_text=2", report.Describe());
            Assert.Contains("tier 2", report.TierLines()[1], StringComparison.Ordinal);
            Assert.Contains("components=0, never-through-set_text=0", report.TierLines()[1], StringComparison.Ordinal);
            Assert.Contains("components=0, never-through-set_text=0", report.TierLines()[2], StringComparison.Ordinal);
        }

        // ================= task-35：caller 聚合（退出 dump 的一行证据） =================

        /// <summary>
        /// 退出 dump 的 <c>capture callers:</c> 行：distinct 只数真实调用者，<c>unknown</c> 单独计，
        /// top5 按「来自它的 distinct 文本条数」降序（同条数按 Ordinal 升序，保证可复现）。
        /// </summary>
        [Fact]
        public void Store_CallerSummary_AggregatesAndSeparatesUnknown()
        {
            var store = new CaptureStore();
            string seen = "2026-10-23T06:01:28Z";

            Assert.True(store.Record(1UL, "Vessel mass", "Vessel mass", "GUILayout.DoLabel", "RCSBuildAid.RCSBuildAidEditor.OnGUI", seen));
            Assert.True(store.Record(2UL, "Part list", "Part list", "GUILayout.DoLabel", "RCSBuildAid.RCSBuildAidEditor.OnGUI", seen));
            Assert.True(store.Record(3UL, "Basic Options", "Basic Options", "GUILayout.DoLabel", "KSPWheel.KSPWheelGUI.OnWindow", seen));
            Assert.True(store.Record(4UL, "Damage Options", "Damage Options", "GUILayout.DoButton", "KSPWheel.KSPWheelGUI.OnWindow", seen));
            Assert.True(store.Record(5UL, "Console", "Console", "GUI.DoModalWindow", "KSP.DebugToolbar.OnGUI", seen));
            Assert.True(store.Record(6UL, "unknown caller text", "unknown caller text", "GUI.Box", string.Empty, seen));
            Assert.True(store.Record(7UL, "no stack text", "no stack text", "GUI.Box", "unknown", seen));

            Assert.Equal(3, store.DistinctCallers);
            Assert.Equal(2, store.UnknownCallers);

            string line = store.DescribeCallers();
            Assert.StartsWith("capture callers: distinct=3, top5=[", line, StringComparison.Ordinal);
            Assert.EndsWith("], unknown=2", line, StringComparison.Ordinal);
            // 同条数（2 条）按 Ordinal 升序：KSPWheel… 在 RCSBuildAid… 之前
            Assert.True(line.IndexOf("KSPWheel.KSPWheelGUI.OnWindow x2", StringComparison.Ordinal)
                        < line.IndexOf("RCSBuildAid.RCSBuildAidEditor.OnGUI x2", StringComparison.Ordinal),
                        "same-count callers must be ordered ordinally: " + line);
            Assert.Contains("KSP.DebugToolbar.OnGUI x1", line, StringComparison.Ordinal);
        }

        /// <summary>空捕获器也要打出完整行（证明这条代码路径跑过），不抛。</summary>
        [Fact]
        public void Store_CallerSummary_EmptyStore_IsWellFormed()
        {
            var store = new CaptureStore();

            Assert.Equal(0, store.DistinctCallers);
            Assert.Equal(0, store.UnknownCallers);
            Assert.Empty(store.TopCallers(5));
            Assert.Equal("capture callers: distinct=0, top5=[], unknown=0", store.DescribeCallers());
            Assert.Empty(store.TopCallers(0));
            Assert.Empty(store.TopCallers(-3));
        }

        /// <summary>top5 最多 5 项；重复 miss 不增加权重（权重是 distinct 文本条数）。</summary>
        [Fact]
        public void Store_CallerSummary_TopFiveOnly_AndRepeatedMissDoesNotWeighIn()
        {
            var store = new CaptureStore();
            string seen = "2026-10-23T06:01:28Z";
            for (int i = 0; i < 7; i++)
            {
                store.Record((ulong)(100 + i), "text " + i.ToString(), "text " + i.ToString(), "GUI.Label", "Mod.Caller" + i.ToString(), seen);
            }

            store.CountMiss(100UL);
            store.CountMiss(100UL);

            Assert.Equal(7, store.DistinctCallers);
            Assert.Equal(5, store.TopCallers(5).Length);
            Assert.Equal(7, store.TopCallers(100).Length);
            Assert.Equal("Mod.Caller0 x1", store.TopCallers(5)[0]);
        }

        // ================= task-42 (a)：富文本标签先剥再判路径 =================

        /// <summary>白名单与 python <c>tools/i18n_tools/resolve_keyrefs.py::_TMP_TAG_RE</c> 同口径（大小写敏感）。</summary>
        [Theory]
        [InlineData("<b>bold</b>", "bold")]
        [InlineData("<b><color=#79C0FF>Cinematic</color></b>", "Cinematic")]
        [InlineData("<size=20>Title</size>", "Title")]
        [InlineData("<material=foo>x</material>", "x")]
        [InlineData("<link=bar>Go</link>", "Go")]
        [InlineData("<s>gone</s>", "gone")]
        [InlineData("<u>under</u>", "under")]
        [InlineData("<i>ital</i>", "ital")]
        [InlineData("<sprite=icon>", "")]
        [InlineData("plain text", "plain text")]
        [InlineData("", "")]
        public void StripRichText_RemovesWhitelistedTags(string text, string expected)
        {
            Assert.Equal(expected, CaptureClassify.StripRichText(text));
        }

        /// <summary>null 安全；大小写敏感 + 白名单外的尖括号原样保留（与 python 正则一致）。</summary>
        [Fact]
        public void StripRichText_NullIsEmpty_AndUnknownTagsSurvive()
        {
            Assert.Equal(string.Empty, CaptureClassify.StripRichText(null));
            Assert.Equal("<COLOR=red>x</COLOR>", CaptureClassify.StripRichText("<COLOR=red>x</COLOR>"));
            Assert.Equal("a<br>b", CaptureClassify.StripRichText("a<br>b"));
        }

        /// <summary>
        /// 空格形式的<b>开标签</b>（<c>&lt;quad width=2&gt;</c>、<c>&lt;sprite name=icon&gt;</c>）两侧白名单都不匹配 ——
        /// 这是<b>刻意的口径一致</b>：python <c>_TMP_TAG_RE</c> 只认 <c>&lt;tag&gt;</c> 与 <c>&lt;tag=value&gt;</c>，
        /// 我们自己改宽会让离线重算与运行时分类给出不同结论。要改必须两边同时改。
        /// </summary>
        [Theory]
        [InlineData("<quad width=2>x", "<quad width=2>x")]
        [InlineData("<sprite name=icon>", "<sprite name=icon>")]
        [InlineData("x < sprite > y", "x < sprite > y")]
        public void StripRichText_SpaceFormOpenerIsLeftAlone_LikePython(string text, string expected)
        {
            Assert.Equal(expected, CaptureClassify.StripRichText(text));
        }

        /// <summary>
        /// 同一个串里「开标签带空格、闭标签不带空格」时只剥闭标签 —— 期望值不是我推的，
        /// 是<b>跑 python 拿到的实测值</b>：
        /// <code>
        /// &gt;&gt;&gt; _TMP_TAG_RE.sub('', '&lt;quad width=2&gt;x&lt;/quad&gt;')
        /// '&lt;quad width=2&gt;x'
        /// </code>
        /// 这条断言的作用是把「C# 与 python 会给出同一个结果」钉死：哪天有人把 C# 正则改宽到接受空格，
        /// 这条会先红，而不是等到 reports/coverage.md 与运行时 reason 分布自相矛盾才发现。
        /// </summary>
        [Fact]
        public void StripRichText_SpaceFormOpener_ButBareCloserStillMatches()
        {
            Assert.Equal("<quad width=2>x", CaptureClassify.StripRichText("<quad width=2>x</quad>"));
        }

        /// <summary>标签白名单是与 python 的跨语言契约：改这里必须同时改 <c>resolve_keyrefs.py</c>。</summary>
        [Fact]
        public void TmpTagPattern_EqualsPythonRegex()
        {
            Assert.Equal("</?(?:b|i|u|s|color|size|material|quad|sprite|link)(?:=[^>]*)?>", CaptureClassify.TmpTagPattern);
        }

        [Fact]
        public void HasVisibleText_IgnoresTagsAndWhitespaceOnly()
        {
            Assert.False(CaptureClassify.HasVisibleText(null));
            Assert.False(CaptureClassify.HasVisibleText("<b></b>"));
            Assert.False(CaptureClassify.HasVisibleText("<b>   </b>"));
            Assert.False(CaptureClassify.HasVisibleText("   "));
            Assert.True(CaptureClassify.HasVisibleText("<b>x</b>"));
        }

        /// <summary>task-42 的回归本体：富文本曾被 <c>&lt;/color&gt;</c> 的斜杠判成 path（23 个会话 35 个 h）。</summary>
        [Theory]
        [InlineData("<b><color=#79C0FF>Cinematic</color></b>", false)]
        [InlineData("<b>Cinematic</b>", false)]
        [InlineData("<size=20>a/b</size>", false)]
        [InlineData("<link=foo>bar</link>", false)]
        [InlineData("<color=red>one/two</color>", false)]
        [InlineData("<color=red>ReStockPlus/Parts/RCS/foo</color>", true)]
        [InlineData("<b><color=#fff>GameData/Squad/Parts/x</color></b>", true)]
        [InlineData("ReStockPlus/Parts/Control/RCS/restock-rcs-5way", true)]
        [InlineData("GameData/Squad/Parts/Command/mk1pod", true)]
        [InlineData("a/b", false)]
        [InlineData("1/2/3", false)]
        public void IsPathLike_StripsTagsFirst(string text, bool expected)
        {
            Assert.Equal(expected, CaptureClassify.IsPathLike(text));
        }

        /// <summary>剥完为空/纯空白 → <c>empty</c>/<c>never</c>（不是 path）；剥完是正常文案 → candidate 且 template 不受标签里数字影响。</summary>
        [Theory]
        [InlineData("<b><color=#79C0FF>Cinematic</color></b>", false, "", "candidate")]
        [InlineData("<b>Cinematic</b>", false, "", "candidate")]
        [InlineData("<b>ReStockPlus/Parts/RCS/foo</b>", false, "path", "never")]
        [InlineData("<b></b>", false, "empty", "never")]
        [InlineData("</color>", false, "empty", "never")]
        [InlineData("<b>   </b>", false, "empty", "never")]
        [InlineData("<color=#79C0FF>ModuleManager: 8448 patches applied</color>", true, "counter", "template")]
        public void Classify_RichText_StripsBeforeJudging(string text, bool template, string reason, string bucket)
        {
            bool actualTemplate;
            string actualReason;
            string actualBucket;
            CaptureClassify.Classify(text, out actualTemplate, out actualReason, out actualBucket);

            Assert.Equal(template, actualTemplate);
            Assert.Equal(reason, actualReason);
            Assert.Equal(bucket, actualBucket);
        }

        /// <summary><c>empty</c> 是新原因码：取值与 <c>CaptureFilter.ReasonEmpty</c> 一致，但判定点是「剥完标签才空」。</summary>
        [Fact]
        public void Classify_ReasonEmpty_MatchesFilterConstant()
        {
            Assert.Equal("empty", CaptureClassify.ReasonEmpty);
            Assert.Equal(CaptureFilter.ReasonEmpty, CaptureClassify.ReasonEmpty);
        }

        // ================= task-42 (b)：caller 用 DeclaringType 反射拼名 =================

        /// <summary>已构造泛型类型：结果不带程序集限定名、逗号、方括号、arity 反引号。</summary>
        [Fact]
        public void CleanTypeName_ConstructedGeneric_HasNoAssemblyInfo()
        {
            string name = CaptureCaller.CleanTypeName(typeof(Dictionary<string, int>));

            Assert.Equal("System.Collections.Generic.Dictionary", name);
            Assert.DoesNotContain(",", name, StringComparison.Ordinal);
            Assert.DoesNotContain("]", name, StringComparison.Ordinal);
            Assert.DoesNotContain("[", name, StringComparison.Ordinal);
            Assert.DoesNotContain("Culture=", name, StringComparison.Ordinal);
            Assert.DoesNotContain("Version=", name, StringComparison.Ordinal);
            Assert.DoesNotContain("`", name, StringComparison.Ordinal);
        }

        /// <summary>反证：v1.1 走的是 <c>Type.FullName</c>，它确实带程序集限定名（那 5 条残片的来源）。</summary>
        [Fact]
        public void FullName_OfConstructedGeneric_DoesCarryAssemblyInfo()
        {
            string fullName = typeof(Dictionary<string, int>).FullName;

            Assert.Contains("Culture=", fullName, StringComparison.Ordinal);
            Assert.Contains("`2", fullName, StringComparison.Ordinal);
        }

        /// <summary>嵌套泛型：<c>Ns.Outer.Inner</c>（用点连接，不用反射的 <c>+</c>；外层 arity 去掉）。</summary>
        [Fact]
        public void CleanTypeName_NestedGeneric_KeepsOuterChainWithoutArity()
        {
            string name = CaptureCaller.CleanTypeName(typeof(OuterGeneric<int>.Inner));

            Assert.Equal("UI18n.Core.Tests.CaptureQualityTests.OuterGeneric.Inner", name);
            Assert.DoesNotContain("`", name, StringComparison.Ordinal);
            Assert.DoesNotContain("+", name, StringComparison.Ordinal);
        }

        [Fact]
        public void CleanTypeName_NullAndPlainType()
        {
            Assert.Equal(string.Empty, CaptureCaller.CleanTypeName(null));
            Assert.Equal("UI18n.Core.Tests.CaptureQualityTests", CaptureCaller.CleanTypeName(typeof(CaptureQualityTests)));
        }

        [Fact]
        public void StripArity_RemovesGenericSuffix()
        {
            Assert.Equal("List", CaptureCaller.StripArity("List`1"));
            Assert.Equal("Dictionary", CaptureCaller.StripArity("Dictionary`2"));
            Assert.Equal("Plain", CaptureCaller.StripArity("Plain"));
            Assert.Equal(string.Empty, CaptureCaller.StripArity(null));
        }

        [Fact]
        public void DescribeMethod_JoinsCleanTypeAndMethod()
        {
            MethodInfo method = typeof(Dictionary<string, int>).GetMethod("Add", new[] { typeof(string), typeof(int) });

            Assert.Equal("System.Collections.Generic.Dictionary.Add", CaptureCaller.DescribeMethod(method));
            Assert.Equal(string.Empty, CaptureCaller.DescribeMethod(null));
        }

        /// <summary>导出格式是 <c>Namespace.Type.Method</c>：python 侧 <c>infer_mods()</c> 拿第一段当 mod 名。</summary>
        [Fact]
        public void PickCaller_ExportsFullTypeName()
        {
            var frames = new List<CaptureFrame>
            {
                new CaptureFrame("UI18n.Runtime.Translator", "Translate"),
                new CaptureFrame("KSPWheel.KSPWheelGUI", "OnWindow")
            };

            Assert.Equal("KSPWheel.KSPWheelGUI.OnWindow", CaptureCaller.PickCaller(frames));
            Assert.Equal("KSPWheel.KSPWheelGUI.OnWindow", frames[1].ToString());
        }

        /// <summary>真栈结果里不许出现程序集限定名的任何特征（v1.1 实测 5 条 <c>…PublicKeyToken=null]].X</c>）。</summary>
        [Fact]
        public void FromStack_HasNoAssemblyQualifiedFragments()
        {
            string caller = CaptureCaller.FromStack(0);

            Assert.False(string.IsNullOrEmpty(caller));
            Assert.DoesNotContain(",", caller, StringComparison.Ordinal);
            Assert.DoesNotContain("]", caller, StringComparison.Ordinal);
            Assert.DoesNotContain("`", caller, StringComparison.Ordinal);
            Assert.DoesNotContain("Culture=", caller, StringComparison.Ordinal);
            Assert.DoesNotContain("Version=", caller, StringComparison.Ordinal);
        }

        /// <summary>嵌套泛型类型（<c>OuterGeneric&lt;T&gt;.Inner</c>）—— 只为 <see cref="CleanTypeName_NestedGeneric_KeepsOuterChainWithoutArity"/> 存在。</summary>
        private class OuterGeneric<T>
        {
            internal class Inner
            {
            }
        }
    }
}
