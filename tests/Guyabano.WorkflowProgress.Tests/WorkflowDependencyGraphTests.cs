using FluentAssertions;
using Microsoft.Data.Sqlite;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Guyabano.WorkflowProgressTests;

public sealed class WorkflowDependencyGraphTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "guyabano-dependency-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task FanOutAndFanInTopology_ProducesExplicitDependencies()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var runId = Guid.NewGuid();
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(rootPath, ".gen", "zhinu.db"),
            Pooling = false
        });
        var workflow = new FanOutFanInWorkflow();
        var engine = new WorkflowEngine(store, new WorkflowRegistry().Register("fan-test", "1", workflow));

        await engine.StartAsync("fan-test", "1", "input", runId, cancellationToken: cancellationToken);
        await engine.ExecuteAsync(runId, cancellationToken);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: cancellationToken);

        var steps = await engine.GetStepsAsync(runId, cancellationToken);
        steps.Should().Contain(s => s.StepKey == "root");
        steps.Should().Contain(s => s.StepKey == "branch-a");
        steps.Should().Contain(s => s.StepKey == "branch-b");
        steps.Should().Contain(s => s.StepKey == "join");
        steps.Should().Contain(s => s.StepKey == "leaf");

        var graph = await engine.GetDependencyGraphAsync(runId, cancellationToken);
        graph.Should().NotBeNull();

        // Direct DB check for workflow_step_dependencies non-empty
        var count = await CountDependenciesAsync(Path.Combine(rootPath, ".gen", "zhinu.db"), cancellationToken);
        count.Should().BeGreaterThan(0, "a successful run must have explicit dependencies");

        // Verify fan-out/fan-in via restart preview: join depends on both branches (fan-in), leaf depends on join
        var planA = await engine.PlanRestartAsync(runId, "branch-a", StepRestartMode.Dependents, cancellationToken);
        var invalidatedA = planA.StepsToInvalidate.Select(s => s.StepKey).ToArray();
        invalidatedA.Should().Contain("branch-a");
        invalidatedA.Should().Contain("join");
        invalidatedA.Should().Contain("leaf");
        invalidatedA.Should().NotContain("branch-b");

        var planB = await engine.PlanRestartAsync(runId, "branch-b", StepRestartMode.Dependents, cancellationToken);
        var invalidatedB = planB.StepsToInvalidate.Select(s => s.StepKey).ToArray();
        invalidatedB.Should().Contain("branch-b");
        invalidatedB.Should().Contain("join");
        invalidatedB.Should().Contain("leaf");
        invalidatedB.Should().NotContain("branch-a");

        var planJoin = await engine.PlanRestartAsync(runId, "join", StepRestartMode.Dependents, cancellationToken);
        planJoin.StepsToInvalidate.Select(s => s.StepKey).Should().Contain("leaf");
        planJoin.StepsToInvalidate.Select(s => s.StepKey).Should().NotContain("root");
    }

    [Fact]
    public async Task UnaffectedSiblingBranches_RemainReusableOnRestartPreview()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var runId = Guid.NewGuid();
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(rootPath, ".gen", "zhinu.db"),
            Pooling = false
        });
        var workflow = new SiblingBranchWorkflow();
        var engine = new WorkflowEngine(store, new WorkflowRegistry().Register("sibling-test", "1", workflow));

        await engine.StartAsync("sibling-test", "1", "input", runId, cancellationToken: cancellationToken);
        await engine.ExecuteAsync(runId, cancellationToken);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: cancellationToken);

        // Preview restart of branch-a: should invalidate branch-a and its child a-child, but not branch-b or b-child
        var plan = await engine.PlanRestartAsync(runId, "branch-a", StepRestartMode.Dependents, cancellationToken);
        var invalidated = plan.StepsToInvalidate.Select(s => s.StepKey).ToArray();
        invalidated.Should().Contain("branch-a");
        invalidated.Should().Contain("a-child");
        invalidated.Should().NotContain("branch-b");
        invalidated.Should().NotContain("b-child");
        invalidated.Should().NotContain("root");

        // Restart of branch-b symmetrically
        var planB = await engine.PlanRestartAsync(runId, "branch-b", StepRestartMode.Dependents, cancellationToken);
        var invalidatedB = planB.StepsToInvalidate.Select(s => s.StepKey).ToArray();
        invalidatedB.Should().Contain("branch-b");
        invalidatedB.Should().Contain("b-child");
        invalidatedB.Should().NotContain("branch-a");
        invalidatedB.Should().NotContain("a-child");

        // Join depends on both branches, so restarting either branch should invalidate join and downstream
        var fanInWorkflow = new FanInSiblingWorkflow();
        var fanInRunId = Guid.NewGuid();
        var fanInStore = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(rootPath, ".gen", "zhinu-fanin.db"),
            Pooling = false
        });
        var fanInEngine = new WorkflowEngine(fanInStore, new WorkflowRegistry().Register("fanin-test", "1", fanInWorkflow));
        await fanInEngine.StartAsync("fanin-test", "1", "input", fanInRunId, cancellationToken: cancellationToken);
        await fanInEngine.ExecuteAsync(fanInRunId, cancellationToken);
        await fanInEngine.WaitForCompletionAsync<string>(fanInRunId, cancellationToken: cancellationToken);

        var fanInPlan = await fanInEngine.PlanRestartAsync(fanInRunId, "branch-a", StepRestartMode.Dependents, cancellationToken);
        var fanInInvalidated = fanInPlan.StepsToInvalidate.Select(s => s.StepKey).ToArray();
        fanInInvalidated.Should().Contain("branch-a");
        fanInInvalidated.Should().Contain("join");
        fanInInvalidated.Should().Contain("leaf");
        fanInInvalidated.Should().NotContain("branch-b");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(rootPath))
            Directory.Delete(rootPath, recursive: true);
    }

    private static async Task<int> CountDependenciesAsync(string dbPath, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM workflow_step_dependencies";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }

    private sealed class FanOutFanInWorkflow : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            await context.StepAsync("root", input, (value, step, token) => Task.FromResult("root"), new StepOptions(), cancellationToken);

            var branchA = context.StepAsync("branch-a", input, (value, step, token) => Task.FromResult("a"), new StepOptions { DependsOn = ["root"] }, cancellationToken);
            var branchB = context.StepAsync("branch-b", input, (value, step, token) => Task.FromResult("b"), new StepOptions { DependsOn = ["root"] }, cancellationToken);
            await Task.WhenAll(branchA, branchB);

            await context.StepAsync("join", input, (value, step, token) => Task.FromResult("join"), new StepOptions { DependsOn = ["branch-a", "branch-b"] }, cancellationToken);
            await context.StepAsync("leaf", input, (value, step, token) => Task.FromResult("leaf"), new StepOptions { DependsOn = ["join"] }, cancellationToken);
            return input;
        }
    }

    private sealed class SiblingBranchWorkflow : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            await context.StepAsync("root", input, (value, step, token) => Task.FromResult("root"), new StepOptions(), cancellationToken);
            var branchA = context.StepAsync("branch-a", input, (value, step, token) => Task.FromResult("a"), new StepOptions { DependsOn = ["root"] }, cancellationToken);
            var branchB = context.StepAsync("branch-b", input, (value, step, token) => Task.FromResult("b"), new StepOptions { DependsOn = ["root"] }, cancellationToken);
            await Task.WhenAll(branchA, branchB);
            await context.StepAsync("a-child", input, (value, step, token) => Task.FromResult("a-child"), new StepOptions { DependsOn = ["branch-a"] }, cancellationToken);
            await context.StepAsync("b-child", input, (value, step, token) => Task.FromResult("b"), new StepOptions { DependsOn = ["branch-b"] }, cancellationToken);
            return input;
        }
    }

    private sealed class FanInSiblingWorkflow : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            await context.StepAsync("root", input, (value, step, token) => Task.FromResult("root"), new StepOptions(), cancellationToken);
            var branchA = context.StepAsync("branch-a", input, (value, step, token) => Task.FromResult("a"), new StepOptions { DependsOn = ["root"] }, cancellationToken);
            var branchB = context.StepAsync("branch-b", input, (value, step, token) => Task.FromResult("b"), new StepOptions { DependsOn = ["root"] }, cancellationToken);
            await Task.WhenAll(branchA, branchB);
            await context.StepAsync("join", input, (value, step, token) => Task.FromResult("join"), new StepOptions { DependsOn = ["branch-a", "branch-b"] }, cancellationToken);
            await context.StepAsync("leaf", input, (value, step, token) => Task.FromResult("leaf"), new StepOptions { DependsOn = ["join"] }, cancellationToken);
            return input;
        }
    }
}
