// High-performance logging pipeline:
// ILogger -> FastLogger -> Channel<LogEvent> -> CentralLogCollector -> JsonLogWriter (SIMD-accelerated escaping)

using Microsoft.Extensions.Logging;

namespace ZeroAllocLog.Core;

public sealed class ZeroAllocLogger(
    string category,
    CentralLogCollector collector,
    IExternalScopeProvider scopes) : ILogger
{
    private readonly string _category = category;
    private readonly CentralLogCollector _collector = collector;
    private readonly IExternalScopeProvider _scopes = scopes;

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => _scopes.Push(state!);

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

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

        string message;
        try
        {
            message = formatter(state, exception);
        }
        catch (Exception ex)
        {
            message = $"Logger formatter threw: {ex}";
        }

        var props = new Dictionary<string, object?>(4)
        {
            ["EventId"] = eventId.Id,
            ["EventName"] = eventId.Name
        };

        if (exception is not null)
        {
            props["ExceptionType"] = exception.GetType().FullName;
        }

        _scopes.ForEachScope((scope, _) =>
        {
            switch (scope)
            {
                case IEnumerable<KeyValuePair<string, object?>> kvs:
                    foreach (var kv in kvs)
                    {
                        props[kv.Key] = kv.Value;
                    }

                    break;
                case KeyValuePair<string, object?> kv:
                    props[kv.Key] = kv.Value;
                    break;
                default:
                    props["Scope"] = scope;
                    break;
            }
        }, state);

        var evt = new LogEvent(
            Timestamp: DateTime.UtcNow,
            Level: logLevel,
            Category: _category,
            Message: message,
            Exception: exception,
            Properties: props
        );

        _collector.TryWrite(evt);
    }
}
