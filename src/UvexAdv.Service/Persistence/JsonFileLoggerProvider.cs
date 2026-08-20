using System.Text.Json;

namespace UvexAdv.Service.Persistence;

internal sealed class JsonFileLoggerProvider(UvexDataPaths paths) : ILoggerProvider
{
    private readonly object gate = new();

    public ILogger CreateLogger(string categoryName) => new JsonFileLogger(categoryName, paths, gate);
    public void Dispose() { }

    private sealed class JsonFileLogger(string category, UvexDataPaths paths, object gate) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) =>
            logLevel >= LogLevel.Information ||
            (logLevel == LogLevel.Debug && category == "UvexAdv.Service.Transport.SerialUvexTransport");

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var line = JsonSerializer.Serialize(new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                level = logLevel.ToString(),
                category,
                eventId = eventId.Id,
                message = formatter(state, exception),
                exception = exception?.ToString(),
            });
            var file = Path.Combine(paths.Logs, $"service-{DateTime.UtcNow:yyyyMMdd}.jsonl");
            lock (gate)
            {
                File.AppendAllText(file, line + Environment.NewLine);
            }
        }
    }
}
