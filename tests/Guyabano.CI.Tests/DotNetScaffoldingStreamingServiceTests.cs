using FluentAssertions;
using Microsoft.Extensions.Options;
using Guyabano.CI.Contracts;
using Guyabano.CI.Server;
using Guyabano.CI.Server.Services;

namespace Guyabano.CI.Tests;

public sealed class DotNetScaffoldingStreamingServiceTests
{
    [Fact]
    public async Task RunAsync_CreatesSolutionProjectsAndReference()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "guyabano-ci-scaffolding-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);

        try
        {
            var service = new DotNetScaffoldingStreamingService(
                Options.Create(new CiServerOptions
                {
                    GeneratedRoot = testRoot,
                    DotNetCommand = "dotnet"
                }),
                new SafePathResolver(testRoot),
                new ProcessRunner());
            var events = new List<CiStreamEvent>();

            await foreach (var streamEvent in service.RunAsync(
                new CiScaffoldRequest(
                    ".",
                    new CiScaffoldSolution("Todo", "./Todo.sln"),
                    [
                        new CiScaffoldProject(
                            "Todo.Api",
                            "src/Todo.Api/Todo.Api.csproj",
                            "WebApi",
                            "net10.0",
                            [],
                            []),
                        new CiScaffoldProject(
                            "Todo.Contracts",
                            "src/Todo.Contracts/Todo.Contracts.csproj",
                            "Contracts",
                            "net10.0",
                            [],
                            []),
                        new CiScaffoldProject(
                            "Todo.Tests",
                            "tests/Todo.Tests/Todo.Tests.csproj",
                            "UnitTests",
                            "net10.0",
                            ["Todo.Api"],
                            [])
                    ]),
                TestContext.Current.CancellationToken))
            {
                events.Add(streamEvent);
            }

            var resultEvent = events.Should().ContainSingle(streamEvent =>
                streamEvent.Type == "result" &&
                streamEvent.Success == true).Subject;
            resultEvent.Data.Should().BeOfType<CiScaffoldResult>()
                .Which.RemovedFiles.Should().BeEquivalentTo([
                    "src/Todo.Contracts/Class1.cs",
                    "tests/Todo.Tests/UnitTest1.cs"
                ]);
            File.Exists(Path.Combine(testRoot, "Todo.sln"))
                .Should().BeTrue();
            File.Exists(Path.Combine(
                    testRoot,
                    "src",
                    "Todo.Api",
                    "Todo.Api.csproj"))
                .Should().BeTrue();
            File.Exists(Path.Combine(
                    testRoot,
                    "src",
                    "Todo.Contracts",
                    "Class1.cs"))
                .Should().BeFalse();
            File.Exists(Path.Combine(
                    testRoot,
                    "tests",
                    "Todo.Tests",
                    "UnitTest1.cs"))
                .Should().BeFalse();

            var testProject = await File.ReadAllTextAsync(
                Path.Combine(
                    testRoot,
                    "tests",
                    "Todo.Tests",
                    "Todo.Tests.csproj"),
                TestContext.Current.CancellationToken);
            testProject.Should().Contain("Todo.Api.csproj");

            var solution = await File.ReadAllTextAsync(
                Path.Combine(testRoot, "Todo.sln"),
                TestContext.Current.CancellationToken);
            solution.Should().Contain("Todo.Api");
            solution.Should().Contain("Todo.Contracts");
            solution.Should().Contain("Todo.Tests");
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }
}
