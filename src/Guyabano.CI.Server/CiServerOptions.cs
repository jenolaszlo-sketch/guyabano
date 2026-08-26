namespace Guyabano.CI.Server;

public sealed class CiServerOptions
{
    public const string SectionName = "CI";

    public string GeneratedRoot { get; set; } = string.Empty;

    public string DotNetCommand { get; set; } = "dotnet";

    public string JetBrainsCommand { get; set; } = "jb";

    /// <summary>
    /// API key required in the <c>X-Api-Key</c> header on all requests.
    /// When empty, authentication is disabled (development only).
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;
}
