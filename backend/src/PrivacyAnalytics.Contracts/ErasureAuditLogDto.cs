namespace PrivacyAnalytics.Contracts;

public record ErasureAuditLogDto(
    Guid AuditId,
    Guid OrganizationId,
    string PurgedIdentifierHash,
    string RequestedBy,
    DateTimeOffset PurgedAtUtc,
    int RecordsAffected
);
