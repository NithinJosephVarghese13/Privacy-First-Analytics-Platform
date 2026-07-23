namespace PrivacyAnalytics.Contracts.Analytics;

public record AskAiRequest(string Prompt, bool UseCache = false);
