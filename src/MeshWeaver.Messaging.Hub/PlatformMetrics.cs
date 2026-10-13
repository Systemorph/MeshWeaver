using System.Diagnostics.Metrics;

namespace MeshWeaver.Messaging;

/// <summary>
/// 🚨 <b>The platform's own meter — the first one (#3488).</b> Before this, <c>grep -rn "new
/// Meter(" src/</c> returned <b>zero results</b> and <c>System.Diagnostics.Metrics</c> was not
/// referenced anywhere: every counter any investigation had ever used was a .NET runtime built-in
/// standing in for what was actually wanted.
///
/// <para><b>What that cost, and why it is not merely inconvenient.</b> #3432 asks why ~6,925
/// <c>Started</c> <c>sync/</c> hubs hold ~2.7 GB of a 3.5 GB heap. Nothing reported live hub
/// counts, the <c>sync/</c>-versus-node split, or the RunLevel histogram — so the only way to
/// answer was a heap dump against a live replica, and that <b>restarts the replica</b>
/// (106 s of suspended threads against a 90 s liveness budget). The freeze scales with heap size,
/// which makes it self-defeating: the population only exists on a replica fat enough that measuring
/// it destroys the state being measured. #3321's author asked readers to <i>"expect the curve to
/// flatten, not vanish"</i> — and nobody could see the curve.</para>
///
/// <para>🚨 <b>OBSERVABLE GAUGES, never counters, and the choice is the design.</b> A counter has
/// to be incremented at every add site and decremented at every remove site; miss one — a hub that
/// dies down a path nobody thought about, which is precisely #3321's 1,485 — and the number drifts
/// from reality while still looking like a measurement. A gauge computed at collection time walks
/// the live tree and cannot disagree with it. This repository's own rule: a field that cannot fail
/// is not a measurement.</para>
///
/// <para>🚨 <b>An INSTANCE owned by the mesh, never a static.</b> The conventional
/// <c>static readonly Meter</c> would survive mesh disposal and bleed across every test in a
/// process — the exact shape <c>Doc/Architecture/NoStaticState</c> forbids — and would report two
/// meshes' hubs as one population.</para>
///
/// <para><b>Deliberately v1.</b> Hub counts, split by address kind and RunLevel: the two figures
/// the last two investigations actually needed. Streams, queues and callbacks are not here — each
/// wants its own owner and its own scrape cost, and a meter that tries to answer everything on day
/// one is how instrumentation becomes the thing nobody trusts.</para>
/// </summary>
public sealed class PlatformMetrics : IDisposable
{
    /// <summary>The meter name a collector subscribes to.</summary>
    public const string MeterName = "MeshWeaver.Platform";

    private readonly Meter meter;

    /// <summary>Creates the mesh's meter and arms its gauges.</summary>
    /// <param name="root">The hub whose tree is the population — the mesh's root.</param>
    public PlatformMetrics(IMessageHub root)
    {
        ArgumentNullException.ThrowIfNull(root);
        meter = new Meter(MeterName);
        meter.CreateObservableGauge(
            "meshweaver.hubs.live",
            () => Observe(root),
            unit: "{hub}",
            description:
                "Hubs alive in this mesh, by address kind, satellite segment and run level. Computed "
                + "by walking the live tree at collection time, so it cannot drift from the population "
                + "it reports.");
    }

    /// <summary>
    /// One measurement per (kind, runlevel) pair present right now.
    ///
    /// <para>🚨 <b>Never throws.</b> An exception out of a gauge callback is swallowed by the
    /// metrics infrastructure and the instrument silently stops reporting — a meter that goes
    /// quiet is indistinguishable from a mesh with no hubs, which is the failure mode this whole
    /// issue is about. On a fault it reports nothing for this scrape and the next one tries
    /// again.</para>
    /// </summary>
    private static IEnumerable<Measurement<long>> Observe(IMessageHub root)
    {
        // 🚨 A dead ROOT is a torn-down mesh, and reporting one Dead hub for it is noise the
        // summary above already promises not to emit — nothing throws on a disposed hub, so
        // without this the promise was prose. This is the root ONLY: a Dead hub INSIDE a live
        // tree is exactly what the runlevel tag exists to show ("how many are still running
        // versus already dead" is the question the meter was added for).
        if (root.RunLevel == MessageHubRunLevel.Dead)
            yield break;

        Dictionary<(string Kind, string Satellite, string RunLevel), long> counts;
        try
        {
            counts = Tally(root);
        }
        catch
        {
            // A torn-down mesh, or a tree mutating under the walk. Report nothing rather than
            // poison the instrument.
            yield break;
        }

        foreach (var ((kind, satellite, runLevel), count) in counts)
            yield return new Measurement<long>(count,
                new KeyValuePair<string, object?>("kind", kind),
                new KeyValuePair<string, object?>("satellite", satellite),
                new KeyValuePair<string, object?>("runlevel", runLevel));
    }

    /// <summary>The <c>satellite</c> tag of a hub whose address has no satellite segment.</summary>
    public const string NoSatellite = "none";

    /// <summary>The <c>satellite</c> tag every value past <see cref="MaxSatelliteValues"/> folds into.</summary>
    public const string OtherSatellite = "_other";

    /// <summary>
    /// How many distinct satellite values one scrape reports by name (#6632). Satellite segment names
    /// are a small, module-declared set, but nothing ENFORCES that a segment starting with <c>_</c>
    /// is one — so the bound is the meter's, not the deployment's: the most populous values are named
    /// and the rest fold into <see cref="OtherSatellite"/>, never into a new series each.
    /// </summary>
    public const int MaxSatelliteValues = 32;

    internal static Dictionary<(string, string, string), long> Tally(IMessageHub root)
    {
        var raw = new Dictionary<(string, string, string), long>();
        var hubs = root is MessageHub messageHub
            ? messageHub.LiveHubTree()
            : [root];
        foreach (var hub in hubs)
        {
            var key = (KindOf(hub.Address), SatelliteOf(hub.Address), hub.RunLevel.ToString());
            raw[key] = raw.TryGetValue(key, out var existing) ? existing + 1 : 1;
        }

        var named = raw
            .GroupBy(e => e.Key.Item2)
            .Where(g => g.Key != NoSatellite)
            .OrderByDescending(g => g.Sum(e => e.Value))
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(MaxSatelliteValues)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);
        var counts = new Dictionary<(string, string, string), long>();
        foreach (var ((kind, satellite, runLevel), count) in raw)
        {
            var key = (kind, satellite == NoSatellite || named.Contains(satellite) ? satellite : OtherSatellite, runLevel);
            counts[key] = counts.TryGetValue(key, out var existing) ? existing + count : count;
        }
        return counts;
    }

    /// <summary>
    /// 🚨 The address's SATELLITE shape (#6632): its LAST segment that starts with <c>_</c>
    /// (<c>_DocumentPart</c>, <c>_Activity</c>, <c>_Thread</c>, …), or <see cref="NoSatellite"/>.
    /// The kind alone folded 5,000 <c>Admin/content/_Documents/…/_DocumentPart/{index}</c> hubs into
    /// <c>kind=Admin</c> during the 2026-10-04/05 OOM burst, so nothing named the address shape that
    /// drove it. The segment NAME, never the ids around it: a satellite name is a type-like word, the
    /// ids are unbounded — and <see cref="MaxSatelliteValues"/> bounds the names too.
    /// </summary>
    internal static string SatelliteOf(Address? address)
    {
        if (address?.Segments is not { Length: > 1 } segments)
            return NoSatellite;
        for (var i = segments.Length - 1; i >= 1; i--)
            if (segments[i] is { Length: > 1 } segment && segment[0] == '_')
                return segment;
        return NoSatellite;
    }

    /// <summary>
    /// The address's KIND — its type segment, never the address itself.
    ///
    /// <para>🚨 A per-address tag would be unbounded cardinality: one time series per
    /// <c>sync/{guid}</c>, which is the population that reached 6,925 on one replica. The whole
    /// question is "how many of each sort", and the sort is the type.</para>
    ///
    /// <para>🚨 <b><see cref="Address.Type"/>, never <c>ToString()</c>.</b> This first split
    /// <c>ToString()</c> at its first <c>/</c>, and <c>ToString()</c> carries the HOST chain for a
    /// hosted address — so the first <c>/</c> can come out of the host and the kind became a
    /// host-derived string like <c>admin~portal</c>, silently re-admitting host ids to the tag and
    /// recreating the exact unbounded cardinality the paragraph above forbids. <c>Type</c> is
    /// <c>Segments[0]</c>: the type segment by construction, host-free, no parsing.</para>
    /// </summary>
    private static string KindOf(Address? address)
    {
        var type = address?.Type;
        return string.IsNullOrEmpty(type) ? "unknown" : type;
    }

    /// <summary>Releases the meter with the mesh that owns it.</summary>
    public void Dispose() => meter.Dispose();
}
