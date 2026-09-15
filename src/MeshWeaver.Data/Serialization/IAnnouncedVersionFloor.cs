namespace MeshWeaver.Data.Serialization;

/// <summary>
/// 🚨 The freshness floor of ONE remote mirror — the version the mesh change feed announced for
/// the mirror's owner, which a cross-hub write's base read must reach before it may be diffed
/// against (issue #1174).
///
/// <para><b>Why it is a member of the mirror and not an entry in a map beside the cache.</b> The
/// first cut kept a per-owner map: "does the cache still mirror this owner?", then write the map.
/// Those two steps were not atomic with the cache's removals, so a mirror removed in between (an
/// idle release, an eviction, a fault) left a floor with nothing behind it, and the NEXT mirror
/// built for that owner inherited it (review finding on PR #4428,
/// <c>AnnouncedFloorLifetimeTest</c>). A floor that is a field of the instance cannot outlive the
/// instance and cannot reach a different one: whatever removes a mirror removes its floor, and a
/// new mirror starts with none — which is correct, because it hydrates from the owner's current
/// state, at or past every version announced before it.</para>
///
/// <para>Internal, like <see cref="IStreamLivenessSource"/>: it is a contract between the workspace's
/// stream cache and the write path, not part of the public stream surface.</para>
/// </summary>
internal interface IAnnouncedVersionFloor
{
    /// <summary>The highest version announced for this mirror's owner, or <c>0</c> for none.</summary>
    long AnnouncedVersion { get; }

    /// <summary>
    /// Raises the floor to <paramref name="version"/> if that is higher — monotonic, lock-free — and
    /// returns the floor now in force.
    /// </summary>
    long RaiseAnnouncedVersion(long version);
}
