namespace Guyabano.Llm.Prompting;

public sealed record CodeGenerationTaskContext(
    string OriginalRequest,
    string TaskId,
    string TaskTitle,
    string Objective,
    string SolutionName,
    string SolutionPath,
    string ProjectName,
    string ProjectPath,
    string ProjectDirectory,
    string RootNamespace,
    string TargetFramework,
    string ModuleName,
    IReadOnlyList<string> ModuleResponsibilities,
    IReadOnlyList<string> Deliverables,
    IReadOnlyList<CodeGenerationTaskContractContext> Contracts,
    IReadOnlyList<CodeGenerationTaskAcceptanceContext> AcceptanceCriteria,
    IReadOnlyList<CodeGenerationTaskDecisionContext> Decisions,
    IReadOnlyList<ProjectFileContext> Files,
    CodeGenerationTaskRetryContext? Retry = null,
    string? ParentTaskId = null,
    IReadOnlyList<string>? ImplementationRequirements = null,
    IReadOnlyList<CodeGenerationArtifactContext>? Artifacts = null,
    IReadOnlyList<CodeGenerationTaskArchitectureNoteContext>?
        ArchitectureNotes = null,
    bool AllowBuildArtifacts = false);
