using System.Threading;

namespace opendataloader.api;

public interface IConversionQueue
{
    Task<ConversionQueueLease?> TryAcquireAsync(CancellationToken cancellationToken);

    ConversionQueueSnapshot GetSnapshot();
}

public sealed record ConversionQueueSnapshot(
    int ActiveConversions,
    int QueuedConversions,
    int MaxConcurrentConversions,
    int MaxQueuedConversions);

public sealed class ConversionQueueLease : IAsyncDisposable
{
    private readonly ConversionQueue _queue;
    private int _disposed;

    internal ConversionQueueLease(ConversionQueue queue)
    {
        _queue = queue;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _queue.Release();
        }

        return ValueTask.CompletedTask;
    }
}

public sealed class ConversionQueue : IConversionQueue
{
    private readonly ILogger<ConversionQueue> _logger;
    private readonly OpenDataLoaderOptions _options;
    private readonly SemaphoreSlim _conversionSlots;
    private int _activeConversions;
    private int _queuedConversions;

    public ConversionQueue(ILogger<ConversionQueue> logger, OpenDataLoaderOptions options)
    {
        _logger = logger;
        _options = options;
        _conversionSlots = new SemaphoreSlim(options.MaxConcurrentConversions, options.MaxConcurrentConversions);
    }

    public async Task<ConversionQueueLease?> TryAcquireAsync(CancellationToken cancellationToken)
    {
        if (await _conversionSlots.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            Interlocked.Increment(ref _activeConversions);
            return new ConversionQueueLease(this);
        }

        if (!TryEnterQueue())
        {
            return null;
        }

        var queued = true;
        try
        {
            using var queueTimeoutCts = new CancellationTokenSource(_options.QueueTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, queueTimeoutCts.Token);

            try
            {
                await _conversionSlots.WaitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException) when (queueTimeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "OpenDataLoader conversion queue wait timed out after {queueTimeoutSeconds}s.",
                    _options.QueueTimeoutSeconds);
                return null;
            }

            Interlocked.Decrement(ref _queuedConversions);
            queued = false;
            Interlocked.Increment(ref _activeConversions);
            return new ConversionQueueLease(this);
        }
        finally
        {
            if (queued)
            {
                Interlocked.Decrement(ref _queuedConversions);
            }
        }
    }

    public ConversionQueueSnapshot GetSnapshot() => new(
        ActiveConversions: Volatile.Read(ref _activeConversions),
        QueuedConversions: Volatile.Read(ref _queuedConversions),
        MaxConcurrentConversions: _options.MaxConcurrentConversions,
        MaxQueuedConversions: _options.MaxQueuedConversions);

    internal void Release()
    {
        Interlocked.Decrement(ref _activeConversions);
        _conversionSlots.Release();
    }

    private bool TryEnterQueue()
    {
        while (true)
        {
            var queued = Volatile.Read(ref _queuedConversions);
            if (queued >= _options.MaxQueuedConversions)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _queuedConversions, queued + 1, queued) == queued)
            {
                return true;
            }
        }
    }
}
