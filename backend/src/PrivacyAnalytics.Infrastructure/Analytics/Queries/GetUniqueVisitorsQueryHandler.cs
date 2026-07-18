using MediatR;
using PrivacyAnalytics.Infrastructure.Analytics.Queries;
using PrivacyAnalytics.Infrastructure.Data;

namespace PrivacyAnalytics.Infrastructure.Analytics.Queries;

public class GetUniqueVisitorsQueryHandler(IDapperQueryHelper dapperQueryHelper)
    : IRequestHandler<GetUniqueVisitorsQuery, UniqueVisitorsDto>
{
    public async Task<UniqueVisitorsDto> Handle(GetUniqueVisitorsQuery request, CancellationToken cancellationToken)
    {
        var sql = @"
            SELECT 
                COUNT(DISTINCT durable_hash) AS ExactTier2Uniques,
                COALESCE(ROUND(hll_cardinality(hll_add_agg(hll_hash_text(anonymous_daily_hash::text))))::bigint, 0) AS EstimatedTier1Uniques
            FROM analytics_events
            WHERE timestamp >= @StartDate AND timestamp <= @EndDate";

        var result = await dapperQueryHelper.QueryFirstOrDefaultAsync<UniqueVisitorsDto>(sql, new { request.StartDate, request.EndDate }, cancellationToken);
        
        return result ?? new UniqueVisitorsDto(0, 0);
    }
}
