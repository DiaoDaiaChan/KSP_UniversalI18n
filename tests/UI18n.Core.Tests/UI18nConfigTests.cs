using UI18n.Core.Config;
using Xunit;

namespace UI18n.Core.Tests
{
    /// <summary>
    /// 配置默认值（00:488 更新检查开 / 自动下载关；00:655 D32 强制覆盖默认关；02:283 MaxEntries 60000）。
    /// 默认值直接决定「玩家装完 Mod 会不会在没同意的情况下联网 / 被覆盖英文界面」，属于合规项，不是口味问题。
    /// </summary>
    public class UI18nConfigTests
    {
        [Fact]
        public void Defaults_MatchTheDocumentedSafeDefaults()
        {
            UI18nConfig config = new UI18nConfig();

            Assert.True(config.Enabled);
            Assert.False(config.ForceOverride);      // D32
            Assert.True(config.CheckForUpdates);     // 00:488
            Assert.False(config.AutoDownload);       // 00:488 联网行为需玩家知情
            Assert.False(config.ReportOptIn);        // opt-in
            Assert.Equal(60000, config.MaxEntries);  // 02:283
        }

        [Fact]
        public void Default_PropertyHasTheSameValues()
        {
            UI18nConfig config = UI18nConfig.Default;

            Assert.True(config.Enabled);
            Assert.False(config.ForceOverride);
            Assert.True(config.CheckForUpdates);
            Assert.False(config.AutoDownload);
            Assert.False(config.ReportOptIn);
            Assert.Equal(60000, config.MaxEntries);
        }

        /// <summary>Default 每次返回新实例：配置可变，共享静态实例等于把「读默认值」变成「持有全局可变状态」。</summary>
        [Fact]
        public void Default_ReturnsFreshInstanceEveryTime()
        {
            UI18nConfig first = UI18nConfig.Default;
            UI18nConfig second = UI18nConfig.Default;

            Assert.NotSame(first, second);

            first.Enabled = false;
            first.MaxEntries = 1;
            first.ForceOverride = true;

            Assert.True(second.Enabled);
            Assert.Equal(60000, second.MaxEntries);
            Assert.False(second.ForceOverride);
            Assert.True(UI18nConfig.Default.Enabled);
        }

        [Fact]
        public void Properties_AreMutable()
        {
            UI18nConfig config = new UI18nConfig();

            config.Enabled = false;
            config.ForceOverride = true;
            config.CheckForUpdates = false;
            config.AutoDownload = true;
            config.ReportOptIn = true;
            config.MaxEntries = 1;

            Assert.False(config.Enabled);
            Assert.True(config.ForceOverride);
            Assert.False(config.CheckForUpdates);
            Assert.True(config.AutoDownload);
            Assert.True(config.ReportOptIn);
            Assert.Equal(1, config.MaxEntries);
        }

        /// <summary>
        /// v1.3 骨架通道默认<b>开</b>：它是正常翻译路径的一部分（不是实验开关），
        /// 关掉只是退回 v1.2 的精确匹配（模板串重新变成不翻）。实验开关是 Shadow* 那几个。
        /// </summary>
        [Fact]
        public void PatternMatching_DefaultsOn()
        {
            Assert.True(new UI18nConfig().PatternMatching);
            Assert.True(UI18nConfig.Default.PatternMatching);

            UI18nConfig config = new UI18nConfig();
            config.PatternMatching = false;
            Assert.False(config.PatternMatching);
        }

        [Fact]
        public void Config_IsSealedPoco_WithoutStaticsHoldingState()
        {
            Assert.True(typeof(UI18nConfig).IsSealed);
            Assert.Empty(typeof(UI18nConfig).GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public));
        }
    }
}
