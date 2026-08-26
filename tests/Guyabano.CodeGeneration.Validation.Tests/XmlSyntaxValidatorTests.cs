using FluentAssertions;
using Guyabano.CodeGeneration.Validation.Validators;

namespace Guyabano.CodeGeneration.Validation.Tests;

public sealed class XmlSyntaxValidatorTests
{
    private readonly XmlSyntaxValidator validator = new();

    [Fact]
    public async Task ValidProjectXmlHasNoDiagnostics()
    {
        var result = await validator.ValidateAsync(
            new GeneratedFileContent(
                "Sample.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup /></Project>"),
            TestContext.Current.CancellationToken);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task InvalidXmlReturnsLocation()
    {
        var result = await validator.ValidateAsync(
            new GeneratedFileContent(
                "Sample.csproj",
                "<Project><PropertyGroup></Project>"),
            TestContext.Current.CancellationToken);

        result.IsValid.Should().BeFalse();
        result.Diagnostics.Should().ContainSingle();
        result.Diagnostics[0].Code.Should().Be("XML001");
        result.Diagnostics[0].Line.Should().Be(1);
        result.Diagnostics[0].Column.Should().NotBeNull();
    }
}
