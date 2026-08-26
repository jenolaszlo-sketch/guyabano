namespace Guyabano.Llm.CodeGeneration;

internal static class GeneratedFileScopeValidator
{
    private static readonly HashSet<string> ForbiddenFileNames = new(
        [
            "Directory.Build.props",
            "Directory.Build.targets",
            "Directory.Packages.props"
        ],
        StringComparer.OrdinalIgnoreCase);

    public static string? Validate(
        CodeGenerationResult result,
        string projectDirectory,
        string projectPath,
        string solutionPath,
        IReadOnlyCollection<string>? allowedBuildArtifactPaths = null)
    {
        var allowedPrefix = Normalize(projectDirectory).TrimEnd('/') + "/";
        var normalizedProjectPath = Normalize(projectPath);
        var normalizedSolutionPath = Normalize(solutionPath);
        var allowedBuildArtifacts = (allowedBuildArtifactPaths ?? [])
            .Select(Normalize)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var invalidPaths = new List<string>();
        var exactBuildArtifactScope = allowedBuildArtifacts.Count > 0;

        foreach (var file in result.Files)
        {
            var path = Normalize(file.Path);
            var extension = Path.GetExtension(path);
            var isAllowedBuildArtifact =
                allowedBuildArtifacts.Contains(path) &&
                (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
                 extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
                 extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase));
            var forbidden = string.IsNullOrWhiteSpace(path) ||
                exactBuildArtifactScope && !isAllowedBuildArtifact ||
                !exactBuildArtifactScope && !isAllowedBuildArtifact &&
                (!path.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase) ||
                 path.Equals(normalizedProjectPath, StringComparison.OrdinalIgnoreCase) ||
                 path.Equals(normalizedSolutionPath, StringComparison.OrdinalIgnoreCase) ||
                 extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
                 extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
                 extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)) ||
                ForbiddenFileNames.Contains(Path.GetFileName(path)) ||
                !seen.Add(path);

            if (forbidden)
                invalidPaths.Add(file.Path);
        }

        if (exactBuildArtifactScope)
        {
            invalidPaths.AddRange(allowedBuildArtifacts
                .Where(path => !seen.Contains(path))
                .Select(path => $"{path} (missing)"));
        }

        return invalidPaths.Count == 0
            ? null
            : $"Task output contains duplicate, protected, or out-of-scope paths: {string.Join(", ", invalidPaths)}.";
    }

    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            Path.IsPathRooted(path) ||
            path.StartsWith('/') ||
            path.StartsWith('\\'))
            return string.Empty;

        var segments = path
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries);

        return segments.Length == 0 ||
               segments.Any(segment => segment is "." or "..")
            ? string.Empty
            : string.Join('/', segments);
    }
}
