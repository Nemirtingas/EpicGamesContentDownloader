using Microsoft.Extensions.Logging;

namespace EpicGamesContentDownloader;

internal class DummyLogger : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => false;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    { }
}

public static class Utils
{
    public static ILogger Logger = new DummyLogger();
}

public class AsyncMutex
{
    private SemaphoreSlim _Lock = new(1);

    private class AsyncMutexOwner : IDisposable
    {
        internal AsyncMutex Mutex;

        internal AsyncMutexOwner(AsyncMutex mutex)
        {
            Mutex = mutex;
        }

        internal AsyncMutexOwner Lock()
        {
            Mutex._Lock.Wait();
            return this;
        }

        internal async Task<AsyncMutexOwner> LockAsync()
        {
            await Mutex._Lock.WaitAsync().ConfigureAwait(false);
            return this;
        }

        public void Dispose()
        {
            var m = Mutex;
            Mutex = null;
            if (m != null)
            {
                m._Lock.Release();
                m = null;
            }
        }
    }

    public IDisposable Lock()
        => new AsyncMutexOwner(this).Lock();

    public async Task<IDisposable> LockAsync()
        => await new AsyncMutexOwner(this).LockAsync();
}