namespace PrivacyAnalytics.Domain.Entities;

public class AnalyticsEvent
{
    public Guid EventId { get; set; }
    public Guid OrganizationId { get; set; }
    public string? AnonymousDailyHash { get; set; }
    public string? DurableHash { get; set; }
    public bool IsAuthenticated { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
}
