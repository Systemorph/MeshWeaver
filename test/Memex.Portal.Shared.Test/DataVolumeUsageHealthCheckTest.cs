using System;
using System.Collections.Immutable;
using System.Threading.Tasks;
using Memex.Portal.ServiceDefaults;
using MeshWeaver.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The <c>data_volume_usage</c> census as <c>/health</c> publishes it (MeshWeaver#6983): the last
/// walk's per-directory reading, "not measured yet" as its own sentence, Degraded only when the
/// instrument itself is missing or a walk faulted.
/// </summary>
public class DataVolumeUsageHealthCheckTest
{
    private static IServiceProvider With(DataVolumeUsageCensus? census)
    {
        var services = new ServiceCollection();
        if (census is not null)
            services.AddSingleton(census);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task A_host_without_the_census_is_Degraded_never_a_silent_Healthy()
    {
        var result = await new DataVolumeUsageHealthCheck(With(null)).CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("not registered");
    }

    [Fact]
    public async Task A_reading_prints_per_directory_and_is_Healthy()
    {
        var census = new DataVolumeUsageCensus();
        census.SetRoots(["/data"]);
        census.Record(new DataVolumeUsageReading(
            "/data", DateTimeOffset.UnixEpoch, TimeSpan.FromMinutes(3),
            [
                new DataVolumeUsageEntry("prebuilt-bundles", 90L << 30, 1000, 811, null, 0, false,
                    [new DataVolumeUsageEntry("c003e001", 40L << 30, 400, 3, null, 0, false, [])]),
                new DataVolumeUsageEntry("dumps", 0, 0, 0, null, 0, true, []),
            ],
            0, 0, null));

        var result = await new DataVolumeUsageHealthCheck(With(census)).CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Contain("prebuilt-bundles/ 92,160 MiB (100%)");
        result.Description.Should().Contain("811 entr(ies)");
        result.Description.Should().Contain("[largest: c003e001 40,960 MiB]");
        result.Description.Should().Contain("dumps/ is another volume's mount point");
    }

    [Fact]
    public async Task Before_the_first_walk_it_says_not_measured_yet()
    {
        var census = new DataVolumeUsageCensus();
        census.SetRoots(["/data"]);

        var result = await new DataVolumeUsageHealthCheck(With(census)).CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Contain("NOT MEASURED YET");
    }
}
