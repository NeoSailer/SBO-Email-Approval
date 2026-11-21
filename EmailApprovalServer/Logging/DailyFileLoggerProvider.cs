using System.Collections.Concurrent;

namespace EmailApprovalServer.Logging;

public sealed class DailyFileLoggerProvider : ILoggerProvider
{
    private readonly string _logDirectory;
    private readonly object _writeLock = new();
    private readonly ConcurrentDictionary<string, DailyFileLogger> _loggers = new();

    public DailyFileLoggerProvider(string logDirectory)
    {
        _logDirectory = logDirectory;
        Directory.CreateDirectory(_logDirectory);
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new DailyFileLogger(name, WriteLine));

    public void Dispose() => _loggers.Clear();

    private void WriteLine(string line)
    {
        lock (_writeLock)
        {
            Directory.CreateDirectory(_logDirectory);
            var path = Path.Combine(_logDirectory, $"EmailApprovalServer-{DateTime.Now:yyyyMMdd}.log");
            File.AppendAllText(path, line + Environment.NewLine);
        }
    }

    private sealed class DailyFileLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly Action<string> _writeLine;

        public DailyFileLogger(string categoryName, Action<string> writeLine)
        {
            _categoryName = categoryName;
            _writeLine = writeLine;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

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

            var message = formatter(state, exception);
            var exceptionText = exception is null ? string.Empty : $" | {exception}";
            _writeLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{logLevel}] {_categoryName}: {message}{exceptionText}");
        }
    }
}
