using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace PrivacyAnalytics.Infrastructure.Identity;

/// <summary>
/// Tier 1 pseudonymizer: <c>SHA-256(IP + UserAgent + DailySalt)</c>. The IP and user agent are
/// joined with a NUL-free separator (newline) so the pair <c>("1.1","2")</c> and <c>("1","1.2")</c>
/// cannot collide into the same preimage; the salt is then appended as raw bytes.
/// </summary>
/// <remarks>
/// Every identifier produced here is a <b>pseudonym</b> (GDPR Recital 26), not anonymous data —
/// the daily hash could in principle be re-linked via IP-space brute force against a known daily
/// salt. The architecture's claim is re-identification resistance and PII minimization, not
/// anonymity.
/// </remarks>
public sealed class AnonymousDailyHashProvider(IDailySaltProvider dailySaltProvider, ILogger<AnonymousDailyHashProvider> logger)
    : Domain.Identity.IAnonymousDailyHashProvider
{
    public string ComputeAnonymousDailyHash(string clientIp, string userAgent, DateOnly utcDate)
    {
        if (string.IsNullOrEmpty(clientIp))
        {
            throw new ArgumentException("Client IP is required to compute the anonymous daily hash.", nameof(clientIp));
        }
        if (string.IsNullOrEmpty(userAgent))
        {
            throw new ArgumentException("User agent is required to compute the anonymous daily hash.", nameof(userAgent));
        }

        var salt = dailySaltProvider.GetDailySalt(utcDate);

        // Preimage = UTF8(ip + "\n" + userAgent) || salt. The separator disambiguates the two
        // variable-length inputs; the cryptographic salt (which the attacker does not know) makes
        // crafted collisions infeasible regardless.
        var identityBytes = Encoding.UTF8.GetBytes(clientIp + "\n" + userAgent);
        var preimage = new byte[identityBytes.Length + salt.Length];
        identityBytes.CopyTo(preimage, 0);
        salt.CopyTo(preimage, identityBytes.Length);

        var hash = SHA256.HashData(preimage);

        logger.LogDebug(
            "AnonymousDailyHash computed for UTC date {Date} (salt length {SaltLen} bytes).",
            utcDate.ToString("yyyy-MM-dd"),
            salt.Length);

        return Convert.ToHexStringLower(hash);
    }
}
