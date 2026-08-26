using Microsoft.Extensions.Options;

namespace Guyabano.CI.Server;

/// <summary>
/// Rejects requests that do not carry a valid <c>X-Api-Key</c> header when
/// an API key is configured in <see cref="CiServerOptions.ApiKey"/>.
/// </summary>
internal sealed class ApiKeyMiddleware(
    RequestDelegate next,
    IOptions<CiServerOptions> options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var expectedKey = options.Value.ApiKey;
        if (!string.IsNullOrEmpty(expectedKey))
        {
            if (!context.Request.Headers.TryGetValue("X-Api-Key", out var provided) ||
                !string.Equals(provided.FirstOrDefault(), expectedKey, StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("Invalid or missing X-Api-Key header.");
                return;
            }
        }

        await next(context);
    }
}
