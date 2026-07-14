using Npgsql;
using Xunit;

namespace PrivacyAnalytics.IntegrationTests.Rls;

/// <summary>
/// Marks a test method as a fact that requires a reachable PostgreSQL (TimescaleDB) instance.
/// Connectivity is probed once per process and cached; when the instance is unavailable the test
/// is reported as skipped (rather than failed) with a clear reason. This is the idiomatic
/// dynamic-skip mechanism for xUnit v2, which has no <c>Assert.Skip</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class PostgresRequiredFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> IsAvailable = new(ProbeOnce, LazyThreadSafetyMode.ExecutionAndPublication);

    public PostgresRequiredFactAttribute()
    {
        if (!IsAvailable.Value)
        {
            Skip = "PostgreSQL (TimescaleDB) is not reachable at the configured connection string. " +
                   "Start the docker-compose stack before running this integration test.";
        }
    }

    private static bool ProbeOnce()
    {
        try
        {
            using var connection = new NpgsqlConnection(TestDatabaseHarness.ResolveMaintenanceConnectionString());
            connection.Open();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
