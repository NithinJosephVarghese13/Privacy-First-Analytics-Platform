using System.Text.Json.Serialization;

namespace PrivacyAnalytics.Contracts;

/// <summary>
/// Inbound payload for <c>POST /api/v1/track</c>. The <see cref="Url"/> is the page URL reported by
/// the client script; the URL-scrubbing middleware strips its query string before this object is
/// ever bound or inspected by later stages (the most common PII leak vector is query-string
/// parameters such as <c>?email=</c>, <c>?token=</c>, UTM values, etc.).
/// </summary>
public sealed class TrackRequest
{
    /// <summary>
    /// The full page URL as captured by the client script. Query-string parameters are removed by
    /// <c>UrlScrubbingMiddleware</c> prior to model binding, so handlers only ever see the scheme,
    /// host and path. Path-segment-embedded PII is explicitly out of scope for v1 (see Risk Register).
    /// </summary>
    [JsonPropertyName("url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; set; }

    /// <summary>
    /// The referral source reported by the client (e.g. document.referrer). Treated as opaque by the
    /// ingestion stage; downstream hashing applies the same daily-salt / HMAC scheme as for identity.
    /// </summary>
    [JsonPropertyName("referralSource")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReferralSource { get; set; }

    /// <summary>
    /// The event type, e.g. <c>pageview</c>, <c>click</c>, <c>conversion</c>. Free-form but expected
    /// to be constrained to an enumerable set per tenant configuration post-MVP.
    /// </summary>
    [JsonPropertyName("eventType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EventType { get; set; }
}
