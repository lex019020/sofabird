using System.Globalization;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Sofabird.Companion.Tests.Support;

/// <summary>Writes the companion's log into the test output (shown for a failed test).</summary>
internal sealed class TestLoggerFactory(ITestOutputHelper output) : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName) => new Logger(output, categoryName[(categoryName.LastIndexOf('.') + 1)..]);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private sealed class Logger(ITestOutputHelper output, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var time = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
            try
            {
                output.WriteLine($"{time} {logLevel} {category}: {formatter(state, exception)}{(exception is null ? "" : " " + exception)}");
            }
            catch (InvalidOperationException)
            {
                // A background thread (the RC read loop) logged after the test had finished.
            }
        }
    }
}
