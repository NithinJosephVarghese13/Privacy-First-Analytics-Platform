using System.Text.Json.Serialization;

namespace PrivacyAnalytics.Contracts;

/// <summary>
/// Integration event published to RabbitMQ when an analytics event is received and hashed.
/// This matches the domain AnalyticsEvent shape.
/// </summary>
public sealed class AnalyticsEventReceived
{
    [JsonPropertyName("eventId")]
    public Guid EventId { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; }

    [JsonPropertyName("organizationId")]
    public Guid OrganizationId { get; set; }

    [JsonPropertyName("anonymousDailyHash")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AnonymousDailyHash { get; set; }

    [JsonPropertyName("durableHash")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DurableHash { get; set; }

    [JsonPropertyName("isAuthenticated")]
    public bool IsAuthenticated { get; set; }

    [JsonPropertyName("eventType")]
    public string EventType { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;
}
