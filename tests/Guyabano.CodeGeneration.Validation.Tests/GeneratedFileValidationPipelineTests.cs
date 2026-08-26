using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Guyabano.CodeGeneration.Validation.Extensions;

namespace Guyabano.CodeGeneration.Validation.Tests;

public sealed class GeneratedFileValidationPipelineTests
{
    [Fact]
    public async Task RunsEveryValidatorRegisteredForAnExtension()
    {
        var services = new ServiceCollection();
        services.AddCodeGenerationValidation(validation => validation
            .AddValidator<FirstSampleValidator>("sample")
            .AddValidator<SecondSampleValidator>(".sample"));

        await using var serviceProvider = services.BuildServiceProvider();
        var pipeline = serviceProvider.GetRequiredService<
            IGeneratedFileValidationPipeline>();

        var result = await pipeline.ValidateAsync(
            [new GeneratedFileContent("example.SAMPLE", "content")],
            TestContext.Current.CancellationToken);

        result.ValidatedFiles.Should().ContainSingle()
            .Which.Should().Be("example.SAMPLE");
        result.Diagnostics.Select(diagnostic => diagnostic.Code)
            .Should().BeEquivalentTo("FIRST", "SECOND");
        result.Files.Should().ContainSingle()
            .Which.WasValidated.Should().BeTrue();
    }

    [Fact]
    public async Task ReportsFilesWithoutARegisteredValidator()
    {
        var services = new ServiceCollection();
        services.AddCodeGenerationValidation();

        await using var serviceProvider = services.BuildServiceProvider();
        var pipeline = serviceProvider.GetRequiredService<
            IGeneratedFileValidationPipeline>();

        var result = await pipeline.ValidateAsync(
            [new GeneratedFileContent("README.md", "# Sample")],
            TestContext.Current.CancellationToken);

        result.ValidatedFiles.Should().BeEmpty();
        result.UnvalidatedFiles.Should().ContainSingle()
            .Which.Should().Be("README.md");
        result.IsValid.Should().BeTrue();
        result.Files.Should().ContainSingle()
            .Which.WasValidated.Should().BeFalse();
    }

    private sealed class FirstSampleValidator : SampleValidator
    {
        public override string Name => "first";

        protected override string Code => "FIRST";
    }

    private sealed class SecondSampleValidator : SampleValidator
    {
        public override string Name => "second";

        protected override string Code => "SECOND";
    }

    private abstract class SampleValidator : IGeneratedFileValidator
    {
        public abstract string Name { get; }

        protected abstract string Code { get; }

        public ValueTask<FileValidationResult> ValidateAsync(
            GeneratedFileContent file,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                new FileValidationResult(
                    [
                        new FileValidationDiagnostic(
                            Name,
                            Code,
                            FileValidationSeverity.Warning,
                            "Sample diagnostic.",
                            file.Path)
                    ]));
    }
}
