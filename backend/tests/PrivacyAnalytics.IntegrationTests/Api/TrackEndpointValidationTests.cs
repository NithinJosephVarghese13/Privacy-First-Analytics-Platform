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
}
