namespace Guyabano.CI.Server.Services;

public sealed class SafePathResolver
{
    private readonly string rootDirectory;
    private readonly StringComparison pathComparison;

    public SafePathResolver(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        this.rootDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(rootDirectory));
        pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }

    public string Resolve(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException(
                $"Path must be relative: {relativePath}");
        }

        if (relativePath.Trim() == ".")
        {
            return rootDirectory;
        }

        var segments = relativePath
            .Replace('\\', '/')
            .Split(
                '/',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries)
            .Where(segment => segment != ".")
            .ToArray();

        if (segments.Any(segment => segment == ".."))
        {
            throw new InvalidOperationException(
                $"Path contains an unsafe segment: {relativePath}");
        }

        if (segments.Length == 0)
            return rootDirectory;

        var fullPath = Path.GetFullPath(
            Path.Combine(rootDirectory, Path.Combine(segments)));
        var rootWithSeparator = rootDirectory +
            Path.DirectorySeparatorChar;

        if (!string.Equals(
                fullPath,
                rootDirectory,
                pathComparison) &&
            !fullPath.StartsWith(rootWithSeparator, pathComparison))
        {
            throw new InvalidOperationException(
                $"Path escapes the generated root: {relativePath}");
        }

        return fullPath;
    }
}
