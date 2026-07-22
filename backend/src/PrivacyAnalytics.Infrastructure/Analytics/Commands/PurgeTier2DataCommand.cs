using MediatR;
using PrivacyAnalytics.Contracts;

namespace PrivacyAnalytics.Infrastructure.Analytics.Commands;

public record PurgeTier2DataCommand(string DurableHash, string RequestedBy) : IRequest<ErasureAuditLogDto>;
