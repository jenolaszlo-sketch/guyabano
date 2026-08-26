using Guyabano.CodeGeneration.Workflows;

namespace Guyabano.WorkflowWorker;

internal static class CodeGenerationModelSelector
{
    public static CodeGenerationModelSelection Select(
        CodeGenerationWorkerOptions options,
        int attempt,
        int startingTier = 1)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!HasValidConfiguration(options))
            throw new InvalidOperationException(
                "Code-generation models must be non-empty and distinct.");
        var models = ResolveModels(options);
        var effectiveStartingTier = Math.Min(startingTier, models.Length);
        var maximumAttempts = MaximumAttempts(options, startingTier);
        if (attempt < 1 || attempt > maximumAttempts)
        {
            throw new ArgumentOutOfRangeException(nameof(attempt));
        }

        var tier = effectiveStartingTier - 1 + (attempt - 1) /
            CodeGenerationWorkflowConstants.MaximumAttemptsPerModel;
        var modelAttempt = (attempt - 1) %
            CodeGenerationWorkflowConstants.MaximumAttemptsPerModel + 1;

        return new CodeGenerationModelSelection(
            models[tier],
            tier + 1,
            modelAttempt);
    }

    public static int MaximumAttempts(
        CodeGenerationWorkerOptions options,
        int startingTier)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (startingTier < 1)
            throw new ArgumentOutOfRangeException(nameof(startingTier));
        var modelCount = ResolveModels(options).Length;
        var effectiveStartingTier = Math.Min(startingTier, modelCount);

        return (modelCount - effectiveStartingTier + 1) *
            CodeGenerationWorkflowConstants.MaximumAttemptsPerModel;
    }

    internal static bool HasValidConfiguration(
        CodeGenerationWorkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var models = ResolveModels(options);
        return models.Length <=
                CodeGenerationWorkflowConstants.MaximumModelTiers &&
            models.All(model => !string.IsNullOrWhiteSpace(model)) &&
            models.Distinct(StringComparer.OrdinalIgnoreCase).Count() ==
                models.Length;
    }

    private static string[] ResolveModels(
        CodeGenerationWorkerOptions options) =>
        options.EscalationModels.Prepend(options.Model).ToArray();
}

internal sealed record CodeGenerationModelSelection(
    string Model,
    int Tier,
    int ModelAttempt);
