using MediatR;

namespace PrivacyAnalytics.Infrastructure.Analytics.Queries;

public record PageviewsOverTimeDto(DateOnly Date, long Pageviews);

public record GetPageviewsOverTimeQuery(DateTimeOffset StartDate, DateTimeOffset EndDate) : IRequest<IEnumerable<PageviewsOverTimeDto>>;
