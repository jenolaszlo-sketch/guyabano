using FluentAssertions;
using Guyabano.CI.Client;
using Guyabano.CI.Contracts;
using System.Net;
using System.Text;

namespace Guyabano.CI.Tests;

public sealed class GuyabanoCiClientTests
{
    [Fact]
    public async Task ScaffoldAsync_StreamsSseEventsFromScaffoldEndpoint()
    {
        var handler = new StubHandler(
            "data: {\"type\":\"result\",\"phase\":\"scaffold-result\",\"success\":true,\"exitCode\":0}\n\n");
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://guyabano-ci-server:8080")
        };
        var client = new GuyabanoCiClient(httpClient);
        var events = new List<CiStreamEvent>();

        await foreach (var streamEvent in client.ScaffoldAsync(
            new CiScaffoldRequest(
                ".",
                new CiScaffoldSolution("Todo", "Todo.sln"),
                [
                    new CiScaffoldProject(
                        "Todo.Api",
                        "src/Todo.Api/Todo.Api.csproj",
                        "WebApi",
                        "net10.0",
                        [],
                        [])
                ]),
            TestContext.Current.CancellationToken))
        {
            events.Add(streamEvent);
        }

        handler.RequestUri.Should().Be(
            new Uri("http://guyabano-ci-server:8080/api/ci/scaffold"));
        events.Should().ContainSingle().Which.Success.Should().BeTrue();
    }

    [Fact]
    public async Task BuildAsync_StreamsSseEventsFromUnifiedEndpoint()
    {
        var handler = new StubHandler(
            "data: {\"type\":\"phase\",\"phase\":\"build\",\"message\":\"Building\"}\n\n" +
            "data: {\"type\":\"result\",\"phase\":\"build-result\",\"success\":true,\"exitCode\":0}\n\n");
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://guyabano-ci-server:8080")
        };
        var client = new GuyabanoCiClient(httpClient);
        var events = new List<CiStreamEvent>();

        await foreach (var streamEvent in client.BuildAsync(
            new CiBuildRequest("."),
            TestContext.Current.CancellationToken))
        {
            events.Add(streamEvent);
        }

        handler.RequestUri.Should().Be(
            new Uri("http://guyabano-ci-server:8080/api/ci/build"));
        events.Should().HaveCount(2);
        events[0].Phase.Should().Be("build");
        events[1].Success.Should().BeTrue();
    }

    private sealed class StubHandler(string response) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;

            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        response,
                        Encoding.UTF8,
                        "text/event-stream")
                });
        }
    }
}
