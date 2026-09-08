namespace MeshWeaver.Mesh.Persistence;

/// <summary>
/// The pure half of the storage-capacity health check: given a volume reading and a floor, say
/// whether the volume is too full to publish into and describe it in one line. The host-side
/// <c>IHealthCheck</c> (Memex.Portal.ServiceDefaults) reads the assembly store's root, takes the
/// reading with <see cref="VolumeCapacity.Of"/>, and reports this verdict — so the description an
/// operator reads on <c>/health</c> is assertable without a host.
///
/// <para>Why it exists: on 2026-09-08 <c>/data</c> on memex sat at 3 MiB free for hours and the only
/// symptom was a NodeType that would not activate. A full volume is a platform fact, and
/// <c>/health</c> is where platform facts are read — but it must never pull a pod: a replica on a
/// full share still serves every page whose bytes are already loaded, and pulling it would turn
/// "cannot compile" into "cannot serve". Degraded, never Unhealthy.</para>
/// </summary>
public static class StorageCapacityHealth
{
    /// <summary>The name the check is registered under on <c>/health</c>.</summary>
    public const string HealthCheckName = "storage_capacity";

    /// <summary>
    /// Configuration key for the free-space floor in MiB. Below it the check reports Degraded.
    /// </summary>
    public const string MinimumFreeMiBConfigKey = "AssemblyCache:MinimumFreeMiB";

    /// <summary>
    /// The shipped floor: 256 MiB. A NodeType compile publishes a DLL and a PDB of a few hundred
    /// KiB to a few MiB, a module landing a bundle of ~1.6 MiB, and a rolling image seeds every
    /// prebuilt bundle at once — so the floor is a roll's worth of headroom, not a single write's.
    /// </summary>
    public const long DefaultMinimumFreeMiB = 256;

    /// <summary>The configured floor in bytes, or the default when unset or malformed.</summary>
    /// <param name="configured">The raw configuration value under <see cref="MinimumFreeMiBConfigKey"/>.</param>
    /// <returns>The floor in bytes.</returns>
    public static long MinimumFreeBytes(string? configured) =>
        (long.TryParse(configured, out var mib) && mib >= 0 ? mib : DefaultMinimumFreeMiB) * 1024 * 1024;

    /// <summary>
    /// The verdict over one volume reading.
    /// </summary>
    /// <param name="path">The path the reading was taken on (named in the description).</param>
    /// <param name="capacity">The reading, or <c>null</c> when the volume could not be read.</param>
    /// <param name="minimumFreeBytes">The floor below which the volume is reported as low.</param>
    /// <returns><c>IsLow</c> when the volume should be reported Degraded, and the one-line
    /// description for <c>/health</c> either way.</returns>
    public static (bool IsLow, string Description) Evaluate(string path, VolumeCapacity? capacity, long minimumFreeBytes)
    {
        if (capacity is not { } c)
            return (false, $"storage volume at '{path}' could not be read — no capacity verdict");

        var floorMiB = minimumFreeBytes / (1024 * 1024);
        return c.FreeBytes < minimumFreeBytes
            ? (true,
                $"storage volume at '{path}' is below the {floorMiB:N0} MiB free-space floor: "
                + $"{c.FreeMiB:N0} MiB free of {c.TotalMiB:N0} MiB total. NodeType compiles and module "
                + "landings write into this volume, and a write on a full volume lands short and is "
                + "refused. Free space on the volume, then recompile any type that reports the refusal.")
            : (false,
                $"storage volume at '{path}' has {c.FreeMiB:N0} MiB free of {c.TotalMiB:N0} MiB total "
                + $"(floor {floorMiB:N0} MiB)");
    }
}
