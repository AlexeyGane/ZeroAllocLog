// FastLogging.cs
// Single-file high-performance logging pipeline:
// ILogger -> FastLogger -> Channel<LogEvent> -> CentralLogCollector -> JsonLogWriter (SIMD-accelerated escaping)

using System.Buffers;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace ZeroAllocLog.Core;

// =======================
// Core event
// =======================

public readonly record struct LogEvent(
    DateTime Timestamp,
    LogLevel Level,
    string Category,
    string Message,
    Exception? Exception,
    IReadOnlyDictionary<string, object?> Properties
);
