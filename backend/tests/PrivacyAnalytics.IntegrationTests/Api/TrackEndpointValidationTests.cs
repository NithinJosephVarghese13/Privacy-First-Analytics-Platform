using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using PrivacyAnalytics.IntegrationTests.Rls;
using Xunit;
using Dapper;
using Npgsql;
using PrivacyAnalytics.Domain.Entities;
using PrivacyAnalytics.Contracts;

namespace PrivacyAnalytics.IntegrationTests.Api;

public class TrackEndpointValidationTests : IAsyncLifetime
{
    private TestDatabaseHarness _harness = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var maintenanceConnString = TestDatabaseHarness.ResolveMaintenanceConnectionString();
        var harness = await TestDatabaseHarness.CreateAsync(
            maintenanceConnString, 
            $"analytics_test_track_{Guid.NewGuid():N}");

        if (harness == null)
        {
            // If postgres is not running, we could skip. But we throw here to fail the test in CI.
            throw new InvalidOperationException("PostgreSQL test database could not be provisioned.");
        }
        
        _harness = harness;

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((context, config) =>
                {
                    config.AddInMemoryCollection(new[]
                    {
                        new KeyValuePair<string, string?>("ConnectionStrings:AnalyticsDb", _harness.AppConnectionString)
                    });
                });
            });

        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
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
    public async Task TrackEndpoint_GivenInvalidWriteKey_ReturnsBadRequest()
    {
        // Arrange
        var request = new TrackRequest { Url = "https://example.com", EventType = "pageview", ReferralSource = null };
        
        var reqMessage = new HttpRequestMessage(HttpMethod.Post, "/api/v1/track");
        reqMessage.Headers.Add("X-Tenant-Id", Guid.NewGuid().ToString()); // Random GUID, not in DB
        reqMessage.Headers.Add("X-Tenant-Origin", "example.com");
        reqMessage.Content = JsonContent.Create(request);

        // Act
        var response = await _client.SendAsync(reqMessage);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [PostgresRequiredFact]
    public async Task TrackEndpoint_GivenValidWriteKey_ReturnsAccepted()
    {
        // Arrange
        var validOrgId = Guid.NewGuid();
        var validWriteKey = Guid.NewGuid();

        // Insert valid org into DB
        await using (var adminConn = new NpgsqlConnection(_harness.AdminConnectionString))
        {
            await adminConn.OpenAsync();
            await adminConn.ExecuteAsync(
                "INSERT INTO organizations (org_id, name, public_write_key) VALUES (@Id, @Name, @WriteKey)",
                new { Id = validOrgId, Name = "Test Org", WriteKey = validWriteKey });
        }

        var request = new TrackRequest { Url = "https://example.com", EventType = "pageview", ReferralSource = null };
        
        var reqMessage = new HttpRequestMessage(HttpMethod.Post, "/api/v1/track");
        reqMessage.Headers.Add("X-Tenant-Id", validWriteKey.ToString());
        reqMessage.Headers.Add("X-Tenant-Origin", "example.com");
        reqMessage.Content = JsonContent.Create(request);

        // Act
        var response = await _client.SendAsync(reqMessage);

        // Assert
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [PostgresRequiredFact]
    public async Task TrackEndpoint_RateLimiter_PartitionsByTenantId()
    {
        // Arrange
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

        var request = new TrackRequest { Url = "https://example.com", EventType = "pageview" };
        var jsonContent = JsonContent.Create(request);

        // Act 1: Fire 300 requests for Tenant A, alternating between two DIFFERENT origins.
        var tasksA = new List<Task<HttpResponseMessage>>();
        for (int i = 0; i < 300; i++)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/track") { Content = JsonContent.Create(request) };
            req.Headers.Add("X-Tenant-Id", tenantA_WriteKey.ToString());
            req.Headers.Add("X-Tenant-Origin", i % 2 == 0 ? "origin1.com" : "origin2.com"); 
            tasksA.Add(_client.SendAsync(req));
        }
        
        var responsesA = await Task.WhenAll(tasksA);
        var acceptedA = responsesA.Count(r => r.StatusCode == HttpStatusCode.Accepted);

        // Assert 1: Proves partitioning by TenantId. If partitioned by Origin, each origin gets
        // its own 100 bucket, yielding 200+ accepted. Because they share a TenantId bucket,
        // it caps at 100 (or slightly more if a replenish tick happens, but strictly < 180).
        Assert.True(acceptedA < 180, $"Expected shared Tenant bucket to cap < 180, got {acceptedA}");
        Assert.True(acceptedA >= 90, $"Expected some requests to succeed, got {acceptedA}");

        // Act 2: Fire requests for Tenant B. Proves isolation by TenantId.
        var tasksB = new List<Task<HttpResponseMessage>>();
        for (int i = 0; i < 50; i++)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/track") { Content = JsonContent.Create(request) };
            req.Headers.Add("X-Tenant-Id", tenantB_WriteKey.ToString());
            req.Headers.Add("X-Tenant-Origin", "origin1.com");
            tasksB.Add(_client.SendAsync(req));
        }

        var responsesB = await Task.WhenAll(tasksB);
        var acceptedB = responsesB.Count(r => r.StatusCode == HttpStatusCode.Accepted);
        
        // Assert 2: Tenant B gets its own fresh bucket despite Tenant A being exhausted.
        Assert.Equal(50, acceptedB);
    }
}
