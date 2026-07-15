namespace PrivacyAnalytics.Domain.Messaging;

public interface IMessagePublisher
{
    Task PublishAsync<T>(T message, CancellationToken cancellationToken = default);
}
