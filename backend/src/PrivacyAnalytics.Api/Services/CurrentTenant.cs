using System;
using Microsoft.AspNetCore.Http;
using PrivacyAnalytics.Domain.Identity;

namespace PrivacyAnalytics.Api.Services;

public class CurrentTenant : ICurrentTenant
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentTenant(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public Guid? TenantId
    {
        get
        {
            var user = _httpContextAccessor.HttpContext?.User;
            if (user == null || !user.Identity?.IsAuthenticated == true)
            {
                return null;
            }

            // Keycloak exposes the tenant_id directly in the JWT since we disabled inbound claim mapping
            var tenantClaim = user.FindFirst("tenant_id");
            if (tenantClaim != null && Guid.TryParse(tenantClaim.Value, out var tenantId))
            {
                return tenantId;
            }

            return null;
        }
    }
}
