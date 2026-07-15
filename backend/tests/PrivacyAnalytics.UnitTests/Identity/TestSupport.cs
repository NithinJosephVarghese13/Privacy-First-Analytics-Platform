using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PrivacyAnalytics.Infrastructure.Identity;

namespace PrivacyAnalytics.UnitTests.Identity;

/// <summary>
/// In-memory <see cref="ISecretReader"/> for unit tests. Keys the unit tests to never touch the
/// filesystem, so the forced-null / tenant-scoping rules can be proven deterministically.
/// </summary>
internal sealed class FakeSecretReader : ISecretReader
{
    private readonly Dictionary<string, byte[]> _secrets;

    public FakeSecretReader(Dictionary<string, byte[]> secrets) => _secrets = secrets;

    public byte[]? TryReadSecret(string name) =>
        _secrets.TryGetValue(name, out var value) ? value : null;
}

internal static class IdentityTestOptions
{
    public static IOptionsMonitor<IdentityOptions> Create(
        string dailySeedName = "analytics_daily_salt_seed",
        string hmacKeyName = "analytics_durable_hmac_key",
        bool allowDevFallback = false,
        string secretsPath = "/run/secrets") =>
        new TestOptionsMonitor(new IdentityOptions
        {
            DailySaltSecretName = dailySeedName,
            DurableHmacSecretName = hmacKeyName,
            AllowUnmanagedDevSecrets = allowDevFallback,
            SecretsPath = secretsPath
        });
}

internal sealed class TestOptionsMonitor(IdentityOptions current) : IOptionsMonitor<IdentityOptions>
{
    public IdentityOptions CurrentValue => current;
    public IdentityOptions Get(string? name) => current;
    public IDisposable OnChange(Action<IdentityOptions, string> listener) => new NoOpDisposable();
    private sealed class NoOpDisposable : IDisposable { public void Dispose() { } }
}
