namespace PrivacyAnalytics.Domain.Identity;

/// <summary>
/// The two pseudonymous identifiers produced for a tracking event. At most one is non-null per
/// event: <see cref="AnonymousDailyHash"/> and <see cref="DurableHash"/> are mutually exclusive
/// by construction (see <see cref="IIdentityHashService"/>), which is what prevents
/// double-counting a single event between the two tiers.
/// </summary>
public sealed record IdentityHashResult(string? AnonymousDailyHash, string? DurableHash);
