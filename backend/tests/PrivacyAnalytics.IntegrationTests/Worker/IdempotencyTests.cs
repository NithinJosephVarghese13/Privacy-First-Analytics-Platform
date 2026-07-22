using System.Text;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PrivacyAnalytics.Domain.Entities;
using PrivacyAnalytics.Infrastructure.Data;
using PrivacyAnalytics.IntegrationTests.Rls;
using Xunit;

namespace PrivacyAnalytics.IntegrationTests.Worker;

[Collection("RlsTests")]
public class IdempotencyTests
{
    [PostgresRequiredFact]
    public async Task RedeliveredBatch_WithMidBatchFailure_DoesNotCreateDuplicates()
    {
        // Arrange
        var dbName = "test_idempotency_" + Guid.NewGuid().ToString("N");
        await using var harness = await TestDatabaseHarness.CreateAsync(
            TestDatabaseHarness.ResolveMaintenanceConnectionString(), dbName);
        Assert.NotNull(harness);

        var tenant1 = Guid.NewGuid();
        var tenant2 = Guid.NewGuid();

        await using (var adminConnection = new Npgsql.NpgsqlConnection(harness.AdminConnectionString))
        {
            await adminConnection.OpenAsync();
            await adminConnection.ExecuteAsync(
                "INSERT INTO organizations (org_id, name, public_write_key) VALUES (@Id1, 'Tenant1', @Id1), (@Id2, 'Tenant2', @Id2)",
                new { Id1 = tenant1, Id2 = tenant2 });
        }

        var options = new DbContextOptionsBuilder<AnalyticsDbContext>()
            .UseNpgsql(harness.AppConnectionString)
            .Options;
        
        await using var dbContext = new AnalyticsDbContext(options);

        // Simulate the worker's grouped batch
        var eventsTenant1 = new List<AnalyticsEvent>
        {
            new AnalyticsEvent { EventId = Guid.NewGuid(), OrganizationId = tenant1, EventType = "pageview", Path = "/", Timestamp = DateTimeOffset.UtcNow }
        };
        var eventsTenant2 = new List<AnalyticsEvent>
        {
            new AnalyticsEvent { EventId = Guid.NewGuid(), OrganizationId = tenant2, EventType = "pageview", Path = "/about", Timestamp = DateTimeOffset.UtcNow }
        };

        var eventsByTenant = new Dictionary<Guid, List<AnalyticsEvent>>
        {
            { tenant1, eventsTenant1 },
            { tenant2, eventsTenant2 }
        };

        // Act 1: Simulate group 1 committing, then a crash before group 2
        var processedGroups = 0;
        try
        {
            foreach (var group in eventsByTenant)
            {
                await ProcessGroupAsync(dbContext, group.Key, group.Value);
                processedGroups++;

                if (processedGroups == 1)
                {
                    throw new ApplicationException("Mid-batch simulated failure!");
                }
            }
        }
        catch (ApplicationException)
        {
            // Expected
        }

        Assert.Equal(1, processedGroups);

        // Verify only tenant 1 has data
        await using (var adminConnection = new Npgsql.NpgsqlConnection(harness.AdminConnectionString))
        {
            var count1 = await adminConnection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM analytics_events");
            Assert.Equal(1, count1);
        }

        // Act 2: Redeliver the exact same batch
        foreach (var group in eventsByTenant)
        {
            await ProcessGroupAsync(dbContext, group.Key, group.Value);
        }

        // Assert: no duplicates, total count should be 2 (one for each event)
        await using (var adminConnection = new Npgsql.NpgsqlConnection(harness.AdminConnectionString))
        {
            var totalCount = await adminConnection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM analytics_events");
            Assert.Equal(2, totalCount); // The two exact rows, no duplicates
        }
    }

    private async Task ProcessGroupAsync(AnalyticsDbContext dbContext, Guid organizationId, List<AnalyticsEvent> events)
    {
        using var transaction = await dbContext.Database.BeginTransactionAsync();

        var rlsSql = $"SET LOCAL app.current_tenant_id = '{organizationId}'";
        await dbContext.Database.ExecuteSqlRawAsync(rlsSql);

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
            await dbContext.Database.ExecuteSqlRawAsync(sqlBuilder.ToString(), parameters.Cast<object>().ToArray());
        }
        
        await transaction.CommitAsync();
    }
}
