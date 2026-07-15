namespace PrivacyAnalytics.Domain.Identity;

/// <summary>
/// Produces the Tier 1 pseudonym <c>SHA-256(IP + UserAgent + DailySalt)</c> for a given UTC date.
/// The salt rotates daily, so the same real visitor yields an unlinkable hash from one day to the
/// next — this is the actual privacy guarantee: no cross-day tracking of anonymous users, by
/// construction (see FR-2.1).
/// </summary>
public interface IAnonymousDailyHashProvider
{
    /// <summary>
    /// Returns the lowercase-hex SHA-256 pseudonym for the supplied IP, user agent and UTC date.
    /// </summary>
    string ComputeAnonymousDailyHash(string clientIp, string userAgent, DateOnly utcDate);
}
