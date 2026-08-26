namespace Guyabano.CodeGeneration.Planning;

internal static class RequirementIdentity
{
    public static string UseCaseId(DiscoveredUseCase useCase) =>
        StablePlanningId.Create("UC", useCase.Name);

    public static string AcceptanceCriterionId(
        DiscoveredUseCase useCase,
        DiscoveredAcceptanceCriterion criterion) =>
        StablePlanningId.Create(
            "AC",
            $"{useCase.Name}-{criterion.Scenario}");
}
