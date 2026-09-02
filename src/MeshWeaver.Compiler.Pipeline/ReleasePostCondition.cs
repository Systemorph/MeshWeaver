using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// 🚨 The POST-CONDITION of a successful compile (issue #781): once a release request has been
/// CONSUMED, <see cref="NodeTypeDefinition.LatestReleasePath"/> must never name a build older than
/// <see cref="NodeTypeDefinition.LastCompiledVersion"/>.
///
/// <para><b>Why it is checked HERE, where the compile SETTLES.</b> The release watcher already
/// refuses to fire mid-compile (it gates on a SETTLED status), and that gate is not the gap. A
/// request and a compile can still be ordered so the release is cut, correctly by its own contract,
/// against a build the compile is about to supersede — or the release create can simply not land:
/// <c>TryCreateReleaseNode</c> is best-effort by design (compile correctness must not depend on a
/// MeshNode create), so a bound that expires, a fault, or a refusal — it runs under the REQUESTER's
/// identity for attribution, and a partition the requester may not create in refuses it — all emit
/// <c>null</c>, logged at Warning and swallowed. <c>ApplyCompileSuccess</c> then stamps
/// <c>releasePath ?? def.LatestReleasePath</c>, i.e. the PREVIOUS build's release, and the trigger
/// is already spent (<c>LastReleaseRequestHandledAt == RequestedReleaseAt</c>, stamped on the
/// dispatch commit). Nothing revisits it.</para>
///
/// <para>The resulting node looks healthy from every angle — <c>compilationStatus: Ok</c>, sources
/// current, an assembly built, a release path present — while every instance keeps binding the
/// previous build. Only comparing the release against <c>lastCompiledVersion</c> reveals it, and
/// the settle is the one moment at which that comparison can be made from facts all in hand. It
/// catches the state regardless of which ordering produced it (<c>Publish/Deck</c>, 2026-08-27:
/// <c>lastCompiledVersion 575</c> beside a release cut the previous day).</para>
///
/// <para><b>The response is to RE-CUT, and to say so loudly.</b> The bytes are in hand — the
/// compile just produced them — so the missing release is minted from those exact store coordinates
/// with NO recompile, under SYSTEM (the credential that produced the bytes; the attributed create
/// is what failed, and the requester already passed the <see cref="Permission.Compile"/> gate at
/// the entry point, so nothing is widened by cutting the artefact the compile owed them). Reporting
/// alone would leave the mesh in exactly the state the incident describes: unrepairable from the
/// outside, because the trigger is spent and a fresh request is absorbed by the build already in
/// hand. Un-consuming the trigger instead is worse — a release create that keeps failing would
/// re-dispatch a compile forever, a reconcile fed by its own writes.</para>
///
/// <para>🚨 And it stays LOUD either way: the violation is logged as an ERROR naming the type, the
/// stale path and the build, and the remedy's outcome is recorded on the compile <c>_Activity</c>
/// (the official diagnosis surface). Silence is what made the incident invisible for a day.</para>
/// </summary>
internal static class ReleasePostCondition
{
    /// <summary>
    /// The pure verdict — <c>null</c> when the post-condition HOLDS, otherwise a human-readable
    /// description of the violation. No hub, no stream, no IO: unit-testable on its own.
    /// </summary>
    /// <param name="before">The NodeType definition as observed when the compile was dispatched
    /// (<c>outcome.PendingNode</c>) — it carries the request stamps and the coordinates of the
    /// build the standing release was cut for.</param>
    /// <param name="result">The SUCCESSFUL compile's result — the build about to be stamped.</param>
    /// <param name="newReleasePath">The release cut on THIS settle, or <c>null</c> when none landed.</param>
    internal static string? Violation(
        NodeTypeDefinition? before, NodeCompilationResult result, string? newReleasePath)
    {
        if (before is null) return null;
        // A release for these exact bytes was just cut — the invariant holds by construction.
        if (!string.IsNullOrEmpty(newReleasePath)) return null;

        // SCOPE: only a CONSUMED request (#781's wording). A request still standing re-fires by
        // itself on the next settled emission — that is the release watcher's own contract and
        // must not be pre-empted here. And a build nobody asked to release (a first-build kickoff,
        // an adopted bundle) has never had a release to be stale: absence there is inconclusive,
        // not evidence of a lost request.
        if (before.RequestedReleaseAt is not { } requested) return null;
        if (before.LastReleaseRequestHandledAt is not { } handled || handled < requested) return null;

        if (string.IsNullOrEmpty(before.LatestReleasePath))
            return $"a release request (requestedReleaseAt={requested:O}) was consumed and this "
                 + "compile succeeded, yet the node names NO release at all";

        return NamesAnEarlierBuild(before, result) is { } drift
            ? $"a release request (requestedReleaseAt={requested:O}) was consumed and this compile "
              + $"succeeded, yet latestReleasePath still names '{before.LatestReleasePath}' — cut "
              + $"for an EARLIER build ({drift})"
            : null;
    }

    /// <summary>
    /// Which recorded build identity MOVED with this compile — i.e. the evidence that the standing
    /// release cannot be naming the bytes the settle is about to stamp. Any single fact is enough;
    /// a fact the result does not carry (a producer without an assembly store) is INCONCLUSIVE and
    /// never counted, so this never invents a violation from an absence.
    /// </summary>
    private static string? NamesAnEarlierBuild(NodeTypeDefinition before, NodeCompilationResult result)
    {
        if (result.Version is { } version && before.LastCompiledVersion != version)
            return $"lastCompiledVersion {before.LastCompiledVersion?.ToString() ?? "(none)"} → {version}";
        if (Moved(before.LatestAssemblyPath, result.ContentPath))
            return $"assembly path '{before.LatestAssemblyPath}' → '{result.ContentPath}'";
        if (Moved(before.LatestAssemblyCollection, result.Collection))
            return $"assembly collection '{before.LatestAssemblyCollection}' → '{result.Collection}'";
        // The source snapshot is the identity available on EVERY producer, store or not: a release
        // cut from other sources is by definition a release of another build.
        if (result.CompiledSources is { Count: > 0 } compiled
            && before.CompiledSources is { } previous
            && !SameSnapshot(previous, compiled))
            return "the compiled-source snapshot changed";
        return null;
    }

    private static bool Moved(string? recorded, string? produced)
        => !string.IsNullOrEmpty(produced) && !string.Equals(recorded, produced, StringComparison.Ordinal);

    private static bool SameSnapshot(
        IReadOnlyDictionary<string, long> previous,
        IReadOnlyDictionary<string, long> current)
    {
        if (previous.Count != current.Count) return false;
        foreach (var (path, version) in current)
            if (!previous.TryGetValue(path, out var was) || was != version) return false;
        return true;
    }

    /// <summary>
    /// The settle-path remedy. Emits the release path the terminal stamp should use: the one this
    /// settle already cut, or — when the post-condition is violated — the one re-cut from the bytes
    /// the compile just produced, or <c>null</c> when even that could not land. The second element
    /// is the diagnosis line for the compile <c>_Activity</c> (<c>null</c> when there is nothing to
    /// report), so the official surface carries the story rather than only a log sink.
    ///
    /// <para>🚨 Never faults and always emits exactly once — the terminal Status write runs in this
    /// observable's OnNext, so a sequence that completed empty or errored would wedge the NodeType
    /// at <c>Compiling</c>.</para>
    /// </summary>
    internal static IObservable<(string? ReleasePath, string? Diagnosis)> Restore(
        IMessageHub hub,
        string nodeTypePath,
        NodeCompilationResult result,
        MeshNode pendingNode,
        string? activityPath,
        string? newReleasePath,
        ILogger? logger)
    {
        var before = pendingNode.ContentAs<NodeTypeDefinition>(hub.JsonSerializerOptions);
        if (Violation(before, result, newReleasePath) is not { } violation)
            return Observable.Return<(string?, string?)>((newReleasePath, null));

        logger?.LogError(
            "[ReleasePostCondition] {HubPath}: {Violation}. Re-cutting the release from the bytes "
            + "this compile produced (no recompile) — see issue #781.",
            nodeTypePath, violation);

        // Under SYSTEM, and with the requester cleared so TryCreateReleaseNode does not re-attempt
        // the very attribution that was refused. RunAsSystem (not Observable.Using over
        // ImpersonateAsSystem) — the sealed impersonation boundary is what keeps the scope off the
        // subscriber and off the terminating thread.
        var access = hub.ServiceProvider.GetService<AccessService>();
        var systemPending = pendingNode with { Content = before! with { RequestedReleaseBy = null } };

        return access
            .RunAsSystem(() => NodeTypeBuildState.TryCreateReleaseNode(
                hub, nodeTypePath, result, systemPending, activityPath, logger))
            .Take(1)
            .Catch((Exception ex) =>
            {
                logger?.LogError(ex,
                    "[ReleasePostCondition] {HubPath}: the re-cut faulted", nodeTypePath);
                return Observable.Return<string?>(null);
            })
            .DefaultIfEmpty()
            .Select<string?, (string? ReleasePath, string? Diagnosis)>(restored =>
            {
                if (restored is not null)
                {
                    logger?.LogInformation(
                        "[ReleasePostCondition] {HubPath}: release restored at {ReleasePath} — the "
                        + "node no longer advertises a build no release names",
                        nodeTypePath, restored);
                    return ((string?)restored,
                        (string?)$"Release post-condition (#781): {violation}. Restored at {restored} "
                               + "from the bytes this compile produced — no recompile.");
                }

                logger?.LogError(
                    "[ReleasePostCondition] {HubPath}: {Violation} — AND the release could not be "
                    + "re-cut. The node advertises a build no release names; instances will keep "
                    + "binding '{Stale}' until a release is created for it.",
                    nodeTypePath, violation, before!.LatestReleasePath);
                return ((string?)null,
                    (string?)$"Release post-condition (#781) VIOLATED: {violation}. The re-cut did "
                           + "not land either — this build has no release.");
            });
    }
}
