using FluentAssertions;

namespace Guyabano.CodeGeneration.Planning.Tests;

public sealed class ArchitectureDecisionPackageReferenceValidatorTests
{
    [Fact]
    public void Validate_AcceptsEmptyRelatedPackages()
    {
        var plan = PlanTestData.Create();
        var decision = CreateDecision([]);

        ArchitectureDecisionPackageReferenceValidator.Validate(
                plan.Projects,
                decision)
            .Should()
            .BeEmpty();
    }

    [Fact]
    public void Validate_AcceptsDeclaredNuGetPackageIdCaseInsensitively()
    {
        var plan = PlanTestData.Create();
        plan.Projects[0].Packages.Add(new PackageRequirement
        {
            Name = "Microsoft.Extensions.Http",
            Version = "10.0.0",
            Purpose = "HTTP client integration"
        });
        var decision = CreateDecision(["microsoft.extensions.http"]);

        ArchitectureDecisionPackageReferenceValidator.Validate(
                plan.Projects,
                decision)
            .Should()
            .BeEmpty();
    }

    [Theory]
    [InlineData("TodoApi")]
    [InlineData("TodoApi.Contracts")]
    public void Validate_RejectsProjectAssemblyOrNamespaceNames(string value)
    {
        var plan = PlanTestData.Create();
        var decision = CreateDecision([value]);

        ArchitectureDecisionPackageReferenceValidator.Validate(
                plan.Projects,
                decision)
            .Should()
            .ContainSingle()
            .Which.Should()
            .Contain($"unknown NuGet package '{value}'")
            .And.Contain("relatedPackages accepts declared NuGet package IDs only")
            .And.Contain("use an empty array");
    }

    [Fact]
    public void GapResolutionValidation_RejectsInvalidAdrBeforeIntegration()
    {
        var plan = PlanTestData.Create();
        var finding = ArchitectureReviewValidatorTests.CreateReview(false)
            .Findings[0];
        var resolution = new ArchitectureGapResolution
        {
            FindingId = finding.Id,
            ResolutionKind = "ProjectDecision",
            Decision = "Separate API contracts from the host.",
            DecisionRecord = new ArchitectureDecision
            {
                Id = "ADR-RESOLUTION-V2-AR-01",
                Title = "Separate API contracts",
                Decision = "Separate API contracts from the host.",
                Reasons = ["Keeps dependency direction explicit."],
                AlternativesRejected = ["Keep all types in the host."],
                RelatedPackages = ["TodoApi", "TodoApi.Contracts"]
            },
            AppliedPractice = new ArchitecturePractice
            {
                Id = "project.contract-boundary",
                Title = "Explicit contract boundary",
                Guidance = "Keep public contracts separate from hosting concerns.",
                Applicability = "The current project architecture.",
                Reasons = ["Keeps dependencies explicit."],
                Scope = "Project"
            },
            ReusedExistingPractice = false,
            Reasons = ["Keeps dependency direction explicit."],
            AlternativesConsidered = ["Keep all types in the host."],
            Consequences = ["The host references the contracts project."],
            AffectedIds = [.. finding.AffectedIds],
            UserOverridable = true,
            RequiresUserInput = false,
            UserQuestion = string.Empty
        };

        var errors = ArchitectureGapResolutionService.Validate(
            plan,
            finding,
            [],
            "ADR-RESOLUTION-V2-AR-01",
            resolution);

        errors.Should().Contain(error => error.Contains(
            "unknown NuGet package 'TodoApi'",
            StringComparison.Ordinal));
        errors.Should().Contain(error => error.Contains(
            "unknown NuGet package 'TodoApi.Contracts'",
            StringComparison.Ordinal));
    }

    private static ArchitectureDecision CreateDecision(
        List<string> relatedPackages) =>
        new()
        {
            Id = "ADR-RESOLUTION-V2-F-001",
            Title = "Separate contracts",
            Decision = "Keep API contracts in the contracts project.",
            Reasons = ["Makes the public boundary explicit."],
            AlternativesRejected = ["Place every type in the API project."],
            RelatedPackages = relatedPackages
        };
}
