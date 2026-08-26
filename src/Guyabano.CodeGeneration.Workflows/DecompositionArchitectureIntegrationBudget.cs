namespace Guyabano.CodeGeneration.Workflows;

internal sealed class DecompositionArchitectureIntegrationBudget(
    int maximumAttemptsPerTarget)
{
    private readonly Dictionary<string, int> _attemptsByTarget =
        new(StringComparer.Ordinal);

    public bool TryConsume(string targetId, out int attempt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        if (maximumAttemptsPerTarget <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(maximumAttemptsPerTarget));

        attempt = _attemptsByTarget.GetValueOrDefault(targetId) + 1;
        _attemptsByTarget[targetId] = attempt;
        return attempt <= maximumAttemptsPerTarget;
    }
}
