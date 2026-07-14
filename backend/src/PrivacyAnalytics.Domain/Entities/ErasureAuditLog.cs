namespace PrivacyAnalytics.Domain.Entities;

public class ErasureAuditLog
{
    public Guid AuditId { get; set; }
    public Guid OrganizationId { get; set; }
    public string PurgedIdentifierHash { get; set; } = string.Empty;
    public string RequestedBy { get; set; } = string.Empty;
    public DateTimeOffset PurgedAtUtc { get; set; }
    public int RecordsAffected { get; set; }
}
