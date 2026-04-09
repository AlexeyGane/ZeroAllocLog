using Microsoft.Extensions.Logging;

namespace ZeroAllocLog.Core;

public sealed class ZeroAllocLoggerProvider : ILoggerProvider
{
    private readonly CentralLogCollector _collector;
    private readonly ZeroAllocLoggerScopeProvider _scopeProvider;

    public ZeroAllocLoggerProvider(CentralLogCollector collector)
    {
        _collector = collector;
        _scopeProvider = ZeroAllocLoggerScopeProvider.Instance;
    }

    public ILogger CreateLogger(string categoryName) => new ZeroAllocLogger(categoryName, _collector, _scopeProvider);

    public void Dispose()
    {
        // Nothing to dispose — CentralLogCollector lives in DI
    }
}
