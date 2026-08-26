namespace Guyabano.CodeGeneration.Workflows;

public static class CodeGenerationContinuationValidator
{
    public static IReadOnlyList<string> ValidateBuildAndRepair(
        CodeGenerationRunCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var errors = new List<string>();
        var result = checkpoint.Result;

        if (result.Plan is null)
            errors.Add("The checkpoint does not contain an architecture plan.");
        else if (string.IsNullOrWhiteSpace(result.Plan.Solution.Path))
            errors.Add("The checkpoint plan does not identify a solution path.");

        if (result.Scaffolding?.Succeeded != true)
            errors.Add("The checkpoint does not contain successful scaffolding.");
        if (result.Decompositions.Count == 0)
            errors.Add("The checkpoint does not contain task decompositions.");
        if (result.TaskResults.Count == 0)
            errors.Add("The checkpoint does not contain generated task provenance.");
        if (result.WrittenFiles.Count == 0)
            errors.Add("The checkpoint does not identify generated files.");

        return errors;
    }
}
