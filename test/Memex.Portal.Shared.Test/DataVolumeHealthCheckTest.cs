using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Memex.Portal.ServiceDefaults;
using MeshWeaver.Hosting;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The <c>data_volume_free_space</c> health check as the host registers it: the paths come from
/// the store-root keys, the threshold from <c>DataVolume:MinimumFreeBytes</c>, the probe is a
/// seam. Degraded — never Unhealthy — names the path, used and total.
/// </summary>
public class DataVolumeHealthCheckTest
{
    private const long MiB = 1024L * 1024L;

    private static IConfiguration Configuration(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.ToDictionary(e => e.Key, e => (string?)e.Value))
            .Build();

    [Fact]
    public async Task AFullShare_ReportsDegraded_NamingPathUsedAndTotal()
    {
        var check = new DataVolumeHealthCheck(
            Configuration((ShippedPrebuiltBundles.PublishedRootConfigKey, "/data/prebuilt-bundles")),
            path => new DataVolumeReading(path, "/data", 3 * MiB, 16384 * MiB, null));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("/data/prebuilt-bundles");
        result.Description.Should().Contain("16,381 MiB used");
        result.Description.Should().Contain("16,384 MiB");
        result.Data.Keys.Should().Contain("/data/prebuilt-bundles");
    }

    [Fact]
    public async Task ARoomyShare_IsHealthy()
    {
        var check = new DataVolumeHealthCheck(
            Configuration(
                (ShippedPrebuiltBundles.PublishedRootConfigKey, "/data/prebuilt-bundles"),
                (ModuleRoot.ConfigKey, "/data/modules")),
            path => new DataVolumeReading(path, "/data", 9000 * MiB, 16384 * MiB, null));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data.Keys.Should().Contain("/data/modules");
    }

    [Fact]
    public async Task NothingConfigured_IsHealthy_NotVacuouslyDegraded()
    {
        var check = new DataVolumeHealthCheck(Configuration(), path => throw new System.IO.IOException("never called"));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Contain("no data volume configured");
    }

    [Fact]
    public async Task TheThresholdComesFromConfiguration()
    {
        var check = new DataVolumeHealthCheck(
            Configuration(
                (ShippedPrebuiltBundles.PublishedRootConfigKey, "/data/prebuilt-bundles"),
                (DataVolumeFreeSpace.MinimumFreeBytesConfigKey, (2 * MiB).ToString())),
            path => new DataVolumeReading(path, "/data", 3 * MiB, 16384 * MiB, null));

        (await check.CheckHealthAsync(new HealthCheckContext())).Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public void AdditionalPaths_AreWatchedToo()
    {
        var configuration = Configuration(
            ($"{DataVolumeFreeSpace.PathsConfigKey}:0", "/data/assembly-cache"),
            ($"{DataVolumeFreeSpace.PathsConfigKey}:1", "/data/dataprotection-keys"));

        DataVolumeHealthCheck.PathsOf(configuration).Should().Equal("/data/assembly-cache", "/data/dataprotection-keys");
    }

    /// <summary>
    /// The framework cannot reference PluginCatalog's <see cref="ModuleRoot.ConfigKey"/>, so it
    /// mirrors the string. This assembly sees both; a drift here would silently stop the check
    /// watching the modules root.
    /// </summary>
    [Fact]
    public void TheModulesRootKey_IsTheOnePluginCatalogReads()
    {
        DataVolumeFreeSpace.ModulesRootConfigKey.Should().Be(ModuleRoot.ConfigKey);
        DataVolumeFreeSpace.PathConfigKeys.Should().Contain(ModuleRoot.ConfigKey);
    }
}
