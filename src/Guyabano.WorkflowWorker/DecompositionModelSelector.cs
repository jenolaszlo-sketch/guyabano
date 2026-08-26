using Guyabano.CodeGeneration.Workflows;

namespace Guyabano.WorkflowWorker;

internal static class DecompositionModelSelector
{
    public static DecompositionModelSelection Select(
        CodeGenerationWorkerOptions options,
        int attempt)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!HasValidConfiguration(options))
        {
            throw new InvalidOperationException(
                "Decomposition models must be non-empty and distinct.");
        }

        var models = ResolveModels(options);
        var maximumAttempts = MaximumAttempts(options);
        if (attempt < 1 || attempt > maximumAttempts)
            throw new ArgumentOutOfRangeException(nameof(attempt));

        var tier = (attempt - 1) /
            CodeGenerationWorkflowConstants.MaximumAttemptsPerModel;
        var modelAttempt = (attempt - 1) %
            CodeGenerationWorkflowConstants.MaximumAttemptsPerModel + 1;

        return new DecompositionModelSelection(
            models[tier],
            tier + 1,
            modelAttempt);
    }

    internal static bool HasValidConfiguration(
        CodeGenerationWorkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.DecompositionModel) ||
            options.DecompositionEscalationModels.Count > 1 ||
            options.DecompositionEscalationModels.Any(
                string.IsNullOrWhiteSpace))
        {
            return false;
        }

        var models = ResolveModels(options);
        return models.All(model => !string.IsNullOrWhiteSpace(model)) &&
            models.Distinct(StringComparer.OrdinalIgnoreCase).Count() ==
                models.Length;
    }

    public static int MaximumAttempts(
        CodeGenerationWorkerOptions options) =>
        ResolveModels(options).Length *
        CodeGenerationWorkflowConstants.MaximumAttemptsPerModel;

    private static string[] ResolveModels(
        CodeGenerationWorkerOptions options)
    {
        return options.DecompositionEscalationModels
            .Prepend(options.DecompositionModel)
            .ToArray();
    }
}

internal sealed record DecompositionModelSelection(
    string Model,
    int Tier,
    int ModelAttempt);
