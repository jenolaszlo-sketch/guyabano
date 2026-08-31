using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Guyabano.WorkflowWorker;

internal static partial class FailureFingerprint
{
    public static string Create(string kind, string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        var normalized = Normalize(error);
        var bytes = SHA256.HashData(
            Encoding.UTF8.GetBytes($"v1\n{kind}\n{normalized}"));
        return $"sha256:{Convert.ToHexStringLower(bytes)}";
    }

    public static IReadOnlyList<string> Evidence(
        string kind,
        string? error)
    {
        var result = new List<string>
        {
            $"Failure fingerprint: {Create(kind, error)}"
        };
        var paths = JsonPathRegex().Matches(error ?? string.Empty)
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Take(16)
            .ToArray();
        if (paths.Length > 0)
            result.Add($"Affected JSON paths: {string.Join(", ", paths)}");
        return result;
    }

    private static string Normalize(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return "<none>";
        var value = QuotedValueRegex().Replace(error, "'<value>'");
        value = NumberRegex().Replace(value, "#");
        return WhitespaceRegex().Replace(value, " ").Trim();
    }

    [GeneratedRegex(@"\$(?:\.[A-Za-z_][A-Za-z0-9_-]*|\[\d+\])*")]
    private static partial Regex JsonPathRegex();

    [GeneratedRegex(@"'[^'\r\n]*'")]
    private static partial Regex QuotedValueRegex();

    [GeneratedRegex(@"\b\d+\b")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
