using System.Collections.Immutable;

namespace MeshWeaver.Mesh.Services;

/// <summary>One recorded version of a node, as a history prune sees it: which node, which version,
/// and when that version was written.</summary>
/// <param name="Path">The node path the version belongs to.</param>
/// <param name="Version">The version number.</param>
/// <param name="LastModified">When the version was written.</param>
public sealed record VersionHistoryRow(string Path, long Version, DateTimeOffset LastModified);

/// <summary>
/// What a history prune of ONE partition removes. This record is the rule itself, so every store
/// prunes to the same rule (<c>FileSystemVersionStore</c> applies <see cref="Select"/> directly, and
/// the Postgres store in MeshWeaver.Plugins states the same rule in SQL). It has no clock and no
/// store of its own, so the rule can be tested without either.
///
/// <para><b>A recorded version is removed only when ALL of these hold:</b></para>
/// <list type="number">
///   <item>it is not among the newest <see cref="KeepLast"/> versions of its node, so the current
///     version is never removed (<see cref="KeepLast"/> is at least 1);</item>
///   <item>it was written before <see cref="Cutoff"/>;</item>
///   <item>it is not the newest version written before <see cref="Cutoff"/>. That version is the
///     state the node was in when the window opened. Keeping it means every state inside the
///     window can still be rebuilt: a point-in-time restore to any instant after the cutoff, and
///     the undo of any change made after it (undo reads the version just BEFORE a change).</item>
/// </list>
/// <para>An undated row (<c>default</c> timestamp) is kept: a prune can never age a row whose age
/// it cannot read. At most <see cref="MaxRows"/> rows go per call, oldest first, so a backlog
/// drains over successive runs instead of one run becoming a mass delete.</para>
/// </summary>
/// <param name="Cutoff">Versions written before this instant may go.</param>
/// <param name="KeepLast">How many of each node's newest versions always stay (clamped to at least 1).</param>
/// <param name="MaxRows">The most rows one call removes.</param>
public sealed record VersionHistoryPrune(DateTimeOffset Cutoff, int KeepLast, int MaxRows)
{
    /// <summary>The newest-versions floor actually applied: never below 1, so the current version stays.</summary>
    public int EffectiveKeepLast => Math.Max(1, KeepLast);

    /// <summary>
    /// The rows among <paramref name="rows"/> this prune removes, oldest first, at most
    /// <see cref="MaxRows"/>. Pure.
    /// </summary>
    /// <param name="rows">Every recorded version of the partition (or any subset holding whole nodes).</param>
    public ImmutableList<VersionHistoryRow> Select(IEnumerable<VersionHistoryRow> rows)
    {
        if (MaxRows <= 0)
            return ImmutableList<VersionHistoryRow>.Empty;
        var keepLast = EffectiveKeepLast;
        return rows
            .GroupBy(r => r.Path, StringComparer.Ordinal)
            .SelectMany(node =>
            {
                var newestFirst = node.OrderByDescending(r => r.Version).ToList();
                // The state at the window's opening: the newest version written before the cutoff.
                var atCutoff = newestFirst.FirstOrDefault(r => r.LastModified != default && r.LastModified < Cutoff);
                return newestFirst
                    .Skip(keepLast)
                    .Where(r => r.LastModified != default && r.LastModified < Cutoff)
                    .Where(r => atCutoff is null || r.Version != atCutoff.Version);
            })
            .OrderBy(r => r.LastModified)
            .ThenBy(r => r.Path, StringComparer.Ordinal)
            .ThenBy(r => r.Version)
            .Take(MaxRows)
            .ToImmutableList();
    }
}

/// <summary>What one history prune did.</summary>
/// <param name="Supported">False when the store cannot prune history (its rows, if any, are untouched).
/// True when it pruned, or when it holds no history at all.</param>
/// <param name="Removed">How many recorded versions it removed.</param>
public sealed record VersionHistoryPruneOutcome(bool Supported, int Removed)
{
    /// <summary>The store cannot prune: nothing was removed, and whatever it records is still there.</summary>
    public static VersionHistoryPruneOutcome NotSupported { get; } = new(false, 0);

    /// <summary>The store pruned and removed <paramref name="removed"/> rows.</summary>
    /// <param name="removed">How many rows went.</param>
    public static VersionHistoryPruneOutcome Pruned(int removed) => new(true, removed);
}
