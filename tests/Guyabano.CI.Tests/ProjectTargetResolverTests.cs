using FluentAssertions;
using Guyabano.CI.Server.Services;

namespace Guyabano.CI.Tests;

public sealed class ProjectTargetResolverTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"guyabano-ci-target-tests-{Guid.NewGuid():N}");

    public ProjectTargetResolverTests()
    {
        Directory.CreateDirectory(root);
    }

    [Fact]
    public void Resolve_PrefersSolutionOverProject()
    {
        File.WriteAllText(
            Path.Combine(root, "Sample.csproj"),
            "<Project />");
        File.WriteAllText(
            Path.Combine(root, "Sample.sln"),
            "Microsoft Visual Studio Solution File");

        var result = new ProjectTargetResolver().Resolve(root, null);

        result.Should().Be("Sample.sln");
    }

    [Fact]
    public void Resolve_NormalizesLeadingCurrentDirectorySegment()
    {
        File.WriteAllText(
            Path.Combine(root, "Sample.sln"),
            "Microsoft Visual Studio Solution File");

        var result = new ProjectTargetResolver().Resolve(
            root,
            "./Sample.sln");

        result.Should().Be("Sample.sln");
    }

    [Fact]
    public void Resolve_RejectsExplicitTraversal()
    {
        var action = () => new ProjectTargetResolver().Resolve(
            root,
            "../Sample.sln");

        action.Should().Throw<InvalidOperationException>();
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
