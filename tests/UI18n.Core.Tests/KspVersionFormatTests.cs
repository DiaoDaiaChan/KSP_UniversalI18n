using UI18n.Core.Diagnostics;
using Xunit;

namespace UI18n.Core.Tests
{
    /// <summary>
    /// <see cref="KspVersionFormat"/>：KSP 版本串的拼法。
    /// <para>
    /// 这一层能在测试工程里测，是因为它不碰 UnityEngine；Runtime 侧真正去读
    /// <c>Versioning</c> 的那一步（<c>UI18n.Runtime.Diagnostics.KspVersionInfo</c>）需要
    /// Assembly-CSharp，测试工程里没有，只能在游戏里验。
    /// </para>
    /// </summary>
    public class KspVersionFormatTests
    {
        // 正常形态：diag 行与投稿 bundle 里要的就是这三个数拼起来。
        [Theory]
        [InlineData(1, 12, 4, "1.12.4")]
        [InlineData(1, 12, 5, "1.12.5")]
        [InlineData(1, 0, 0, "1.0.0")]
        [InlineData(1, 11, 2, "1.11.2")]
        [InlineData(2, 0, 0, "2.0.0")]
        [InlineData(10, 20, 30, "10.20.30")]
        [InlineData(1, 12, 0, "1.12.0")]
        public void FromParts_JoinsWithDots(int major, int minor, int revision, string expected)
        {
            Assert.Equal(expected, KspVersionFormat.FromParts(major, minor, revision));
        }

        // 全 0 = Unity 还没初始化 / 字段读不到：绝不放 "0.0.0" 出去（那会被读者当成真版本）。
        [Fact]
        public void FromParts_AllZero_IsUnknown()
        {
            Assert.Equal("?", KspVersionFormat.FromParts(0, 0, 0));
            Assert.True(KspVersionFormat.IsUnknown(KspVersionFormat.FromParts(0, 0, 0)));
        }

        // 任何一段为负、或 major 非正：不是合法版本号，一律回退占位串（负向输入不抛）。
        [Theory]
        [InlineData(0, 12, 4)]
        [InlineData(-1, 12, 4)]
        [InlineData(1, -1, 4)]
        [InlineData(1, 12, -1)]
        [InlineData(int.MinValue, 0, 0)]
        public void FromParts_InvalidParts_FallBackWithoutThrowing(int major, int minor, int revision)
        {
            Assert.Equal(KspVersionFormat.Unknown, KspVersionFormat.FromParts(major, minor, revision));
        }

        [Theory]
        [InlineData("1.12.4", false)]
        [InlineData("2.0.0", false)]
        [InlineData("?", true)]
        [InlineData("", true)]
        [InlineData(null, true)]
        public void IsUnknown_RecognizesPlaceholder(string version, bool expected)
        {
            Assert.Equal(expected, KspVersionFormat.IsUnknown(version));
        }

        // 口径注释里写死的关键事实：本机 1.12.5 的 bundleVersion 是 1.12.4，
        // 两个口径拼出来的串必须不同 —— 否则 diag 行里 bundle= 那一项就没有存在意义。
        [Fact]
        public void AuthoritativeAndBundleVersions_CanDisagree()
        {
            string authoritative = KspVersionFormat.FromParts(1, 12, 5);
            string bundleVersion = "1.12.4";

            Assert.NotEqual(authoritative, bundleVersion);
            Assert.Equal("1.12.5", authoritative);
        }
    }
}
