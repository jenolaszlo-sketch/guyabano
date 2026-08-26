namespace Guyabano.CodeGeneration.Workflows;

public sealed class CodeGenerationActivityException : Exception
{
    public CodeGenerationActivityException(
        string message,
        string? errorType = null,
        bool nonRetryable = false)
        : base(message)
    {
        ErrorType = errorType;
        NonRetryable = nonRetryable;
    }

    public CodeGenerationActivityException(
        string message,
        Exception innerException,
        string? errorType = null,
        bool nonRetryable = false)
        : base(message, innerException)
    {
        ErrorType = errorType;
        NonRetryable = nonRetryable;
    }

    public string? ErrorType { get; }

    public bool NonRetryable { get; }
}
