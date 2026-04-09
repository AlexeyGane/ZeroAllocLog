// High-performance logging pipeline:
// ILogger -> FastLogger -> Channel<LogEvent> -> CentralLogCollector -> JsonLogWriter (SIMD-accelerated escaping)

using System.IO.Pipelines;
using System.Threading.Channels;

namespace ZeroAllocLog.Core;

public sealed class CentralLogCollector : IAsyncDisposable
{
    public Channel<LogEvent> Channel { get; }

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;

    private readonly string _directory;
    private readonly string _fileNamePrefix;
    private readonly long _maxFileSizeBytes;

    private FileStream _currentStream = null!;
    private PipeWriter _writer = null!;
    private int _fileIndex = 0;

    public CentralLogCollector(
        string directory,
        string fileNamePrefix = "log",
        long maxFileSizeBytes = 10 * 1024 * 1024,
        int channelCapacity = 8192)
    {
        _directory = directory;
        _fileNamePrefix = fileNamePrefix;
        _maxFileSizeBytes = maxFileSizeBytes;

        Directory.CreateDirectory(_directory);

        // bounded channel → backpressure
        Channel = System.Threading.Channels.Channel.CreateBounded<LogEvent>(
            new BoundedChannelOptions(channelCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropWrite
            });

        OpenNewFile();

        _worker = Task.Run(WorkerLoopAsync);
    }

    public bool TryWrite(in LogEvent evt) => Channel.Writer.TryWrite(evt);

    private async Task WorkerLoopAsync()
    {
        try
        {
            await foreach (var evt in Channel.Reader.ReadAllAsync(_cts.Token))
            {
                // zero-alloc JSON writer
                JsonLogWriter.WriteEvent(_writer, evt);

                // PipeWriter batching
                var result = await _writer.FlushAsync(_cts.Token);

                // rotation
                if (result.IsCompleted || _currentStream.Length >= _maxFileSizeBytes)
                {
                    await RotateAsync();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        finally
        {
            await _writer.CompleteAsync();
            await _currentStream.DisposeAsync();
        }
    }

    private async Task RotateAsync()
    {
        await _writer.FlushAsync();
        await _currentStream.FlushAsync();

        await _writer.CompleteAsync();
        await _currentStream.DisposeAsync();

        OpenNewFile();
    }

    private void OpenNewFile()
    {
        string fileName =
            $"{_fileNamePrefix}_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{_fileIndex++}.jsonl";

        string path = Path.Combine(_directory, fileName);

        _currentStream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true);

        _writer = PipeWriter.Create(
            _currentStream,
            new StreamPipeWriterOptions(
                minimumBufferSize: 16 * 1024,
                leaveOpen: false));
    }

    public async ValueTask DisposeAsync()
    {
        Channel.Writer.TryComplete();
        _cts.Cancel();

        try { await _worker; } catch { }

        _cts.Dispose();
    }
}