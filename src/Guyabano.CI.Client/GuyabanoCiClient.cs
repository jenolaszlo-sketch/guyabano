using Guyabano.CI.Contracts;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Guyabano.CI.Client;

internal sealed class GuyabanoCiClient(HttpClient httpClient) : IGuyabanoCiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web);

    public IAsyncEnumerable<CiStreamEvent> ScaffoldAsync(
        CiScaffoldRequest request,
        CancellationToken cancellationToken = default) =>
        StreamAsync("api/ci/scaffold", request, cancellationToken);

    public IAsyncEnumerable<CiStreamEvent> BuildAsync(
        CiBuildRequest request,
        CancellationToken cancellationToken = default) =>
        StreamAsync("api/ci/build", request, cancellationToken);

    public IAsyncEnumerable<CiStreamEvent> TestAsync(
        CiTestRequest request,
        CancellationToken cancellationToken = default) =>
        StreamAsync("api/ci/test", request, cancellationToken);

    public IAsyncEnumerable<CiStreamEvent> AnalyzeWithJetBrainsAsync(
        CiJetBrainsAnalysisRequest request,
        CancellationToken cancellationToken = default) =>
        StreamAsync(
            "api/ci/analyze/jetbrains",
            request,
            cancellationToken);

    private async IAsyncEnumerable<CiStreamEvent> StreamAsync<TRequest>(
        string relativeUri,
        TRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            relativeUri)
        {
            Content = JsonContent.Create(request, options: JsonOptions)
        };
        using var response = await httpClient.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(
            cancellationToken);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line[6..];
            var streamEvent = JsonSerializer.Deserialize<CiStreamEvent>(
                payload,
                JsonOptions);

            if (streamEvent is not null)
            {
                yield return streamEvent;
            }
        }
    }
}
