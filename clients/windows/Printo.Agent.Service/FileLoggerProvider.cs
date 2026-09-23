using Microsoft.Extensions.Logging;
using Printo.Agent.Runtime;

namespace Printo.Agent.Service;

/// <summary>
/// Sends the host's log to <see cref="RollingFileLog"/>, when local log files are on.
/// </summary>
/// <remarks>
/// A provider rather than a sink bolted onto the agent's own logging, so everything the host
/// writes - Kestrel refusing a port, the service host failing to start, the agent itself - goes
/// to the same file in the same order as the event log sees it. The level is the agent's
/// setting, read per line; the framework's own categories are held at Information even when the
/// agent logs at Debug, because at Debug they describe every socket read and bury the lines that
/// explain the agent.
/// </remarks>
public sealed class FileLoggerProvider(RollingFileLog log) : ILoggerProvider
{
    private readonly RollingFileLog log = log ?? throw new ArgumentNullException(nameof(log));

    public ILogger CreateLogger(string categoryName) => new FileLogger(log, categoryName);

    public void Dispose() => log.Dispose();

    private sealed class FileLogger(RollingFileLog log, string category) : ILogger
    {
        private readonly bool framework = category.StartsWith("Microsoft.", StringComparison.Ordinal)
            || category.StartsWith("System.", StringComparison.Ordinal);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None && log.IsEnabled(Map(logLevel, framework));

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            log.Write(Map(logLevel, framework), Shorten(category), formatter(state, exception), exception);
        }

        /// <summary>
        /// The framework's Debug and Trace are written as if they were one level below the agent's
        /// Debug, which is to say not at all: its Information and above are kept.
        /// </summary>
        private static AgentLogLevel Map(LogLevel level, bool framework) => level switch
        {
            LogLevel.Trace or LogLevel.Debug when framework => (AgentLogLevel)(-1),
            LogLevel.Trace or LogLevel.Debug => AgentLogLevel.Debug,
            LogLevel.Information => AgentLogLevel.Information,
            LogLevel.Warning => AgentLogLevel.Warning,
            LogLevel.Error => AgentLogLevel.Error,
            _ => AgentLogLevel.Critical,
        };

        /// <summary><c>Printo.Agent.Service.AgentService</c> reads as <c>AgentService</c>.</summary>
        private static string Shorten(string category)
        {
            var dot = category.LastIndexOf('.');
            return category.StartsWith("Printo.", StringComparison.Ordinal) && dot >= 0 ? category[(dot + 1)..] : category;
        }
    }
}
