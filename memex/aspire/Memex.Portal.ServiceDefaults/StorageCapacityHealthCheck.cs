using MeshWeaver.Compiler;
using MeshWeaver.Mesh.Persistence;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Memex.Portal.ServiceDefaults;

/// <summary>
/// The <c>/health</c> check over the volume the assembly store publishes into: <c>Degraded</c> —
/// never <c>Unhealthy</c>, a replica on a full share still serves every page whose bytes are
/// already loaded and pulling it would turn "cannot compile" into "cannot serve" — when the free
/// space is below <see cref="StorageCapacityHealth.MinimumFreeMiBConfigKey"/> (default
/// <see cref="StorageCapacityHealth.DefaultMinimumFreeMiB"/> MiB), naming the path and the numbers.
///
/// <para>Why: on 2026-09-08 <c>/data</c> on memex — the ReadWriteMany share holding
/// <c>/data/assembly-cache</c>, the module generations and the prebuilt bundles — sat at 3 MiB free
/// for hours, and the only symptom was a NodeType whose every recompile published a truncated DLL.
/// The compile pipeline now REFUSES such a publication and says why on the record; this check says
/// it BEFORE a compile has to find out. The verdict itself is the pure
/// <see cref="StorageCapacityHealth.Evaluate"/>; this host-side check only takes the reading, exactly
/// as <see cref="ContentTypeHealthCheck"/> only reads its registry. The store is resolved per call
/// because the mesh's services are composed after the host's health checks are registered; a
/// deployment whose store is not filesystem-backed (blob) has no volume to read and answers Healthy
/// with that said.</para>
/// </summary>
public sealed class StorageCapacityHealthCheck(IServiceProvider services, IConfiguration configuration) : IHealthCheck
{
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (services.GetService<IAssemblyStore>() is not FileSystemAssemblyStore store)
            return Task.FromResult(HealthCheckResult.Healthy(
                "the assembly store is not filesystem-backed — no volume to read"));

        var floor = StorageCapacityHealth.MinimumFreeBytes(
            configuration[StorageCapacityHealth.MinimumFreeMiBConfigKey]);
        var (isLow, description) = StorageCapacityHealth.Evaluate(
            store.RootDirectory, VolumeCapacity.Of(store.RootDirectory), floor);
        return Task.FromResult(isLow
            ? HealthCheckResult.Degraded(description)
            : HealthCheckResult.Healthy(description));
    }
}
