// High-performance logging pipeline:
// ILogger -> FastLogger -> Channel<LogEvent> -> CentralLogCollector -> JsonLogWriter (SIMD-accelerated escaping)

using Microsoft.Extensions.Logging;
using System.Buffers;

namespace ZeroAllocLog.Core;

// =======================
// Scope provider
// =======================

public sealed class ZeroAllocLoggerScopeProvider : IExternalScopeProvider
{
    public static ZeroAllocLoggerScopeProvider Instance { get; } = new();

    private readonly AsyncLocal<ScopeNode?> _current = new();

    private sealed class ScopeNode
    {
        public readonly object State;
        public readonly ScopeNode? Parent;

        public ScopeNode(object state, ScopeNode? parent)
        {
            State = state;
            Parent = parent;
        }
    }

    private ZeroAllocLoggerScopeProvider() { }

    public IDisposable Push(object state)
    {
        var parent = _current.Value;
        var node = new ScopeNode(state, parent);
        _current.Value = node;

        return new PopWhenDisposed(this, node);
    }

    private sealed class PopWhenDisposed : IDisposable
    {
        private readonly ZeroAllocLoggerScopeProvider _provider;
        private readonly ScopeNode _node;
        private bool _disposed;

        public PopWhenDisposed(ZeroAllocLoggerScopeProvider provider, ScopeNode node)
        {
            _provider = provider;
            _node = node;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_provider._current.Value == _node)
            {
                _provider._current.Value = _node.Parent;
            }
        }
    }

    public void ForEachScope<TState>(Action<object?, TState> callback, TState state)
    {
        var node = _current.Value;
        if (node is null)
        {
            return;
        }

        object?[] buffer = ArrayPool<object?>.Shared.Rent(8);
        int count = 0;

        try
        {
            while (node is not null)
            {
                if (count == buffer.Length)
                {
                    // extend the array. Allocation happens here, but it should be rare since most scopes are expected to be shallow.
                    var newBuf = ArrayPool<object?>.Shared.Rent(buffer.Length * 2);
                    Array.Copy(buffer, newBuf, buffer.Length);
                    ArrayPool<object?>.Shared.Return(buffer, clearArray: true);
                    buffer = newBuf;
                }

                buffer[count++] = node.State;
                node = node.Parent;
            }

            for (int i = count - 1; i >= 0; i--)
            {
                callback(buffer[i], state);
            }
        }
        finally
        {
            ArrayPool<object?>.Shared.Return(buffer, clearArray: true);
        }
    }

}
