using System.Text.RegularExpressions;

namespace PrivacyAnalytics.Infrastructure.Ai;

public class SqlShapeValidator : ISqlShapeValidator
{
    // Defense-in-depth only. This shape validation is NOT the primary security boundary.
    // Tenant isolation is enforced structurally by PostgreSQL Row-Level Security.
    private static readonly Regex ForbiddenKeywordsRegex = new(
        @"\b(INSERT|UPDATE|DELETE|DROP|ALTER|GRANT|COPY)\b|pg_",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public (bool IsValid, string? ErrorMessage) Validate(string sql)
    {
        // Defense-in-depth only. This shape validation is NOT the primary security boundary.
        // Tenant isolation is enforced structurally by PostgreSQL Row-Level Security.
        if (string.IsNullOrWhiteSpace(sql))
        {
            return (false, "SQL string cannot be empty.");
        }

        var trimmed = sql.Trim();

        // Must start with SELECT or WITH (case-insensitive)
        if (!trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
        {
            return (false, "Query must start with SELECT or WITH.");
        }

        // Semicolon check: Reject if a semicolon exists anywhere except at the very end (single optional trailing position)
        var semicolonIndex = trimmed.IndexOf(';');
        if (semicolonIndex >= 0 && semicolonIndex != trimmed.Length - 1)
        {
            return (false, "Semicolons are only permitted at the very end of the SQL query string.");
        }

        // Forbidden keywords / constructs check
        if (ForbiddenKeywordsRegex.IsMatch(trimmed))
        {
            var match = ForbiddenKeywordsRegex.Match(trimmed).Value;
            return (false, $"Query contains forbidden keyword or construct: '{match}'. Only read-only queries are permitted.");
        }

        return (true, null);
    }
}
