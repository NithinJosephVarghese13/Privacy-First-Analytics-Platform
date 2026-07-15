namespace PrivacyAnalytics.Domain.Identity;

/// <summary>
/// Default implementation of <see cref="IIdentityHashService"/>. This is the single place that
/// decides which tier an event belongs to and applies the FR-2.1 mutual-exclusion rule.
/// </summary>
/// <remarks>
/// <b>Forced-null rule (load-bearing):</b> when <see cref="IdentityHashInput.IsAuthenticated"/>
/// is <c>true</c>, <see cref="IdentityHashResult.AnonymousDailyHash"/> is unconditionally
/// <c>null</c> — regardless of opt-in — so authenticated traffic is never double-counted against
/// the anonymous HLL unique-visitor estimate. An authenticated-but-not-opted-in event stores
/// <em>no</em> identifier at all: neither tier is populated. That is the correct privacy posture
/// (the user did not consent to durable tracking, and authenticated traffic is excluded from the
/// anonymous bucket by design).
/// </remarks>
public sealed class IdentityHashService(
    IAnonymousDailyHashProvider anonymousDailyHashProvider,
    IDurableHashProvider durableHashProvider) : IIdentityHashService
{
    public IdentityHashResult Compute(IdentityHashInput input, DateOnly utcDate)
    {
        if (input.IsAuthenticated)
        {
            // Tier 2 only: the durable HMAC is computed iff the user is authenticated AND has
            // opted in. AnonymousDailyHash is forced null here — this is the double-count guard.
            if (!input.UserOptedIn || string.IsNullOrEmpty(input.UserId))
            {
                return new IdentityHashResult(AnonymousDailyHash: null, DurableHash: null);
            }

            var durable = durableHashProvider.ComputeDurableHash(input.OrganizationId, input.UserId);
            return new IdentityHashResult(AnonymousDailyHash: null, DurableHash: durable);
        }

        // Tier 1: anonymous daily hash. We refuse to mint a pseudonym when IP or UA is absent —
        // hashing "" produces a stable but meaningless value that would corrupt unique-visitor
        // counts, so we prefer null (an unattributable event) over a degenerate hash.
        if (string.IsNullOrEmpty(input.ClientIp) || string.IsNullOrEmpty(input.UserAgent))
        {
            return new IdentityHashResult(AnonymousDailyHash: null, DurableHash: null);
        }

        var anonymous = anonymousDailyHashProvider.ComputeAnonymousDailyHash(
            input.ClientIp, input.UserAgent, utcDate);
        return new IdentityHashResult(AnonymousDailyHash: anonymous, DurableHash: null);
    }
}
