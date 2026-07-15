namespace PrivacyAnalytics.Infrastructure.Identity;

/// <summary>
/// Strongly-typed binding for the <c>Identity</c> configuration section. Importantly this holds
/// only <b>pointers</b> to where secrets live (a directory path and secret <em>names</em>) — never
/// the secret bytes themselves. The literal HMAC key never appears in appsettings, an environment
/// variable, or a DB column (FR-2.1); it is read from a file mounted as a Docker secret.
/// </summary>
public sealed class IdentityOptions
{
    public const string SectionName = "Identity";

    /// <summary>
    /// Directory Docker mounts secrets into. Defaults to <c>/run/secrets</c>, the standard
    /// mount point for Docker secrets and for the Swarm/Compose secrets API. Override per
    /// environment if a managed secrets store is sidecar-mounted elsewhere.
    /// </summary>
    public string SecretsPath { get; set; } = "/run/secrets";

    /// <summary>Name of the Docker secret holding the daily-salt derivation seed (Tier 1).</summary>
    public string DailySaltSecretName { get; set; } = "analytics_daily_salt_seed";

    /// <summary>Name of the Docker secret holding the durable-HMAC signing key (Tier 2).</summary>
    public string DurableHmacSecretName { get; set; } = "analytics_durable_hmac_key";

    /// <summary>
    /// When <c>true</c> and a secret file is missing, fall back to a fixed, well-known dev value
    /// and log a loud warning — so <c>dotnet run</c> works outside Docker in local dev. When
    /// <c>false</c> (the production default) a missing secret file fails app startup fast rather
    /// than silently degrading re-identification resistance.
    /// </summary>
    public bool AllowUnmanagedDevSecrets { get; set; } = false;
}
