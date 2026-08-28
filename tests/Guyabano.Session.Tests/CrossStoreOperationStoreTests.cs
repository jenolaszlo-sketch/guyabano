using FluentAssertions;
using Guyabano.Session;

namespace Guyabano.SessionTests;

public sealed class CrossStoreOperationStoreTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "guyabano-cross-store-operation-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Start_IsDurableAndIdempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var sessionId = GuyabanoSessionId.New();
        var workflowRunId = Guid.CreateVersion7();
        var request = new StartCrossStoreOperationRequest(
            sessionId,
            workflowRunId,
            "generation-promotion",
            $"{workflowRunId:D}:generation-promotion",
            DateTimeOffset.UtcNow);

        CrossStoreOperation started;
        using (var store = new FileSystemCrossStoreOperationStore(rootPath))
        {
            started = await store.StartAsync(request, ct);
            var replay = await store.StartAsync(request, ct);
            replay.Should().BeEquivalentTo(started);
        }

        started.Id.Value.Version.Should().Be(7);
        started.State.Should().Be(CrossStoreOperationState.Prepared);
        started.Transitions.Should().ContainSingle().Which.State.Should().Be(
            CrossStoreOperationState.Prepared);
        using var reopened = new FileSystemCrossStoreOperationStore(rootPath);
        (await reopened.GetAsync(started.Id, ct)).Should().BeEquivalentTo(started);
        (await reopened.FindByWorkflowRunAsync(workflowRunId, ct))
            .Should().BeEquivalentTo(started);
    }

    [Fact]
    public async Task ParticipantReceipt_IsImmutableAndRetrySafe()
    {
        var ct = TestContext.Current.CancellationToken;
        using var store = new FileSystemCrossStoreOperationStore(rootPath);
        var operation = await StartAsync(store, ct);
        var receipt = new CrossStoreParticipantReceipt
        {
            Participant = "workspace-promotion",
            IdempotencyKey = operation.ParticipantIdempotencyKey("workspace-promotion"),
            State = CrossStoreParticipantState.Applied,
            RecordedAt = DateTimeOffset.UtcNow,
            BeforeIdentity = "workspace:1",
            AfterIdentity = "workspace:2",
            ResultHash = "sha256:abc"
        };

        var recorded = await store.RecordParticipantAsync(operation.Id, receipt, ct);
        var replayed = await store.RecordParticipantAsync(
            operation.Id,
            receipt with { RecordedAt = receipt.RecordedAt.AddMinutes(1) },
            ct);

        recorded.Participants.Should().ContainSingle().Which.Should().Be(receipt);
        replayed.Should().BeEquivalentTo(recorded);
        var conflicting = receipt with { AfterIdentity = "workspace:3" };
        var act = () => store.RecordParticipantAsync(operation.Id, conflicting, ct);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*different immutable receipt*");
    }

    [Fact]
    public async Task Transition_EnforcesSagaOrderingAndIdempotency()
    {
        var ct = TestContext.Current.CancellationToken;
        using var store = new FileSystemCrossStoreOperationStore(rootPath);
        var operation = await StartAsync(store, ct);

        var invalid = () => store.TransitionAsync(
            operation.Id,
            CrossStoreOperationState.Completed,
            DateTimeOffset.UtcNow,
            cancellationToken: ct);
        await invalid.Should().ThrowAsync<InvalidOperationException>();

        var promoted = await store.TransitionAsync(
            operation.Id,
            CrossStoreOperationState.WorkspacePromoted,
            DateTimeOffset.UtcNow,
            cancellationToken: ct);
        var replay = await store.TransitionAsync(
            operation.Id,
            CrossStoreOperationState.WorkspacePromoted,
            DateTimeOffset.UtcNow.AddMinutes(1),
            cancellationToken: ct);
        replay.Should().BeEquivalentTo(promoted);

        var published = await store.TransitionAsync(
            operation.Id,
            CrossStoreOperationState.Published,
            DateTimeOffset.UtcNow,
            cancellationToken: ct);
        var completed = await store.TransitionAsync(
            operation.Id,
            CrossStoreOperationState.Completed,
            DateTimeOffset.UtcNow,
            cancellationToken: ct);
        published.State.Should().Be(CrossStoreOperationState.Published);
        completed.State.Should().Be(CrossStoreOperationState.Completed);
        completed.Transitions.Select(item => item.State).Should().Equal(
            CrossStoreOperationState.Prepared,
            CrossStoreOperationState.WorkspacePromoted,
            CrossStoreOperationState.Published,
            CrossStoreOperationState.Completed);
    }

    [Fact]
    public async Task Failure_RequiresReconciliationBeforeCompletion()
    {
        var ct = TestContext.Current.CancellationToken;
        using var store = new FileSystemCrossStoreOperationStore(rootPath);
        var operation = await StartAsync(store, ct);
        operation = await store.RecordParticipantAsync(
            operation.Id,
            new CrossStoreParticipantReceipt
            {
                Participant = "cangjie-publication",
                IdempotencyKey = operation.ParticipantIdempotencyKey("cangjie-publication"),
                State = CrossStoreParticipantState.Failed,
                RecordedAt = DateTimeOffset.UtcNow,
                RecoveryAction = "Replay publication from the typed artifact receipt."
            },
            ct);

        var missingReason = () => store.TransitionAsync(
            operation.Id,
            CrossStoreOperationState.ReconciliationRequired,
            DateTimeOffset.UtcNow,
            cancellationToken: ct);
        await missingReason.Should().ThrowAsync<ArgumentException>();

        var reconciliation = await store.TransitionAsync(
            operation.Id,
            CrossStoreOperationState.ReconciliationRequired,
            DateTimeOffset.UtcNow,
            "Cangjie publication outcome is unknown.",
            ct);
        reconciliation.ReconciliationReason.Should().Be(
            "Cangjie publication outcome is unknown.");
        reconciliation.Transitions.Should().Contain(item =>
            item.State == CrossStoreOperationState.ReconciliationRequired &&
            item.Reason == "Cangjie publication outcome is unknown.");

        var complete = () => store.TransitionAsync(
            operation.Id,
            CrossStoreOperationState.Completed,
            DateTimeOffset.UtcNow,
            cancellationToken: ct);
        await complete.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*failed participant*");
    }

    private static Task<CrossStoreOperation> StartAsync(
        ICrossStoreOperationStore store,
        CancellationToken cancellationToken)
    {
        var workflowRunId = Guid.CreateVersion7();
        return store.StartAsync(
            new StartCrossStoreOperationRequest(
                GuyabanoSessionId.New(),
                workflowRunId,
                "generation-promotion",
                $"{workflowRunId:D}:generation-promotion",
                DateTimeOffset.UtcNow),
            cancellationToken);
    }

    public void Dispose()
    {
        if (Directory.Exists(rootPath))
            Directory.Delete(rootPath, recursive: true);
    }
}
