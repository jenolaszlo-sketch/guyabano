using FluentAssertions;
using Guyabano.CodeGeneration.Planning;
using Guyabano.WorkflowWorker;
using Microsoft.Data.Sqlite;
using Penghou.Cangjie;
using Penghou.Cangjie.Sqlite;
using Guyabano.Session;

namespace Guyabano.WorkflowProgressTests;

public sealed class CangjieRevisionedConceptTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "guyabano-cangjie-revisioned-tests",
        Guid.NewGuid().ToString("N"));

    private static ArchitectureDecision CreateDecision(string id, string decisionText) => new()
    {
        Id = id,
        Title = $"Decision {id}",
        Decision = decisionText,
        Reasons = ["reason"],
        AlternativesRejected = ["alt"],
        RelatedPackages = []
    };

    [Fact]
    public async Task ConceptWrite_RecordsSagaReceiptAndAppendOnlyEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        var contextStore = CreateStore();
        using var operations = new FileSystemCrossStoreOperationStore(
            Path.Combine(rootPath, "operations"));
        using var events = new FileSystemSessionEventStore(
            Path.Combine(rootPath, "events"));
        var sessionId = GuyabanoSessionId.New();
        var runId = Guid.CreateVersion7();
        var operation = await operations.StartAsync(
            new StartCrossStoreOperationRequest(
                sessionId,
                runId,
                "cangjie-proof",
                $"{runId:D}:cangjie-proof",
                DateTimeOffset.UtcNow),
            ct);
        var service = new CangjieRevisionedConceptService(
            contextStore,
            operations,
            events);

        var item = await service.StoreKnowledgeAsync(
            sessionId.ToString(),
            "accepted-clarification",
            "Use append-only audit history.",
            runId.ToString("D"),
            "clarification/promote",
            1,
            cancellationToken: ct);
        await service.StoreKnowledgeAsync(
            sessionId.ToString(),
            "accepted-clarification",
            "Use append-only audit history.",
            runId.ToString("D"),
            "clarification/promote",
            1,
            cancellationToken: ct);

        var recorded = await operations.GetAsync(operation.Id, ct);
        recorded!.Participants.Should().ContainSingle(receipt =>
            receipt.Participant == $"cangjie-publication:{item.Id:D}" &&
            receipt.State == CrossStoreParticipantState.Applied);
        (await events.ReadAsync(sessionId, cancellationToken: ct))
            .Should().ContainSingle(item => item.EventType ==
                SessionEventTypes.OperationParticipantRecorded);
    }

    [Fact]
    public async Task Decision_RegeneratingSameLogicalKey_ProducesDeterministicHistory()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var service = new CangjieRevisionedConceptService(store);
        var sessionId = Guid.NewGuid().ToString();
        var workflowRunId = Guid.NewGuid().ToString();
        var decisionId = "ADR-001";

        var first = CreateDecision(decisionId, "Use SQLite for embedded store");
        var firstItem = await service.StoreDecisionAsync(sessionId, first, workflowRunId, "architecture-review/1/1", 1, cancellationToken: ct);

        var latest1 = await service.GetLatestDecisionAsync(sessionId, decisionId, cancellationToken: ct);
        latest1.Should().NotBeNull();
        latest1!.Id.Should().Be(firstItem.Id);

        // Regenerating with same content should be idempotent (same revision, no new history)
        var sameContent = CreateDecision(decisionId, "Use SQLite for embedded store");
        var sameItem = await service.StoreDecisionAsync(sessionId, sameContent, workflowRunId, "architecture-review/1/1", 1, cancellationToken: ct);
        sameItem.Id.Should().Be(firstItem.Id);
        var historySame = await service.GetDecisionHistoryAsync(sessionId, decisionId, cancellationToken: ct);
        historySame.Should().HaveCount(1);

        // Regenerating with different content should create new revision with supersedes
        var second = CreateDecision(decisionId, "Use Postgres for embedded store");
        var secondItem = await service.StoreDecisionAsync(sessionId, second, workflowRunId, "architecture-review/1/2", 2, cancellationToken: ct);
        secondItem.Id.Should().NotBe(firstItem.Id);
        secondItem.Revision.Should().BeGreaterThan(latest1.Revision);

        var latest2 = await service.GetLatestDecisionAsync(sessionId, decisionId, cancellationToken: ct);
        latest2!.Id.Should().Be(secondItem.Id);
        latest2.Content.Should().Contain("Postgres");

        var history = await service.GetDecisionHistoryAsync(sessionId, decisionId, cancellationToken: ct);
        history.Should().HaveCount(2);
        history.Select(h => h.Revision).Should().Contain(latest1.Revision);
        history.Select(h => h.Revision).Should().Contain(secondItem.Revision);

        // Verify supersedes relation from new to old
        var relations = await store.GetRelationsAsync(secondItem.Id, ContextRelationDirection.Outgoing, ct);
        relations.Should().Contain(r => r.Kind == ContextRelationKinds.Supersedes && r.ToId == firstItem.Id);

        // Prior revision remains auditable
        var firstFromHistory = history.Single(h => h.Id == firstItem.Id);
        firstFromHistory.Content.Should().Contain("SQLite");
    }

    [Fact]
    public async Task Evidence_StoresBuildObservation_AsRevisionedEvidence()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var service = new CangjieRevisionedConceptService(store);
        var sessionId = Guid.NewGuid().ToString();
        var workflowRunId = Guid.NewGuid().ToString();

        var evidenceKey = "build:run-1:attempt-1";
        var firstContent = "Build succeeded with 0 errors";
        var first = await service.StoreEvidenceAsync(sessionId, evidenceKey, firstContent, workflowRunId, "build/1", 1, cancellationToken: ct);
        first.Kind.Should().Be(ContextKinds.Evidence);

        var latest = await store.GetLatestByKeyAsync($"guyabano:session:{sessionId}", $"evidence:{evidenceKey}", ct);
        latest.Should().NotBeNull();
        latest!.Id.Should().Be(first.Id);

        var secondContent = "Build failed with 2 errors: CS0001";
        var second = await service.StoreEvidenceAsync(sessionId, evidenceKey, secondContent, workflowRunId, "build/1", 2, cancellationToken: ct);
        second.Id.Should().NotBe(first.Id);
        second.Revision.Should().BeGreaterThan(first.Revision);

        var history = await store.GetHistoryByKeyAsync($"guyabano:session:{sessionId}", $"evidence:{evidenceKey}", ct);
        history.Should().HaveCount(2);
    }

    [Fact]
    public async Task Knowledge_StoresLesson_WithDerivedFromRelation()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var service = new CangjieRevisionedConceptService(store);
        var sessionId = Guid.NewGuid().ToString();
        var workflowRunId = Guid.NewGuid().ToString();

        var evidence = await service.StoreEvidenceAsync(sessionId, "build-failure-1", "Build failed CS0001", workflowRunId, "build/1", 1, cancellationToken: ct);
        var knowledge = await service.StoreKnowledgeAsync(sessionId, "lesson:cs0001", "Lesson: missing using directive", workflowRunId, "build/1", 1, derivedFromIds: [evidence.Id], cancellationToken: ct);

        knowledge.Kind.Should().Be(ContextKinds.Knowledge);
        var relations = await store.GetRelationsAsync(knowledge.Id, ContextRelationDirection.Outgoing, ct);
        relations.Should().Contain(r => r.Kind == ContextRelationKinds.DerivedFrom && r.ToId == evidence.Id);
    }

    [Fact]
    public async Task Scope_IsSessionRepository_NotWorkflowRun()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var service = new CangjieRevisionedConceptService(store);
        var sessionId = Guid.NewGuid().ToString();
        var repositoryId = "repo:test";
        var decisionId = "ADR-002";

        var decision = CreateDecision(decisionId, "Use session scope");
        var item = await service.StoreDecisionAsync(sessionId, decision, Guid.NewGuid().ToString(), "step-1", 1, repositoryId, cancellationToken: ct);

        item.Scope.Should().Be($"guyabano:session:{sessionId}:repository:{repositoryId}");
        item.Key.Should().Be($"decision:{decisionId}");

        // Same decision in different session should be different scope, not same history
        var otherSessionId = Guid.NewGuid().ToString();
        var otherItem = await service.StoreDecisionAsync(otherSessionId, decision, Guid.NewGuid().ToString(), "step-1", 1, repositoryId, cancellationToken: ct);
        otherItem.Scope.Should().NotBe(item.Scope);
        otherItem.Id.Should().NotBe(item.Id);

        var history = await service.GetDecisionHistoryAsync(sessionId, decisionId, repositoryId, ct);
        history.Should().HaveCount(1);
        var otherHistory = await service.GetDecisionHistoryAsync(otherSessionId, decisionId, repositoryId, ct);
        otherHistory.Should().HaveCount(1);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(rootPath))
            Directory.Delete(rootPath, recursive: true);
    }

    private IContextStore CreateStore()
    {
        Directory.CreateDirectory(rootPath);
        var dbPath = Path.Combine(rootPath, $"cangjie-{Guid.NewGuid():N}.db");
        return new SqliteContextStore(new CangjieSqliteOptions { DatabasePath = dbPath, Pooling = false });
    }
}
