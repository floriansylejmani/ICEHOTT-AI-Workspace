using ICEHOTT.API.Background;
using ICEHOTT.Infrastructure.Artifacts;

namespace ICEHOTT.API.Hosting;

/// <summary>
/// Which duties this process performs. One image serves every role; the role is an
/// explicit setting (<c>Service:Role</c>), never inferred from host names or ports.
/// </summary>
public enum ServiceRole
{
    /// <summary>Public HTTP API only. Runs no background processing.</summary>
    Api = 1,

    /// <summary>Durable background services only. Serves just liveness and release info.</summary>
    Worker = 2,

    /// <summary>API and workers in one process. Local development only.</summary>
    All = 3
}

public static class ServiceRoles
{
    public const string SettingName = "Service:Role";

    public static ServiceRole Resolve(IConfiguration configuration, bool requireExplicit)
    {
        var value = configuration[SettingName]?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return requireExplicit
                ? throw new InvalidOperationException(
                    $"{SettingName} must be set explicitly to 'Api' or 'Worker'.")
                : ServiceRole.All;
        }

        // Enum.TryParse would accept numeric strings; only the names are valid.
        var name = Enum.GetNames<ServiceRole>()
            .FirstOrDefault(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase));
        return name is null
            ? throw new InvalidOperationException(
                $"{SettingName} must be one of: Api, Worker, All.")
            : Enum.Parse<ServiceRole>(name);
    }

    public static bool ServesHttpApi(this ServiceRole role) =>
        role is ServiceRole.Api or ServiceRole.All;

    public static bool RunsBackgroundServices(this ServiceRole role) =>
        role is ServiceRole.Worker or ServiceRole.All;
}

public static class BackgroundServiceRegistration
{
    /// <summary>
    /// Registers the durable background services for roles that own them. The API role
    /// registers none, so scaling API replicas never multiplies background workloads.
    /// The PostgreSQL leasing/fencing model is unchanged.
    /// </summary>
    public static void Add(
        IServiceCollection services,
        ServiceRole role,
        KnowledgeWorkerOptions knowledgeWorker,
        ArtifactStorageOptions artifactStorage)
    {
        if (!role.RunsBackgroundServices())
            return;

        if (knowledgeWorker.Enabled)
            services.AddHostedService<KnowledgeIngestionWorker>();
        if (artifactStorage.MaintenanceEnabled)
            services.AddHostedService<ArtifactMaintenanceWorker>();
        services.AddHostedService<ToolExecutionRecoveryWorker>();
        services.AddHostedService<WorkflowRunnerWorker>();
        services.AddHostedService<WorkflowSchedulerWorker>();
    }
}
