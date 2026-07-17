using System;

namespace PrivacyAnalytics.Domain.Identity;

/// <summary>
/// Provides access to the current tenant context.
/// Missing or invalid tenant context returns null.
/// </summary>
public interface ICurrentTenant
{
    /// <summary>
    /// Gets the current tenant ID extracted from the authenticated context (e.g., JWT).
    /// If the context is missing, invalid, or anonymous, this returns null.
    /// Downstream layers (like Dapper queries) must handle null by enforcing zero-rows (fail-closed).
    /// </summary>
    Guid? TenantId { get; }
}
