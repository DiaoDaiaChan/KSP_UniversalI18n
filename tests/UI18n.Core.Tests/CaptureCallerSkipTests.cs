using System;
using System.Collections.Generic;
using UI18n.Core.Capture;
using Xunit;

namespace UI18n.Core.Tests
{
    /// <summary>
    /// 调用者归属的「跳过命名空间表」只能装自身与框架 —— 审计存疑项 R2 的落地断言。
    /// <para>
    /// <see cref="CaptureCaller.SkipNamespacePrefixes"/> 在形态上就是一张内建黑名单：谁的命名空间在这张表里，
    /// 谁的栈帧就被跳过。当前内容全是本产品与引擎/BCL 的命名空间，按审计口径不算违规；但它是
    /// 全仓库最容易被后人补一行 <c>"某Mod"</c> 的地方 —— 那一行会让归属逻辑从此对这个 mod 静默失明
    /// （不报错、只是永远归不到它），是典型的「一次性特判」入口。
    /// </para>
    /// <para>
    /// 所以这条用例把表**逐项钉死**：要加一项就必须改这里，并回答「为什么这不是某个 mod 的名字」。
    /// </para>
    /// </summary>
    public class CaptureCallerSkipTests
    {
        // 允许出现在表里的命名空间：本产品 + Harmony/MonoMod（补丁框架）+ Unity/TMP（引擎）+
        // System（BCL 转发层）。不含任何 KSP 本体或第三方 mod 的命名空间。
        private static readonly string[] Allowed =
        {
            "UI18n", "HarmonyLib", "MonoMod", "UnityEngine", "Unity", "TMPro", "System",
        };

        [Fact]
        public void SkipNamespacePrefixes_ContainsOnlyFrameworkNamespaces()
        {
            var allowed = new HashSet<string>(Allowed, StringComparer.Ordinal);
            var extra = new List<string>();
            foreach (string prefix in CaptureCaller.SkipNamespacePrefixes)
            {
                if (!allowed.Contains(prefix))
                {
                    extra.Add(prefix);
                }
            }

            Assert.True(extra.Count == 0,
                "跳过表里出现了非框架/自身命名空间：" + string.Join(", ", extra.ToArray())
                + "。若这是某个 mod 的名字，归属逻辑会对它静默失明（审计口径 V3），请改为数据驱动或直接删掉。");
        }

        [Fact]
        public void SkipNamespacePrefixes_KeepsEveryAllowedEntry()
        {
            // 反向也要钉：允许的项被删掉不会造成误归属，但通常是误删（Unity/TMP 转发层被放进来后
            // 归属会跳到更深一层的引擎帧，等于没归属）。删项要连着改这条用例。
            var actual = new HashSet<string>(CaptureCaller.SkipNamespacePrefixes, StringComparer.Ordinal);
            var missing = new List<string>();
            foreach (string name in Allowed)
            {
                if (!actual.Contains(name))
                {
                    missing.Add(name);
                }
            }

            Assert.True(missing.Count == 0, "跳过表缺少框架命名空间：" + string.Join(", ", missing.ToArray()));
        }

        [Fact]
        public void SkipNamespacePrefixes_HasNoDuplicatesOrBlanks()
        {
            Assert.NotEmpty(CaptureCaller.SkipNamespacePrefixes);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < CaptureCaller.SkipNamespacePrefixes.Length; i++)
            {
                string prefix = CaptureCaller.SkipNamespacePrefixes[i];
                Assert.False(string.IsNullOrWhiteSpace(prefix), "跳过表第 " + i + " 项为空");
                Assert.Equal(prefix.Trim(), prefix);
                // 前缀形态：不含点、不含路径分隔符（匹配逻辑按「前缀」或「前缀.」比，带点会永不命中）。
                Assert.DoesNotContain(".", prefix);
                Assert.DoesNotContain("/", prefix);
                Assert.True(seen.Add(prefix), "跳过表重复项：" + prefix);
            }
        }

        [Fact]
        public void SkipNamespacePrefix_CompatibilityConstant_IsInTheTable()
        {
            // 历史常量（单数）必须仍是表里的一项，否则两个入口会给出不同的归属。
            Assert.Contains(CaptureCaller.SkipNamespacePrefix,
                new List<string>(CaptureCaller.SkipNamespacePrefixes));
        }
    }
}
