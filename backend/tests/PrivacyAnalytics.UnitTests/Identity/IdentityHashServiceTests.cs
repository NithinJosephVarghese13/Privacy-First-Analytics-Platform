using PrivacyAnalytics.Domain.Identity;
using Xunit;

namespace PrivacyAnalytics.UnitTests.Identity;

/// <summary>
/// Proves the FR-2.1 mutual-exclusion / forced-null rule in <see cref="IdentityHashService"/>:
/// an authenticated event NEVER carries an <see cref="IdentityHashResult.AnonymousDailyHash"/>
/// (preventing double-counting between tiers), and an unauthenticated event NEVER carries a
/// <see cref="IdentityHashResult.DurableHash"/>. These are pure unit tests — no Docker secrets,
/// no database — because the privacy invariant lives in the domain orchestrator.
/// </summary>
public sealed class IdentityHashServiceTests
{
    private static readonly Guid OrgA = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid OrgB = Guid.Parse("00000000-0000-0000-0000-000000000002");

    private readonly IdentityHashService _service = new(
        new StubAnonymousHashProvider(),
        new StubDurableHashProvider());

    [Fact]
    public void AnonymousTraffic_ProducesOnlyAnonymousDailyHash()
    {
        var input = new IdentityHashInput(OrgA, IsAuthenticated: false, UserOptedIn: false,
            UserId: null, ClientIp: "203.0.113.7", UserAgent: "Mozilla/5.0");

        var result = _service.Compute(input, new DateOnly(2026, 7, 15));

        Assert.NotNull(result.AnonymousDailyHash);
        Assert.Null(result.DurableHash);
        Assert.Equal("ANON:203.0.113.7|Mozilla/5.0|2026-07-15", result.AnonymousDailyHash);
    }

    [Fact]
    public void AuthenticatedOptedIn_ProducesOnlyDurableHash_AndForcesAnonymousNull()
    {
        var input = new IdentityHashInput(OrgA, IsAuthenticated: true, UserOptedIn: true,
            UserId: "user-42", ClientIp: "203.0.113.7", UserAgent: "Mozilla/5.0");

        var result = _service.Compute(input, new DateOnly(2026, 7, 15));

        // The load-bearing assertion: AnonymousDailyHash is null even though IP+UA are present.
        Assert.Null(result.AnonymousDailyHash);
        Assert.NotNull(result.DurableHash);
        Assert.Equal("DURABLE:00000000000000000000000000000001:user-42", result.DurableHash);
    }

    [Fact]
    public void AuthenticatedNotOptedIn_StoresNoIdentifierAtAll()
    {
        var input = new IdentityHashInput(OrgA, IsAuthenticated: true, UserOptedIn: false,
            UserId: "user-42", ClientIp: "203.0.113.7", UserAgent: "Mozilla/5.0");

        var result = _service.Compute(input, new DateOnly(2026, 7, 15));

        Assert.Null(result.AnonymousDailyHash);
        Assert.Null(result.DurableHash);
    }

    [Fact]
    public void AnonymousTraffic_MissingClientIp_StoresNoIdentifier()
    {
        var input = new IdentityHashInput(OrgA, IsAuthenticated: false, UserOptedIn: false,
            UserId: null, ClientIp: null, UserAgent: "Mozilla/5.0");

        var result = _service.Compute(input, new DateOnly(2026, 7, 15));

        Assert.Null(result.AnonymousDailyHash);
        Assert.Null(result.DurableHash);
    }

    [Fact]
    public void AuthenticatedOptedIn_MissingUserId_StoresNoDurableHash()
    {
        var input = new IdentityHashInput(OrgA, IsAuthenticated: true, UserOptedIn: true,
            UserId: "", ClientIp: null, UserAgent: null);

        var result = _service.Compute(input, new DateOnly(2026, 7, 15));

        Assert.Null(result.AnonymousDailyHash);
        Assert.Null(result.DurableHash);
    }

    [Fact]
    public void TwoTiers_AreMutuallyExclusiveAcrossAllBranches()
    {
        var date = new DateOnly(2026, 7, 15);
        var inputs = new[]
        {
            new IdentityHashInput(OrgA, false, false, null, "1.2.3.4", "UA"),
            new IdentityHashInput(OrgA, true, true, "u", "1.2.3.4", "UA"),
            new IdentityHashInput(OrgA, true, false, "u", "1.2.3.4", "UA")
        };

        foreach (var input in inputs)
        {
            var result = _service.Compute(input, date);
            Assert.False(
                result.AnonymousDailyHash is not null && result.DurableHash is not null,
                "At no point should both tiers be populated simultaneously — that would double-count.");
        }
    }

    private sealed class StubAnonymousHashProvider : IAnonymousDailyHashProvider
    {
        public string ComputeAnonymousDailyHash(string clientIp, string userAgent, DateOnly utcDate) =>
            $"ANON:{clientIp}|{userAgent}|{utcDate:O}";
    }

    private sealed class StubDurableHashProvider : IDurableHashProvider
    {
        public string ComputeDurableHash(Guid organizationId, string userId) =>
            $"DURABLE:{organizationId.ToString("N")}:{userId}";
    }
}
