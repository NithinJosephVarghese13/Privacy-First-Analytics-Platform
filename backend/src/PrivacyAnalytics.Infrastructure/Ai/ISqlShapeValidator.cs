namespace PrivacyAnalytics.Infrastructure.Ai;

public interface ISqlShapeValidator
{
    (bool IsValid, string? ErrorMessage) Validate(string sql);
}
