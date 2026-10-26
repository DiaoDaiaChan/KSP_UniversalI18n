using UI18n.Core.Config;
using Xunit;

namespace UI18n.Core.Tests
{
    /// <summary>
    /// 日志级别（task-44）。三件必须钉住的事：
    /// ① 数值顺序是门控契约（<c>level &lt;= Level</c>）——重排会让 <c>LogLevel=Warn</c> 反而更吵；
    /// ② 默认必须是 <see cref="LogLevel.Info"/>，即 v1.2 的行为逐行不变；
    /// ③ 写错一个词绝不等于「把日志全关掉」，只能回落默认值。
    /// </summary>
    public class LogLevelTests
    {
        [Theory]
        [InlineData("Error")]
        [InlineData("error")]
        [InlineData("ERROR")]
        [InlineData(" errors ")]
        [InlineData("quiet")]
        [InlineData("silent")]
        [InlineData("off")]
        [InlineData("0")]
        public void TryParseLogLevel_ErrorFamily(string raw)
        {
            LogLevel level;
            Assert.True(ConfigEnums.TryParseLogLevel(raw, out level));
            Assert.Equal(LogLevel.Error, level);
        }

        [Theory]
        [InlineData("Warn")]
        [InlineData("warning")]
        [InlineData("Warnings")]
        [InlineData("1")]
        public void TryParseLogLevel_WarnFamily(string raw)
        {
            LogLevel level;
            Assert.True(ConfigEnums.TryParseLogLevel(raw, out level));
            Assert.Equal(LogLevel.Warn, level);
        }

        [Theory]
        [InlineData("Info")]
        [InlineData("INFO")]
        [InlineData("2")]
        public void TryParseLogLevel_InfoFamily(string raw)
        {
            LogLevel level;
            Assert.True(ConfigEnums.TryParseLogLevel(raw, out level));
            Assert.Equal(LogLevel.Info, level);
        }

        [Theory]
        [InlineData("Verbose")]
        [InlineData("debug")]
        [InlineData("trace")]
        [InlineData("all")]
        [InlineData("3")]
        public void TryParseLogLevel_VerboseFamily(string raw)
        {
            LogLevel level;
            Assert.True(ConfigEnums.TryParseLogLevel(raw, out level));
            Assert.Equal(LogLevel.Verbose, level);
        }

        /// <summary>无法识别 → false，且 out 回落 Info（绝不回落 Error：写错一个词不该把日志静音）。</summary>
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("verboze")]
        [InlineData("4")]
        [InlineData("-1")]
        [InlineData("loud")]
        public void TryParseLogLevel_UnknownFallsBackToInfo(string raw)
        {
            LogLevel level = LogLevel.Error; // 先塞一个错误值，确认方法真的写了 out
            Assert.False(ConfigEnums.TryParseLogLevel(raw, out level));
            Assert.Equal(LogLevel.Info, level);
        }

        [Fact]
        public void TryParseLogLevel_NullIsRejectedWithoutThrowing()
        {
            LogLevel level;
            Assert.False(ConfigEnums.TryParseLogLevel(null, out level));
            Assert.Equal(LogLevel.Info, level);
        }

        [Fact]
        public void ToName_RoundTripsEveryLevel()
        {
            foreach (LogLevel level in new[] { LogLevel.Error, LogLevel.Warn, LogLevel.Info, LogLevel.Verbose })
            {
                string name = ConfigEnums.ToName(level);
                LogLevel parsed;
                Assert.True(ConfigEnums.TryParseLogLevel(name, out parsed), "ToName 产出的名字必须能被 TryParseLogLevel 读回来");
                Assert.Equal(level, parsed);
            }

            Assert.Equal("Error", ConfigEnums.ToName(LogLevel.Error));
            Assert.Equal("Warn", ConfigEnums.ToName(LogLevel.Warn));
            Assert.Equal("Info", ConfigEnums.ToName(LogLevel.Info));
            Assert.Equal("Verbose", ConfigEnums.ToName(LogLevel.Verbose));
        }

        /// <summary>
        /// 门控契约：<c>level &lt;= 当前级别</c> 才输出。数值顺序一旦被重排，
        /// <c>LogLevel=Warn</c> 就会把告警也吞掉（或把 Info 放出来），而这种错在运行时很难看出来。
        /// </summary>
        [Fact]
        public void NumericOrder_IsTheGateContract()
        {
            Assert.Equal(0, (int)LogLevel.Error);
            Assert.Equal(1, (int)LogLevel.Warn);
            Assert.Equal(2, (int)LogLevel.Info);
            Assert.Equal(3, (int)LogLevel.Verbose);

            Assert.True((int)LogLevel.Error < (int)LogLevel.Warn);
            Assert.True((int)LogLevel.Warn < (int)LogLevel.Info);
            Assert.True((int)LogLevel.Info < (int)LogLevel.Verbose);

            // 与 Runtime 里 Log.IsEnabled 同一判据：设成 Warn → Warn/Error 出，Info/Verbose 不出。
            Assert.True(IsEnabled(LogLevel.Warn, LogLevel.Error));
            Assert.True(IsEnabled(LogLevel.Warn, LogLevel.Warn));
            Assert.False(IsEnabled(LogLevel.Warn, LogLevel.Info));
            Assert.False(IsEnabled(LogLevel.Warn, LogLevel.Verbose));

            // 设成 Verbose → 全部出；设成 Error → 只有错误出。
            Assert.True(IsEnabled(LogLevel.Verbose, LogLevel.Verbose));
            Assert.True(IsEnabled(LogLevel.Error, LogLevel.Error));
            Assert.False(IsEnabled(LogLevel.Error, LogLevel.Warn));
            Assert.False(IsEnabled(LogLevel.Error, LogLevel.Verbose));
        }

        [Fact]
        public void DefaultConfig_UsesInfo()
        {
            Assert.Equal(LogLevel.Info, new UI18nConfig().LogLevel);
            Assert.Equal(LogLevel.Info, UI18nConfig.Default.LogLevel);
        }

        private static bool IsEnabled(LogLevel current, LogLevel message)
        {
            return message <= current;
        }
    }
}
