#pragma warning disable xUnit1030
using System.Text.Json;
using FluentAssertions;
using Guyabano.CodeGeneration.Planning.Fuwen;
using Penghou.Fuwen;

namespace Guyabano.WorkflowProgressTests;

/// <summary>
/// Fix 3 of the pre-slice-4 hardening: the Fuwen context node performs the
/// real prompt assembly (worker parity for disclosure opt-in, truncation,
/// and markers), with content-addressed snapshot evidence.
/// </summary>
public sealed class RealPlanningContextTests
{
    [Fact]
    public async Task Disabled_opt_in_passes_request_through_untouched()
    {
        var output = await AssembleAsync("Plan todos.", "repo stuff", include: false, maxCharacters: 40000);

        output.Should().Be("Plan todos.");
    }

    [Fact]
    public async Task Omitted_repository_context_passes_request_through()
    {
        // R25 omission at the context boundary.
        var provider = new PlanningRequestContextProvider();
        var request = Request("Plan todos.", include: true, maxCharacters: 40000, repositoryContent: null, omitRepositoryContext: true);

        var result = await provider.ExecuteAsync(request, TestContext.Current.CancellationToken);

        ((JsonRuntimeValue)result.Output!).Value.GetString().Should().Be("Plan todos.");
    }

    [Fact]
    public async Task Short_content_is_wrapped_with_untrusted_banner()
    {
        var output = await AssembleAsync("Plan todos.", "var x = 1;", include: true, maxCharacters: 40000);

        output.Should().Contain("Plan todos.");
        output.Should().Contain("untrusted reference data");
        output.Should().Contain("var x = 1;");
        output.Should().Contain("<session-context>");
    }

    [Fact]
    public async Task Long_content_is_truncated_with_marker()
    {
        var output = await AssembleAsync("Plan todos.", "123456789", include: true, maxCharacters: 5);

        output.Should().Contain("12345");
        output.Should().NotContain("6789");
        output.Should().Contain(PlanningRequestContextProvider.TruncationMarker.Trim());
    }

    [Fact]
    public async Task Snapshots_are_content_addressed_and_deterministic()
    {
        var provider = new PlanningRequestContextProvider();
        var first = await provider.ExecuteAsync(
            Request("Plan todos.", include: true, maxCharacters: 40000, repositoryContent: "abc", omitRepositoryContext: false),
            TestContext.Current.CancellationToken);
        var second = await provider.ExecuteAsync(
            Request("Plan todos.", include: true, maxCharacters: 40000, repositoryContent: "abc", omitRepositoryContext: false),
            TestContext.Current.CancellationToken);
        var third = await provider.ExecuteAsync(
            Request("Plan notes.", include: true, maxCharacters: 40000, repositoryContent: "abc", omitRepositoryContext: false),
            TestContext.Current.CancellationToken);

        first.ContextSnapshot.Should().NotBeNull();
        second.ContextSnapshot.Should().NotBeNull();
        third.ContextSnapshot.Should().NotBeNull();
        first.ContextSnapshot!.ContentDigest.Should().Be(second.ContextSnapshot!.ContentDigest);
        first.ContextSnapshot!.ContentDigest.Should().NotBe(third.ContextSnapshot!.ContentDigest);
    }

    private static async Task<string> AssembleAsync(
        string request, string? content, bool include, int maxCharacters)
    {
        var provider = new PlanningRequestContextProvider();
        var result = await provider.ExecuteAsync(
            Request(request, include, maxCharacters, content, omitRepositoryContext: false),
            TestContext.Current.CancellationToken);
        var output = result.Output.Should().BeOfType<JsonRuntimeValue>().Subject;
        return output.Value.GetString()!;
    }

    private static ContextExecutionRequest Request(
        string request, bool include, int maxCharacters, string? repositoryContent, bool omitRepositoryContext)
    {
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        var arguments = new List<RuntimeArgument>
        {
            new("request", RuntimeValue.FromJson(JsonDocument.Parse(JsonSerializer.Serialize(request)).RootElement)),
            new("includeRepositoryContext", RuntimeValue.FromJson(JsonDocument.Parse(include ? "true" : "false").RootElement)),
            new("maxCharacters", RuntimeValue.FromJson(JsonDocument.Parse(maxCharacters.ToString()).RootElement)),
        };
        if (!omitRepositoryContext && repositoryContent is not null)
            arguments.Add(new("repositoryContext", RuntimeValue.FromJson(JsonDocument.Parse(JsonSerializer.Serialize(repositoryContent)).RootElement)));
        var provider = PlanningFuwenDescriptors.Context();
        return new ContextExecutionRequest(
            new ExecutionInvocation(
                $"sha256:fuwen-execution/v3:{new string('a', 64)}",
                "realCtx/ctx", "run/realCtx/ctx", 1L,
                $"sha256:request/v1:{new string('b', 64)}"),
            provider,
            arguments,
            str);
    }
}
