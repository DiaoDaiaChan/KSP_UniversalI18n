using UI18n.Core;
using UI18n.Core.Update;
using Xunit;

namespace UI18n.Core.Tests
{
    /// <summary>
    /// 安装目录单源（v1.7 硬编码审计存疑项 R1 的落地断言）：<c>GameData/UI18n/</c> 这个字符串
    /// 以前在 <see cref="ZipSafety.ModRootPrefix"/> 与 Runtime 的 <c>ConfigLoader.ModFolder</c>
    /// 各写一遍。它不是「写死某个第三方 mod」（写的是我们自己），但如果两处漂移，
    /// 更新包会装到一个运行时并不读的目录 —— 而且没有任何报错，只是字典永远是空的。
    /// <para>
    /// 这里锁的是「形状 + 单源」：改目录名只该改 <see cref="ProductInfo"/> 一处，
    /// 改错了（漏斜杠、多一级、混反斜杠）必须红。
    /// </para>
    /// </summary>
    public class ProductInfoTests
    {
        [Fact]
        public void FolderName_IsSinglePlainSegment()
        {
            Assert.False(string.IsNullOrWhiteSpace(ProductInfo.FolderName));
            Assert.Equal(ProductInfo.FolderName, ProductInfo.FolderName.Trim());
            Assert.DoesNotContain("/", ProductInfo.FolderName);
            Assert.DoesNotContain("\\", ProductInfo.FolderName);
            Assert.DoesNotContain("..", ProductInfo.FolderName);
            // 带下划线前缀在 KSP 里是「开发期目录」的约定（打包脚本会跳过），产品目录不该是那种形态。
            Assert.NotEqual('_', ProductInfo.FolderName[0]);
        }

        [Fact]
        public void FolderPath_IsGameDataPlusFolderName()
        {
            Assert.Equal("GameData/" + ProductInfo.FolderName, ProductInfo.FolderPath);
            Assert.StartsWith("GameData/", ProductInfo.FolderPath);
            Assert.DoesNotContain("\\", ProductInfo.FolderPath);
            Assert.False(ProductInfo.FolderPath.EndsWith("/"), "FolderPath 不带结尾斜杠（RootPrefix 才带）");
        }

        [Fact]
        public void RootPrefix_IsFolderPathWithTrailingSlash()
        {
            Assert.Equal(ProductInfo.FolderPath + "/", ProductInfo.RootPrefix);
            Assert.EndsWith("/", ProductInfo.RootPrefix);
            // 单段目录：GameData/X/ 去掉前缀与结尾斜杠后不该再含分隔符。
            string inner = ProductInfo.RootPrefix.Substring("GameData/".Length);
            inner = inner.Substring(0, inner.Length - 1);
            Assert.DoesNotContain("/", inner);
        }

        [Fact]
        public void ZipSafety_ModRootPrefix_ComesFromProductInfo()
        {
            // 更新包校验用的前缀必须和产品身份是同一个字符串，否则「能装」与「能读」会脱钩。
            Assert.Equal(ProductInfo.RootPrefix, ZipSafety.ModRootPrefix);
        }
    }
}
