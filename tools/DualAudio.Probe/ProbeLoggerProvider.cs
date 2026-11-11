using Microsoft.Extensions.Logging;

namespace DualAudio.Probe;

/// <summary>Routes Microsoft.Extensions.Logging output (Core + Platform adapters) into the probe log.</summary>
internal sealed class ProbeLoggerProvider(ProbeLog log) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Logger(log, categoryName[(categoryName.LastIndexOf('.') + 1)..]);

    public void Dispose()
    {
    }

    private sealed class Logger(ProbeLog log, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = $"[{category}] {formatter(state, exception)}";
            if (exception is not null)
            {
                message += $" | {exception.GetType().Name} HRESULT=0x{exception.HResult:X8}";
            }

            switch (logLevel)
            {
                case LogLevel.Warning:
                    log.Warn(message);
                    break;
                case >= LogLevel.Error:
                    log.Error(message);
                    break;
                default:
                    log.Info(message);
                    break;
            }
        }
    }
}
