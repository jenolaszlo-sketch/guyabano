using FluentAssertions;
using Guyabano.CodeGeneration.Validation.Validators;

namespace Guyabano.CodeGeneration.Validation.Tests;

public sealed class CSharpSyntaxValidatorTests
{
    private readonly CSharpSyntaxValidator validator = new();

    [Fact]
    public async Task ValidCSharpHasNoDiagnostics()
    {
        var result = await validator.ValidateAsync(
            new GeneratedFileContent(
                "Greeting.cs",
                "namespace Sample; public sealed class Greeting { }"),
            TestContext.Current.CancellationToken);

        result.IsValid.Should().BeTrue();
        result.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task InvalidCSharpReturnsRoslynDiagnosticWithLocation()
    {
        var result = await validator.ValidateAsync(
            new GeneratedFileContent(
                "Greeting.cs",
                "namespace Sample; public sealed class Greeting {"),
            TestContext.Current.CancellationToken);

        result.IsValid.Should().BeFalse();
        result.Diagnostics.Should().ContainSingle();
        result.Diagnostics[0].Code.Should().Be("CS1513");
        result.Diagnostics[0].Line.Should().NotBeNull();
        result.Diagnostics[0].Column.Should().NotBeNull();
    }
}
