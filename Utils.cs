using Microsoft.Extensions.Logging;

namespace EpicContentContentDownloader;

internal class DummyLogger : ILogger
{
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => false;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
    { }
}

public static class Utils
{
    public static ILogger Logger = new DummyLogger();
}