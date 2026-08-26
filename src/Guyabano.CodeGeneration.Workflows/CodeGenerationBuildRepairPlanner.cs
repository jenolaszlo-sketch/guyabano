using Guyabano.CodeGeneration.Planning;

namespace Guyabano.CodeGeneration.Workflows;

internal static class CodeGenerationBuildRepairPlanner
{
    public static IReadOnlyList<CodeGenerationTaskWorkflowRequest> Create(
        CodeGenerationPlan plan,
        CodeGenerationBuildResult build,
        IReadOnlyList<CodeGenerationTaskWorkflowResult> taskResults,
        IReadOnlyList<CodeGenerationDecompositionWorkflowResult> decompositions,
        int repairCycle,
        CodeGenerationBuildResult? previousBuild = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(taskResults);
        ArgumentNullException.ThrowIfNull(decompositions);

        var errors = build.Diagnostics
            .Where(item => item.Severity.Equals(
                "Error",
                StringComparison.OrdinalIgnoreCase))
            .Where(item => !string.IsNullOrWhiteSpace(item.FilePath))
            .ToArray();
        var assignments = new Dictionary<string, RepairAssignment>(
            StringComparer.Ordinal);
        var previousErrors = previousBuild?.Diagnostics
            .Where(item => item.Severity.Equals(
                "Error",
                StringComparison.OrdinalIgnoreCase))
            .ToArray() ?? [];
        var buildArtifactDiagnostics = errors
            .Select(diagnostic => (
                Diagnostic: diagnostic,
                Path: ResolveBuildArtifactPath(plan, diagnostic.FilePath!)))
            .Where(item => item.Path is not null)
            .Select(item => (item.Diagnostic, Path: item.Path!))
            .ToList();

        foreach (var diagnostic in errors)
        {
            if (ResolveBuildArtifactPath(plan, diagnostic.FilePath!) is not null)
                continue;

            var persistent = previousErrors.Any(previous =>
                SameDiagnostic(previous, diagnostic));
            if (persistent &&
                IsReferenceResolutionDiagnostic(diagnostic) &&
                ResolveDiagnosticProjectPath(plan, diagnostic) is { } projectPath)
            {
                if (!WasBuildArtifactRepairAttempted(taskResults, projectPath))
                    buildArtifactDiagnostics.Add((diagnostic, projectPath));
                continue;
            }

            var owner = taskResults
                .Reverse()
                .FirstOrDefault(result => result.WrittenFiles.Any(path =>
                    MatchesPath(path, diagnostic.FilePath!)));
            if (owner is null)
                continue;
            if (persistent && owner.IsBuildRepair)
                continue;

            var decomposition = decompositions.Single(item =>
                item.Decomposition?.LeafTasks.Any(leaf => leaf.Id.Equals(
                    owner.TaskId,
                    StringComparison.Ordinal)) == true);
            var leaf = decomposition.Decomposition!.LeafTasks.Single(item =>
                item.Id.Equals(owner.TaskId, StringComparison.Ordinal));
            var artifact = leaf.Artifacts.FirstOrDefault(item =>
                MatchesPath(item.Path, diagnostic.FilePath!));
            if (artifact is null)
                continue;

            if (!assignments.TryGetValue(owner.TaskId, out var assignment))
            {
                assignment = new RepairAssignment(
                    decomposition.ParentTaskId,
                    leaf,
                    owner);
                assignments.Add(owner.TaskId, assignment);
            }

            assignment.Artifacts.TryAdd(
                Normalize(artifact.Path),
                artifact);
            assignment.Diagnostics.Add(diagnostic);
        }

        var sourceRepairs = assignments.Values
            .Select(assignment => CreateRequest(
                plan,
                build,
                assignment,
                repairCycle))
            .ToList();
        var buildArtifactRepair = CreateBuildArtifactRequest(
            plan,
            build,
            buildArtifactDiagnostics,
            repairCycle);
        if (buildArtifactRepair is not null)
            sourceRepairs.Add(buildArtifactRepair);
        return sourceRepairs;
    }

    private static CodeGenerationTaskWorkflowRequest?
        CreateBuildArtifactRequest(
            CodeGenerationPlan plan,
            CodeGenerationBuildResult build,
            IReadOnlyList<(CodeGenerationBuildDiagnostic Diagnostic,
                string Path)> diagnostics,
            int repairCycle)
    {
        if (diagnostics.Count == 0)
            return null;

        var parent = FindRepairParent(plan, diagnostics.Select(item =>
            item.Path));
        if (parent is null)
            return null;

        var paths = diagnostics
            .Select(item => item.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var artifacts = paths.Select(path =>
        {
            var project = plan.Projects.FirstOrDefault(item =>
                MatchesPath(item.Path, path));
            var requirements = project is null
                ? new[]
                {
                    $"Keep the solution at {plan.Solution.Path} loadable by the .NET SDK.",
                    $"Include exactly the planned projects: {string.Join(", ", plan.Projects.Select(item => item.Path))}.",
                    "Preserve valid solution project references and do not add source files."
                }
                : new[]
                {
                    $"Use target framework {project.TargetFramework}.",
                    $"Preserve project kind {project.Kind} and project name {project.Name}.",
                    $"Reference the planned projects: {FormatList(project.ProjectDependencies)}.",
                    $"Reference the planned packages: {FormatPackages(project.Packages)}.",
                    "Keep the project valid SDK-style XML and do not add unplanned dependencies."
                };
            return new DecomposedArtifactPlan
            {
                Path = path,
                Kind = project is null ? "DotNetSolution" : "DotNetProject",
                Namespace = string.Empty,
                TypeNames = [],
                Requirements = requirements.ToList()
            };
        }).ToArray();
        var leaf = new CodeGenerationLeafTask
        {
            Id = "BUILD-ARTIFACTS-REPAIR",
            Title = "Repair .NET solution and project artifacts",
            Objective =
                $"Repair build metadata errors in {string.Join(", ", paths)} without changing source code or approved architecture.",
            ComplexityPoints = 1,
            DependsOn = [],
            ContractIds = parent.ContractIds,
            AcceptanceCriterionIds = parent.AcceptanceCriterionIds,
            DecisionIds = parent.DecisionIds,
            ImplementationRequirements =
            [
                "Correct only the supplied MSBuild or solution diagnostics.",
                "Emit every repaired artifact as complete final content.",
                "Do not modify C# source, architecture contracts, or observable behavior.",
                "Do not introduce projects, packages, target frameworks, or references absent from the approved plan."
            ],
            Artifacts = artifacts.ToList(),
            VerificationKinds = ["Compilation"]
        };
        var correction = new CodeGenerationBuildCorrection(
            repairCycle,
            "dotnet-scaffolding",
            "CompilationFailed",
            build.Error,
            diagnostics.Select(item => FormatDiagnostic(item.Diagnostic))
                .ToArray(),
            paths);
        return new CodeGenerationTaskWorkflowRequest(
            plan,
            parent.Id,
            leaf,
            correction,
            CodeGenerationWorkflowConstants.MaximumModelTiers,
            IsBuildRepair: true,
            BuildRepairCycle: repairCycle);
    }

    private static GenerationTaskPlan? FindRepairParent(
        CodeGenerationPlan plan,
        IEnumerable<string> paths)
    {
        var projectNames = paths
            .Select(path => plan.Projects.FirstOrDefault(project =>
                MatchesPath(project.Path, path))?.Name)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        var moduleIds = plan.Modules
            .Where(module => projectNames.Contains(module.ProjectName))
            .Select(module => module.Id)
            .ToHashSet(StringComparer.Ordinal);
        return plan.Tasks.FirstOrDefault(task =>
                task.ExecutionKind == PlanTaskExecutionKind.CodeGeneration &&
                task.ModuleId is not null &&
                moduleIds.Contains(task.ModuleId)) ??
            plan.Tasks.FirstOrDefault(task =>
                task.ExecutionKind == PlanTaskExecutionKind.CodeGeneration);
    }

    private static string? ResolveBuildArtifactPath(
        CodeGenerationPlan plan,
        string diagnosticPath) =>
        plan.Projects.Select(item => item.Path)
            .Append(plan.Solution.Path)
            .FirstOrDefault(path => MatchesPath(path, diagnosticPath));

    private static string? ResolveDiagnosticProjectPath(
        CodeGenerationPlan plan,
        CodeGenerationBuildDiagnostic diagnostic)
    {
        if (!string.IsNullOrWhiteSpace(diagnostic.ProjectPath))
        {
            var reportedProject = plan.Projects.FirstOrDefault(project =>
                MatchesPath(project.Path, diagnostic.ProjectPath));
            if (reportedProject is not null)
                return reportedProject.Path;
        }

        var sourcePath = Normalize(diagnostic.FilePath ?? string.Empty);
        return plan.Projects
            .Select(project => new
            {
                project.Path,
                Directory = ProjectDirectory(project.Path)
            })
            .Where(item => sourcePath.StartsWith(
                $"{item.Directory}/",
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Directory.Length)
            .Select(item => item.Path)
            .FirstOrDefault();
    }

    private static bool WasBuildArtifactRepairAttempted(
        IReadOnlyList<CodeGenerationTaskWorkflowResult> taskResults,
        string path) =>
        taskResults.Any(result =>
            result.IsBuildRepair &&
            result.TaskId.Equals(
                "BUILD-ARTIFACTS-REPAIR",
                StringComparison.Ordinal) &&
            result.WrittenFiles.Any(written => MatchesPath(written, path)));

    private static bool IsReferenceResolutionDiagnostic(
        CodeGenerationBuildDiagnostic diagnostic) =>
        diagnostic.Code is "CS0012" or "CS0234" or "CS0246" or "CS1069";

    private static bool SameDiagnostic(
        CodeGenerationBuildDiagnostic left,
        CodeGenerationBuildDiagnostic right) =>
        left.Code.Equals(right.Code, StringComparison.OrdinalIgnoreCase) &&
        Normalize(left.FilePath ?? string.Empty).Equals(
            Normalize(right.FilePath ?? string.Empty),
            StringComparison.OrdinalIgnoreCase) &&
        Normalize(left.ProjectPath ?? string.Empty).Equals(
            Normalize(right.ProjectPath ?? string.Empty),
            StringComparison.OrdinalIgnoreCase) &&
        left.Message.Equals(right.Message, StringComparison.Ordinal);

    private static string ProjectDirectory(string projectPath)
    {
        var normalized = Normalize(projectPath);
        var separator = normalized.LastIndexOf('/');
        return separator < 0 ? string.Empty : normalized[..separator];
    }

    private static string FormatList(IReadOnlyList<string> values) =>
        values.Count == 0 ? "none" : string.Join(", ", values);

    private static string FormatPackages(
        IReadOnlyList<PackageRequirement> packages) =>
        packages.Count == 0
            ? "none"
            : string.Join(", ", packages.Select(package =>
                $"{package.Name} {package.Version}"));

    private static CodeGenerationTaskWorkflowRequest CreateRequest(
        CodeGenerationPlan plan,
        CodeGenerationBuildResult build,
        RepairAssignment assignment,
        int repairCycle)
    {
        var source = assignment.Leaf;
        var paths = assignment.Artifacts.Keys.ToArray();
        var leaf = new CodeGenerationLeafTask
        {
            Id = source.Id,
            Title = source.Title,
            Objective =
                $"Repair compiler errors in {string.Join(", ", paths)} without changing the approved architecture.",
            ComplexityPoints = source.ComplexityPoints,
            DependsOn = source.DependsOn,
            ContractIds = source.ContractIds,
            AcceptanceCriterionIds = source.AcceptanceCriterionIds,
            DecisionIds = source.DecisionIds,
            ImplementationRequirements = source.ImplementationRequirements
                .Concat([
                    "Correct the supplied compiler diagnostics.",
                    "Preserve the approved public contracts and observable behavior.",
                    "Emit only the compiler-failing artifacts listed for this repair."
                ])
                .Distinct(StringComparer.Ordinal)
                .ToList(),
            Artifacts = assignment.Artifacts.Values.ToList(),
            VerificationKinds = source.VerificationKinds
        };
        var previousTier = Math.Clamp(
            assignment.Owner.ModelTier,
            1,
            CodeGenerationWorkflowConstants.MaximumModelTiers);
        var startingTier = Math.Min(
            previousTier + 1,
            CodeGenerationWorkflowConstants.MaximumModelTiers);
        var correction = new CodeGenerationBuildCorrection(
            repairCycle,
            assignment.Owner.Model,
            "CompilationFailed",
            build.Error,
            assignment.Diagnostics.Select(FormatDiagnostic).ToArray(),
            paths);

        return new CodeGenerationTaskWorkflowRequest(
            plan,
            assignment.ParentTaskId,
            leaf,
            correction,
            startingTier,
            IsBuildRepair: true,
            BuildRepairCycle: repairCycle);
    }

    private static string FormatDiagnostic(
        CodeGenerationBuildDiagnostic diagnostic)
    {
        var location = diagnostic.Line is null
            ? diagnostic.FilePath
            : $"{diagnostic.FilePath}({diagnostic.Line},{diagnostic.Column ?? 0})";
        return $"{location}: {diagnostic.Code}: {diagnostic.Message}";
    }

    private static bool MatchesPath(string expected, string actual)
    {
        var normalizedExpected = Normalize(expected);
        var normalizedActual = Normalize(actual);
        return normalizedActual.Equals(
                normalizedExpected,
                StringComparison.OrdinalIgnoreCase) ||
            normalizedActual.EndsWith(
                $"/{normalizedExpected}",
                StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) =>
        path.Replace('\\', '/').TrimStart('.', '/');

    private sealed class RepairAssignment(
        string parentTaskId,
        CodeGenerationLeafTask leaf,
        CodeGenerationTaskWorkflowResult owner)
    {
        public string ParentTaskId { get; } = parentTaskId;

        public CodeGenerationLeafTask Leaf { get; } = leaf;

        public CodeGenerationTaskWorkflowResult Owner { get; } = owner;

        public Dictionary<string, DecomposedArtifactPlan> Artifacts { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public List<CodeGenerationBuildDiagnostic> Diagnostics { get; } = [];
    }
}
