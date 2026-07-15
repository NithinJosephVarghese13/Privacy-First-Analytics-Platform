using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PrivacyAnalytics.Domain.Identity;
using PrivacyAnalytics.Infrastructure.Identity;

namespace PrivacyAnalytics.Infrastructure.Identity;

/// <summary>
/// DI registration for the two-tier identity hashing pipeline. Registers the Docker secret
/// reader, the daily-salt provider, both pseudonymizers, and the <see cref="IdentityHashService"/>
/// orchestrator that enforces the FR-2.1 forced-null rule.
/// </summary>
public static class IdentityServiceCollectionExtensions
{
    public static IServiceCollection AddIdentityHashing(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<IdentityOptions>(configuration.GetSection(IdentityOptions.SectionName));

        // Singleton by design: the secret files are read once at startup and the signing key is
        // cached. If a key rotation is ever needed, recycle the process (or implement a
        // reload-on-change reader) rather than reading the key file per request, which would widen
        // the attack surface on the secret path.
        services.AddSingleton<ISecretReader, DockerSecretReader>();
        services.AddSingleton<IDailySaltProvider, DailySaltProvider>();
        services.AddSingleton<Domain.Identity.IAnonymousDailyHashProvider, AnonymousDailyHashProvider>();
        services.AddSingleton<Domain.Identity.IDurableHashProvider, DurableHashProvider>();
        services.AddSingleton<IIdentityHashService, IdentityHashService>();
        return services;
    }
}
