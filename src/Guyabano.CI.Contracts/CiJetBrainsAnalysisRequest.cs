namespace Guyabano.CI.Contracts;

public sealed record CiJetBrainsAnalysisRequest(
    string RelativePath,
    string? ProjectOrSolutionFile = null)
    : CiOperationRequest(RelativePath, ProjectOrSolutionFile);
