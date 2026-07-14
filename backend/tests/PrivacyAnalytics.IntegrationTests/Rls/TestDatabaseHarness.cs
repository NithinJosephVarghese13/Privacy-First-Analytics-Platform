using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PrivacyAnalytics.Infrastructure.Data;

namespace PrivacyAnalytics.IntegrationTests.Rls;

/// <summary>
/// Provisions an isolated PostgreSQL database (TimescaleDB-enabled) for a single RLS
/// integration test run, runs EF Core migrations (which enable FORCE ROW LEVEL SECURITY
/// and create the tenant-isolation policies), and creates a NON-superuser login role that is
/// made the OWNER of the RLS-protected tables. Ownership is the crucial ingredient: without
/// FORCE ROW LEVEL SECURITY a table owner silently bypasses RLS, so only by asserting as the
/// owner can the test prove that FORCE is actually in effect (comment out FORCE and the test
/// must fail). The role has neither SUPERUSER nor BYPASSRLS, so FORCE is the only thing keeping
/// it subject to the policy.
/// </summary>
internal sealed class TestDatabaseHarness : IAsyncDisposable
{
    private const string AppRole = "analytics_app";
    private const string AppRolePassword = "analytics_app_dev";

    private readonly string _maintenanceConnectionString;
    private readonly string _testDbName;
    private readonly string _quotedTestDb;

    private TestDatabaseHarness(string adminConnectionString, string appConnectionString,
        string maintenanceConnectionString, string testDbName, string quotedTestDb)
    {
        AdminConnectionString = adminConnectionString;
        AppConnectionString = appConnectionString;
        _maintenanceConnectionString = maintenanceConnectionString;
        _testDbName = testDbName;
        _quotedTestDb = quotedTestDb;
    }

    /// <summary>
    /// Superuser connection to the freshly provisioned test database. Used for seeding (a
    /// superuser bypasses RLS, so inserts are not gated by the INSERT WITH CHECK policy).
    /// </summary>
    public string AdminConnectionString { get; }

    /// <summary>
    /// Connection as the non-superuser TABLE OWNER. Because the role owns the tables, the only
    /// thing subjecting it to RLS is FORCE ROW LEVEL SECURITY — exactly what the test exercises.
    /// </summary>
    public string AppConnectionString { get; }

    /// <summary>
    /// Returns a provisioned harness, or <c>null</c> when a PostgreSQL instance is not
    /// reachable so the caller can skip the test rather than fail.
    /// </summary>
    public static async Task<TestDatabaseHarness?> CreateAsync(
        string maintenanceConnectionString, string testDbName)
    {
        NpgsqlConnectionStringBuilder adminBase;
        try
        {
            adminBase = new NpgsqlConnectionStringBuilder(maintenanceConnectionString);
            await using var probe = new NpgsqlConnection(adminBase.ConnectionString);
            await probe.OpenAsync();
        }
        catch
        {
            return null;
        }

        var quotedDb = QuoteIdent(testDbName);
        var quotedRole = QuoteIdent(AppRole);

        await EnsureAppRoleAsync(adminBase.ConnectionString);

        // Create the test database owned by the app role so it gets CONNECT and schema USAGE
        // rights by default; the tables themselves are re-owned below.
        await using (var m = new NpgsqlConnection(adminBase.ConnectionString))
        {
            await m.OpenAsync();
            await DropDatabaseAsync(m, testDbName, quotedDb);
            await m.ExecuteAsync($"CREATE DATABASE {quotedDb} OWNER {quotedRole};");
        }

        var adminToTestDb = new NpgsqlConnectionStringBuilder(adminBase.ConnectionString)
        {
            Database = testDbName
        };
        var appToTestDb = new NpgsqlConnectionStringBuilder(adminToTestDb.ConnectionString)
        {
            Username = AppRole,
            Password = AppRolePassword
        };

        // The TimescaleDB extension requires a superuser to install; create it before migrations.
        await using (var ext = new NpgsqlConnection(adminToTestDb.ConnectionString))
        {
            await ext.OpenAsync();
            await ext.ExecuteAsync("CREATE EXTENSION IF NOT EXISTS timescaledb;");
        }

        // Run migrations as the superuser so hypertable creation and RLS DDL succeed regardless of
        // TimescaleDB's non-superuser privileges. The tables are therefore initially owned by the
        // superuser.
        var options = new DbContextOptionsBuilder<AnalyticsDbContext>()
            .UseNpgsql(adminToTestDb.ConnectionString)
            .Options;
        await using (var db = new AnalyticsDbContext(options))
        {
            await db.Database.MigrateAsync();
        }

        // Transfer ownership of every RLS-protected table to the non-superuser app role. This is
        // what makes FORCE ROW LEVEL SECURITY load-bearing: an owner bypasses RLS unless FORCE is
        // set. No chunks exist yet (no data has been inserted), so only the parent hypertables are
        // re-owned; chunks created later during seeding inherit the hypertable owner.
        await using (var g = new NpgsqlConnection(adminToTestDb.ConnectionString))
        {
            await g.OpenAsync();
            await g.ExecuteAsync($"ALTER TABLE analytics_events OWNER TO {quotedRole};");
            await g.ExecuteAsync($"ALTER TABLE erasure_audit_log OWNER TO {quotedRole};");
            await g.ExecuteAsync($"ALTER TABLE organizations OWNER TO {quotedRole};");
            await g.ExecuteAsync($"ALTER SCHEMA public OWNER TO {quotedRole};");
            // Ensure future chunks (owned by the hypertable owner) are readable by the owner.
            await g.ExecuteAsync($"GRANT USAGE ON SCHEMA public TO {quotedRole};");
        }

        return new TestDatabaseHarness(
            adminToTestDb.ConnectionString,
            appToTestDb.ConnectionString,
            adminBase.ConnectionString,
            testDbName,
            quotedDb);
    }

    private static async Task EnsureAppRoleAsync(string maintenanceConnectionString)
    {
        await using var m = new NpgsqlConnection(maintenanceConnectionString);
        await m.OpenAsync();
        await m.ExecuteAsync($$"""
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{{AppRole}}') THEN
                    CREATE ROLE {{QuoteIdent(AppRole)}} LOGIN PASSWORD '{{AppRolePassword}}' NOSUPERUSER NOBYPASSRLS;
                END IF;
            END
            $$;
            """);
        await m.ExecuteAsync(
            $"ALTER ROLE {QuoteIdent(AppRole)} LOGIN PASSWORD '{AppRolePassword}' NOSUPERUSER NOBYPASSRLS;");
    }

    private static async Task DropDatabaseAsync(NpgsqlConnection m, string dbName, string quotedDb)
    {
        await m.ExecuteAsync(
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @Db AND pid <> pg_backend_pid();",
            new { Db = dbName });
        await m.ExecuteAsync($"DROP DATABASE IF EXISTS {quotedDb};");
    }

    private static string QuoteIdent(string ident) => "\"" + ident.Replace("\"", "\"\"") + "\"";

    /// <summary>
    /// Resolves the superuser/maintenance connection string from the
    /// <c>ANALYTICS_DB_CONNECTION_STRING</c> environment variable, falling back to the
    /// docker-compose development defaults.
    /// </summary>
    public static string ResolveMaintenanceConnectionString()
    {
        var env = Environment.GetEnvironmentVariable("ANALYTICS_DB_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(env))
            return env;

        return "Host=localhost;Port=5432;Database=analytics;Username=analytics;Password=analytics_dev";
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var m = new NpgsqlConnection(_maintenanceConnectionString);
        await m.OpenAsync();
        await DropDatabaseAsync(m, _testDbName, _quotedTestDb);
    }
}
