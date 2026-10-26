using System;
using System.Collections.Generic;
using UI18n.Core.Submission;
using Xunit;

namespace UI18n.Core.Tests
{
    /// <summary>
    /// 捕获行的 caller 形如 <c>Namespace.Type.Method</c>，而 modId 是包文件名派生的、可以含点
    /// （AtmosphereAutopilot.UI）。所以归属规则 = 取 caller 第一段，精确命中优先，其次双向包含最长者，
    /// 都不中则 UI18n-Unattributed。口径与 tools/i18n_tools/capture_ingest.py 的 infer_mods() 一致，
    /// 不依赖 packs/mods.json（它现在 assemblies/folders/aliases 全是占位空表）。
    /// </summary>
    public class ModResolverTests
    {
        private static readonly string[] Known =
        {
            "Squad",
            "FerramAerospaceResearch",
            "AtmosphereAutopilot",
            "AtmosphereAutopilot.UI",
            "UI18n-ConfigCache",
        };

        [Fact]
        public void ExactFirstSegment_Wins()
        {
            ModResolver resolver = new ModResolver(Known);
            Assert.Equal("FerramAerospaceResearch",
                resolver.Resolve("FerramAerospaceResearch.FARDebugAndSettings.Update"));
            Assert.Equal("Squad", resolver.Resolve("Squad.ModuleEngines.SomeMethod"));
        }

        [Fact]
        public void ExactBeatsLongerDottedSibling()
        {
            ModResolver resolver = new ModResolver(Known);
            // 第一段恰好是 AtmosphereAutopilot 这个 modId 时，不要因为它还是 AtmosphereAutopilot.UI 的前缀就升级。
            Assert.Equal("AtmosphereAutopilot", resolver.Resolve("AtmosphereAutopilot.FlyByWire.Update"));
        }

        [Fact]
        public void DottedModId_IsMatchedByPrefix()
        {
            ModResolver resolver = new ModResolver(new List<string> { "AtmosphereAutopilot.UI" });
            Assert.Equal("AtmosphereAutopilot.UI", resolver.Resolve("AtmosphereAutopilot.UI.SomeClass.OnGUI"));
        }

        [Fact]
        public void CaseInsensitive()
        {
            ModResolver resolver = new ModResolver(Known);
            Assert.Equal("Squad", resolver.Resolve("squad.Foo.Bar"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("SomeUnknownAssembly.Type.Method")]
        public void UnknownCaller_IsUnattributed(string caller)
        {
            ModResolver resolver = new ModResolver(Known);
            Assert.Equal(ModResolver.Unattributed, resolver.Resolve(caller));
        }

        [Fact]
        public void NullCaller_IsUnattributed()
        {
            ModResolver resolver = new ModResolver(Known);
            Assert.Equal(ModResolver.Unattributed, resolver.Resolve(null));
        }

        [Fact]
        public void StaticResolve_MatchesInstance()
        {
            string caller = "FerramAerospaceResearch.FARDebugAndSettings.Update";
            Assert.Equal(new ModResolver(Known).Resolve(caller),
                ModResolver.Resolve(caller, new List<string>(Known)));
        }

        [Theory]
        [InlineData("A.B.C", "A")]
        [InlineData("NoDots", "NoDots")]
        [InlineData("", "")]
        public void FirstSegment_SplitsOnFirstDot(string caller, string expected)
        {
            Assert.Equal(expected, ModResolver.FirstSegment(caller));
        }

        [Fact]
        public void Count_IgnoresDuplicates()
        {
            ModResolver resolver = new ModResolver(new List<string> { "Squad", "squad", "SQUAD" });
            Assert.Equal(1, resolver.Count);
        }
    }
}
