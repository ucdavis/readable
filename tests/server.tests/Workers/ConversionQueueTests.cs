using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using opendataloader.api;

namespace server.tests.Workers;

public sealed class ConversionQueueTests
{
    [Fact]
    public async Task TryAcquireAsync_AllowsOnlyConfiguredConcurrentConversions()
    {
        var queue = CreateQueue(maxConcurrentConversions: 1, maxQueuedConversions: 0, queueTimeoutSeconds: 1);

        await using var lease = await queue.TryAcquireAsync(CancellationToken.None);
        lease.Should().NotBeNull();

        var rejected = await queue.TryAcquireAsync(CancellationToken.None);

        rejected.Should().BeNull();
        queue.GetSnapshot().Should().BeEquivalentTo(new ConversionQueueSnapshot(
            ActiveConversions: 1,
            QueuedConversions: 0,
            MaxConcurrentConversions: 1,
            MaxQueuedConversions: 0));
    }

    [Fact]
    public async Task TryAcquireAsync_QueuesUntilAConversionSlotIsReleased()
    {
        var queue = CreateQueue(maxConcurrentConversions: 1, maxQueuedConversions: 1, queueTimeoutSeconds: 5);

        await using var firstLease = await queue.TryAcquireAsync(CancellationToken.None);
        var secondLeaseTask = queue.TryAcquireAsync(CancellationToken.None);

        queue.GetSnapshot().QueuedConversions.Should().Be(1);

        await firstLease!.DisposeAsync();
        await using var secondLease = await secondLeaseTask.WaitAsync(TimeSpan.FromSeconds(2));

        secondLease.Should().NotBeNull();
        queue.GetSnapshot().ActiveConversions.Should().Be(1);
        queue.GetSnapshot().QueuedConversions.Should().Be(0);
    }

    [Fact]
    public async Task TryAcquireAsync_ReturnsNullWhenQueueWaitTimesOut()
    {
        var queue = CreateQueue(maxConcurrentConversions: 1, maxQueuedConversions: 1, queueTimeoutSeconds: 1);

        await using var lease = await queue.TryAcquireAsync(CancellationToken.None);

        var rejected = await queue.TryAcquireAsync(CancellationToken.None);

        rejected.Should().BeNull();
        queue.GetSnapshot().Should().BeEquivalentTo(new ConversionQueueSnapshot(
            ActiveConversions: 1,
            QueuedConversions: 0,
            MaxConcurrentConversions: 1,
            MaxQueuedConversions: 1));
    }

    private static ConversionQueue CreateQueue(
        int maxConcurrentConversions,
        int maxQueuedConversions,
        int queueTimeoutSeconds)
    {
        return new ConversionQueue(
            NullLogger<ConversionQueue>.Instance,
            new OpenDataLoaderOptions
            {
                MaxConcurrentConversions = maxConcurrentConversions,
                MaxQueuedConversions = maxQueuedConversions,
                QueueTimeoutSeconds = queueTimeoutSeconds
            });
    }
}
