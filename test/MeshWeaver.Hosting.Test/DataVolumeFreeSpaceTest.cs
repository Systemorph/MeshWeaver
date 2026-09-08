using System;
using System.Linq;
using MeshWeaver.Hosting;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The free-space signal, evaluated over a fake drive probe: Degraded below the threshold naming
/// path, used and total; Degraded when a configured path cannot be measured; Healthy with nothing
/// configured. The 2026-09-08 share (16384 MiB, 3 MiB free) is the worked example.
/// </summary>
public class DataVolumeFreeSpaceTest
{
    private const long MiB = 1024L * 1024L;

    private static DataVolumeReading Full(string path) => new(path, "/data", 3 * MiB, 16384 * MiB, null);
    private static DataVolumeReading Roomy(string path) => new(path, "/data", 9000 * MiB, 16384 * MiB, null);

    [Fact]
    public void BelowTheThreshold_IsDegraded_NamingPathUsedAndTotal()
    {
        var verdict = DataVolumeFreeSpace.Evaluate(["/data/prebuilt-bundles"], Full, DataVolumeFreeSpace.DefaultMinimumFreeBytes);

        verdict.Degraded.Should().BeTrue();
        verdict.Description.Should().Contain("/data/prebuilt-bundles");
        verdict.Description.Should().Contain("3 MiB free");
        verdict.Description.Should().Contain("16,384 MiB");
        verdict.Description.Should().Contain("16,381 MiB used");
        verdict.Description.Should().Contain("BELOW the 1,024 MiB threshold");
    }

    [Fact]
    public void AboveTheThreshold_IsHealthy_AndStillReportsTheNumbers()
    {
        var verdict = DataVolumeFreeSpace.Evaluate(["/data/prebuilt-bundles", "/data/modules"], Roomy, DataVolumeFreeSpace.DefaultMinimumFreeBytes);

        verdict.Degraded.Should().BeFalse();
        verdict.Readings.Should().HaveCount(2);
        verdict.Description.Should().Contain("9,000 MiB free");
        verdict.Description.Should().NotContain("BELOW");
    }

    [Fact]
    public void APathThatCannotBeMeasured_IsDegraded_NeverHealthy()
    {
        var verdict = DataVolumeFreeSpace.Evaluate(
            ["/data/prebuilt-bundles"],
            path => new DataVolumeReading(path, null, null, null, "IOException: not mounted"),
            DataVolumeFreeSpace.DefaultMinimumFreeBytes);

        verdict.Degraded.Should().BeTrue();
        verdict.Description.Should().Contain("could not be measured");
        verdict.Description.Should().Contain("not mounted");
    }

    [Fact]
    public void NoPathConfigured_IsHealthy_AndSaysNothingWasMeasured()
    {
        var verdict = DataVolumeFreeSpace.Evaluate([" ", ""], Full, DataVolumeFreeSpace.DefaultMinimumFreeBytes);

        verdict.Degraded.Should().BeFalse();
        verdict.Readings.Should().BeEmpty();
        verdict.Description.Should().Contain("no data volume configured");
    }

    [Fact]
    public void TheThresholdIsConfigurable_AndAMalformedValueFallsBackToOneGiB()
    {
        DataVolumeFreeSpace.MinimumFreeBytesOf("2097152").Should().Be(2 * MiB);
        DataVolumeFreeSpace.MinimumFreeBytesOf("lots").Should().Be(DataVolumeFreeSpace.DefaultMinimumFreeBytes);
        DataVolumeFreeSpace.MinimumFreeBytesOf(null).Should().Be(1L << 30);

        // 3 MiB free clears a 2 MiB threshold — the configured value is the one applied.
        DataVolumeFreeSpace.Evaluate(["/data"], Full, 2 * MiB).Degraded.Should().BeFalse();
    }

    [Fact]
    public void TheRealProbe_AnswersForAnExistingPath()
    {
        var reading = DataVolumeFreeSpace.Probe(Environment.CurrentDirectory);

        reading.Fault.Should().BeNull();
        reading.TotalBytes!.Value.Should().BeGreaterThan(0);
        reading.FreeBytes!.Value.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void DuplicatePaths_AreMeasuredOnce()
    {
        var probed = 0;
        DataVolumeFreeSpace.Evaluate(["/data", "/data"], p => { probed++; return Roomy(p); }, MiB)
            .Readings.Select(r => r.Path).Should().Equal("/data");
        probed.Should().Be(1);
    }
}
