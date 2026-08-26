using FluentAssertions;
using Guyabano.Llm.CodeGeneration;

namespace Guyabano.Llm.CodeGeneration.Tests;

public sealed class GeneratedFileScopeValidatorTests
{
    [Fact]
    public void Validate_AcceptsFilesInsideAssignedProject()
    {
        var result = CreateResult("src/Todo.Api/Services/TodoService.cs");

        var error = GeneratedFileScopeValidator.Validate(
            result,
            "src/Todo.Api",
            "src/Todo.Api/Todo.Api.csproj",
            "Todo.sln");

        error.Should().BeNull();
    }

    [Theory]
    [InlineData("Todo.sln")]
    [InlineData("src/Todo.Api/Todo.Api.csproj")]
    [InlineData("tests/Todo.Tests/TodoTests.cs")]
    [InlineData("src/Todo.Api/../Todo.Tests/Test.cs")]
    public void Validate_RejectsProtectedOrOutOfScopePaths(string path)
    {
        var error = GeneratedFileScopeValidator.Validate(
            CreateResult(path),
            "src/Todo.Api",
            "src/Todo.Api/Todo.Api.csproj",
            "Todo.sln");

        error.Should().Contain(path);
    }

    [Theory]
    [InlineData("Todo.sln")]
    [InlineData("src/Todo.Api/Todo.Api.csproj")]
    public void Validate_AcceptsExplicitBuildArtifact(string path)
    {
        var error = GeneratedFileScopeValidator.Validate(
            CreateResult(path),
            "src/Todo.Api",
            "src/Todo.Api/Todo.Api.csproj",
            "Todo.sln",
            [path]);

        error.Should().BeNull();
    }

    [Fact]
    public void Validate_StillRejectsUnlistedBuildArtifact()
    {
        const string path = "tests/Todo.Tests/Todo.Tests.csproj";

        var error = GeneratedFileScopeValidator.Validate(
            CreateResult(path),
            "src/Todo.Api",
            "src/Todo.Api/Todo.Api.csproj",
            "Todo.sln",
            ["src/Todo.Api/Todo.Api.csproj"]);

        error.Should().Contain(path);
    }

    [Fact]
    public void Validate_RejectsBackupFileDuringExactBuildArtifactRepair()
    {
        const string project = "src/Todo.Api/Todo.Api.csproj";
        var result = new CodeGenerationResult
        {
            Files =
            [
                CreateResult(project).Files[0],
                CreateResult($"{project}.bak").Files[0]
            ]
        };

        var error = GeneratedFileScopeValidator.Validate(
            result,
            "src/Todo.Api",
            project,
            "Todo.sln",
            [project]);

        error.Should().Contain($"{project}.bak");
    }

    [Fact]
    public void Validate_RejectsMissingRequestedBuildArtifact()
    {
        const string project = "src/Todo.Api/Todo.Api.csproj";

        var error = GeneratedFileScopeValidator.Validate(
            new CodeGenerationResult { Files = [] },
            "src/Todo.Api",
            project,
            "Todo.sln",
            [project]);

        error.Should().Contain($"{project} (missing)");
    }

    private static CodeGenerationResult CreateResult(string path) =>
        new()
        {
            Files =
            [
                new GeneratedFile
                {
                    Path = path,
                    Content = "namespace Todo.Api;"
                }
            ]
        };
}
