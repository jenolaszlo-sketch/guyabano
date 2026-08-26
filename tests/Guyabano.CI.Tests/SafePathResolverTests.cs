using FluentAssertions;
using Guyabano.CI.Server.Services;

namespace Guyabano.CI.Tests;

public sealed class SafePathResolverTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"guyabano-ci-path-tests-{Guid.NewGuid():N}");

    public SafePathResolverTests()
    {
        Directory.CreateDirectory(root);
    }

    [Fact]
    public void Resolve_ReturnsChildOfConfiguredRoot()
    {
        var resolver = new SafePathResolver(root);

        var result = resolver.Resolve("run-123/src");

        result.Should().Be(
            Path.Combine(root, "run-123", "src"));
    }

    [Fact]
    public void Resolve_AllowsConfiguredRootToken()
    {
        var resolver = new SafePathResolver(root);

        var result = resolver.Resolve(".");

        result.Should().Be(root);
    }

    [Theory]
    [InlineData("./TodoApi.sln", "TodoApi.sln")]
    [InlineData("run/./src", "run/src")]
    [InlineData("./run/./src", "run/src")]
    public void Resolve_NormalizesCurrentDirectorySegments(
        string path,
        string expected)
    {
        var resolver = new SafePathResolver(root);

        var result = resolver.Resolve(path);

        result.Should().Be(Path.Combine(
            root,
            expected.Replace('/', Path.DirectorySeparatorChar)));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("run/../../outside")]
    public void Resolve_RejectsUnsafeSegments(string path)
    {
        var resolver = new SafePathResolver(root);

        var action = () => resolver.Resolve(path);

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
