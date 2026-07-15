using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PrivacyAnalytics.Infrastructure.Identity;
using Xunit;

namespace PrivacyAnalytics.UnitTests.Identity;

/// <summary>
/// Proves the Tier 1 daily-rotation guarantee: the anonymous hash is stable within a UTC day and
/// unlinkable across days (the salt changes), and that the salt derivation is deterministic given
/// a shared seed so every instance agrees on the same salt for a day.
/// </summary>
public sealed class AnonymousDailyHashProviderTests
{
    private static readonly byte[] Seed = Convert.FromHexString(
        "d220441ca4c6034e2fab801d9effe53d03b1f4f615f16d69a65362a53cf9af39");

    private static AnonymousDailyHashProvider CreateProvider()
    {
        var secrets = new FakeSecretReader(new Dictionary<string, byte[]>
        {
            ["analytics_daily_salt_seed"] = Seed
        });
        return new AnonymousDailyHashProvider(
            new DailySaltProvider(secrets, IdentityTestOptions.Create(), NullLogger<DailySaltProvider>.Instance),
            NullLogger<AnonymousDailyHashProvider>.Instance);
    }

    [Fact]
    public void SameIpUaAndDay_ProducesStableHash()
    {
        var provider = CreateProvider();
        var day = new DateOnly(2026, 7, 15);

        var h1 = provider.ComputeAnonymousDailyHash("203.0.113.7", "Mozilla/5.0", day);
        var h2 = provider.ComputeAnonymousDailyHash("203.0.113.7", "Mozilla/5.0", day);

        Assert.Equal(h1, h2);
        Assert.Equal(64, h1.Length); // SHA-256 -> 64 lowercase hex chars
    }

    [Fact]
    public void SameVisitor_NextDay_HashesDifferently_DailyUnlinkability()
    {
        var provider = CreateProvider();
        var day1 = new DateOnly(2026, 7, 15);
        var day2 = day1.AddDays(1);

        var h1 = provider.ComputeAnonymousDailyHash("203.0.113.7", "Mozilla/5.0", day1);
        var h2 = provider.ComputeAnonymousDailyHash("203.0.113.7", "Mozilla/5.0", day2);

        Assert.NotEqual(h1, h2);
    }

    [Fact]
    public void DifferentIp_ProducesDifferentHash()
    {
        var provider = CreateProvider();
        var day = new DateOnly(2026, 7, 15);

        var h1 = provider.ComputeAnonymousDailyHash("203.0.113.7", "Mozilla/5.0", day);
        var h2 = provider.ComputeAnonymousDailyHash("198.51.100.9", "Mozilla/5.0", day);

        Assert.NotEqual(h1, h2);
    }

    [Fact]
    public void SeparatorPreventsIpUaBoundaryCollision()
    {
        var provider = CreateProvider();
        var day = new DateOnly(2026, 7, 15);

        // ("1.1","2") vs ("1","1.2") must NOT hash the same — the newline separator disambiguates.
        var h1 = provider.ComputeAnonymousDailyHash("1.1", "2", day);
        var h2 = provider.ComputeAnonymousDailyHash("1", "1.2", day);

        Assert.NotEqual(h1, h2);
    }

    [Fact]
    public void HashMatchesIndependentRecomputation()
    {
        var provider = CreateProvider();
        var day = new DateOnly(2026, 7, 15);
        var ip = "203.0.113.7";
        var ua = "Mozilla/5.0";

        var salt = new DailySaltProvider(
            new FakeSecretReader(new Dictionary<string, byte[]> { ["analytics_daily_salt_seed"] = Seed }),
            IdentityTestOptions.Create(),
            NullLogger<DailySaltProvider>.Instance).GetDailySalt(day);

        var identityBytes = Encoding.UTF8.GetBytes(ip + "\n" + ua);
        var preimage = new byte[identityBytes.Length + salt.Length];
        identityBytes.CopyTo(preimage, 0);
        salt.CopyTo(preimage, identityBytes.Length);
        var expected = Convert.ToHexStringLower(SHA256.HashData(preimage));

        Assert.Equal(expected, provider.ComputeAnonymousDailyHash(ip, ua, day));
    }

    [Fact]
    public void DailySalt_StableWithinDay_DifferentAcrossDays()
    {
        var saltProvider = new DailySaltProvider(
            new FakeSecretReader(new Dictionary<string, byte[]> { ["analytics_daily_salt_seed"] = Seed }),
            IdentityTestOptions.Create(),
            NullLogger<DailySaltProvider>.Instance);

        var day = new DateOnly(2026, 7, 15);
        var s1 = saltProvider.GetDailySalt(day);
        var s2 = saltProvider.GetDailySalt(day);
        var s3 = saltProvider.GetDailySalt(day.AddDays(1));

        Assert.Equal(s1, s2); // stable within the day
        Assert.NotEqual(s1, s3); // rotates daily
    }

    [Fact]
    public void DailySalt_MissingSeed_WithoutDevFallback_ThrowsAtConstruction()
    {
        Assert.Throws<InvalidOperationException>(() => new DailySaltProvider(
            new MissingSecretReader(),
            IdentityTestOptions.Create(allowDevFallback: false),
            NullLogger<DailySaltProvider>.Instance));
    }

    [Fact]
    public void DailySalt_MissingSeed_WithDevFallback_StillProducesStableSalts()
    {
        var saltProvider = new DailySaltProvider(
            new MissingSecretReader(),
            IdentityTestOptions.Create(allowDevFallback: true),
            NullLogger<DailySaltProvider>.Instance);

        var day = new DateOnly(2026, 7, 15);
        Assert.Equal(saltProvider.GetDailySalt(day), saltProvider.GetDailySalt(day));
        Assert.NotEqual(saltProvider.GetDailySalt(day), saltProvider.GetDailySalt(day.AddDays(1)));
    }

    private sealed class MissingSecretReader : ISecretReader
    {
        public byte[]? TryReadSecret(string name) => null;
    }
}
