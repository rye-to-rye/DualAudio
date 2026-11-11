using Microsoft.Extensions.Logging;

namespace DualAudio.Platform.Tests;

/// <summary>Routes adapter logs to the xUnit test output. Never throws (late WinRT callbacks after a test ends).</summary>
internal sealed class TestOutputLogger<T>(ITestOutputHelper output) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        try
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} [{logLevel}] {typeof(T).Name}: {formatter(state, exception)}";
            if (exception is not null)
            {
                line += $" | {exception.GetType().Name} HRESULT=0x{exception.HResult:X8}";
            }

            output.WriteLine(line);
        }
        catch (InvalidOperationException)
        {
            // The test already finished.
        }
    }
}
