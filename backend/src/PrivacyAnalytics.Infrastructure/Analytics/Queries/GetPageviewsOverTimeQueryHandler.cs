using MediatR;
using PrivacyAnalytics.Infrastructure.Analytics.Queries;
using PrivacyAnalytics.Infrastructure.Data;

namespace PrivacyAnalytics.Infrastructure.Analytics.Queries;

public class GetPageviewsOverTimeQueryHandler(IDapperQueryHelper dapperQueryHelper)
    : IRequestHandler<GetPageviewsOverTimeQuery, IEnumerable<PageviewsOverTimeDto>>
{
    public async Task<IEnumerable<PageviewsOverTimeDto>> Handle(GetPageviewsOverTimeQuery request, CancellationToken cancellationToken)
    {
        var sql = @"
            SELECT date_trunc('day', timestamp)::date AS Date, COUNT(*) AS Pageviews
            FROM analytics_events
            WHERE timestamp >= @StartDate AND timestamp <= @EndDate
            GROUP BY date_trunc('day', timestamp)::date
            ORDER BY Date ASC";

        return await dapperQueryHelper.QueryAsync<PageviewsOverTimeDto>(sql, new { request.StartDate, request.EndDate }, cancellationToken);
    }
}
