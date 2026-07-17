using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;
using PrivacyAnalytics.Domain.Identity;
using System.Data;

namespace PrivacyAnalytics.Infrastructure.Data;

#pragma warning disable RS0030 // Do not use banned APIs

public class DapperQueryHelper(
    IConfiguration configuration,
    ICurrentTenant currentTenant) : IDapperQueryHelper
{
    private readonly string _connectionString = configuration.GetConnectionString("AnalyticsDb") 
        ?? throw new InvalidOperationException("Connection string 'AnalyticsDb' not found.");

    public async Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null, CancellationToken cancellationToken = default)
    {
        if (!currentTenant.TenantId.HasValue)
        {
            return Enumerable.Empty<T>();
        }

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        
        await connection.ExecuteAsync(
            new CommandDefinition(
                "SELECT set_config('app.current_tenant_id', @TenantId::text, true);",
                new { currentTenant.TenantId },
                transaction,
                cancellationToken: cancellationToken));

        var result = await connection.QueryAsync<T>(
            new CommandDefinition(sql, param, transaction, cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);

        return result;
    }

    public async Task<T?> QueryFirstOrDefaultAsync<T>(string sql, object? param = null, CancellationToken cancellationToken = default)
    {
        if (!currentTenant.TenantId.HasValue)
        {
            return default;
        }

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        
        await connection.ExecuteAsync(
            new CommandDefinition(
                "SELECT set_config('app.current_tenant_id', @TenantId::text, true);",
                new { currentTenant.TenantId },
                transaction,
                cancellationToken: cancellationToken));

        var result = await connection.QueryFirstOrDefaultAsync<T>(
            new CommandDefinition(sql, param, transaction, cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);

        return result;
    }
}
