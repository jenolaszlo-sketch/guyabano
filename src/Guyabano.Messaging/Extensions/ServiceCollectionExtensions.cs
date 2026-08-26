using Microsoft.Extensions.DependencyInjection;

namespace Guyabano.Messaging.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWorkflowProgress(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<InMemoryWorkflowProgressHub>();
        services.AddSingleton<IWorkflowProgressPublisher>(provider =>
            provider.GetRequiredService<InMemoryWorkflowProgressHub>());
        services.AddSingleton<IWorkflowProgressSubscriber>(provider =>
            provider.GetRequiredService<InMemoryWorkflowProgressHub>());
        return services;
    }
}
