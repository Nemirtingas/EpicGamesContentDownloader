using log4net;
using Microsoft.Extensions.Logging;

namespace EpicGamesContentDownloader;

public class Log4NetLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName)
    {
        return new Log4NetLogger(categoryName);
    }

    public void Dispose() { }
}

public class Log4NetLogger : ILogger
{
    private readonly ILog _logger;

    public Log4NetLogger(string categoryName)
    {
        _logger = LogManager.GetLogger(categoryName);
    }

    public IDisposable BeginScope<TState>(TState state) => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel switch
    {
        LogLevel.Trace or LogLevel.Debug => _logger.IsDebugEnabled,
        LogLevel.Information => _logger.IsInfoEnabled,
        LogLevel.Warning => _logger.IsWarnEnabled,
        LogLevel.Error => _logger.IsErrorEnabled,
        LogLevel.Critical => _logger.IsFatalEnabled,
        _ => false
    };

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                            Exception exception, Func<TState, Exception, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var message = formatter(state, exception);

        switch (logLevel)
        {
            case LogLevel.Trace:
            case LogLevel.Debug:
                _logger.Debug(message, exception);
                break;
            case LogLevel.Information:
                _logger.Info(message, exception);
                break;
            case LogLevel.Warning:
                _logger.Warn(message, exception);
                break;
            case LogLevel.Error:
                _logger.Error(message, exception);
                break;
            case LogLevel.Critical:
                _logger.Fatal(message, exception);
                break;
        }
    }
}
