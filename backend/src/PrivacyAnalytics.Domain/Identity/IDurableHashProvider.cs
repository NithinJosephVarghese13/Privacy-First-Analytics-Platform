namespace PrivacyAnalytics.Domain.Identity;

/// <summary>
/// Produces the Tier 2 pseudonym: a stable, tenant-scoped HMAC of the authenticated user's stable
/// identifier. <b>Only</b> computed when the request indicates an authenticated, opted-in user.
/// The HMAC signing key is intentionally NOT modelled here — it is an infrastructure concern that
/// must be sourced out-of-band (a Docker secret / managed secrets store), never as a DB column or
/// an appsettings value holding the literal key (see FR-2.1). A key sitting next to the data it
/// protects means one DB compromise re-identifies every authenticated user retroactively.
/// </summary>
public interface IDurableHashProvider
{
    /// <summary>
    /// Returns the lowercase-hex tenant-scoped HMAC for the supplied organization and user
    /// identifier. The organization id is folded into the message so the same user mapped into
    /// two tenants gets two independent durable hashes — tenant isolation at the identity layer.
    /// </summary>
    string ComputeDurableHash(Guid organizationId, string userId);
}
