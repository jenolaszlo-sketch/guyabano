namespace Guyabano.Llm.Prompting;

public sealed class FilePromptLoader(string promptRoot) : IPromptLoader
{
    private readonly string _promptRoot = promptRoot;

    public async Task<string> LoadAsync(
        string promptName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(promptName))
            throw new ArgumentException("Prompt name cannot be empty.", nameof(promptName));

        if (Path.IsPathRooted(promptName))
            throw new InvalidOperationException($"Prompt name must be relative: {promptName}");

        var fullRoot = Path.GetFullPath(_promptRoot);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, promptName));

        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Prompt path escapes prompt root: {promptName}");

        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Prompt file not found: {fullPath}");

        return await File.ReadAllTextAsync(fullPath, cancellationToken);
    }
}