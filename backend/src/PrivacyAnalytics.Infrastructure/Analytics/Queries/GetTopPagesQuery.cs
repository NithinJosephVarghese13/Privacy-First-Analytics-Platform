using MediatR;

namespace PrivacyAnalytics.Infrastructure.Analytics.Queries;

public record TopPageDto(string Path, long Pageviews);

public record GetTopPagesQuery(DateTimeOffset StartDate, DateTimeOffset EndDate) : IRequest<IEnumerable<TopPageDto>>;
