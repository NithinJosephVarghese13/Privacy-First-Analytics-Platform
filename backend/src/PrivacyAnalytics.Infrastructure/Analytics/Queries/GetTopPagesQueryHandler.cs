using MediatR;
using PrivacyAnalytics.Infrastructure.Analytics.Queries;
using PrivacyAnalytics.Infrastructure.Data;

namespace PrivacyAnalytics.Infrastructure.Analytics.Queries;

public class GetTopPagesQueryHandler(IDapperQueryHelper dapperQueryHelper)
    : IRequestHandler<GetTopPagesQuery, IEnumerable<TopPageDto>>
{
    public async Task<IEnumerable<TopPageDto>> Handle(GetTopPagesQuery request, CancellationToken cancellationToken)
    {
        var sql = @"
            SELECT path AS Path, COUNT(*) AS Pageviews
            FROM analytics_events
            WHERE timestamp >= @StartDate AND timestamp <= @EndDate
            GROUP BY path
            ORDER BY Pageviews DESC
            LIMIT 5";

        return await dapperQueryHelper.QueryAsync<TopPageDto>(sql, new { request.StartDate, request.EndDate }, cancellationToken);
    }
}
