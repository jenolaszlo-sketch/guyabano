namespace Guyabano.WorkflowWorker;

internal static class ActivityExceptionClassifier
{
    private static readonly string[] TransientLlmHttpStatusMarkers =
    [
        "HTTP 408",
        "HTTP 429",
        "HTTP 500",
        "HTTP 502",
        "HTTP 503",
        "HTTP 504"
    ];

    public static bool IsTransient(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (var current = exception;
             current is not null;
             current = current.InnerException)
        {
            if (current is HttpRequestException or IOException or TimeoutException)
                return true;

            // Baize 0.2.0 preserves the provider status in the exception message,
            // but does not yet expose it as typed failure metadata. Keep this
            // compatibility check narrow so model/schema failures are not retried
            // as transport failures.
            if (current.GetType().Name == "LlmClientException" &&
                TransientLlmHttpStatusMarkers.Any(marker =>
                    current.Message.Contains(
                        marker,
                        StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }
}
