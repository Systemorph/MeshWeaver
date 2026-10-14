using System.Collections.Immutable;
using MeshWeaver.Compiler;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// 🚨 <b>A source set OLDER than the type's own record is not the source set</b> (MeshWeaver#6631).
///
/// <para>The record is <see cref="NodeTypeDefinition.CurrentSourceVersions"/> — the sources
/// watcher's publication of what the mesh holds — so a set carrying an earlier version of a path
/// the record names is a view that has not yet seen a write the store already has: a cached
/// synced query that missed an update, an index that lags. Measured on memex.systemorph.com
/// 2026-10-06: <c>Hosting/Deployment</c> was compiled twice (activation and bake) over a set that
/// held the NEW <c>PrBabysitterTending</c> (<c>partial class PrBabysitter</c>) beside the OLD,
/// non-partial <c>PrBabysitter</c> — the record had named the new one for 50 s — and Roslyn's
/// <c>CS0260</c> plus ~90 follow-ons were reported as a verdict about code that was fine.</para>
///
/// <para>Deliberately ONE-SIDED. A set NEWER than the record is the ordinary watcher lag (the
/// record trails the files) and compiles; a path present in only one of the two is an add/remove
/// in flight whose direction neither side can establish, and stays the business of
/// <see cref="NodeTypeDefinition.IsDirty"/> and the re-drive. Only "this view is provably behind
/// what the record has already seen" refuses — and it refuses as UNESTABLISHED
/// (<see cref="SourceDiscoveryUnavailableException"/> → <c>CompilationStatus.Unavailable</c>,
/// "not evaluated"), never as a verdict.</para>
/// </summary>
internal static class SourceSetFreshness
{
    /// <summary>
    /// Names every source in <paramref name="set"/> whose version
    /// (<see cref="NodeTypeDefinition.SourceVersionOf"/>, the one rule every snapshot folds
    /// through) is STRICTLY LOWER than the version <paramref name="record"/> names for the same
    /// path; <c>null</c> when there is none, or when there is no record to witness against.
    /// </summary>
    internal static string? OlderThanRecord(
        IEnumerable<MeshNode> set, IReadOnlyDictionary<string, long>? record)
    {
        if (record is not { Count: > 0 })
            return null;
        var byPath = record.ToImmutableDictionary(
            kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        var behind = set
            .Where(n => !string.IsNullOrEmpty(n.Path))
            .Select(n => (n.Path, Held: NodeTypeDefinition.SourceVersionOf(n),
                Recorded: byPath.TryGetValue(n.Path, out var v) ? v : (long?)null))
            .Where(t => t.Recorded is { } recorded && t.Held < recorded)
            .OrderBy(t => t.Path, StringComparer.Ordinal)
            .ToList();
        return behind.Count == 0
            ? null
            : "the source set is OLDER than the type's own source-version record for "
              + string.Join(", ", behind.Select(t => $"'{t.Path}' (held {t.Held}, recorded {t.Recorded})"))
              + " — the view that answered has not yet seen a write the store already holds";
    }

    /// <summary>
    /// <paramref name="snapshot"/> unchanged, unless it is ESTABLISHED and
    /// <see cref="OlderThanRecord"/> names a source it holds behind the record — then the same
    /// set reported UNESTABLISHED, so it can neither win
    /// <see cref="NodeCompileShaping.RaceSourceSnapshot"/> nor reach Roslyn.
    /// </summary>
    internal static SourceSnapshot RefuseIfOlderThanRecord(
        SourceSnapshot snapshot, IReadOnlyDictionary<string, long>? record)
        => snapshot.IsEstablished && OlderThanRecord(snapshot.Sources, record) is { } why
            ? SourceSnapshot.Unavailable(why, snapshot.Sources)
            : snapshot;
}
