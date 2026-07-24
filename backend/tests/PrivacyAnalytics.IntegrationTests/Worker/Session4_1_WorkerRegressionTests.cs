using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using PrivacyAnalytics.Contracts;
using PrivacyAnalytics.Infrastructure.Data;
using PrivacyAnalytics.Infrastructure.Identity;
using PrivacyAnalytics.IntegrationTests.Rls;
using RabbitMQ.Client;
using Xunit;

namespace PrivacyAnalytics.IntegrationTests.Worker;

[Collection("NonParallelWorkerTests")]
public class Session4_1_WorkerRegressionTests
{
    private const string ExchangeName = "analytics.events";
    private const string QueueName = "analytics.events.queue";
    private const string RoutingKey = "event.received";

    [PostgresRequiredFact]
    public async Task Publish500Messages_AllLandInAnalyticsEvents()
    {
        var rabbitMqUri = "amqp://analytics:analytics_dev@localhost:5672/";

        // 1. Arrange DB
        var dbName = "test_worker_500_" + Guid.NewGuid().ToString("N");
        await using var harness = await TestDatabaseHarness.CreateAsync(
            TestDatabaseHarness.ResolveMaintenanceConnectionString(), dbName);

        Assert.NotNull(harness);

        var tenantId = Guid.NewGuid();
        var writeKey = Guid.NewGuid();

        await using (var adminConn = new NpgsqlConnection(harness.AdminConnectionString))
        {
            await adminConn.OpenAsync();
            await adminConn.ExecuteAsync(
                "INSERT INTO organizations (org_id, name, public_write_key) VALUES (@OrgId, 'Test Tenant', @WriteKey)",
                new { OrgId = tenantId, WriteKey = writeKey });
        }

        // Clean queue before starting test
        var factory = new ConnectionFactory { Uri = new Uri(rabbitMqUri) };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();

        await channel.ExchangeDeclareAsync(ExchangeName, ExchangeType.Topic, durable: true);
        await channel.QueueDeclareAsync(QueueName, durable: true, exclusive: false, autoDelete: false, arguments: new Dictionary<string, object?>
        {
            { "x-queue-type", "quorum" },
            { "x-dead-letter-exchange", "analytics.events.dlx" },
            { "x-delivery-limit", 5 }
        });
        await channel.QueueBindAsync(QueueName, ExchangeName, RoutingKey);
        await channel.QueuePurgeAsync(QueueName);

        var messageCount = 500;
        for (int i = 0; i < messageCount; i++)
        {
            var eventMessage = new AnalyticsEventReceived
            {
                EventId = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow.AddSeconds(-i),
                OrganizationId = tenantId,
                AnonymousDailyHash = $"anon_hash_{i}",
                DurableHash = null,
                IsAuthenticated = false,
                EventType = "pageview",
                Path = $"/page/{i}"
            };

            var json = JsonSerializer.Serialize(eventMessage);
            var body = Encoding.UTF8.GetBytes(json);
            var props = new BasicProperties { Persistent = true, ContentType = "application/json" };

            await channel.BasicPublishAsync(
                exchange: ExchangeName,
                routingKey: RoutingKey,
                mandatory: false,
                basicProperties: props,
                body: body);
        }

        // 3. Start Worker Host
        using var workerHost = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(config =>
            {
                config.AddInMemoryCollection(new[]
                {
                    new KeyValuePair<string, string?>("ConnectionStrings:AnalyticsDb", harness.AppConnectionString),
                    new KeyValuePair<string, string?>("ConnectionStrings:RabbitMQ", rabbitMqUri),
                    new KeyValuePair<string, string?>("Identity:AllowUnmanagedDevSecrets", "true")
                });
            })
            .ConfigureServices((hostContext, services) =>
            {
                services.AddIdentityHashing(hostContext.Configuration);
                services.AddDbContext<AnalyticsDbContext>(options =>
                    options.UseNpgsql(harness.AppConnectionString));
                services.AddSingleton<PrivacyAnalytics.Worker.WorkerHealthState>();
                services.AddHostedService<PrivacyAnalytics.Worker.Worker>();
            })
            .Build();

        await workerHost.StartAsync();

        // 4. Poll until all 500 messages land in analytics_events
        var startTime = DateTime.UtcNow;
        var totalPersisted = 0;

        await using (var adminConn = new NpgsqlConnection(harness.AdminConnectionString))
        {
            await adminConn.OpenAsync();
            while (DateTime.UtcNow - startTime < TimeSpan.FromSeconds(20))
            {
                totalPersisted = await adminConn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM analytics_events");
                if (totalPersisted == messageCount)
                {
                    break;
                }
                await Task.Delay(500);
            }
        }

        await workerHost.StopAsync();

        Assert.Equal(500, totalPersisted);
    }

    [PostgresRequiredFact]
    public async Task MidBatchPostgresFailure_RequeuesMessagesWithoutLoss()
    {
        var rabbitMqUri = "amqp://analytics:analytics_dev@localhost:5672/";
        var factory = new ConnectionFactory { Uri = new Uri(rabbitMqUri) };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();

        // Purge queue before test
        await channel.QueuePurgeAsync(QueueName);

        var tenantId = Guid.NewGuid();
        var messageCount = 10;

        for (int i = 0; i < messageCount; i++)
        {
            var eventMessage = new AnalyticsEventReceived
            {
                EventId = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow,
                OrganizationId = tenantId,
                AnonymousDailyHash = $"anon_hash_fail_{i}",
                DurableHash = null,
                IsAuthenticated = false,
                EventType = "pageview",
                Path = $"/fail-test/{i}"
            };

            var json = JsonSerializer.Serialize(eventMessage);
            var body = Encoding.UTF8.GetBytes(json);
            var props = new BasicProperties { Persistent = true, ContentType = "application/json" };

            await channel.BasicPublishAsync(
                exchange: ExchangeName,
                routingKey: RoutingKey,
                mandatory: false,
                basicProperties: props,
                body: body);
        }

        // Configure worker with an unreachable Postgres connection string to simulate DB outage
        var badDbConnectionString = "Host=localhost;Port=59999;Database=analytics_invalid;Username=invalid;Password=invalid;Timeout=1;";

        using var workerHost = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(config =>
            {
                config.AddInMemoryCollection(new[]
                {
                    new KeyValuePair<string, string?>("ConnectionStrings:AnalyticsDb", badDbConnectionString),
                    new KeyValuePair<string, string?>("ConnectionStrings:RabbitMQ", rabbitMqUri),
                    new KeyValuePair<string, string?>("Identity:AllowUnmanagedDevSecrets", "true")
                });
            })
            .ConfigureServices((hostContext, services) =>
            {
                services.AddIdentityHashing(hostContext.Configuration);
                services.AddDbContext<AnalyticsDbContext>(options =>
                    options.UseNpgsql(badDbConnectionString));
                services.AddSingleton<PrivacyAnalytics.Worker.WorkerHealthState>();
                services.AddHostedService<PrivacyAnalytics.Worker.Worker>();
            })
            .Build();

        await workerHost.StartAsync();

        // Allow worker to attempt processing and trigger Nack with requeue
        await Task.Delay(4000);

        await workerHost.StopAsync();

        // Give RabbitMQ broker 1 second to transition unacked messages back to ready state upon consumer disconnect
        await Task.Delay(1000);

        // Check RabbitMQ queue message count: messages must be requeued, not lost/dropped
        var queueDeclare = await channel.QueueDeclarePassiveAsync(QueueName);
        Assert.True(queueDeclare.MessageCount > 0, $"Messages should be requeued in RabbitMQ upon Postgres failure rather than dropped. Actual MessageCount: {queueDeclare.MessageCount}");
    }
}
