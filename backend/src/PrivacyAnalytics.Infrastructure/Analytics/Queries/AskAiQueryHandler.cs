using MediatR;
using PrivacyAnalytics.Contracts.Analytics;
using PrivacyAnalytics.Infrastructure.Ai;
using PrivacyAnalytics.Infrastructure.Data;

namespace PrivacyAnalytics.Infrastructure.Analytics.Queries;

public class AskAiQueryHandler : IRequestHandler<AskAiQuery, AskAiResponse>
{
    private readonly IAiTextToSqlService _aiTextToSqlService;
    private readonly IDapperQueryHelper _dapperQueryHelper;

    public AskAiQueryHandler(
        IAiTextToSqlService aiTextToSqlService,
        IDapperQueryHelper dapperQueryHelper)
    {
        _aiTextToSqlService = aiTextToSqlService;
        _dapperQueryHelper = dapperQueryHelper;
    }

    public async Task<AskAiResponse> Handle(AskAiQuery request, CancellationToken cancellationToken)
    {
        // 1. Generate & validate SQL from prompt via AI service
        var (sql, isCached) = await _aiTextToSqlService.GenerateSqlAsync(request.Prompt, request.UseCache, cancellationToken);

        // 2. Execute query via IDapperQueryHelper (which executes SET LOCAL app.current_tenant_id = @tenantId)
        // This guarantees that PostgreSQL RLS tenant isolation is structurally enforced on every query!
        var rawRows = await _dapperQueryHelper.QueryAsync<dynamic>(sql, null, cancellationToken);

        var dataList = new List<IDictionary<string, object?>>();
        foreach (var row in rawRows)
        {
            if (row is IDictionary<string, object?> dictRow)
            {
                dataList.Add(dictRow);
            }
            else if (row is System.Dynamic.ExpandoObject expando)
            {
                dataList.Add((IDictionary<string, object?>)expando);
            }
            else
            {
                var dict = new Dictionary<string, object?>();
                foreach (var prop in ((object)row).GetType().GetProperties())
                {
                    dict[prop.Name] = prop.GetValue(row);
                }
                dataList.Add(dict);
            }
        }

        return new AskAiResponse(
            Prompt: request.Prompt,
            GeneratedSql: sql,
            IsCachedResponse: isCached,
            Data: dataList);
    }
}
