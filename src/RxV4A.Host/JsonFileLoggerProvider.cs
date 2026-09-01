using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RxV4A.Host;

public sealed class JsonFileLoggerProvider(string logDirectory) : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, JsonFileLogger> _loggers = new(StringComparer.Ordinal);
    private readonly Lock _writeLock = new();

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, category => new JsonFileLogger(category, Write));

    public void Dispose() => _loggers.Clear();

    private void Write(
        LogLevel level,
        string category,
        EventId eventId,
        string message,
        IReadOnlyDictionary<string, object?> properties,
        string? exceptionType)
    {
        try
        {
            Directory.CreateDirectory(logDirectory);
            var path = Path.Combine(logDirectory, $"rxv4a-{DateTime.UtcNow:yyyyMMdd}.jsonl");
            var entry = JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.UtcNow,
                level = level.ToString(),
                category,
                eventId = eventId.Id,
                message,
                properties,
                exceptionType
            });

            lock (_writeLock)
            {
                File.AppendAllText(path, entry + Environment.NewLine);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Logging must never stop the tray application.
        }
    }

    private sealed class JsonFileLogger(
        string category,
        Action<LogLevel, string, EventId, string, IReadOnlyDictionary<string, object?>, string?> write) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                var properties = state is IEnumerable<KeyValuePair<string, object?>> values
                    ? values
                        .Where(item => item.Key != "{OriginalFormat}")
                        .ToDictionary(item => item.Key, item => Normalize(item.Value), StringComparer.Ordinal)
                    : new Dictionary<string, object?>();
                write(
                    logLevel,
                    category,
                    eventId,
                    formatter(state, null),
                    properties,
                    exception?.GetType().Name);
            }
        }

        private static object? Normalize(object? value) => value switch
        {
            null => null,
            string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or
                float or double or decimal or DateTime or DateTimeOffset or Guid => value,
            Enum => value.ToString(),
            TimeSpan timeSpan => timeSpan.ToString(),
            _ => value.GetType().Name
        };
    }
}
