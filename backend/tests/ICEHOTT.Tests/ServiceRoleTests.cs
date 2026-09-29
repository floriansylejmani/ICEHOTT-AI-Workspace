using ICEHOTT.API.Background;
using ICEHOTT.API.Hosting;
using ICEHOTT.Infrastructure.Artifacts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ICEHOTT.Tests;

public sealed class ServiceRoleTests
{
    private static IConfiguration Config(string? role) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Service:Role"] = role })
            .Build();

    [Theory]
    [InlineData("Api", ServiceRole.Api)]
    [InlineData("api", ServiceRole.Api)]
    [InlineData("Worker", ServiceRole.Worker)]
    [InlineData("WORKER", ServiceRole.Worker)]
    [InlineData("All", ServiceRole.All)]
    public void Known_Roles_Resolve(string value, ServiceRole expected) =>
        Assert.Equal(expected, ServiceRoles.Resolve(Config(value), requireExplicit: false));

    [Fact]
    public void Unset_Role_Defaults_To_All_Only_When_Not_Required()
    {
        Assert.Equal(ServiceRole.All, ServiceRoles.Resolve(Config(null), requireExplicit: false));
        Assert.Throws<InvalidOperationException>(() =>
            ServiceRoles.Resolve(Config(null), requireExplicit: true));
    }

    [Theory]
    [InlineData("Sidecar")]
    [InlineData("1")]
    [InlineData("Api,Worker")]
    public void Unknown_Role_Fails_Closed_Everywhere(string value)
    {
        Assert.Throws<InvalidOperationException>(() =>
            ServiceRoles.Resolve(Config(value), requireExplicit: false));
        Assert.Throws<InvalidOperationException>(() =>
            ServiceRoles.Resolve(Config(value), requireExplicit: true));
    }

    [Fact]
    public void Unknown_Role_Error_Does_Not_Echo_Arbitrary_Input()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ServiceRoles.Resolve(Config("leaky-value-42"), requireExplicit: false));
        Assert.DoesNotContain("leaky-value-42", exception.Message);
    }

    [Fact]
    public void Role_Capabilities_Are_Disjoint_For_Api_And_Worker()
    {
        Assert.True(ServiceRole.Api.ServesHttpApi());
        Assert.False(ServiceRole.Api.RunsBackgroundServices());
        Assert.False(ServiceRole.Worker.ServesHttpApi());
        Assert.True(ServiceRole.Worker.RunsBackgroundServices());
        Assert.True(ServiceRole.All.ServesHttpApi());
        Assert.True(ServiceRole.All.RunsBackgroundServices());
    }

    private static IReadOnlyList<Type> RegisteredWorkers(
        ServiceRole role,
        bool knowledgeEnabled = true,
        bool maintenanceEnabled = true)
    {
        var services = new ServiceCollection();
        BackgroundServiceRegistration.Add(
            services,
            role,
            new KnowledgeWorkerOptions { Enabled = knowledgeEnabled },
            new ArtifactStorageOptions { MaintenanceEnabled = maintenanceEnabled });
        return services
            .Where(x => x.ServiceType == typeof(IHostedService))
            .Select(x => x.ImplementationType!)
            .ToArray();
    }

    [Fact]
    public void Api_Role_Registers_No_Background_Services() =>
        Assert.Empty(RegisteredWorkers(ServiceRole.Api));

    [Theory]
    [InlineData(ServiceRole.Worker)]
    [InlineData(ServiceRole.All)]
    public void Worker_Capable_Roles_Register_The_Durable_Workers(ServiceRole role)
    {
        var workers = RegisteredWorkers(role);
        Assert.Equal(
            new[]
            {
                typeof(KnowledgeIngestionWorker),
                typeof(ArtifactMaintenanceWorker),
                typeof(ToolExecutionRecoveryWorker),
                typeof(WorkflowRunnerWorker),
                typeof(WorkflowSchedulerWorker)
            }.OrderBy(x => x.Name),
            workers.OrderBy(x => x.Name));
        Assert.Equal(workers.Count, workers.Distinct().Count());
    }

    [Fact]
    public void Worker_Respects_Existing_Enabled_Flags()
    {
        var workers = RegisteredWorkers(
            ServiceRole.Worker, knowledgeEnabled: false, maintenanceEnabled: false);
        Assert.DoesNotContain(typeof(KnowledgeIngestionWorker), workers);
        Assert.DoesNotContain(typeof(ArtifactMaintenanceWorker), workers);
        Assert.Contains(typeof(WorkflowRunnerWorker), workers);
        Assert.Contains(typeof(WorkflowSchedulerWorker), workers);
    }

    [Fact]
    public async Task Worker_Role_Serves_Only_Health_And_Release_Not_The_Api()
    {
        using var factory = new IcehottApiFactory(b => b.UseSetting("Service:Role", "Worker"));
        using var client = factory.CreateClient();

        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/release")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await client.GetAsync("/ready")).StatusCode);
        Assert.Equal(
            System.Net.HttpStatusCode.NotFound,
            (await client.PostAsync("/api/auth/login", null)).StatusCode);
    }

    [Fact]
    public async Task Api_Role_Serves_The_Api_Surface()
    {
        using var factory = new IcehottApiFactory(b => b.UseSetting("Service:Role", "Api"));
        using var client = factory.CreateClient();

        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/ready")).StatusCode);
        Assert.NotEqual(
            System.Net.HttpStatusCode.NotFound,
            (await client.PostAsync("/api/auth/login", null)).StatusCode);
    }
}
