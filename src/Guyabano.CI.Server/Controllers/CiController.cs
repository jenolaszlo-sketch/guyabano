using Microsoft.AspNetCore.Mvc;
using Guyabano.CI.Contracts;
using Guyabano.CI.Server.Services;
using System.Text.Json;

namespace Guyabano.CI.Server.Controllers;

[ApiController]
[Route("api/ci")]
public sealed class CiController(
    DotNetScaffoldingStreamingService scaffoldingService,
    DotNetBuildStreamingService buildService,
    DotNetTestStreamingService testService,
    JetBrainsAnalysisStreamingService jetBrainsService)
    : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web);

    [HttpPost("scaffold")]
    public Task ScaffoldAsync(
        [FromBody] CiScaffoldRequest request,
        CancellationToken cancellationToken) =>
        StreamAsync(
            scaffoldingService.RunAsync(request, cancellationToken),
            cancellationToken);

    [HttpPost("build")]
    public Task BuildAsync(
        [FromBody] CiBuildRequest request,
        CancellationToken cancellationToken) =>
        StreamAsync(
            buildService.RunAsync(request, cancellationToken),
            cancellationToken);

    [HttpPost("test")]
    public Task TestAsync(
        [FromBody] CiTestRequest request,
        CancellationToken cancellationToken) =>
        StreamAsync(
            testService.RunAsync(request, cancellationToken),
            cancellationToken);

    [HttpPost("analyze/jetbrains")]
    public Task AnalyzeWithJetBrainsAsync(
        [FromBody] CiJetBrainsAnalysisRequest request,
        CancellationToken cancellationToken) =>
        StreamAsync(
            jetBrainsService.RunAsync(request, cancellationToken),
            cancellationToken);

    private async Task StreamAsync(
        IAsyncEnumerable<CiStreamEvent> events,
        CancellationToken cancellationToken)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Append("X-Accel-Buffering", "no");

        await foreach (var streamEvent in events.WithCancellation(
            cancellationToken))
        {
            var json = JsonSerializer.Serialize(streamEvent, JsonOptions);

            await Response.WriteAsync("data: ", cancellationToken);
            await Response.WriteAsync(json, cancellationToken);
            await Response.WriteAsync("\n\n", cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
        }
    }
}
