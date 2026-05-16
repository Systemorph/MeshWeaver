using MeshWeaver.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// <see cref="IHostedService"/> wrapper that calls
/// <see cref="PostgreSqlPartitionStorageProvider.SubscribeToWorkspace"/> at
/// host startup so the provider populates its <c>_partitions</c> dictionary
/// from the <c>Admin/Partition/*</c> MeshNode stream. Without this wrapper
/// the subscription is dead code and the provider's
/// <see cref="PostgreSqlPartitionStorageProvider.Matches"/> always returns
/// false — even for built-in partitions like <c>Admin</c>, <c>User</c>,
/// <c>Portal</c>, <c>Kernel</c>, and the global satellite namespaces
/// (<c>_Access</c>, <c>_Activity</c>, <c>_UserActivity</c>, <c>_Thread</c>)
/// — and every write through <see cref="MeshWeaver.Hosting.Persistence.PersistenceService"/>
/// faults with "no IPartitionStorageProvider matches" (repro:
/// <c>EffectivePermissionPostgresTest.CreateOrganization_HasPermission_ReturnsAdmin</c>).
/// </summary>
internal sealed class PostgreSqlPartitionSubscriptionHostedService : IHostedService
{
    private readonly PostgreSqlPartitionStorageProvider _provider;
    private readonly IMessageHub _meshHub;
    private readonly ILogger<PostgreSqlPartitionSubscriptionHostedService>? _logger;
    private IDisposable? _subscription;

    public PostgreSqlPartitionSubscriptionHostedService(
        PostgreSqlPartitionStorageProvider provider,
        IMessageHub meshHub,
        ILogger<PostgreSqlPartitionSubscriptionHostedService>? logger = null)
    {
        _provider = provider;
        _meshHub = meshHub;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger?.LogInformation(
            "Starting PostgreSqlPartitionStorageProvider.SubscribeToWorkspace");
        _subscription = _provider.SubscribeToWorkspace(_meshHub);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }
}
