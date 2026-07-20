using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using PrivacyAnalytics.Contracts;
using PrivacyAnalytics.Domain.Entities;
using PrivacyAnalytics.Infrastructure.Data;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace PrivacyAnalytics.Worker;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IConfiguration _configuration;
    private readonly IServiceScopeFactory _serviceScopeFactory;

    private const string ExchangeName = "analytics.events";
    private const string QueueName = "analytics.events.queue";
    private const string RoutingKey = "event.received";

    private const string DlxExchangeName = "analytics.events.dlx";
    private const string DlqQueueName = "analytics.events.dlq";

    private const int BatchSize = 500;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(3);

    private record struct QueuedEvent(ulong DeliveryTag, byte[] Body);

    public Worker(ILogger<Worker> logger, IConfiguration configuration, IServiceScopeFactory serviceScopeFactory)
    {
        _logger = logger;
        _configuration = configuration;
        _serviceScopeFactory = serviceScopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connectionString = _configuration.GetConnectionString("RabbitMQ");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _logger.LogError("RabbitMQ connection string is missing.");
            return;
        }

        var factory = new ConnectionFactory
        {
            Uri = new Uri(connectionString),
            ClientProvidedName = "PrivacyAnalytics.Worker"
        };

        using var connection = await factory.CreateConnectionAsync(stoppingToken);
        using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);

        await channel.ExchangeDeclareAsync(
            exchange: ExchangeName,
            type: ExchangeType.Topic,
            durable: true,
            cancellationToken: stoppingToken);

        await channel.ExchangeDeclareAsync(
            exchange: DlxExchangeName,
            type: ExchangeType.Direct,
            durable: true,
            cancellationToken: stoppingToken);

        await channel.QueueDeclareAsync(
            queue: DlqQueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                { "x-queue-type", "quorum" }
            },
            cancellationToken: stoppingToken);

        await channel.QueueBindAsync(
            queue: DlqQueueName,
            exchange: DlxExchangeName,
            routingKey: "", // Direct exchange, route all dead-lettered messages here
            cancellationToken: stoppingToken);

        await channel.QueueDeclareAsync(
            queue: QueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                { "x-queue-type", "quorum" },
                { "x-dead-letter-exchange", DlxExchangeName },
                { "x-delivery-limit", 5 }
            },
            cancellationToken: stoppingToken);

        await channel.QueueBindAsync(
            queue: QueueName,
            exchange: ExchangeName,
            routingKey: RoutingKey,
            cancellationToken: stoppingToken);

        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: BatchSize, global: false, cancellationToken: stoppingToken);

        var messageChannel = Channel.CreateBounded<QueuedEvent>(new BoundedChannelOptions(BatchSize * 2)
        {
            FullMode = BoundedChannelFullMode.Wait
        });

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (sender, ea) =>
        {
            var bodyCopy = ea.Body.ToArray();
            await messageChannel.Writer.WriteAsync(new QueuedEvent(ea.DeliveryTag, bodyCopy), stoppingToken);
        };

        await channel.BasicConsumeAsync(QueueName, autoAck: false, consumer: consumer, cancellationToken: stoppingToken);

        _logger.LogInformation("Worker started consuming from RabbitMQ.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var batch = new List<QueuedEvent>(BatchSize);

            using var batchCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            batchCts.CancelAfter(FlushInterval);

            try
            {
                while (batch.Count < BatchSize)
                {
                    var msg = await messageChannel.Reader.ReadAsync(batchCts.Token);
                    batch.Add(msg);
                }
            }
            catch (OperationCanceledException)
            {
                // Timeout reached or application stopping, proceed with current batch
            }

            if (batch.Count > 0)
            {
                await ProcessBatchAsync(channel, batch, stoppingToken);
            }
        }
    }

    private async Task ProcessBatchAsync(IChannel channel, List<QueuedEvent> batch, CancellationToken stoppingToken)
    {
        var maxDeliveryTag = batch.Max(b => b.DeliveryTag);

        try
        {
            var eventsByTenant = new Dictionary<Guid, List<AnalyticsEvent>>();

            foreach (var ea in batch)
            {
                try
                {
                    var message = JsonSerializer.Deserialize<AnalyticsEventReceived>(Encoding.UTF8.GetString(ea.Body));

                    if (message == null) continue;

                    // Enforce Two-Tier Identity: AnonymousDailyHash is forced null whenever IsAuthenticated = true
                    if (message.IsAuthenticated)
                    {
                        message.AnonymousDailyHash = null;
                    }

                    var entity = new AnalyticsEvent
                    {
                        EventId = message.EventId,
                        OrganizationId = message.OrganizationId,
                        AnonymousDailyHash = message.AnonymousDailyHash,
                        DurableHash = message.DurableHash,
                        IsAuthenticated = message.IsAuthenticated,
                        EventType = message.EventType ?? string.Empty,
                        Path = message.Path ?? string.Empty,
                        Timestamp = message.Timestamp
                    };

                    if (!eventsByTenant.TryGetValue(entity.OrganizationId, out var tenantEvents))
                    {
                        tenantEvents = new List<AnalyticsEvent>();
                        eventsByTenant[entity.OrganizationId] = tenantEvents;
                    }

                    tenantEvents.Add(entity);
                }
                catch (JsonException ex)
                {
                    var bodyStr = string.Empty;
                    try
                    {
                        bodyStr = Encoding.UTF8.GetString(ea.Body);
                    }
                    catch {}
                    _logger.LogWarning(ex, "Failed to deserialize message body. DeliveryTag: {DeliveryTag}. Content: {Content}", ea.DeliveryTag, bodyStr);
                    // Invalid messages are ignored to prevent poison message loops.
                }
            }

            if (eventsByTenant.Count > 0)
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<AnalyticsDbContext>();

                foreach (var group in eventsByTenant)
                {
                    var organizationId = group.Key;
                    var events = group.Value;

                    using var transaction = await dbContext.Database.BeginTransactionAsync(stoppingToken);

                    // Set RLS session context for the transaction
                    var rlsSql = $"SET LOCAL app.current_tenant_id = '{organizationId}'";
                    await dbContext.Database.ExecuteSqlRawAsync(rlsSql, stoppingToken);

                    // Idempotent batch insert using raw SQL (EF Core equivalent for ON CONFLICT DO NOTHING)
                    if (events.Count > 0)
                    {
                        var sqlBuilder = new StringBuilder();
                        sqlBuilder.Append("INSERT INTO analytics_events (event_id, timestamp, organization_id, anonymous_daily_hash, durable_hash, is_authenticated, event_type, path) VALUES ");
                        var parameters = new List<object?>();
                        for (int i = 0; i < events.Count; i++)
                        {
                            var e = events[i];
                            sqlBuilder.Append($"({{{i * 8 + 0}}}, {{{i * 8 + 1}}}, {{{i * 8 + 2}}}, {{{i * 8 + 3}}}, {{{i * 8 + 4}}}, {{{i * 8 + 5}}}, {{{i * 8 + 6}}}, {{{i * 8 + 7}}})");
                            if (i < events.Count - 1) sqlBuilder.Append(", ");
                            parameters.Add(e.EventId);
                            parameters.Add(e.Timestamp);
                            parameters.Add(e.OrganizationId);
                            parameters.Add(e.AnonymousDailyHash);
                            parameters.Add(e.DurableHash);
                            parameters.Add(e.IsAuthenticated);
                            parameters.Add(e.EventType);
                            parameters.Add(e.Path);
                        }
                        sqlBuilder.Append(" ON CONFLICT (event_id, timestamp) DO NOTHING;");
                        await dbContext.Database.ExecuteSqlRawAsync(sqlBuilder.ToString(), parameters.Cast<object>().ToArray(), stoppingToken);
                    }
                    
                    await transaction.CommitAsync(stoppingToken);
                    dbContext.ChangeTracker.Clear();
                }
            }

            // Ack batch
            await channel.BasicAckAsync(maxDeliveryTag, multiple: true, cancellationToken: stoppingToken);
            _logger.LogInformation("Processed and acked {Count} events.", batch.Count);
        }
        catch (DbUpdateException dbEx)
        {
            _logger.LogError(dbEx, "Database constraint violation or permanent failure. Nacking {Count} messages without requeue (to DLX).", batch.Count);
            // Permanent failure (e.g. constraint violation). Nack without requeue to dead-letter immediately.
            await channel.BasicNackAsync(maxDeliveryTag, multiple: true, requeue: false, cancellationToken: stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process batch. Nacking {Count} messages. (Quorum queue delivery-limit will dead-letter after 5 attempts)", batch.Count);
            
            // Transient failure. Requeue. Once x-delivery-limit (5) is reached, RabbitMQ will route to DLX.
            await channel.BasicNackAsync(maxDeliveryTag, multiple: true, requeue: true, cancellationToken: stoppingToken);
        }
    }
}
