using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PrivacyAnalytics.Infrastructure.Identity;

/// <summary>
/// Tier 2 pseudonymizer: a tenant-scoped HMAC of the authenticated user's stable identifier,
/// signed with a key sourced from a Docker secret. The signing key is loaded ONCE at construction
/// and held in memory; it is never persisted to the database, never written to appsettings, and
/// never placed in an environment variable holding the literal value (FR-2.1). Keeping the key
/// out-of-band means a database compromise alone cannot re-identify authenticated users.
/// </summary>
/// <remarks>
/// <b>Message construction:</b> <c>UTF8(organizationId.ToString("N") + ":" + userId)</c>. The
/// organization id is rendered in the fixed 32-char "N" form so the org/user boundary in the
/// message is unambiguous; folding the org id into the message makes the durable hash
/// tenant-scoped — the same user id provisioned into two tenants yields two independent hashes.
/// </remarks>
public sealed class DurableHashProvider : Domain.Identity.IDurableHashProvider
{
    private readonly byte[] _key;

    public DurableHashProvider(ISecretReader secrets, IOptionsMonitor<IdentityOptions> options, ILogger<DurableHashProvider> logger)
    {
        var opts = options.CurrentValue;
        var key = secrets.TryReadSecret(opts.DurableHmacSecretName);
        if (key is null)
        {
            if (!opts.AllowUnmanagedDevSecrets)
            {
                throw new InvalidOperationException(
                    $"Docker secret '{opts.DurableHmacSecretName}' (durable-HMAC signing key) is missing " +
                    $"at '{Path.Combine(opts.SecretsPath, opts.DurableHmacSecretName)}' and unmanaged dev " +
                    "secrets are disabled. The signing key must be mounted out-of-band (see " +
                    "docker-compose.yml); it must NEVER be a DB column, an appsettings value, or an env " +
                    "var holding the literal key.");
            }

            logger.LogWarning(
                "DURABLE HMAC KEY MISSING: Docker secret '{Name}' was not found. Falling back to a " +
                "FIXED DEV key. This is acceptable ONLY for local development — a fixed key in any " +
                "hosted deployment means one key compromise re-identifies every authenticated user.",
                opts.DurableHmacSecretName);

            _key = Encoding.UTF8.GetBytes("dev-only-durable-hmac-key-DO-NOT-USE-IN-PROD");
        }
        else
        {
            _key = key;
        }
    }

    public string ComputeDurableHash(Guid organizationId, string userId)
    {
        if (string.IsNullOrEmpty(userId))
        {
            throw new ArgumentException("A non-empty user id is required to compute the durable hash.", nameof(userId));
        }

        // "N" => 32 hex chars with no dashes; fixed width, so the org/user boundary is unambiguous.
        var message = Encoding.UTF8.GetBytes(organizationId.ToString("N") + ":" + userId);
        var mac = HMACSHA256.HashData(_key, message);
        return Convert.ToHexStringLower(mac);
    }
}
