namespace Guyabano.CI.Client;

public sealed class GuyabanoCiClientOptions
{
    public const string SectionName = "GuyabanoCI";

    public string BaseAddress { get; set; } =
        "http://guyabano-ci-server:8080";
}
