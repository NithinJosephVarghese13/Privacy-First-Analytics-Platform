using System.Text.Json.Serialization;

namespace PrivacyAnalytics.Contracts;

/// <summary>
/// Integration event published to RabbitMQ when an analytics event is received and hashed.
/// This matches the domain AnalyticsEvent shape minus the database identifier and timestamp.
/// </summary>
public sealed class AnalyticsEventReceived
{
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
