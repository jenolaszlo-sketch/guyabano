using FluentAssertions;
using Guyabano.CodeGeneration.Validation.Validators;

namespace Guyabano.CodeGeneration.Validation.Tests;

public sealed class JsonSyntaxValidatorTests
{
    private readonly JsonSyntaxValidator validator = new();

    [Fact]
    public async Task ValidJsonHasNoDiagnostics()
    {
        var result = await validator.ValidateAsync(
            new GeneratedFileContent(
                "appsettings.json",
                "{\"Logging\":{\"Level\":\"Information\"}}"),
            TestContext.Current.CancellationToken);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task InvalidJsonReturnsLocation()
    {
        var result = await validator.ValidateAsync(
            new GeneratedFileContent(
                "appsettings.json",
                "{\"Logging\": true,}"),
            TestContext.Current.CancellationToken);

        result.IsValid.Should().BeFalse();
        result.Diagnostics.Should().ContainSingle();
        result.Diagnostics[0].Code.Should().Be("JSON001");
        result.Diagnostics[0].Line.Should().Be(1);
        result.Diagnostics[0].Column.Should().NotBeNull();
    }
}
