using Microsoft.Extensions.Options;
using Guyabano.CI.Contracts;
using System.Runtime.CompilerServices;

namespace Guyabano.CI.Server.Services;

public sealed class DotNetScaffoldingStreamingService(
    IOptions<CiServerOptions> options,
    SafePathResolver safePathResolver,
    ProcessRunner processRunner)
    : ProcessStreamingServiceBase<CiScaffoldRequest>(
        safePathResolver,
        processRunner)
{
    private static readonly IReadOnlyDictionary<string, string> Templates =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["WebApi"] = "web",
            ["Library"] = "classlib",
            ["Contracts"] = "classlib",
            ["UnitTests"] = "xunit",
            ["IntegrationTests"] = "xunit",
            ["Console"] = "console",
            ["Worker"] = "worker"
        };

    protected override string ToolName => "dotnet scaffolding";

    protected override void ValidateRequest(CiScaffoldRequest request)
    {
        base.ValidateRequest(request);
        ArgumentNullException.ThrowIfNull(request.Solution);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Solution.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Solution.Path);

        if (request.Projects.Count == 0)
            throw new ArgumentException(
                "At least one project is required.",
                nameof(request));

        var projectNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in request.Projects)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(project.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(project.Path);
            ArgumentException.ThrowIfNullOrWhiteSpace(project.Kind);
            ArgumentException.ThrowIfNullOrWhiteSpace(
                project.TargetFramework);

            if (!projectNames.Add(project.Name))
                throw new ArgumentException(
                    $"Duplicate project name '{project.Name}'.",
                    nameof(request));
            if (!Templates.ContainsKey(project.Kind))
                throw new ArgumentException(
                    $"Unsupported project kind '{project.Kind}'.",
                    nameof(request));
        }

        foreach (var project in request.Projects)
        {
            foreach (var dependency in project.ProjectDependencies)
            {
                if (!projectNames.Contains(dependency))
                    throw new ArgumentException(
                        $"Project '{project.Name}' references unknown project '{dependency}'.",
                        nameof(request));
                if (dependency.Equals(project.Name, StringComparison.Ordinal))
                    throw new ArgumentException(
                        $"Project '{project.Name}' references itself.",
                        nameof(request));
            }

            foreach (var package in project.Packages)
                ArgumentException.ThrowIfNullOrWhiteSpace(package.Name);
        }
    }

    protected override async IAsyncEnumerable<CiStreamEvent> ExecuteCoreAsync(
        CiScaffoldRequest request,
        string workingDirectory,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IReadOnlyList<DotNetOperation>? operations = null;
        IReadOnlyList<string>? artifacts = null;
        string? validationError = null;

        try
        {
            (operations, artifacts) = CreateOperations(
                request,
                workingDirectory);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException)
        {
            validationError = exception.Message;
        }

        if (validationError is not null ||
            operations is null ||
            artifacts is null)
        {
            yield return CiEvents.Error(
                "scaffold-validate",
                validationError ?? "Unable to create scaffolding operations.");
            yield break;
        }

        var completedOperations = 0;
        var removedFiles = new List<string>();
        var cleanupPathResolver = new SafePathResolver(workingDirectory);
        foreach (var operation in operations)
        {
            int? exitCode = null;

            await foreach (var streamEvent in RunProcessStreamingAsync(
                operation.Phase,
                options.Value.DotNetCommand,
                operation.Arguments,
                workingDirectory,
                cancellationToken))
            {
                yield return streamEvent;

                if (streamEvent.Type == "process_result")
                    exitCode = streamEvent.ExitCode;
            }

            if (exitCode != 0)
            {
                yield return CiEvents.Result(
                    "scaffold-result",
                    new CiScaffoldResult(
                        ExistingArtifacts(workingDirectory, artifacts),
                        removedFiles,
                        completedOperations),
                    success: false,
                    exitCode);
                yield break;
            }

            string? cleanupError = null;
            foreach (var cleanupPath in operation.CleanupPaths)
            {
                try
                {
                    var fullPath = cleanupPathResolver.Resolve(cleanupPath);
                    if (!File.Exists(fullPath))
                        continue;

                    File.Delete(fullPath);
                    removedFiles.Add(cleanupPath);
                }
                catch (Exception exception) when (
                    exception is not OperationCanceledException)
                {
                    cleanupError = exception.Message;
                    break;
                }
            }

            if (cleanupError is not null)
            {
                yield return CiEvents.Error(
                    "scaffold-cleanup",
                    cleanupError);
                yield return CiEvents.Result(
                    "scaffold-result",
                    new CiScaffoldResult(
                        ExistingArtifacts(workingDirectory, artifacts),
                        removedFiles,
                        completedOperations),
                    success: false);
                yield break;
            }

            completedOperations++;
        }

        yield return CiEvents.Result(
            "scaffold-result",
            new CiScaffoldResult(
                ExistingArtifacts(workingDirectory, artifacts),
                removedFiles,
                completedOperations),
            success: true,
            exitCode: 0);
    }

    private static (
        IReadOnlyList<DotNetOperation> Operations,
        IReadOnlyList<string> Artifacts) CreateOperations(
        CiScaffoldRequest request,
        string workingDirectory)
    {
        var pathResolver = new SafePathResolver(workingDirectory);
        var solutionPath = ResolveRelativePath(
            pathResolver,
            workingDirectory,
            request.Solution.Path);
        var solutionExtension = Path.GetExtension(solutionPath);

        if (solutionExtension is not ".sln" and not ".slnx")
            throw new InvalidOperationException(
                "Solution path must end in .sln or .slnx.");
        if (!Path.GetFileNameWithoutExtension(solutionPath).Equals(
                request.Solution.Name,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Solution name must match its file name.");

        var projectsByName = request.Projects.ToDictionary(
            project => project.Name,
            StringComparer.Ordinal);
        var projectPaths = request.Projects.ToDictionary(
            project => project.Name,
            project => ResolveRelativePath(
                pathResolver,
                workingDirectory,
                project.Path),
            StringComparer.Ordinal);

        foreach (var project in request.Projects)
        {
            if (!Path.GetExtension(projectPaths[project.Name]).Equals(
                    ".csproj",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Project path for '{project.Name}' must end in .csproj.");
            if (!Path.GetFileNameWithoutExtension(projectPaths[project.Name])
                    .Equals(project.Name, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Project name '{project.Name}' must match its file name.");
        }

        var operations = new List<DotNetOperation>();
        var solutionOutput = NormalizeDirectory(
            Path.GetDirectoryName(solutionPath));
        operations.Add(new DotNetOperation(
            "scaffold-solution",
            [
                "new", "sln",
                "--name", request.Solution.Name,
                "--output", solutionOutput,
                "--format", solutionExtension[1..],
                "--force"
            ],
            []));

        foreach (var project in request.Projects)
        {
            var projectPath = projectPaths[project.Name];
            operations.Add(new DotNetOperation(
                $"scaffold-project-{project.Name}",
                [
                    "new", Templates[project.Kind],
                    "--name", project.Name,
                    "--output", NormalizeDirectory(
                        Path.GetDirectoryName(projectPath)),
                    "--framework", project.TargetFramework,
                    "--no-restore",
                    "--force"
                ],
                GetBoilerplateFiles(project.Kind, projectPath)));
        }

        foreach (var projectPath in projectPaths.Values)
        {
            operations.Add(new DotNetOperation(
                $"scaffold-solution-add-{Path.GetFileNameWithoutExtension(projectPath)}",
                ["sln", solutionPath, "add", projectPath],
                []));
        }

        foreach (var project in request.Projects)
        {
            foreach (var dependency in project.ProjectDependencies)
            {
                operations.Add(new DotNetOperation(
                    $"scaffold-reference-{project.Name}-{dependency}",
                    [
                        "add", projectPaths[project.Name],
                        "reference", projectPaths[projectsByName[dependency].Name]
                    ],
                    []));
            }

            foreach (var package in project.Packages)
            {
                var arguments = new List<string>
                {
                    "add", projectPaths[project.Name],
                    "package", package.Name
                };
                if (!string.IsNullOrWhiteSpace(package.Version))
                {
                    arguments.Add("--version");
                    arguments.Add(package.Version);
                    arguments.Add("--no-restore");
                }

                operations.Add(new DotNetOperation(
                    $"scaffold-package-{project.Name}-{package.Name}",
                    arguments,
                    []));
            }
        }

        return (
            operations,
            [solutionPath, .. projectPaths.Values]);
    }

    private static string ResolveRelativePath(
        SafePathResolver resolver,
        string workingDirectory,
        string path) =>
        Path.GetRelativePath(workingDirectory, resolver.Resolve(path))
            .Replace('\\', '/');

    private static string NormalizeDirectory(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? "."
            : path.Replace('\\', '/');

    private static IReadOnlyList<string> ExistingArtifacts(
        string workingDirectory,
        IEnumerable<string> artifacts) =>
        artifacts
            .Where(artifact => File.Exists(Path.Combine(
                workingDirectory,
                artifact.Replace('/', Path.DirectorySeparatorChar))))
            .ToArray();

    private static IReadOnlyList<string> GetBoilerplateFiles(
        string projectKind,
        string projectPath)
    {
        var projectDirectory = NormalizeDirectory(
            Path.GetDirectoryName(projectPath));
        var fileName = Templates[projectKind] switch
        {
            "classlib" => "Class1.cs",
            "xunit" => "UnitTest1.cs",
            _ => null
        };

        return fileName is null
            ? []
            : [$"{projectDirectory}/{fileName}".TrimStart('.', '/')];
    }

    private sealed record DotNetOperation(
        string Phase,
        IReadOnlyList<string> Arguments,
        IReadOnlyList<string> CleanupPaths);
}
