using FluentAssertions;
using Guyabano.Session;
using Guyabano.Session.Sqlite;

namespace Guyabano.SessionTests;

public sealed class SqliteCrossStoreOperationStoreTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "guyabano-sqlite-operation-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task IndependentInstances_ReplayOneIdempotentOperation()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(rootPath, "catalog.db");
        var first = new SqliteCrossStoreOperationStore(path, pooling: false);
        var second = new SqliteCrossStoreOperationStore(path, pooling: false);
        var runId = Guid.CreateVersion7();
        var request = new StartCrossStoreOperationRequest(
            GuyabanoSessionId.New(),
            runId,
            "test",
            $"{runId:D}:test",
            DateTimeOffset.UtcNow);

        var results = await Task.WhenAll(
            first.StartAsync(request, ct),
            second.StartAsync(request, ct));

        results[0].Should().BeEquivalentTo(results[1]);
        (await first.FindByWorkflowRunAsync(runId, ct))!.Id.Should().Be(results[0].Id);
    }

    [Fact]
    public async Task ConcurrentParticipantAppends_AreSerializedWithoutLostReceipts()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(rootPath, "catalog.db");
        var first = new SqliteCrossStoreOperationStore(path, pooling: false);
        var second = new SqliteCrossStoreOperationStore(path, pooling: false);
        var operation = await StartAsync(first, ct);
        var firstReceipt = Receipt(operation, "workspace");
        var secondReceipt = Receipt(operation, "cangjie");

        await Task.WhenAll(
            first.RecordParticipantAsync(operation.Id, firstReceipt, ct),
            second.RecordParticipantAsync(operation.Id, secondReceipt, ct));

        var stored = await first.GetAsync(operation.Id, ct);
        stored!.Participants.Select(item => item.Participant)
            .Should().BeEquivalentTo("workspace", "cangjie");
        stored.Version.Should().Be(3);
    }

    [Fact]
    public async Task ReceiptsAndTransitions_RemainImmutableAcrossReopen()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(rootPath, "catalog.db");
        var store = new SqliteCrossStoreOperationStore(path, pooling: false);
        var operation = await StartAsync(store, ct);
        var receipt = Receipt(operation, "workspace");
        await store.RecordParticipantAsync(operation.Id, receipt, ct);
        await store.TransitionAsync(
            operation.Id,
            CrossStoreOperationState.WorkspacePromoted,
            DateTimeOffset.UtcNow,
            cancellationToken: ct);
        await store.TransitionAsync(
            operation.Id,
            CrossStoreOperationState.Published,
            DateTimeOffset.UtcNow,
            cancellationToken: ct);

        var reopened = new SqliteCrossStoreOperationStore(path, pooling: false);
        var stored = await reopened.GetAsync(operation.Id, ct);

        stored!.Participants.Should().ContainSingle().Which.Should().BeEquivalentTo(receipt);
        stored.Transitions.Select(item => item.State).Should().Equal(
            CrossStoreOperationState.Prepared,
            CrossStoreOperationState.WorkspacePromoted,
            CrossStoreOperationState.Published);
        var conflict = () => reopened.RecordParticipantAsync(
            operation.Id,
            receipt with { ResultHash = "different" },
            ct);
        await conflict.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*different immutable receipt*");
    }

    private static Task<CrossStoreOperation> StartAsync(
        ICrossStoreOperationStore store,
        CancellationToken cancellationToken)
    {
        var runId = Guid.CreateVersion7();
        return store.StartAsync(
            new StartCrossStoreOperationRequest(
                GuyabanoSessionId.New(),
                runId,
                "test",
                $"{runId:D}:test",
                DateTimeOffset.UtcNow),
            cancellationToken);
    }

    private static CrossStoreParticipantReceipt Receipt(
        CrossStoreOperation operation,
        string participant) => new()
        {
            Participant = participant,
            IdempotencyKey = operation.ParticipantIdempotencyKey(participant),
            State = CrossStoreParticipantState.Applied,
            RecordedAt = DateTimeOffset.UtcNow,
            ResultHash = $"hash:{participant}"
        };

    public void Dispose()
    {
        if (Directory.Exists(rootPath))
            Directory.Delete(rootPath, recursive: true);
    }
}
