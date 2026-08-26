namespace Guyabano.CI.Server.Services;

public sealed class ProjectTargetResolver
{
    public string Resolve(
        string workingDirectory,
        string? projectOrSolutionFile)
    {
        if (!string.IsNullOrWhiteSpace(projectOrSolutionFile))
        {
            return ResolveExplicitTarget(
                workingDirectory,
                projectOrSolutionFile);
        }

        var target = Directory
            .EnumerateFiles(
                workingDirectory,
                "*.*",
                SearchOption.TopDirectoryOnly)
            .Where(IsSupportedTarget)
            .OrderBy(GetTargetPriority)
            .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(Path.GetFileName)
            .FirstOrDefault();

        return target ?? throw new InvalidOperationException(
            $"No .sln, .slnx, or .csproj file exists in {workingDirectory}.");
    }

    private static string ResolveExplicitTarget(
        string workingDirectory,
        string relativeTarget)
    {
        if (Path.IsPathRooted(relativeTarget))
        {
            throw new InvalidOperationException(
                "ProjectOrSolutionFile must be relative.");
        }

        var segments = relativeTarget
            .Replace('\\', '/')
            .Split(
                '/',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries)
            .Where(segment => segment != ".")
            .ToArray();

        if (segments.Length == 0 ||
            segments.Any(segment => segment == ".."))
        {
            throw new InvalidOperationException(
                "ProjectOrSolutionFile contains an unsafe segment.");
        }

        var fullWorkingDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(workingDirectory));
        var fullTarget = Path.GetFullPath(
            Path.Combine(fullWorkingDirectory, Path.Combine(segments)));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!fullTarget.StartsWith(
                fullWorkingDirectory + Path.DirectorySeparatorChar,
                comparison) ||
            !File.Exists(fullTarget))
        {
            throw new InvalidOperationException(
                $"Target does not exist inside the working directory: {relativeTarget}");
        }

        if (!IsSupportedTarget(fullTarget))
        {
            throw new InvalidOperationException(
                "Target must be a .sln, .slnx, or .csproj file.");
        }

        return Path.GetRelativePath(fullWorkingDirectory, fullTarget);
    }

    private static bool IsSupportedTarget(string path) =>
        path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);

    private static int GetTargetPriority(string path)
    {
        if (path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
            ? 1
            : 2;
    }
}
