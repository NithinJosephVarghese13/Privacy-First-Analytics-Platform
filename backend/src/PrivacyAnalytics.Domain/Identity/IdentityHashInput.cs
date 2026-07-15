namespace PrivacyAnalytics.Domain.Identity;

/// <summary>
/// Raw inputs required to pseudonymize a single tracking event per FR-2.1. The combination of
/// <see cref="IsAuthenticated"/> and <see cref="UserOptedIn"/> selects the tier:
/// <list type="bullet">
/// <item>Not authenticated → Tier 1 (anonymous daily hash), keyed on <see cref="ClientIp"/> + <see cref="UserAgent"/>.</item>
/// <item>Authenticated &amp; opted-in → Tier 2 (tenant-scoped durable HMAC), keyed on <see cref="OrganizationId"/> + <see cref="UserId"/>.</item>
/// <item>Authenticated &amp; NOT opted-in → no identifier is stored at all (both hashes null).</item>
/// </list>
/// </summary>
/// <remarks>
/// Per GDPR Recital 26, every identifier below is a <b>pseudonym</b>, not anonymous data — both
/// tiers are personal data; the architecture's claim is PII minimization and re-identification
/// resistance, not "zero PII."
/// </remarks>
public sealed record IdentityHashInput(
    Guid OrganizationId,
    bool IsAuthenticated,
    bool UserOptedIn,
    string? UserId,
    string? ClientIp,
    string? UserAgent);
