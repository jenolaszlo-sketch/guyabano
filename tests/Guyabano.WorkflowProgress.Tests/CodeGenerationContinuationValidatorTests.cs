using FluentAssertions;
using Guyabano.CodeGeneration.Workflows;

namespace Guyabano.WorkflowProgressTests;

public sealed class CodeGenerationContinuationValidatorTests
{
    [Fact]
    public void ValidateBuildAndRepair_ReportsEveryMissingBoundary()
    {
        var checkpoint = new CodeGenerationRunCheckpoint(
            "source-workflow",
            "prompt",
            new CodeGenerationWorkflowResult(
                false,
                "CompilationFailed",
                "Build failed.",
                "model",
                false,
                [],
                [],
                []));

        var errors = CodeGenerationContinuationValidator
            .ValidateBuildAndRepair(checkpoint);

        errors.Should().HaveCount(5);
        errors.Should().Contain(error => error.Contains("architecture plan"));
        errors.Should().Contain(error => error.Contains("scaffolding"));
        errors.Should().Contain(error => error.Contains("decompositions"));
        errors.Should().Contain(error => error.Contains("task provenance"));
        errors.Should().Contain(error => error.Contains("generated files"));
    }
}
