using Microsoft.Extensions.Logging;

namespace ScanWithWeb.Services;

/// <summary>
/// Simple file logger for crash diagnostics
/// </summary>
public class FileLogger : ILogger
{
    private readonly string _categoryName;
    private readonly string _logFilePath;
    private readonly LogLevel _minLevel;
    private static readonly object _lock = new();

    public FileLogger(string categoryName, string logFilePath, LogLevel minLevel)
    {
        _categoryName = categoryName;
        _logFilePath = logFilePath;
        _minLevel = minLevel;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= _minLevel;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        var message = formatter(state, exception);
        var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{logLevel,-11}] [{_categoryName}] {message}";

        if (exception != null)
        {
            logEntry += Environment.NewLine + $"    Exception: {exception.GetType().FullName}: {exception.Message}";
            logEntry += Environment.NewLine + $"    StackTrace: {exception.StackTrace}";

            if (exception.InnerException != null)
            {
                logEntry += Environment.NewLine + $"    InnerException: {exception.InnerException.GetType().FullName}: {exception.InnerException.Message}";
                logEntry += Environment.NewLine + $"    InnerStackTrace: {exception.InnerException.StackTrace}";
            }
        }

        lock (_lock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_logFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.AppendAllText(_logFilePath, logEntry + Environment.NewLine);
            }
            catch
            {
                // Ignore logging errors to prevent infinite loops
            }
        }
    }
}

/// <summary>
/// File logger provider
/// </summary>
public class FileLoggerProvider : ILoggerProvider
{
    private readonly string _logFilePath;
    private readonly LogLevel _minLevel;

    public FileLoggerProvider(string logFilePath, LogLevel minLevel = LogLevel.Information)
    {
        _logFilePath = logFilePath;
        _minLevel = minLevel;

        // Write startup header
        var dir = Path.GetDirectoryName(_logFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var header = $"{Environment.NewLine}========== ScanWithWeb Started at {DateTime.Now:yyyy-MM-dd HH:mm:ss} =========={Environment.NewLine}";
        header += $"Version: 3.0.3 ({(Environment.Is64BitProcess ? "64-bit" : "32-bit")}){Environment.NewLine}";
        header += $"OS: {Environment.OSVersion}{Environment.NewLine}";
        header += $"Working Directory: {Environment.CurrentDirectory}{Environment.NewLine}";
        header += $"========================================{Environment.NewLine}";

        try
        {
            File.AppendAllText(_logFilePath, header);
        }
        catch
        {
            // Ignore
        }
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new FileLogger(categoryName, _logFilePath, _minLevel);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Extension methods for adding file logging
/// </summary>
public static class FileLoggerExtensions
{
    public static ILoggingBuilder AddFileLogger(this ILoggingBuilder builder, string logFilePath, LogLevel minLevel = LogLevel.Information)
    {
        builder.AddProvider(new FileLoggerProvider(logFilePath, minLevel));
        return builder;
    }
}
