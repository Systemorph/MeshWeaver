using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using MeshWeaver.Hosting;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The per-directory usage census of the data volume (MeshWeaver#6983), driven on a temp tree laid
/// out like the AKS share: <c>prebuilt-bundles/&lt;identity&gt;</c>, <c>modules/&lt;generation&gt;</c>,
/// <c>assembly-cache/&lt;NodeType&gt;</c>, and a nested mount (<c>dumps/</c>) that is another volume.
/// The reading must name WHICH store holds the bytes and which child inside it — the question
/// nothing on the fleet could answer while the share sat full for 33 hours.
/// </summary>
public sealed class DataVolumeUsageTest : IDisposable
{
    private const int KiB = 1024;
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 20, 0, 0, TimeSpan.Zero);
    private readonly string root = Path.Combine(Path.GetTempPath(), "dvu-" + Guid.NewGuid().ToString("N"));

    public DataVolumeUsageTest() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private void File(string relative, int bytes)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
    }

    [Fact]
    public void The_reading_names_the_store_and_the_child_that_hold_the_space()
    {
        File("prebuilt-bundles/s0ld0001/plugins/gen-a/Store.zip", 300 * KiB);
        File("prebuilt-bundles/s0ld0001/plugins/gen-b/Store.zip", 300 * KiB);
        File("prebuilt-bundles/c003e001/plugins/gen-c/Store.zip", 100 * KiB);
        File("prebuilt-bundles/_releases/3.1.1", 1 * KiB);
        File("modules/MeshWeaver.AI@1/MeshWeaver.AI.dll", 200 * KiB);
        File("assembly-cache/Store_Coupon/v1-c003e001-abc.dll", 50 * KiB);
        File("loose.txt", 2 * KiB);

        var reading = DataVolumeUsage.Measure(root, ImmutableHashSet<string>.Empty, () => Now);

        reading.Fault.Should().BeNull();
        reading.Directories.Select(d => d.Name).Should().Equal("prebuilt-bundles", "modules", "assembly-cache");
        var bundles = reading.Directories[0];
        bundles.Bytes.Should().Be(701 * KiB);
        bundles.Files.Should().Be(4);
        bundles.Children.Should().Be(3);
        bundles.Largest.Select(c => c.Name).Should().Equal("s0ld0001", "c003e001", "_releases");
        bundles.Largest[0].Bytes.Should().Be(600 * KiB);
        bundles.Largest[0].Files.Should().Be(2);
        reading.LooseFiles.Should().Be(1);
        reading.TotalBytes.Should().Be((701 + 200 + 50 + 2) * KiB);
        reading.UnreadableDirectories.Should().Be(0);

        var text = DataVolumeUsage.Describe(reading);
        text.Should().Contain("every directory read");
        text.IndexOf("prebuilt-bundles/", StringComparison.Ordinal)
            .Should().BeLessThan(text.IndexOf("modules/", StringComparison.Ordinal));
        text.Should().Contain("[largest: s0ld0001 1 MiB");
        text.Should().Contain("74%");
    }

    [Fact]
    public void A_nested_mount_of_another_volume_is_named_and_not_counted()
    {
        File("dumps/core.1234", 500 * KiB);
        File("modules/MeshWeaver.AI@1/MeshWeaver.AI.dll", 10 * KiB);
        var dumps = Path.Combine(root, "dumps");

        var reading = DataVolumeUsage.Measure(root, ImmutableHashSet.Create(dumps, root), () => Now);

        var entry = reading.Directories.Single(d => d.Name == "dumps");
        entry.SeparateVolume.Should().BeTrue();
        entry.Bytes.Should().Be(0);
        reading.TotalBytes.Should().Be(10 * KiB);
        DataVolumeUsage.Describe(reading).Should().Contain("dumps/ is another volume's mount point (not counted)");
    }

    [Fact]
    public void A_root_that_cannot_be_enumerated_is_a_fault_never_an_empty_volume()
    {
        var missing = Path.Combine(root, "not-mounted");

        var reading = DataVolumeUsage.Measure(missing, ImmutableHashSet<string>.Empty, () => Now);

        reading.Fault.Should().NotBeNull();
        reading.Directories.Should().BeEmpty();
        DataVolumeUsage.Describe(reading).Should().Contain("nothing was measured");
    }

    [Fact]
    public void Nested_configured_paths_fold_into_one_walk_of_their_parent()
    {
        var roots = DataVolumeUsage.RootsOf(["/data", "/data/prebuilt-bundles/", "/data/modules", " ", "/data2", "/data"]);

        roots.Should().Equal("/data", "/data2");
    }

    [Fact]
    public void An_absent_reading_says_so_and_a_faulted_pass_keeps_the_previous_reading_and_degrades()
    {
        var census = new DataVolumeUsageCensus();
        census.Describe().Should().Contain("no data volume configured");

        census.SetRoots([root]);
        census.Describe().Should().Contain("NOT MEASURED YET");
        census.IsDegraded.Should().BeFalse();

        File("modules/MeshWeaver.AI@1/MeshWeaver.AI.dll", 10 * KiB);
        census.Record(DataVolumeUsage.Measure(root, ImmutableHashSet<string>.Empty, () => Now));
        census.Describe().Should().Contain("modules/");
        census.IsDegraded.Should().BeFalse();

        census.RecordFault(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), new IOException("share went away"));
        census.IsDegraded.Should().BeTrue();
        census.Describe().Should().Contain("FAULTED (IOException: share went away)");
        census.Describe().Should().Contain("modules/");
    }

    [Fact]
    public void A_relative_configured_root_reads_as_measured_once_walked()
    {
        // Copilot on #6992: Measure records the CANONICAL path; a census keyed by the configured
        // string ("./x/../data") never found it and /health said NOT MEASURED YET for ever.
        File("modules/MeshWeaver.AI@1/MeshWeaver.AI.dll", 10 * KiB);
        var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), root);
        var configured = Path.Combine(relative, "sub", "..");
        var census = new DataVolumeUsageCensus();

        census.SetRoots(DataVolumeUsage.RootsOf([configured]));
        DataVolumeUsageHostedService.Apply(census, census.Roots.Single(),
            () => DataVolumeUsage.Measure(configured, ImmutableHashSet<string>.Empty, () => Now), logger: null);

        census.Roots.Single().Should().Be(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)));
        census.Describe().Should().NotContain("NOT MEASURED YET");
        census.Describe().Should().Contain("modules/");
    }

    [Fact]
    public void A_cancelled_walk_never_replaces_the_previous_reading()
    {
        // Copilot on #6992: a cancelled walk used to BREAK and return partial totals that read
        // "every directory read"; it must throw, and the census must keep the last complete walk.
        File("modules/MeshWeaver.AI@1/MeshWeaver.AI.dll", 10 * KiB);
        var key = DataVolumeUsage.RootsOf([root]).Single();
        var census = new DataVolumeUsageCensus();
        census.SetRoots([key]);
        DataVolumeUsageHostedService.Apply(census, key,
            () => DataVolumeUsage.Measure(root, ImmutableHashSet<string>.Empty, () => Now), logger: null);
        File("prebuilt-bundles/c003e001/plugins/Store.zip", 50 * KiB);
        using var cancelled = new System.Threading.CancellationTokenSource();
        cancelled.Cancel();

        Func<DataVolumeUsageReading> walk = () => DataVolumeUsage.Measure(root, ImmutableHashSet<string>.Empty, () => Now, cancelled.Token);
        Assert.ThrowsAny<OperationCanceledException>(() => walk());
        DataVolumeUsageHostedService.Apply(census, key, walk, logger: null);

        census.Latest[key].TotalBytes.Should().Be(10 * KiB);
        census.IsDegraded.Should().BeTrue();
        census.Describe().Should().Contain("FAULTED");
    }

    [Fact]
    public void A_root_that_vanishes_after_a_good_walk_keeps_the_good_walk_and_degrades()
    {
        // Copilot on #6992: a Fault-bearing reading used to be RECORDED as the latest one,
        // replacing the last real measurement with "nothing was measured".
        File("modules/MeshWeaver.AI@1/MeshWeaver.AI.dll", 10 * KiB);
        var key = DataVolumeUsage.RootsOf([root]).Single();
        var census = new DataVolumeUsageCensus();
        census.SetRoots([key]);
        DataVolumeUsageHostedService.Apply(census, key,
            () => DataVolumeUsage.Measure(root, ImmutableHashSet<string>.Empty, () => Now), logger: null);
        Directory.Delete(root, recursive: true);

        DataVolumeUsageHostedService.Apply(census, key,
            () => DataVolumeUsage.Measure(root, ImmutableHashSet<string>.Empty, () => Now), logger: null);

        census.Latest[key].Fault.Should().BeNull();
        census.Latest[key].TotalBytes.Should().Be(10 * KiB);
        census.IsDegraded.Should().BeTrue();
        census.Describe().Should().Contain("FAULTED");
        census.Describe().Should().Contain("modules/");
    }

    [Fact]
    public void The_interval_defaults_and_is_clamped_to_its_floor()
    {
        DataVolumeUsage.IntervalOf(null).Should().Be(DataVolumeUsage.DefaultInterval);
        DataVolumeUsage.IntervalOf("not a timespan").Should().Be(DataVolumeUsage.DefaultInterval);
        DataVolumeUsage.IntervalOf("00:00:05").Should().Be(DataVolumeUsage.MinimumInterval);
        DataVolumeUsage.IntervalOf("12:00:00").Should().Be(TimeSpan.FromHours(12));
    }
}
