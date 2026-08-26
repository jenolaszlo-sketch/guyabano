using FluentAssertions;
using Guyabano.WorkflowWorker;

namespace Guyabano.WorkflowProgressTests;

public sealed class ActivityExceptionClassifierTests
{
    [Theory]
    [MemberData(nameof(TransientExceptions))]
    public void IsTransient_RecognizesTransportFailures(Exception exception)
    {
        ActivityExceptionClassifier.IsTransient(exception).Should().BeTrue();
    }

    [Fact]
    public void IsTransient_RecognizesWrappedTransportFailure()
    {
        var exception = new InvalidOperationException(
            "provider failed",
            new HttpRequestException(
                "response ended",
                new IOException("premature end")));

        ActivityExceptionClassifier.IsTransient(exception).Should().BeTrue();
    }

    [Fact]
    public void IsTransient_RejectsProgrammingFailure()
    {
        ActivityExceptionClassifier.IsTransient(
                new InvalidOperationException("bad state"))
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public void IsTransient_RecognizesRetryableBaizeHttpFailures(int statusCode)
    {
        var exception = new LlmClientException(
            $"LLM streaming request failed with HTTP {statusCode}: unavailable");

        ActivityExceptionClassifier.IsTransient(exception).Should().BeTrue();
    }

    [Fact]
    public void IsTransient_RejectsNonRetryableBaizeHttpFailure()
    {
        var exception = new LlmClientException(
            "LLM streaming request failed with HTTP 400: invalid request");

        ActivityExceptionClassifier.IsTransient(exception).Should().BeFalse();
    }

    public static IEnumerable<object[]> TransientExceptions()
    {
        yield return [new HttpRequestException("connection reset")];
        yield return [new IOException("response ended prematurely")];
        yield return [new TimeoutException("provider timeout")];
    }

    private sealed class LlmClientException(string message)
        : Exception(message);
}
