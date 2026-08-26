using System.Text;

namespace Guyabano.Llm.CodeGeneration;

internal sealed class FileSystemCodeEmitter : ICodeEmitter
{
    public async Task<CodeEmitResult> EmitAsync(
        CodeGenerationResult result,
        string outputRoot,
        CancellationToken cancellationToken = default)
    {
        if (result is null)
            throw new ArgumentNullException(nameof(result));

        if (string.IsNullOrWhiteSpace(outputRoot))
            throw new ArgumentException("Output root cannot be empty.", nameof(outputRoot));

        var writtenFiles = new List<string>();
        var skippedFiles = new List<string>();

        Directory.CreateDirectory(outputRoot);

        foreach (var file in result.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(file.Path))
            {
                skippedFiles.Add("<empty path>");
                continue;
            }

            if (file.Content is null)
            {
                skippedFiles.Add(file.Path);
                continue;
            }

            var outputPath = GetSafeOutputPath(outputRoot, file.Path);

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

            if (File.Exists(outputPath))
            {
                var existing = await File.ReadAllTextAsync(
                    outputPath,
                    cancellationToken);
                if (existing.Equals(file.Content, StringComparison.Ordinal))
                {
                    skippedFiles.Add(outputPath);
                    continue;
                }
            }

            await File.WriteAllTextAsync(
                outputPath,
                file.Content,
                cancellationToken);

            writtenFiles.Add(outputPath);
        }

        return new CodeEmitResult(writtenFiles, skippedFiles);
    }

    private static string GetSafeOutputPath(
        string outputRoot,
        string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            outputRoot);

        ArgumentException.ThrowIfNullOrWhiteSpace(
            relativePath);

        if (relativePath.Contains('\0'))
        {
            throw new InvalidOperationException(
                "Paths cannot contain null characters.");
        }

        /*
         * Reject genuine absolute paths before repairing malformed separators.
         * The explicit slash checks are useful when running on Linux, where
         * Windows-style paths may not be considered rooted by Path.IsPathRooted.
         */
        if (Path.IsPathRooted(relativePath) ||
            relativePath.StartsWith('/') ||
            relativePath.StartsWith('\\'))
        {
            throw new InvalidOperationException(
                $"Absolute paths are not allowed: {relativePath}");
        }

        /*
         * Granite occasionally emits U+00A0 NO-BREAK SPACE where it appears
         * to intend a path separator.
         *
         * Do not replace ordinary spaces or all Unicode whitespace.
         */
        var repairedRelativePath = relativePath
            .Replace('\u00A0', '/')
            .Normalize(NormalizationForm.FormC)
            .Trim();

        /*
         * A leading malformed separator may become '/' after repairing U+00A0.
         * Genuine leading slashes were already rejected above.
         */
        repairedRelativePath =
            repairedRelativePath.TrimStart('/', '\\');

        var segments = repairedRelativePath.Split(
            ['/', '\\'],
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);

        if (segments.Length == 0)
        {
            throw new InvalidOperationException(
                $"Path contains no file name: {relativePath}");
        }

        if (segments.Any(segment =>
                segment is "." or ".."))
        {
            throw new InvalidOperationException(
                $"Directory traversal is not allowed: {relativePath}");
        }

        var normalizedRelativePath =
            Path.Combine(segments);

        var fullRoot =
            Path.GetFullPath(outputRoot);

        var fullPath =
            Path.GetFullPath(
                normalizedRelativePath,
                fullRoot);

        var pathRelativeToRoot =
            Path.GetRelativePath(
                fullRoot,
                fullPath);

        if (Path.IsPathRooted(pathRelativeToRoot) ||
            pathRelativeToRoot.Equals(
                "..",
                StringComparison.Ordinal) ||
            pathRelativeToRoot.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Path escapes output root: {relativePath}");
        }

        return fullPath;
    }
}
