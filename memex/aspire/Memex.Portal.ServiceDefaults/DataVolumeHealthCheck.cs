using MeshWeaver.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Memex.Portal.ServiceDefaults;

/// <summary>
/// The <c>/health</c> check over the data volume's free space (<see cref="DataVolumeFreeSpace"/>):
/// <c>Degraded</c> — never <c>Unhealthy</c>, pulling the pod frees nothing — when the volume any
/// configured store root sits on (<c>PreWarm:PrebuiltBundleRoot</c>, <c>Modules:Root</c>, plus
/// <c>DataVolume:Paths</c>) has less than <c>DataVolume:MinimumFreeBytes</c> free (default 1 GiB),
/// naming the path, used and total; and <c>Degraded</c> too when a configured path cannot be
/// measured. <c>Healthy</c> with no path configured. No probe tag: it reads on
/// <c>ProbeEndpoints.Health</c> alone. The evaluation is the framework's; this host-side check
/// only reads configuration and hands it the probe.
/// </summary>
public sealed class DataVolumeHealthCheck(
    IConfiguration configuration,
    Func<string, DataVolumeReading>? probe = null) : IHealthCheck
{
    private readonly Func<string, DataVolumeReading> probe = probe ?? DataVolumeFreeSpace.Probe;

    /// <summary>The paths this check watches, from configuration.</summary>
    public static IEnumerable<string> PathsOf(IConfiguration configuration)
    {
        foreach (var key in DataVolumeFreeSpace.PathConfigKeys)
        {
            var value = configuration[key];
            if (!string.IsNullOrWhiteSpace(value))
                yield return value;
        }
        foreach (var child in configuration.GetSection(DataVolumeFreeSpace.PathsConfigKey).GetChildren())
            if (!string.IsNullOrWhiteSpace(child.Value))
                yield return child.Value;
    }

    /// <summary>The verdict, evaluated now.</summary>
    public DataVolumeVerdict Evaluate() =>
        DataVolumeFreeSpace.Evaluate(
            PathsOf(configuration),
            probe,
            DataVolumeFreeSpace.MinimumFreeBytesOf(configuration[DataVolumeFreeSpace.MinimumFreeBytesConfigKey]));

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var verdict = Evaluate();
        var data = verdict.Readings.ToDictionary(
            r => r.Path,
            r => (object)(r.Fault ?? $"free={r.FreeBytes} used={r.UsedBytes} total={r.TotalBytes} volume={r.Volume}"),
            StringComparer.Ordinal);
        return Task.FromResult(verdict.Degraded
            ? HealthCheckResult.Degraded(verdict.Description, data: data)
            : HealthCheckResult.Healthy(verdict.Description, data: data));
    }
}
