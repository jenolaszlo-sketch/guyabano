using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Guyabano.CI.Client.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGuyabanoCiClient(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<GuyabanoCiClientOptions>()
            .Bind(configuration.GetSection(
                GuyabanoCiClientOptions.SectionName))
            .Validate(
                options => Uri.TryCreate(
                    options.BaseAddress,
                    UriKind.Absolute,
                    out _),
                "GuyabanoCI:BaseAddress must be an absolute URI.")
            .ValidateOnStart();

        services.AddHttpClient<IGuyabanoCiClient, GuyabanoCiClient>(
            (serviceProvider, client) =>
            {
                var options = serviceProvider
                    .GetRequiredService<IOptions<GuyabanoCiClientOptions>>()
                    .Value;

                client.BaseAddress = new Uri(options.BaseAddress);
                client.Timeout = Timeout.InfiniteTimeSpan;
            });

        return services;
    }
}
