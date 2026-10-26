namespace UI18n.Core.Config
{
    /// <summary>
    /// 日志级别（配置键 <c>LogLevel</c>，默认 <see cref="Info"/>）。
    /// <para>
    /// 数值顺序即「话多程度」，门控判据是 <c>level &lt;= 当前级别</c>：设成 <see cref="Warn"/> 时
    /// <c>Error</c> 与 <c>Warn</c> 仍然出，<c>Info</c>/<c>Verbose</c> 被静音。
    /// <b>数值是契约</b>：序列化、单测与门控都依赖这个顺序，不要重排。
    /// </para>
    /// <para>
    /// 为什么需要它：v1.2 之前 151 处 <c>Log.Info</c> 全部无条件下发，热路径（每次
    /// <c>set_text</c> 都可能打一行）的日志噪声只能靠改代码关掉。有了级别，玩家在
    /// <c>Config.cfg</c> 里写一行就能静音，不必等新版本。
    /// </para>
    /// </summary>
    public enum LogLevel
    {
        /// <summary>只有错误。</summary>
        Error = 0,

        /// <summary>错误 + 告警（排查「日志被刷屏」时的推荐值）。</summary>
        Warn = 1,

        /// <summary>默认：再加启动摘要、周期统计、加载耗时。</summary>
        Info = 2,

        /// <summary>最啰嗦：再加被过滤掉的捕获行、逐条 patch 决策等细粒度信息。</summary>
        Verbose = 3
    }
}
