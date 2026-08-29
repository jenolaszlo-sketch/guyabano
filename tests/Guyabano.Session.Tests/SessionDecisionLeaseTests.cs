using FluentAssertions;
using Guyabano.Session;

namespace Guyabano.SessionTests;

public sealed class SessionDecisionLeaseTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "guyabano-decision-lease-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task IndependentProviders_SerializeOneSessionButNotOtherSessions()
    {
        var ct = TestContext.Current.CancellationToken;
        var firstProvider = new FileSystemSessionDecisionLeaseProvider(rootPath);
        var secondProvider = new FileSystemSessionDecisionLeaseProvider(rootPath);
        var sessionId = GuyabanoSessionId.New();
        var first = await firstProvider.AcquireAsync(
            sessionId,
            Guid.CreateVersion7(),
            ct);

        var blocked = secondProvider.AcquireAsync(
            sessionId,
            Guid.CreateVersion7(),
            ct).AsTask();
        await using var unrelated = await secondProvider.AcquireAsync(
            GuyabanoSessionId.New(),
            Guid.CreateVersion7(),
            ct);
        await Task.Delay(100, ct);
        blocked.IsCompleted.Should().BeFalse();

        await first.DisposeAsync();
        await using var acquired = await blocked.WaitAsync(TimeSpan.FromSeconds(2), ct);
        acquired.SessionId.Should().Be(sessionId);
    }

    public void Dispose()
    {
        if (Directory.Exists(rootPath))
            Directory.Delete(rootPath, recursive: true);
    }
}
