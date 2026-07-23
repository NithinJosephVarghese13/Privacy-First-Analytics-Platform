using MediatR;
using PrivacyAnalytics.Contracts.Analytics;

namespace PrivacyAnalytics.Infrastructure.Analytics.Queries;

public record AskAiQuery(string Prompt, bool UseCache = false) : IRequest<AskAiResponse>;
