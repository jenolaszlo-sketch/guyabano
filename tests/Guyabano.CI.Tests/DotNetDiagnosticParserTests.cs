using FluentAssertions;
using Microsoft.Extensions.Options;
using Guyabano.CI.Contracts;
using Guyabano.CI.Server;
using Guyabano.CI.Server.Services;

namespace Guyabano.CI.Tests;

public sealed class DotNetDiagnosticParserTests
{
    private readonly string root = Path.GetFullPath(Path.Combine(
        Path.GetTempPath(),
        "guyabano-generated"));

    [Fact]
    public void TryParse_MapsRoslynErrorToGeneratedFile()
    {
        var source = Path.Combine(
            root,
            "src",
            "Guyabano.Generated",
            "Program.cs");
        var project = Path.Combine(
            root,
            "src",
            "Guyabano.Generated",
            "Guyabano.Generated.csproj");
        var parser = CreateParser();

        var parsed = parser.TryParse(
            $"{source}(12,18): error CS0103: The name 'builder' does not exist [{project}]",
            out var diagnostic);

        parsed.Should().BeTrue();
        diagnostic.Should().NotBeNull();
        diagnostic!.Code.Should().Be("CS0103");
        diagnostic.Severity.Should().Be(CiDiagnosticSeverity.Error);
        diagnostic.FilePath.Should().Be("src/Guyabano.Generated/Program.cs");
        diagnostic.ProjectPath.Should().Be(
            "src/Guyabano.Generated/Guyabano.Generated.csproj");
        diagnostic.Line.Should().Be(12);
        diagnostic.Column.Should().Be(18);
    }

    [Fact]
    public void TryParse_MapsSolutionErrorToSolution()
    {
        var solution = Path.Combine(root, "Guyabano.Generated.sln");
        var parser = CreateParser();

        var parsed = parser.TryParse(
            $"{solution} : Solution file error MSB5010: No file format header found.",
            out var diagnostic);

        parsed.Should().BeTrue();
        diagnostic!.FilePath.Should().Be("Guyabano.Generated.sln");
        diagnostic.Code.Should().Be("MSB5010");
    }

    [Fact]
    public void TryParse_UsesProjectForExternalSdkDiagnostic()
    {
        var project = Path.Combine(
            root,
            "src",
            "Guyabano.Generated",
            "Guyabano.Generated.csproj");
        var parser = CreateParser();

        var parsed = parser.TryParse(
            $"/usr/share/dotnet/sdk/10.0/Sdks.targets(1,2): error MSB4019: Imported project was not found [{project}]",
            out var diagnostic);

        parsed.Should().BeTrue();
        diagnostic!.FilePath.Should().Be(
            "src/Guyabano.Generated/Guyabano.Generated.csproj");
        diagnostic.ProjectPath.Should().Be(
            "src/Guyabano.Generated/Guyabano.Generated.csproj");
    }

    [Fact]
    public void TryParse_IgnoresOrdinaryBuildOutput()
    {
        CreateParser().TryParse(
            "Determining projects to restore...",
            out var diagnostic).Should().BeFalse();
        diagnostic.Should().BeNull();
    }

    private DotNetDiagnosticParser CreateParser() =>
        new(Options.Create(new CiServerOptions
        {
            GeneratedRoot = root
        }));
}
