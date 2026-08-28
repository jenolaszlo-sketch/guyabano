using FluentAssertions;
using Guyabano.Session;

namespace Guyabano.SessionTests;

public sealed class CrossStoreOperationReconciliationTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "guyabano-reconciliation-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Inspect_ReportsPreciseForwardRecoveryByCommitBoundary()
    {
        var ct = TestContext.Current.CancellationToken;
        using var store = new FileSystemCrossStoreOperationStore(rootPath);
        var sessionId = GuyabanoSessionId.New();
        var prepared = await StartAsync(store, sessionId, "prepared", ct);
        var promoted = await StartAsync(store, sessionId, "promoted", ct);
        await store.TransitionAsync(
            promoted.Id,
            CrossStoreOperationState.WorkspacePromoted,
            DateTimeOffset.UtcNow,
            cancellationToken: ct);
        var published = await StartAsync(store, sessionId, "published", ct);
        await store.TransitionAsync(
            published.Id,
            CrossStoreOperationState.Published,
            DateTimeOffset.UtcNow,
            cancellationToken: ct);

        var service = new CrossStoreOperationReconciliationService(
            store,
            TimeProvider.System);
        var report = await service.InspectAsync(sessionId, ct);

        report.IsHealthy.Should().BeFalse();
        report.Operations.Single(item => item.OperationId == prepared.Id)
            .OperatorAction.Should().Contain("Resume the Zhinu workflow");
        report.Operations.Single(item => item.OperationId == promoted.Id)
            .OperatorAction.Should().Contain("Do not roll back");
        report.Operations.Single(item => item.OperationId == published.Id)
            .OperatorAction.Should().Contain("final checkpoint");
    }

    [Fact]
    public async Task Inspect_UsesFailedParticipantRecoveryInstruction()
    {
        var ct = TestContext.Current.CancellationToken;
        using var store = new FileSystemCrossStoreOperationStore(rootPath);
        var sessionId = GuyabanoSessionId.New();
        var operation = await StartAsync(store, sessionId, "failed", ct);
        var participant = "hetu-publication";
        await store.RecordParticipantAsync(
            operation.Id,
            new CrossStoreParticipantReceipt
            {
                Participant = participant,
                IdempotencyKey = operation.ParticipantIdempotencyKey(participant),
                State = CrossStoreParticipantState.Failed,
                RecordedAt = DateTimeOffset.UtcNow,
                RecoveryAction = "Re-index the accepted workspace revision."
            },
            ct);
        await store.TransitionAsync(
            operation.Id,
            CrossStoreOperationState.ReconciliationRequired,
            DateTimeOffset.UtcNow,
            "Hetu publication failed.",
            ct);

        var report = await new CrossStoreOperationReconciliationService(
            store,
            TimeProvider.System).InspectAsync(sessionId, ct);
        var item = report.Operations.Should().ContainSingle().Subject;
        item.Health.Should().Be(
            CrossStoreOperationHealth.ReconciliationRequired);
        item.FailedParticipants.Should().Equal(participant);
        item.OperatorAction.Should().Be(
            "Re-index the accepted workspace revision.");
    }

    private static Task<CrossStoreOperation> StartAsync(
        ICrossStoreOperationStore store,
        GuyabanoSessionId sessionId,
        string key,
        CancellationToken cancellationToken)
    {
        var runId = Guid.CreateVersion7();
        return store.StartAsync(
            new StartCrossStoreOperationRequest(
                sessionId,
                runId,
                "test",
                $"{runId:D}:{key}",
                DateTimeOffset.UtcNow),
            cancellationToken);
    }

    public void Dispose()
    {
        if (Directory.Exists(rootPath))
            Directory.Delete(rootPath, recursive: true);
    }
}
