using Microsoft.Extensions.DependencyInjection;
using PrivacyAnalytics.Domain.Messaging;

namespace PrivacyAnalytics.Infrastructure.Messaging;

public static class MessagingServiceCollectionExtensions
{
    public static IServiceCollection AddMessaging(this IServiceCollection services)
    {
        services.AddSingleton<RabbitMqPublisher>();
        services.AddSingleton<IMessagePublisher>(sp => sp.GetRequiredService<RabbitMqPublisher>());
        return services;
    }
}
