using System.Collections.Immutable;
using System.Globalization;

namespace MeshWeaver.Hosting;

/// <summary>One data path's free-space reading: the volume it sits on, and what is free of what.</summary>
/// <param name="Path">The configured data path.</param>
/// <param name="Volume">The volume (mount) the path resolves to, or null when it could not be read.</param>
/// <param name="FreeBytes">Bytes available to this process on the volume, or null when unreadable.</param>
/// <param name="TotalBytes">The volume's size, or null when unreadable.</param>
/// <param name="Fault">Why the reading failed, or null.</param>
public sealed record DataVolumeReading(string Path, string? Volume, long? FreeBytes, long? TotalBytes, string? Fault)
{
    /// <summary>Bytes in use, when both sides are known.</summary>
    public long? UsedBytes => FreeBytes is { } free && TotalBytes is { } total ? total - free : null;

    /// <summary>
    /// True when <see cref="Path"/> does not exist YET and the reading is of the mounted volume it
    /// will be created on (<see cref="Volume"/>): a configured store root nothing has written to — the
    /// control instance's <c>/artifacts/{rid}/prebuilt-bundles</c> before its first bundle — is a
    /// measurable volume, not a fault.
    /// </summary>
    public bool NotCreatedYet { get; init; }
}

/// <summary>What the free-space signal decided.</summary>
/// <param name="Degraded">True when any volume is below the threshold or could not be measured.</param>
/// <param name="Description">One line naming each path, its free/used/total, and the threshold.</param>
/// <param name="Readings">Every reading, one per configured path.</param>
public sealed record DataVolumeVerdict(bool Degraded, string Description, ImmutableList<DataVolumeReading> Readings);

/// <summary>
/// The free-space signal for the data volume the shared stores live on — the prebuilt-bundle
/// store, the modules root, the assembly cache and the DataProtection key ring all share one
/// Azure Files share on AKS. A FULL share truncates writes silently (memex.systemorph.com,
/// 2026-09-08: 3 MiB free, every runtime recompile landing as <c>Bad IL format</c>, CD's bake
/// read-back <c>ResourceNotFound</c> for 39/45 files), and nothing said so until then. Pure over a
/// probe seam, so the verdict is testable without a real mount; the host's <c>IHealthCheck</c>
/// reads it — Degraded, never Unhealthy, because pulling the pod would not free a byte.
/// </summary>
public static class DataVolumeFreeSpace
{
    /// <summary>The health-check name.</summary>
    public const string HealthCheckName = "data_volume_free_space";

    /// <summary>Config key: the free-bytes threshold below which the volume reads Degraded. Default 1 GiB.</summary>
    public const string MinimumFreeBytesConfigKey = "DataVolume:MinimumFreeBytes";

    /// <summary>Config section listing ADDITIONAL paths to watch (an array); the store roots below are watched always.</summary>
    public const string PathsConfigKey = "DataVolume:Paths";

    /// <summary>
    /// The modules root key — mirrors <c>MeshWeaver.PluginCatalog.ModuleRoot.ConfigKey</c>, which
    /// this assembly cannot reference (PluginCatalog references Hosting). Pinned equal by
    /// <c>DataVolumeHealthCheckTest</c>, which sees both.
    /// </summary>
    public const string ModulesRootConfigKey = "Modules:Root";

    /// <summary>1 GiB.</summary>
    public const long DefaultMinimumFreeBytes = 1L << 30;

    /// <summary>The config keys whose values name paths on the data volume.</summary>
    public static readonly ImmutableList<string> PathConfigKeys =
        [ShippedPrebuiltBundles.PublishedRootConfigKey, ModulesRootConfigKey];

    /// <summary>Read the threshold from configuration; absent or malformed ⇒ <see cref="DefaultMinimumFreeBytes"/>.</summary>
    public static long MinimumFreeBytesOf(string? configured) =>
        long.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes) && bytes >= 0
            ? bytes
            : DefaultMinimumFreeBytes;

    /// <summary>The production probe: <see cref="DriveInfo"/> over the path (statvfs on Unix, so any path on the mount answers).</summary>
    public static DataVolumeReading Probe(string path) => ProbeWithin(path, DataVolumeUsage.MountPoints());

    /// <summary>
    /// The probe over a given set of mount points. A path that EXISTS is measured as before. A path
    /// that does not exist yet is measured on the deepest mounted volume above it
    /// (<see cref="MountAbove"/>) and marked <see cref="DataVolumeReading.NotCreatedYet"/>: a store
    /// root is created by its first write, and until then the volume it will land on is what there is
    /// to measure. A path with no mounted volume above it (only the container's root filesystem) stays
    /// a FAULT — that is what a dropped volume mount looks like, and it must keep reading Degraded.
    /// </summary>
    /// <param name="path">The configured data path.</param>
    /// <param name="mountPoints">The mount points this process sees (<see cref="DataVolumeUsage.MountPoints"/>).</param>
    public static DataVolumeReading ProbeWithin(string path, IEnumerable<string> mountPoints)
    {
        try
        {
            if (Directory.Exists(path) || File.Exists(path))
            {
                var drive = new DriveInfo(path);
                return new DataVolumeReading(path, drive.Name, drive.AvailableFreeSpace, drive.TotalSize, null);
            }
            if (MountAbove(path, mountPoints) is not { } mount || !Directory.Exists(mount))
                return new DataVolumeReading(path, null, null, null,
                    "the path does not exist and no volume is mounted above it — the volume mount is missing");
            var volume = new DriveInfo(mount);
            return new DataVolumeReading(path, volume.Name, volume.AvailableFreeSpace, volume.TotalSize, null)
            {
                NotCreatedYet = true,
            };
        }
        catch (Exception ex)
        {
            return new DataVolumeReading(path, null, null, null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The deepest mount point that contains <paramref name="path"/>, excluding the root filesystem
    /// (<c>/</c>) — or null when only the root filesystem is above it. Pure over the given mounts.
    /// </summary>
    /// <param name="path">Any path, existing or not.</param>
    /// <param name="mountPoints">Mount points, by any spelling.</param>
    public static string? MountAbove(string path, IEnumerable<string> mountPoints)
    {
        var full = DataVolumeUsage.Canonical(path);
        return mountPoints
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(DataVolumeUsage.Canonical)
            .Where(m => Path.GetPathRoot(m) is not { } root || !string.Equals(Path.TrimEndingDirectorySeparator(root), m, StringComparison.Ordinal))
            .Where(m => m.Length > 0
                && (string.Equals(full, m, StringComparison.Ordinal)
                    || full.StartsWith(m + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            .OrderByDescending(m => m.Length)
            .FirstOrDefault();
    }

    /// <summary>
    /// The verdict over the given paths. No paths ⇒ Healthy ("no data volume configured"). A path
    /// that cannot be measured is Degraded: a check that cannot reach its evidence says so rather
    /// than defaulting to the reassuring answer.
    /// </summary>
    public static DataVolumeVerdict Evaluate(
        IEnumerable<string> paths, Func<string, DataVolumeReading> probe, long minimumFreeBytes)
    {
        var readings = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.Ordinal)
            .Select(probe)
            .ToImmutableList();
        if (readings.IsEmpty)
            return new DataVolumeVerdict(false, "no data volume configured — nothing to measure", readings);

        var degraded = false;
        var parts = new List<string>();
        foreach (var reading in readings)
        {
            if (reading.Fault is not null || reading.FreeBytes is null || reading.TotalBytes is null)
            {
                degraded = true;
                parts.Add($"{reading.Path}: free space could not be measured ({reading.Fault ?? "no reading"})");
                continue;
            }
            var low = reading.FreeBytes.Value < minimumFreeBytes;
            degraded |= low;
            var where = reading.NotCreatedYet
                ? $"not created yet — measured on the volume mounted at {reading.Volume}"
                : $"volume {reading.Volume}";
            parts.Add(
                $"{reading.Path} ({where}): {Mb(reading.FreeBytes.Value)} free of "
                + $"{Mb(reading.TotalBytes.Value)} ({Mb(reading.UsedBytes!.Value)} used)"
                + (low ? $" — BELOW the {Mb(minimumFreeBytes)} threshold" : ""));
        }
        return new DataVolumeVerdict(degraded, string.Join("; ", parts), readings);
    }

    private static string Mb(long bytes) =>
        (bytes / (1024d * 1024d)).ToString("N0", CultureInfo.InvariantCulture) + " MiB";
}
