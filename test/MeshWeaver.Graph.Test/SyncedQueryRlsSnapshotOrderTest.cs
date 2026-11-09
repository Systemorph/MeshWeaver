using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The per-user RLS filter on a synced query (<c>SyncedQueryDataSourceExtensions.FilterByReadPermission</c>)
/// must emit the NEWEST upstream snapshot's answer LAST, whatever order the permission probes finish in.
///
/// <para><b>The defect.</b> The filter was a plain <c>SelectMany</c> over snapshots, which emits each
/// snapshot's filtered list whenever ITS probes finish. Each snapshot is the whole result set, so
/// whatever comes out last is what every <c>Replay(1)</c> consumer holds. A probe on a cold
/// permission fold is slower than one on a warm fold, so a listing's first snapshot routinely
/// finished after the next one, and the reader was left holding the stale set: a node they may read
/// stayed absent from their listing until some unrelated write under the query moved it again.
/// Measured in Plugins <c>SyncedQueryTest.DeleteAccessAssignment_RemovesFromSyncedCollection</c> (a
/// 16-node Initial in, a 17-node frame in, 17 out, 16 out — the test then waited 15 s for a node that
/// the upstream had already delivered).</para>
///
/// <para><b>The interleaving, forced.</b> The FIRST probe the filter issues is held open by the test;
/// every later probe answers at once. That is exactly the cold-then-warm shape, made deterministic:
/// no clock, no thread, nothing to wait for — every step runs inline on the test thread.</para>
/// </summary>
public class SyncedQueryRlsSnapshotOrderTest
{
    private static MeshNode Node(string id) => new(id, "probe");

    /// <summary>
    /// A newer snapshot whose probes answer first must not be overwritten by the older snapshot's
    /// late answer: the last emission is the newest snapshot, filtered.
    /// </summary>
    [Fact]
    public void The_newest_snapshot_is_emitted_last_when_an_older_snapshots_probe_finishes_late()
    {
        var upstream = new Subject<IEnumerable<MeshNode>>();
        var coldProbe = new Subject<bool>();
        var probes = 0;
        // The first probe ever issued is the cold one (held open); every later one is warm.
        IObservable<bool> CanRead(MeshNode _) =>
            ++probes == 1 ? coldProbe : Observable.Return(true);

        var emitted = ImmutableList<ImmutableHashSet<string>>.Empty;
        using var sub = SyncedQueryDataSourceExtensions
            .FilterByReadPermission(upstream, CanRead)
            .Subscribe(s => emitted = emitted.Add(s.Select(n => n.Path!).ToImmutableHashSet()));

        var older = new[] { Node("a") };
        var newer = new[] { Node("a"), Node("b") };
        upstream.OnNext(older);   // its probe for "a" is the cold one — in flight
        upstream.OnNext(newer);   // arrives while the older filter is still in flight
        coldProbe.OnNext(true);   // the older snapshot's probe finally answers

        Assert.NotEmpty(emitted);
        Assert.Equal(
            new[] { "probe/a", "probe/b" }.ToImmutableHashSet(),
            emitted[^1]);
    }

    /// <summary>
    /// Snapshots the filter has already answered are delivered in upstream order — the filter never
    /// reorders, so a consumer that reads every frame sees the set evolve as the query did.
    /// </summary>
    [Fact]
    public void Answered_snapshots_are_delivered_in_upstream_order()
    {
        var upstream = new Subject<IEnumerable<MeshNode>>();
        var emitted = ImmutableList<int>.Empty;
        using var sub = SyncedQueryDataSourceExtensions
            .FilterByReadPermission(upstream, _ => Observable.Return(true))
            .Subscribe(s => emitted = emitted.Add(s.Count()));

        upstream.OnNext(new[] { Node("a") });
        upstream.OnNext(new[] { Node("a"), Node("b") });
        upstream.OnNext(Array.Empty<MeshNode>());

        Assert.Equal(new[] { 1, 2, 0 }, emitted);
    }
}
