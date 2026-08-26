using Guyabano.Messaging;

namespace Guyabano.WorkflowProgressTests;

public sealed class InMemoryWorkflowProgressHubTests
{
    [Fact]
    public async Task SubscribeAsync_ReplaysEntriesAfterCursor()
    {
        var hub = new InMemoryWorkflowProgressHub();
        var first = await hub.PublishAsync(
            "run-1",
            Progress("first"),
            TestContext.Current.CancellationToken);
        var second = await hub.PublishAsync(
            "run-1",
            Progress("second"),
            TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromSeconds(2));

        await using var subscription = hub.SubscribeAsync(
                "run-1",
                first.EntryId,
                cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);

        Assert.True(await subscription.MoveNextAsync());
        Assert.Equal(second, subscription.Current);
    }

    [Fact]
    public async Task SubscribeAsync_ReceivesEntriesPublishedAfterSubscription()
    {
        var hub = new InMemoryWorkflowProgressHub();
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromSeconds(2));
        await using var subscription = hub.SubscribeAsync(
                "run-2",
                cancellationToken: cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);

        var pending = subscription.MoveNextAsync().AsTask();
        var published = await hub.PublishAsync(
            "run-2",
            Progress("live"),
            TestContext.Current.CancellationToken);

        Assert.True(await pending);
        Assert.Equal(published, subscription.Current);
    }

    [Fact]
    public async Task PublishAsync_IsolatesWorkflowStreams()
    {
        var hub = new InMemoryWorkflowProgressHub();
        await hub.PublishAsync(
            "other",
            Progress("ignore"),
            TestContext.Current.CancellationToken);
        var expected = await hub.PublishAsync(
            "target",
            Progress("include"),
            TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromSeconds(2));
        await using var subscription = hub.SubscribeAsync(
                "target",
                cancellationToken: cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);

        Assert.True(await subscription.MoveNextAsync());
        Assert.Equal(expected, subscription.Current);
    }

    private static Guyabano.Messaging.WorkflowProgress Progress(string message) => new(
        WorkflowProgressEventType.Started,
        "test",
        message,
        DateTimeOffset.UtcNow);
}
