using System.Collections.Immutable;
using MeshWeaver.PluginCatalog;

namespace MeshWeaver.PluginTester;

/// <summary>One shard of a fanned-out gate run: <c>--shard &lt;Index&gt;/&lt;Total&gt;</c>, 1-based.</summary>
/// <param name="Index">1-based shard number.</param>
/// <param name="Total">How many shards the run was split into.</param>
public sealed record GateShard(int Index, int Total)
{
    /// <summary>Parses the <c>i/n</c> form, or returns the reason it is not one.</summary>
    public static (GateShard? Shard, string? Problem) Parse(string value)
    {
        var slash = value.IndexOf('/');
        if (slash <= 0
            || !int.TryParse(value[..slash], out var index)
            || !int.TryParse(value[(slash + 1)..], out var total))
            return (null, $"'{value}' is not a shard — pass '<index>/<total>', 1-based (e.g. 2/4).");
        if (total < 1)
            return (null, $"'{value}' names {total} shard(s) — the total must be at least 1.");
        if (index < 1 || index > total)
            return (null, $"'{value}' is out of range — the index is 1-based and at most the total ({total}).");
        return (new GateShard(index, total), null);
    }

    public override string ToString() => $"{Index}/{Total}";
}

/// <summary>
/// Splits a gate run's discovered packages across shards — the fan-out that took the node-repo
/// gate off its 30-minute cap. The tax being parallelised is <c>portal/nodeops</c> saturation
/// (MeshWeaver#2543), paid once per package install; shards share no mesh and no runner, so it does
/// not follow them.
///
/// <para>🚨 <b>Why a shard MOUNTS more than it GATES.</b> The gate boots a fresh mesh: a package
/// whose <c>requires</c> / <c>shared=@Other/Source</c> / <c>nodeType: Other/Type</c> points at a
/// sibling cannot install unless that sibling landed first. So a shard installs its slice PLUS the
/// forward dependency closure of that slice, and gates only the slice — the same
/// installed-but-not-gated shape an upstream package already has
/// (<see cref="PackageResult.Upstream"/>), for the same reason: a verdict belongs to exactly one
/// place, and double-judging a package would let one shard's flake red a slice that did not
/// change. Every package is therefore gated EXACTLY ONCE across the whole fan-out, which is what
/// makes the shards' summaries mergeable into one and their union checkable against the discovered
/// set.</para>
///
/// <para>🚨 <b>Why equal COUNTS and not a weight table.</b> Measured on MeshWeaver.Plugins run
/// 33758875713 (59 packages, 902 s of attributed per-package wall-clock): every structural proxy a
/// checkout can offer is anti-correlated enough to lose to plain equal counts. Files: Store is 185
/// files / 56 s while RolePlay is 38 files / 129 s. NodeTypes: Store is 17 types / 56 s while
/// RolePlay is 4 types / 129 s. Simulated makespans at 4 shards — equal counts 8.8 min, files
/// 13.4, NodeType count 14.1, test-source bytes 16.6, and a perfect oracle 8.4. Equal counts lands
/// within 0.4 min of the oracle at every shard count from 2 to 6, so this deliberately carries NO
/// weight table: there is nothing to re-measure and nothing to go stale (contrast
/// <c>.github/scripts/shard-assign.sh</c>, whose table drifted for three weeks unnoticed because
/// the loop balances the NUMBERS, not the clock).</para>
///
/// <para>The slice is taken by STRIDE over the dependency order rather than as a contiguous block:
/// both give equal counts, and the stride spreads a run of adjacent heavyweights (the order is
/// alphabetical among the ready set, so neighbours are unrelated) across different runners.</para>
/// </summary>
public static class GateShardPlan
{
    /// <summary>What one shard installs and what it gates.</summary>
    /// <param name="Gated">The packages this shard judges, in dependency order.</param>
    /// <param name="Support">
    /// Packages installed only so <paramref name="Gated"/>'s installs resolve — gated on another
    /// shard, in dependency order.
    /// </param>
    /// <param name="Installed">
    /// Everything the shard installs. 🚨 In the RUN's dependency order, never the slice's own and
    /// never alphabetical: a support package must still land before the package that needs it.
    /// </param>
    public sealed record Assignment(
        ImmutableList<PackageManifest> Gated,
        ImmutableList<PackageManifest> Support,
        ImmutableList<PackageManifest> Installed);

    /// <summary>
    /// Assigns <paramref name="ordered"/> (already dependency-ordered) to <paramref name="shard"/>.
    /// The install list preserves <paramref name="ordered"/>'s order — never the slice's own — so a
    /// support package still lands before the package that needs it.
    /// </summary>
    public static Assignment Assign(
        IReadOnlyList<PackageManifest> ordered,
        ImmutableDictionary<string, ImmutableHashSet<string>> dependencies,
        GateShard shard)
    {
        var gatedIds = ordered
            .Where((_, position) => position % shard.Total == shard.Index - 1)
            .Select(p => p.Id)
            .ToImmutableHashSet(StringComparer.Ordinal);

        // Forward closure of the slice — transitive, because a dependency's own dependencies must
        // land too (a shard mounting only the DIRECT ones would fail its installs one level down).
        var needed = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(gatedIds);
        while (pending.Count > 0)
        {
            foreach (var dependency in dependencies.GetValueOrDefault(pending.Pop(), []))
                if (!gatedIds.Contains(dependency) && needed.Add(dependency))
                    pending.Push(dependency);
        }
        var supportIds = needed.ToImmutable();

        return new Assignment(
            ordered.Where(p => gatedIds.Contains(p.Id)).ToImmutableList(),
            ordered.Where(p => supportIds.Contains(p.Id)).ToImmutableList(),
            ordered.Where(p => gatedIds.Contains(p.Id) || supportIds.Contains(p.Id))
                .ToImmutableList());
    }

    /// <summary>
    /// The line every shard prints before it installs anything. It is the shard's RECEIPT: the
    /// aggregate job reads one per shard and refuses a run whose slices are not a disjoint cover of
    /// the discovered set — a fan-out whose parts silently disagree about the whole is the same
    /// defect as a skipped job rendering a green tick.
    /// </summary>
    public static string Describe(
        GateShard shard, int discovered, Assignment assignment) =>
        $"shard {shard}: gating {assignment.Gated.Count} of {discovered} discovered package(s)"
        + $" — {Join(assignment.Gated)}; installing {assignment.Support.Count} support package(s)"
        + $" gated on another shard: {Join(assignment.Support)}";

    private static string Join(IReadOnlyList<PackageManifest> packages) =>
        packages.Count == 0 ? "(none)" : string.Join(", ", packages.Select(p => p.Id));
}
