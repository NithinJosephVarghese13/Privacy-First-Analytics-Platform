using System.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PrivacyAnalytics.Contracts;
using PrivacyAnalytics.Domain.Entities;
using PrivacyAnalytics.Domain.Identity;
using PrivacyAnalytics.Infrastructure.Data;

namespace PrivacyAnalytics.Infrastructure.Analytics.Commands;

public class PurgeTier2DataCommandHandler(
    AnalyticsDbContext dbContext,
    ICurrentTenant currentTenant) : IRequestHandler<PurgeTier2DataCommand, ErasureAuditLogDto>
{
    public async Task<ErasureAuditLogDto> Handle(PurgeTier2DataCommand request, CancellationToken cancellationToken)
    {
        if (!currentTenant.TenantId.HasValue)
        {
            throw new InvalidOperationException("Tenant context is missing or invalid.");
        }

        if (string.IsNullOrWhiteSpace(request.DurableHash))
        {
            throw new ArgumentException("DurableHash is required for erasure operation.", nameof(request.DurableHash));
        }

        var tenantId = currentTenant.TenantId.Value;

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var dbConnection = dbContext.Database.GetDbConnection();
        var dbTransaction = transaction.GetDbTransaction();

        if (dbContext.Database.ProviderName?.Contains("Npgsql") == true)
        {
            await using var setConfigCmd = dbConnection.CreateCommand();
            setConfigCmd.Transaction = dbTransaction;
            setConfigCmd.CommandText = "SELECT set_config('app.current_tenant_id', @tenantIdStr, true);";

            var pStr = setConfigCmd.CreateParameter();
            pStr.ParameterName = "tenantIdStr";
            pStr.Value = tenantId.ToString();
            setConfigCmd.Parameters.Add(pStr);

            await setConfigCmd.ExecuteScalarAsync(cancellationToken);
        }

        var matchingEvents = await dbContext.AnalyticsEvents
            .Where(e => e.OrganizationId == tenantId && e.DurableHash == request.DurableHash)
            .ToListAsync(cancellationToken);

        var recordsAffected = matchingEvents.Count;

        if (recordsAffected > 0)
        {
            await using var deleteCmd = dbConnection.CreateCommand();
            deleteCmd.Transaction = dbTransaction;
            deleteCmd.CommandText = "DELETE FROM analytics_events WHERE organization_id = @tenantId AND durable_hash = @durableHash;";

            var p1 = deleteCmd.CreateParameter();
            p1.ParameterName = "tenantId";
            p1.Value = tenantId;
            deleteCmd.Parameters.Add(p1);

            var p2 = deleteCmd.CreateParameter();
            p2.ParameterName = "durableHash";
            p2.Value = request.DurableHash;
            deleteCmd.Parameters.Add(p2);

            await deleteCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        var auditLog = new ErasureAuditLog
        {
            AuditId = Guid.NewGuid(),
            OrganizationId = tenantId,
            PurgedIdentifierHash = request.DurableHash,
            RequestedBy = string.IsNullOrWhiteSpace(request.RequestedBy) ? "Admin" : request.RequestedBy,
            PurgedAtUtc = DateTimeOffset.UtcNow,
            RecordsAffected = recordsAffected
        };

        dbContext.ErasureAuditLogs.Add(auditLog);

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new ErasureAuditLogDto(
            auditLog.AuditId,
            auditLog.OrganizationId,
            auditLog.PurgedIdentifierHash,
            auditLog.RequestedBy,
            auditLog.PurgedAtUtc,
            auditLog.RecordsAffected
        );
    }
}
