using Guyabano.CodeGeneration.Planning;
using Guyabano.Llm.Prompting;

namespace Guyabano.WorkflowWorker;

internal static class CodeGenerationTaskContextFactory
{
    private const int MaximumFiles = 80;
    private const int MaximumCharacters = 120_000;
    private const int MaximumLeafFiles = 16;
    private const int MaximumLeafCharacters = 40_000;
    private static readonly HashSet<string> IncludedExtensions = new(
        [
            ".cs", ".csproj", ".json", ".xml", ".config",
            ".props", ".targets", ".proto", ".razor", ".cshtml"
        ],
        StringComparer.OrdinalIgnoreCase);

    public static async Task<CodeGenerationTaskContext> CreateAsync(
        CodeGenerationPlan plan,
        string taskId,
        string originalRequest,
        string outputRoot,
        CancellationToken cancellationToken)
    {
        var task = plan.Tasks.Single(item => item.Id.Equals(
            taskId,
            StringComparison.Ordinal));
        if (task.ExecutionKind != PlanTaskExecutionKind.CodeGeneration)
            throw new InvalidOperationException(
                $"Task '{task.Id}' is not a code-generation task.");

        var module = plan.Modules.Single(item => item.Id.Equals(
            task.ModuleId,
            StringComparison.Ordinal));
        var project = plan.Projects.Single(item => item.Name.Equals(
            module.ProjectName,
            StringComparison.Ordinal));
        var projectDirectory = Normalize(
            Path.GetDirectoryName(project.Path) ?? string.Empty);
        if (string.IsNullOrWhiteSpace(projectDirectory))
            throw new InvalidOperationException(
                $"Project '{project.Name}' must be in a project directory.");

        var contracts = task.ContractIds
            .Select(id => plan.Contracts.Single(item => item.Id.Equals(
                id,
                StringComparison.Ordinal)))
            .Select(contract => new CodeGenerationTaskContractContext(
                contract.Id,
                contract.Name,
                contract.Kind,
                contract.Purpose,
                contract.Members))
            .ToArray();
        var criteria = task.AcceptanceCriterionIds
            .Select(id => plan.AcceptanceCriteria.Single(item =>
                item.Id.Equals(id, StringComparison.Ordinal)))
            .Select(criterion => new CodeGenerationTaskAcceptanceContext(
                criterion.Id,
                criterion.Feature,
                criterion.Scenario,
                criterion.Given,
                criterion.When,
                criterion.Then))
            .ToArray();
        var decisions = task.DecisionIds
            .Select(id => plan.Decisions.Single(item => item.Id.Equals(
                id,
                StringComparison.Ordinal)))
            .Select(decision => new CodeGenerationTaskDecisionContext(
                decision.Id,
                decision.Title,
                decision.Decision,
                decision.Reasons))
            .ToArray();
        var files = await LoadRelevantFilesAsync(
            plan,
            project,
            outputRoot,
            cancellationToken);

        return new CodeGenerationTaskContext(
            originalRequest,
            task.Id,
            task.Title,
            task.Objective,
            plan.Solution.Name,
            plan.Solution.Path,
            project.Name,
            Normalize(project.Path),
            projectDirectory,
            project.Name,
            project.TargetFramework,
            module.Name,
            module.Responsibilities,
            task.Deliverables,
            contracts,
            criteria,
            decisions,
            files,
            ArchitectureNotes: GetArchitectureNotes(
                plan,
                task,
                task.ContractIds,
                task.DecisionIds,
                task.AcceptanceCriterionIds,
                module.Id,
                project.Name));
    }

    public static async Task<CodeGenerationTaskContext> CreateAsync(
        CodeGenerationPlan plan,
        string parentTaskId,
        CodeGenerationLeafTask leaf,
        string outputRoot,
        CancellationToken cancellationToken)
    {
        var parent = plan.Tasks.Single(item => item.Id.Equals(
            parentTaskId,
            StringComparison.Ordinal));
        if (parent.ExecutionKind != PlanTaskExecutionKind.CodeGeneration)
            throw new InvalidOperationException(
                $"Task '{parent.Id}' is not a code-generation task.");

        var module = plan.Modules.Single(item => item.Id.Equals(
            parent.ModuleId,
            StringComparison.Ordinal));
        var project = plan.Projects.Single(item => item.Name.Equals(
            module.ProjectName,
            StringComparison.Ordinal));
        var projectDirectory = Normalize(
            Path.GetDirectoryName(project.Path) ?? string.Empty);
        var contracts = leaf.ContractIds
            .Select(id => plan.Contracts.Single(item => item.Id.Equals(
                id,
                StringComparison.Ordinal)))
            .Select(contract => new CodeGenerationTaskContractContext(
                contract.Id,
                contract.Name,
                contract.Kind,
                contract.Purpose,
                contract.Members))
            .ToArray();
        var criteria = leaf.AcceptanceCriterionIds
            .Select(id => plan.AcceptanceCriteria.Single(item =>
                item.Id.Equals(id, StringComparison.Ordinal)))
            .Select(criterion => new CodeGenerationTaskAcceptanceContext(
                criterion.Id,
                criterion.Feature,
                criterion.Scenario,
                criterion.Given,
                criterion.When,
                criterion.Then))
            .ToArray();
        var decisions = leaf.DecisionIds
            .Select(id => plan.Decisions.Single(item => item.Id.Equals(
                id,
                StringComparison.Ordinal)))
            .Select(decision => new CodeGenerationTaskDecisionContext(
                decision.Id,
                decision.Title,
                decision.Decision,
                decision.Reasons))
            .ToArray();
        var artifacts = leaf.Artifacts
            .Select(item => new CodeGenerationArtifactContext(
                Normalize(item.Path),
                item.Kind,
                item.Namespace,
                item.TypeNames,
                item.Requirements))
            .ToArray();
        var allowBuildArtifacts = artifacts.Length > 0 &&
            artifacts.All(artifact => IsBuildArtifact(artifact.Path));
        var files = await LoadRelevantFilesAsync(
            plan,
            project,
            outputRoot,
            cancellationToken,
            artifacts.Select(item => item.Path).ToArray(),
            contracts.Select(item => item.Name).ToArray(),
            MaximumLeafFiles,
            MaximumLeafCharacters);

        return new CodeGenerationTaskContext(
            OriginalRequest: string.Empty,
            TaskId: leaf.Id,
            TaskTitle: leaf.Title,
            Objective: leaf.Objective,
            SolutionName: plan.Solution.Name,
            SolutionPath: plan.Solution.Path,
            ProjectName: project.Name,
            ProjectPath: Normalize(project.Path),
            ProjectDirectory: projectDirectory,
            RootNamespace: project.Name,
            TargetFramework: project.TargetFramework,
            ModuleName: module.Name,
            ModuleResponsibilities: module.Responsibilities,
            Deliverables: artifacts.Select(item => item.Path).ToArray(),
            Contracts: contracts,
            AcceptanceCriteria: criteria,
            Decisions: decisions,
            Files: files,
            ParentTaskId: parent.Id,
            ImplementationRequirements: leaf.ImplementationRequirements,
            Artifacts: artifacts,
            ArchitectureNotes: GetArchitectureNotes(
                plan,
                parent,
                leaf.ContractIds,
                leaf.DecisionIds,
                leaf.AcceptanceCriterionIds,
                module.Id,
                project.Name),
            AllowBuildArtifacts: allowBuildArtifacts);
    }

    private static IReadOnlyList<CodeGenerationTaskArchitectureNoteContext>
        GetArchitectureNotes(
            CodeGenerationPlan plan,
            GenerationTaskPlan parentTask,
            IEnumerable<string> contractIds,
            IEnumerable<string> decisionIds,
            IEnumerable<string> acceptanceCriterionIds,
            string moduleId,
            string projectName)
    {
        var affectedIds = contractIds
            .Concat(decisionIds)
            .Concat(acceptanceCriterionIds)
            .Append(parentTask.Id)
            .Append(moduleId)
            .Append(projectName)
            .Append(plan.Solution.Name)
            .ToHashSet(StringComparer.Ordinal);

        return plan.ArchitectureNotes
            .Where(note => note.AffectedIds.Any(affectedIds.Contains))
            .Select(note => new CodeGenerationTaskArchitectureNoteContext(
                note.Id,
                note.Category.ToString(),
                note.Subject,
                note.Decision,
                note.Impact,
                note.Reasons))
            .ToArray();
    }

    private static async Task<IReadOnlyList<ProjectFileContext>>
        LoadRelevantFilesAsync(
        CodeGenerationPlan plan,
        PlannedProject project,
        string outputRoot,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? preferredPaths = null,
        IReadOnlyList<string>? contractNames = null,
        int maximumFiles = MaximumFiles,
        int maximumCharacters = MaximumCharacters)
    {
        var projectNames = GetProjectClosure(plan, project);
        var fullRoot = Path.GetFullPath(outputRoot);
        var explicitCandidates = (preferredPaths ?? [])
            .Select(path => ResolveWithinRoot(fullRoot, path))
            .Where(File.Exists);
        var projectCandidates = projectNames
            .Select(name => plan.Projects.Single(item => item.Name.Equals(
                name,
                StringComparison.Ordinal)))
            .Select(item => Path.GetDirectoryName(item.Path) ?? string.Empty)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => ResolveWithinRoot(fullRoot, path))
            .Where(Directory.Exists)
            .SelectMany(path => Directory.EnumerateFiles(
                path,
                "*",
                SearchOption.AllDirectories))
            .Where(path => !ContainsBuildDirectory(
                Path.GetRelativePath(fullRoot, path)))
            .Where(path => IncludedExtensions.Contains(
                Path.GetExtension(path)));
        var candidates = explicitCandidates
            .Concat(projectCandidates)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => GetFilePriority(
                Normalize(Path.GetRelativePath(fullRoot, path)),
                preferredPaths,
                contractNames))
            .ThenBy(path => Path.GetRelativePath(fullRoot, path),
                StringComparer.OrdinalIgnoreCase)
            .Take(maximumFiles)
            .ToArray();

        var files = new List<ProjectFileContext>();
        var characterCount = 0;
        foreach (var path in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = await File.ReadAllTextAsync(
                path,
                cancellationToken);
            if (characterCount + content.Length > maximumCharacters)
                break;

            characterCount += content.Length;
            files.Add(new ProjectFileContext(
                Normalize(Path.GetRelativePath(fullRoot, path)),
                content));
        }

        return files;
    }

    private static int GetFilePriority(
        string path,
        IReadOnlyList<string>? preferredPaths,
        IReadOnlyList<string>? contractNames)
    {
        if (preferredPaths?.Contains(
                path,
                StringComparer.OrdinalIgnoreCase) == true)
            return 0;
        var fileName = Path.GetFileNameWithoutExtension(path);
        if (contractNames?.Any(name => fileName.Equals(
                name,
                StringComparison.OrdinalIgnoreCase)) == true)
            return 1;
        if (Path.GetExtension(path).Equals(
                ".csproj",
                StringComparison.OrdinalIgnoreCase))
            return 2;
        return 3;
    }

    private static IReadOnlySet<string> GetProjectClosure(
        CodeGenerationPlan plan,
        PlannedProject project)
    {
        var result = new HashSet<string>(StringComparer.Ordinal)
        {
            project.Name
        };
        var queue = new Queue<string>(project.ProjectDependencies);

        while (queue.TryDequeue(out var name))
        {
            if (!result.Add(name))
                continue;

            var dependency = plan.Projects.Single(item => item.Name.Equals(
                name,
                StringComparison.Ordinal));
            foreach (var nested in dependency.ProjectDependencies)
                queue.Enqueue(nested);
        }

        return result;
    }

    private static string ResolveWithinRoot(
        string fullRoot,
        string relativePath)
    {
        var fullPath = Path.GetFullPath(relativePath, fullRoot);
        var pathFromRoot = Path.GetRelativePath(fullRoot, fullPath);
        if (Path.IsPathRooted(pathFromRoot) ||
            pathFromRoot.Equals("..", StringComparison.Ordinal) ||
            pathFromRoot.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Project directory escapes output root: {relativePath}");

        return fullPath;
    }

    private static bool ContainsBuildDirectory(string path) =>
        path.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("obj", StringComparison.OrdinalIgnoreCase));

    private static bool IsBuildArtifact(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) =>
        path.Replace('\\', '/').TrimStart('/');
}
