using Guyabano.CI.Contracts;

namespace Guyabano.CI.Client;

public interface IGuyabanoCiClient
{
    IAsyncEnumerable<CiStreamEvent> ScaffoldAsync(
        CiScaffoldRequest request,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<CiStreamEvent> BuildAsync(
        CiBuildRequest request,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<CiStreamEvent> TestAsync(
        CiTestRequest request,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<CiStreamEvent> AnalyzeWithJetBrainsAsync(
        CiJetBrainsAnalysisRequest request,
        CancellationToken cancellationToken = default);
}
