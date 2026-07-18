using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Npgsql;
using PrivacyAnalytics.Contracts;
using PrivacyAnalytics.Domain.Identity;
using PrivacyAnalytics.Infrastructure.Analytics.Queries;
using PrivacyAnalytics.Infrastructure.Data;
using PrivacyAnalytics.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using PrivacyAnalytics.IntegrationTests.Rls;
using Xunit;

namespace PrivacyAnalytics.IntegrationTests;

[Collection("RlsTests")]
public class EndToEndPipelineTests : IAsyncLifetime
{
    private TestDatabaseHarness _harness = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private IHost _workerHost = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var maintenanceConnString = TestDatabaseHarness.ResolveMaintenanceConnectionString();
        var harness = await TestDatabaseHarness.CreateAsync(
            maintenanceConnString,
            $"analytics_e2e_{Guid.NewGuid():N}");

        if (harness == null)
        {
            throw new InvalidOperationException("PostgreSQL test database could not be provisioned.");
        }

        _harness = harness;

        // Start API
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((context, config) =>
                {
                    config.AddInMemoryCollection(new[]
                    {
                        new KeyValuePair<string, string?>("ConnectionStrings:AnalyticsDb", _harness.AppConnectionString),
                        new KeyValuePair<string, string?>("ConnectionStrings:RabbitMQ", "amqp://analytics:analytics_dev@localhost:5672/"),
                        new KeyValuePair<string, string?>("Identity:AllowUnmanagedDevSecrets", "true")
                    });
                });
            });

        _client = _factory.CreateClient();

        // Start Worker
        _workerHost = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(config =>
            {
                config.AddInMemoryCollection(new[]
                {
                    new KeyValuePair<string, string?>("ConnectionStrings:AnalyticsDb", _harness.AppConnectionString),
                    new KeyValuePair<string, string?>("ConnectionStrings:RabbitMQ", "amqp://analytics:analytics_dev@localhost:5672/"),
                    new KeyValuePair<string, string?>("Identity:AllowUnmanagedDevSecrets", "true")
                });
            })
            .ConfigureServices((hostContext, services) =>
            {
                services.AddIdentityHashing(hostContext.Configuration);
                services.AddDbContext<AnalyticsDbContext>(options =>
                    options.UseNpgsql(_harness.AppConnectionString));
                services.AddHostedService<PrivacyAnalytics.Worker.Worker>();
            })
            .Build();

        await _workerHost.StartAsync();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_workerHost != null)
        {
            await _workerHost.StopAsync();
            _workerHost.Dispose();
        }
        if (_factory != null)
        {
            await _factory.DisposeAsync();
        }
        if (_harness != null)
        {
            await _harness.DisposeAsync();
        }
    }

    [PostgresRequiredFact]
    public async Task TrackEvent_FlowsThroughApiAndWorker_AndIsQueryableByTenant()
    {
        // 1. Seeds two Organizations directly in the DB.
        var tenantA_OrgId = Guid.NewGuid();
        var tenantA_WriteKey = Guid.NewGuid();

        var tenantB_OrgId = Guid.NewGuid();
        var tenantB_WriteKey = Guid.NewGuid();

        await using (var adminConn = new NpgsqlConnection(_harness.AdminConnectionString))
        {
            await adminConn.OpenAsync();
            await adminConn.ExecuteAsync(
                "INSERT INTO organizations (org_id, name, public_write_key) VALUES (@Id1, 'Tenant A', @Key1), (@Id2, 'Tenant B', @Key2)",
                new { Id1 = tenantA_OrgId, Key1 = tenantA_WriteKey, Id2 = tenantB_OrgId, Key2 = tenantB_WriteKey });
        }

        // 2. POSTs track events for both through the real /api/v1/track endpoint
        var requestA = new TrackRequest { Url = "https://tenant-a.com/home", EventType = "pageview" };
        var reqMessageA = new HttpRequestMessage(HttpMethod.Post, "/api/v1/track");
        reqMessageA.Headers.Add("X-Tenant-Id", tenantA_WriteKey.ToString());
        reqMessageA.Headers.Add("X-Tenant-Origin", "tenant-a.com");
        reqMessageA.Headers.Add("User-Agent", "Test-Agent");
        reqMessageA.Headers.Add("X-Forwarded-For", "127.0.0.1");
        reqMessageA.Content = JsonContent.Create(requestA);
        var responseA = await _client.SendAsync(reqMessageA);
        Assert.Equal(HttpStatusCode.Accepted, responseA.StatusCode);

        var requestB1 = new TrackRequest { Url = "https://tenant-b.com/home", EventType = "pageview" };
        var reqMessageB1 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/track");
        reqMessageB1.Headers.Add("X-Tenant-Id", tenantB_WriteKey.ToString());
        reqMessageB1.Headers.Add("X-Tenant-Origin", "tenant-b.com");
        reqMessageB1.Headers.Add("User-Agent", "Test-Agent-B1");
        reqMessageB1.Headers.Add("X-Forwarded-For", "127.0.0.2");
        reqMessageB1.Content = JsonContent.Create(requestB1);
        var responseB1 = await _client.SendAsync(reqMessageB1);
        Assert.Equal(HttpStatusCode.Accepted, responseB1.StatusCode);

        var requestB2 = new TrackRequest { Url = "https://tenant-b.com/about", EventType = "pageview" };
        var reqMessageB2 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/track");
        reqMessageB2.Headers.Add("X-Tenant-Id", tenantB_WriteKey.ToString());
        reqMessageB2.Headers.Add("X-Tenant-Origin", "tenant-b.com");
        reqMessageB2.Headers.Add("User-Agent", "Test-Agent-B2");
        reqMessageB2.Headers.Add("X-Forwarded-For", "127.0.0.3");
        reqMessageB2.Content = JsonContent.Create(requestB2);
        var responseB2 = await _client.SendAsync(reqMessageB2);
        Assert.Equal(HttpStatusCode.Accepted, responseB2.StatusCode);

        // 3. Polls until the worker has written both to analytics_events
        var startTime = DateTime.UtcNow;
        bool dataWritten = false;
        await using (var adminConn = new NpgsqlConnection(_harness.AdminConnectionString))
        {
            await adminConn.OpenAsync();
            while (DateTime.UtcNow - startTime < TimeSpan.FromSeconds(15))
            {
                var count = await adminConn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM analytics_events");
                if (count == 3)
                {
                    dataWritten = true;
                    break;
                }
                await Task.Delay(500);
            }
        }
        Assert.True(dataWritten, "Worker did not write the expected events to the DB in time.");

        // Setup query environment
        var configMock = new Mock<IConfiguration>();
        configMock.Setup(x => x.GetSection("ConnectionStrings")["AnalyticsDb"]).Returns(_harness.AppConnectionString);

        // 4. Sets ICurrentTenant to org A's context and runs queries
        var tenantAMock = new Mock<ICurrentTenant>();
        tenantAMock.Setup(x => x.TenantId).Returns(tenantA_OrgId);
        var helperForA = new DapperQueryHelper(configMock.Object, tenantAMock.Object);

        var pageviewsHandlerA = new GetPageviewsOverTimeQueryHandler(helperForA);
        var pageviewsA = await pageviewsHandlerA.Handle(new GetPageviewsOverTimeQuery(DateTimeOffset.MinValue, DateTimeOffset.MaxValue), CancellationToken.None);
        Assert.Equal(1, pageviewsA.Sum(p => p.Pageviews));

        var topPagesHandlerA = new GetTopPagesQueryHandler(helperForA);
        var topPagesA = await topPagesHandlerA.Handle(new GetTopPagesQuery(DateTimeOffset.MinValue, DateTimeOffset.MaxValue), CancellationToken.None);
        Assert.Contains(topPagesA, p => p.Path == "https://tenant-a.com/home" && p.Pageviews == 1);
        Assert.DoesNotContain(topPagesA, p => p.Path.Contains("tenant-b"));

        var uniqueVisitorsHandlerA = new GetUniqueVisitorsQueryHandler(helperForA);
        var uniquesA = await uniqueVisitorsHandlerA.Handle(new GetUniqueVisitorsQuery(DateTimeOffset.MinValue, DateTimeOffset.MaxValue), CancellationToken.None);
        Assert.Equal(1, Math.Max(uniquesA.ExactTier2Uniques, (int)uniquesA.EstimatedTier1Uniques));

        // 5. Repeats for org B
        var tenantBMock = new Mock<ICurrentTenant>();
        tenantBMock.Setup(x => x.TenantId).Returns(tenantB_OrgId);
        var helperForB = new DapperQueryHelper(configMock.Object, tenantBMock.Object);

        var pageviewsHandlerB = new GetPageviewsOverTimeQueryHandler(helperForB);
        var pageviewsB = await pageviewsHandlerB.Handle(new GetPageviewsOverTimeQuery(DateTimeOffset.MinValue, DateTimeOffset.MaxValue), CancellationToken.None);
        Assert.Equal(2, pageviewsB.Sum(p => p.Pageviews));

        var topPagesHandlerB = new GetTopPagesQueryHandler(helperForB);
        var topPagesB = await topPagesHandlerB.Handle(new GetTopPagesQuery(DateTimeOffset.MinValue, DateTimeOffset.MaxValue), CancellationToken.None);
        Assert.Contains(topPagesB, p => p.Path == "https://tenant-b.com/home" && p.Pageviews == 1);
        Assert.Contains(topPagesB, p => p.Path == "https://tenant-b.com/about" && p.Pageviews == 1);
        Assert.DoesNotContain(topPagesB, p => p.Path.Contains("tenant-a"));

        var uniqueVisitorsHandlerB = new GetUniqueVisitorsQueryHandler(helperForB);
        var uniquesB = await uniqueVisitorsHandlerB.Handle(new GetUniqueVisitorsQuery(DateTimeOffset.MinValue, DateTimeOffset.MaxValue), CancellationToken.None);
        Assert.True(Math.Max(uniquesB.ExactTier2Uniques, (int)uniquesB.EstimatedTier1Uniques) > 0);

        // 6. Runs the same queries with no tenant context set — asserts zero rows
        var missingContextMock = new Mock<ICurrentTenant>();
        missingContextMock.Setup(x => x.TenantId).Returns((Guid?)null);
        var helperMissing = new DapperQueryHelper(configMock.Object, missingContextMock.Object);

        var pageviewsHandlerMissing = new GetPageviewsOverTimeQueryHandler(helperMissing);
        var pageviewsMissing = await pageviewsHandlerMissing.Handle(new GetPageviewsOverTimeQuery(DateTimeOffset.MinValue, DateTimeOffset.MaxValue), CancellationToken.None);
        Assert.Empty(pageviewsMissing);

        var topPagesHandlerMissing = new GetTopPagesQueryHandler(helperMissing);
        var topPagesMissing = await topPagesHandlerMissing.Handle(new GetTopPagesQuery(DateTimeOffset.MinValue, DateTimeOffset.MaxValue), CancellationToken.None);
        Assert.Empty(topPagesMissing);

        var uniqueVisitorsHandlerMissing = new GetUniqueVisitorsQueryHandler(helperMissing);
        var uniquesMissing = await uniqueVisitorsHandlerMissing.Handle(new GetUniqueVisitorsQuery(DateTimeOffset.MinValue, DateTimeOffset.MaxValue), CancellationToken.None);
        Assert.Equal(0, uniquesMissing.ExactTier2Uniques);
        Assert.Equal(0, (int)uniquesMissing.EstimatedTier1Uniques);
    }
}
