using Microsoft.Extensions.Options;
using Guyabano.CI.Contracts;
using System.Text.RegularExpressions;

namespace Guyabano.CI.Server.Services;

public sealed partial class DotNetDiagnosticParser
{
    private readonly string generatedRoot;
    private readonly StringComparison pathComparison;

    public DotNetDiagnosticParser(IOptions<CiServerOptions> options)
    {
        generatedRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(options.Value.GeneratedRoot));
        pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }

    public bool TryParse(string line, out CiDiagnostic? diagnostic)
    {
        diagnostic = null;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var marker = DiagnosticMarkerRegex().Match(line);

        if (!marker.Success)
        {
            return false;
        }

        var prefix = line[..marker.Index].TrimEnd();
        var separatorIndex = prefix.LastIndexOf(':');

        if (separatorIndex < 0)
        {
            return false;
        }

        var origin = prefix[..separatorIndex].Trim();
        var message = line[(marker.Index + marker.Length)..].Trim();
        var projectPath = ExtractProjectPath(ref message);
        var location = SourceLocationRegex().Match(origin);
        var sourcePath = location.Success
            ? location.Groups["file"].Value
            : origin;
        var filePath = NormalizeGeneratedPath(sourcePath);
        var normalizedProjectPath = NormalizeGeneratedPath(projectPath);

        if (filePath is null && normalizedProjectPath is not null)
        {
            filePath = normalizedProjectPath;
        }

        diagnostic = new CiDiagnostic(
            Tool: "dotnet",
            Code: marker.Groups["code"].Value,
            Severity: marker.Groups["severity"].Value.Equals(
                "error",
                StringComparison.OrdinalIgnoreCase)
                ? CiDiagnosticSeverity.Error
                : CiDiagnosticSeverity.Warning,
            Message: message,
            FilePath: filePath,
            ProjectPath: normalizedProjectPath,
            Line: ParsePosition(location, "line"),
            Column: ParsePosition(location, "column"));

        return true;
    }

    private string? NormalizeGeneratedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            path.Equals("CSC", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("MSBuild", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var trimmed = path.Trim();

        if (!Path.IsPathRooted(trimmed))
        {
            return trimmed
                .Replace('\\', '/')
                .TrimStart('/');
        }

        var fullPath = Path.GetFullPath(trimmed);

        if (string.Equals(fullPath, generatedRoot, pathComparison))
        {
            return ".";
        }

        var rootWithSeparator = generatedRoot +
            Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootWithSeparator, pathComparison))
        {
            return null;
        }

        return Path.GetRelativePath(generatedRoot, fullPath)
            .Replace('\\', '/');
    }

    private static string? ExtractProjectPath(ref string message)
    {
        if (!message.EndsWith(']'))
        {
            return null;
        }

        var openingBracket = message.LastIndexOf(
            " [",
            StringComparison.Ordinal);

        if (openingBracket < 0)
        {
            return null;
        }

        var projectPath = message[(openingBracket + 2)..^1];
        message = message[..openingBracket].TrimEnd();
        return projectPath;
    }

    private static int? ParsePosition(Match location, string groupName) =>
        location.Success &&
        int.TryParse(location.Groups[groupName].Value, out var value)
            ? value
            : null;

    [GeneratedRegex(
        @"\b(?<severity>error|warning)\s+(?<code>[A-Za-z]+\d+)\s*:\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DiagnosticMarkerRegex();

    [GeneratedRegex(
        @"^(?<file>.+)\((?<line>\d+),(?<column>\d+)(?:,\d+,\d+)?\)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SourceLocationRegex();
}
