using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PrivacyAnalytics.Infrastructure.Identity;

/// <summary>
/// Produces a daily-rotating salt for Tier 1 (anonymous) hashing. The salt is derived
/// <b>deterministically</b> from a secret seed and the UTC date —
/// <c>HMAC-SHA256(seed, "analytics-daily-salt/v1:" + YYYY-MM-DD)</c> — so every API/worker instance
/// in the deployment agrees on the same salt for a given day without any coordination, while the
/// salt still changes every midnight UTC (same real visitor → unlinkable hash day to day).
/// </summary>
/// <remarks>
/// Using a <b>secret</b> seed (rather than the date alone) materially improves re-identification
/// resistance: with a public date-only salt an attacker could brute-force the IPv4 space against
/// the known salt to invert the daily hash for any target IP. A secret seed turns that into an
/// offline attack requiring the seed, which is kept out-of-band alongside the HMAC key.
/// </remarks>
public interface IDailySaltProvider
{
    byte[] GetDailySalt(DateOnly utcDate);
}

public sealed class DailySaltProvider : IDailySaltProvider
{
    private const string SaltLabel = "analytics-daily-salt/v1:";

    private static readonly byte[] LabelBytes = System.Text.Encoding.UTF8.GetBytes(SaltLabel);

    private readonly byte[] _seed;

    public DailySaltProvider(ISecretReader secrets, IOptionsMonitor<IdentityOptions> options, ILogger<DailySaltProvider> logger)
    {
        var opts = options.CurrentValue;
        var seed = secrets.TryReadSecret(opts.DailySaltSecretName);
        if (seed is null)
        {
            if (!opts.AllowUnmanagedDevSecrets)
            {
                throw new InvalidOperationException(
                    $"Docker secret '{opts.DailySaltSecretName}' (daily-salt seed) is missing at " +
                    $"'{Path.Combine(opts.SecretsPath, opts.DailySaltSecretName)}' and unmanaged dev " +
                    "secrets are disabled. Mount the secret (see docker-compose.yml) or enable " +
                    "Identity:AllowUnmanagedDevSecrets for local development only.");
            }

            logger.LogWarning(
                "DAILY SALT SEED MISSING: Docker secret '{Name}' was not found. Falling back to a " +
                "FIXED DEV seed. This is acceptable ONLY for local development — never ship this to a " +
                "hosted environment, where re-identification resistance collapses.",
                opts.DailySaltSecretName);

            // Fixed, well-known dev seed so local `dotnet run` is deterministic across restarts.
            _seed = System.Text.Encoding.UTF8.GetBytes("dev-only-daily-salt-seed-DO-NOT-USE-IN-PROD");
        }
        else
        {
            _seed = seed;
        }
    }

    public byte[] GetDailySalt(DateOnly utcDate)
    {
        // ISO-8601 date string guarantees a single canonical salt per UTC day and a clean daily boundary.
        var dateBytes = System.Text.Encoding.UTF8.GetBytes(utcDate.ToString("yyyy-MM-dd"));

        // message = labelBytes || dateBytes — the label namespaces this derivation so the same seed
        // can later be reused for other derived secrets without collision risk.
        var message = new byte[LabelBytes.Length + dateBytes.Length];
        LabelBytes.CopyTo(message, 0);
        dateBytes.CopyTo(message, LabelBytes.Length);

        return HMACSHA256.HashData(_seed, message);
    }
}
