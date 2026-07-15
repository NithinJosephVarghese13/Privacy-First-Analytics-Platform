using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PrivacyAnalytics.Infrastructure.Identity;
using Xunit;

namespace PrivacyAnalytics.UnitTests.Identity;

/// <summary>
/// Proves the Tier 2 durable-HMAC properties: deterministic for a given (org, user, key),
/// tenant-scoped (same user in two tenants hashes differently), and that the signing key is read
/// from the secret channel with fail-fast when absent and dev fallback otherwise.
/// </summary>
public sealed class DurableHashProviderTests
{
    private static readonly Guid OrgA = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid OrgB = Guid.Parse("00000000-0000-0000-0000-000000000002");

    private static readonly byte[] Key = Convert.FromHexString(
        "f7fad8058975349527df75ebdf0a3a9a5f10657ac3cb13c8dbf9eda80f255d92");

    private static DurableHashProvider CreateProvider(byte[]? key, bool allowDevFallback = false)
    {
        var secrets = new FakeSecretReader(
            new Dictionary<string, byte[]>
            {
                ["analytics_durable_hmac_key"] = key ?? Array.Empty<byte>()
            });
        var opts = IdentityTestOptions.Create(allowDevFallback: allowDevFallback);
        return new DurableHashProvider(
            key is null ? new MissingSecretReader() : secrets,
            opts,
            NullLogger<DurableHashProvider>.Instance);
    }

    [Fact]
    public void SameOrgAndUser_ProducesStableHash()
    {
        var provider = CreateProvider(Key);
        var h1 = provider.ComputeDurableHash(OrgA, "user-42");
        var h2 = provider.ComputeDurableHash(OrgA, "user-42");
        Assert.Equal(h1, h2);
    }

    [Fact]
    public void HashIsLowercaseHex_OfExpectedHmac()
    {
        var provider = CreateProvider(Key);
        var hash = provider.ComputeDurableHash(OrgA, "user-42");

        // Independently recompute HMAC-SHA256(key, "00000000000000000000000000000001:user-42").
        var message = Encoding.UTF8.GetBytes(OrgA.ToString("N") + ":user-42");
        var expected = Convert.ToHexStringLower(HMACSHA256.HashData(Key, message));
        Assert.Equal(expected, hash);
    }

    [Fact]
    public void SameUser_DifferentTenants_HashDifferently_TenantScopingHolds()
    {
        var provider = CreateProvider(Key);
        var hA = provider.ComputeDurableHash(OrgA, "user-42");
        var hB = provider.ComputeDurableHash(OrgB, "user-42");
        Assert.NotEqual(hA, hB);
    }

    [Fact]
    public void DifferentUsers_SameTenant_HashDifferently()
    {
        var provider = CreateProvider(Key);
        var h1 = provider.ComputeDurableHash(OrgA, "user-42");
        var h2 = provider.ComputeDurableHash(OrgA, "user-99");
        Assert.NotEqual(h1, h2);
    }

    [Fact]
    public void MissingSecret_WithDevFallbackAllowed_UsesDevKey_AndStillHashes()
    {
        // allowDevFallback=true and the secret reader returns null -> dev key path.
        var provider = new DurableHashProvider(
            new MissingSecretReader(),
            IdentityTestOptions.Create(allowDevFallback: true),
            NullLogger<DurableHashProvider>.Instance);

        var h1 = provider.ComputeDurableHash(OrgA, "user-42");
        var h2 = provider.ComputeDurableHash(OrgA, "user-42");
        Assert.Equal(h1, h2); // stable across calls
        Assert.NotEmpty(h1);
    }

    [Fact]
    public void MissingSecret_WithoutDevFallback_ThrowsAtConstruction()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new DurableHashProvider(
            new MissingSecretReader(),
            IdentityTestOptions.Create(allowDevFallback: false),
            NullLogger<DurableHashProvider>.Instance));

        Assert.Contains("analytics_durable_hmac_key", ex.Message);
        Assert.Contains("never", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class MissingSecretReader : ISecretReader
    {
        public byte[]? TryReadSecret(string name) => null;
    }
}
