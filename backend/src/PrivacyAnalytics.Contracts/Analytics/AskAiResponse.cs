using System.Collections.Generic;

namespace PrivacyAnalytics.Contracts.Analytics;

public record AskAiResponse(
    string Prompt,
    string GeneratedSql,
    bool IsCachedResponse,
    IEnumerable<IDictionary<string, object?>> Data);
