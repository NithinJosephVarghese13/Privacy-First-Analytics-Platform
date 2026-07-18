namespace PrivacyAnalytics.Domain.Entities;

public class Organization
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid PublicWriteKey { get; set; } = Guid.NewGuid();
}
