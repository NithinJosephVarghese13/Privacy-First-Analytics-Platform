using Microsoft.Extensions.Diagnostics.HealthChecks;
using RabbitMQ.Client;

namespace PrivacyAnalytics.Worker;

public class WorkerHealthState
{
    public IConnection? Connection { get; set; }
    public IChannel? Channel { get; set; }

    public bool IsHealthy => Connection != null && Connection.IsOpen && Channel != null && Channel.IsOpen;
}

public class WorkerRabbitMqHealthCheck : IHealthCheck
{
    private readonly WorkerHealthState _healthState;

    public WorkerRabbitMqHealthCheck(WorkerHealthState healthState)
    {
        _healthState = healthState;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (_healthState != null && _healthState.IsHealthy)
        {
            return Task.FromResult(HealthCheckResult.Healthy("Worker RabbitMQ connection and consumer channel are open."));
        }

        return Task.FromResult(HealthCheckResult.Unhealthy("Worker RabbitMQ connection or channel is closed or uninitialized."));
    }
}
