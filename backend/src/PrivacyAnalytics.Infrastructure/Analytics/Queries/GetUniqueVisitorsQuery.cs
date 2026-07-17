using MediatR;

namespace PrivacyAnalytics.Infrastructure.Analytics.Queries;

public record UniqueVisitorsDto(long ExactTier2Uniques, long EstimatedTier1Uniques);

public record GetUniqueVisitorsQuery(DateTimeOffset StartDate, DateTimeOffset EndDate) : IRequest<UniqueVisitorsDto>;
