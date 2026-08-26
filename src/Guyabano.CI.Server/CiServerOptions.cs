namespace Guyabano.CI.Server;

public sealed class CiServerOptions
{
    public const string SectionName = "CI";

    public string GeneratedRoot { get; set; } = string.Empty;

    public string DotNetCommand { get; set; } = "dotnet";

    public string JetBrainsCommand { get; set; } = "jb";
}
