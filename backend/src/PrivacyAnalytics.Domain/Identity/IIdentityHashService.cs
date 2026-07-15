using PrivacyAnalytics.Domain.Identity;

namespace PrivacyAnalytics.Domain.Identity;

/// <summary>
/// Orchestrates Tier 1 / Tier 2 pseudonymization and enforces the FR-2.1 mutual-exclusion rule:
/// <see cref="IdentityHashResult.AnonymousDailyHash"/> is forced to <c>null</c> whenever the event
/// is authenticated, so a single authenticated event can never contribute to both tiers (which
/// would double-count unique visitors). This is the single place where the privacy invariant lives,
/// so it can be unit-tested without touching Docker secrets or the database.
/// </summary>
public interface IIdentityHashService
{
    IdentityHashResult Compute(IdentityHashInput input, DateOnly utcDate);
}
