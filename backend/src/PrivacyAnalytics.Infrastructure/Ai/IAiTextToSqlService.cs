namespace PrivacyAnalytics.Infrastructure.Ai;

public interface IAiTextToSqlService
{
    Task<(string Sql, bool IsCached)> GenerateSqlAsync(string prompt, bool useCache = false, CancellationToken cancellationToken = default);
}
