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
/// identity for attribution, and a partition the requester may not create in refuses it — all yield
/// a <see cref="NodeTypeBuildState.ReleaseCreateOutcome"/> carrying no path (and, since #5057, the
/// REASON; they used to yield a bare <c>null</c>). <c>ApplyCompileSuccess</c> then stamps
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
///
/// <para>🚨 <b>LOUD IS NOT THE SAME AS DIAGNOSABLE — issue #5057.</b> The remedy can fail, and when
/// it did, this reported it in the one shape nobody can act on: an ERROR reading <i>"AND the release
/// could not be re-cut"</i> with no reason, because the reason was discarded one frame below where
/// <c>TryCreateReleaseNode</c> collapsed four distinct failures into a bare <c>null</c> — and the
/// channel the incident actually names, a <see cref="NodeTypeBuildState.CreateBound"/> that expired,
/// wrote no log line at all. Eight occurrences over three minutes across seven node types, and the
/// filed issue's first task was <i>"log why the re-cut fails"</i>. A remedy that reports its own
/// failure without being able to report the failure is the same defect class as the state it exists
/// to catch, one level up: the output looks like a diagnosis and carries none. Every sentence this
/// class emits now names the cause of BOTH attempts — the settle's own create (previously a
/// <c>Warning</c> that reached no operator-facing surface) and the re-cut.</para>
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
    /// <param name="hub">The hub the compile settled on.</param>
    /// <param name="nodeTypePath">The NodeType being settled.</param>
    /// <param name="result">The successful compile's result.</param>
    /// <param name="pendingNode">The definition as observed at dispatch.</param>
    /// <param name="activityPath">The compile <c>_Activity</c>, or null when its create did not land.</param>
    /// <param name="firstAttempt">
    /// What the settle's OWN release create amounted to — including, when it failed, the reason,
    /// which until #5057 existed only as a <c>Warning</c> in the log and reached no operator-facing
    /// surface at all. It is the first half of the story this method tells.
    /// </param>
    /// <param name="logger">Where the violation and the remedy's outcome are published.</param>
    internal static IObservable<(string? ReleasePath, LogMessage? Diagnosis)> Restore(
        IMessageHub hub,
        string nodeTypePath,
        NodeCompilationResult result,
        MeshNode pendingNode,
        string? activityPath,
        NodeTypeBuildState.ReleaseCreateOutcome firstAttempt,
        ILogger? logger)
    {
        var before = pendingNode.ContentAs<NodeTypeDefinition>(hub.JsonSerializerOptions);
        if (Violation(before, result, firstAttempt.ReleasePath) is not { } violation)
            return Observable.Return<(string?, LogMessage?)>((firstAttempt.ReleasePath, null));

        // 🚨 WHY THE FIRST CREATE FAILED, on the line an operator reads (#5057). This was the missing
        // half of #781's own diagnosis: the settle's create is best-effort and logged its refusal at
        // Warning, so the ERROR line that announced the consequence never named the cause, and the
        // incident's Loki selector — cut at Error — could not have shown it.
        var firstFailure = FirstAttemptClause(firstAttempt);
        logger?.LogError(
            "[ReleasePostCondition] {HubPath}: {Violation}.{FirstFailure} Re-cutting the release from "
            + "the bytes this compile produced (no recompile) — see issue #781.",
            nodeTypePath, violation, firstFailure);

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
                // The stack goes here; the reason travels, so the Error line below can say it.
                logger?.LogError(ex,
                    "[ReleasePostCondition] {HubPath}: the re-cut faulted", nodeTypePath);
                return Observable.Return(NodeTypeBuildState.ReleaseCreateOutcome.Failed(
                    $"the re-cut faulted: {ex.GetType().Name}: {ex.Message}"));
            })
            // 🚨 An EMPTY completion is its OWN reason, not a shared null (#5057). It cannot happen
            // today — TryCreateReleaseNode emits exactly once on every path — and naming it is what
            // keeps that fact from becoming an assumption the next operator pays for.
            .DefaultIfEmpty(NodeTypeBuildState.ReleaseCreateOutcome.Failed(
                "the re-cut produced no answer at all — an inner observable completed without emitting"))
            .Select<NodeTypeBuildState.ReleaseCreateOutcome, (string? ReleasePath, LogMessage? Diagnosis)>(recut =>
            {
                if (recut.ReleasePath is { } restored)
                {
                    logger?.LogInformation(
                        "[ReleasePostCondition] {HubPath}: release restored at {ReleasePath} — the "
                        + "node no longer advertises a build no release names",
                        nodeTypePath, restored);
                    return ((string?)restored, (LogMessage?)RestoredEntry(violation, firstFailure, restored));
                }

                // 🚨 THE LINE THE INCIDENT WAS FILED FROM, and it now SAYS WHY (#5057). It used to
                // end at "could not be re-cut" — an Error naming the consequence, the stale path and
                // the build, with the one fact needed to fix it discarded one frame below.
                logger?.LogError(
                    "[ReleasePostCondition] {HubPath}: {Violation} — AND the release could not be "
                    + "re-cut{Because}. The node advertises a build no release names; instances will "
                    + "keep binding '{Stale}' until a release is created for it.",
                    nodeTypePath, violation, recut.Because, before!.LatestReleasePath);
                return ((string?)null, (LogMessage?)ViolatedEntry(violation, firstFailure, recut));
            });
    }

    /// <summary>
    /// What the settle's OWN release create amounted to, as a clause for the sentences below — so
    /// "the create was refused because x", "the create timed out" and "no create was made" are three
    /// readable statements rather than one absent release path. Pure.
    /// </summary>
    /// <param name="firstAttempt">The settle's own create.</param>
    internal static string FirstAttemptClause(NodeTypeBuildState.ReleaseCreateOutcome firstAttempt) =>
        !firstAttempt.Attempted
            ? " No create was attempted on this settle."
            : firstAttempt.Succeeded
                ? $" The settle's own create landed at {firstAttempt.ReleasePath}."
                : $" The settle's own create{firstAttempt.Because}.";

    /// <summary>
    /// The compile <c>_Activity</c> line for a violation the re-cut REPAIRED. Pure.
    /// </summary>
    /// <param name="violation">The violation, as <see cref="Violation"/> worded it.</param>
    /// <param name="firstAttemptClause"><see cref="FirstAttemptClause"/>.</param>
    /// <param name="restored">Where the re-cut landed.</param>
    internal static string RestoredDiagnosis(string violation, string firstAttemptClause, string restored) =>
        $"Release post-condition (#781): {violation}.{firstAttemptClause} Restored at {restored} "
        + "from the bytes this compile produced — no recompile.";

    /// <summary>Catalog key for <see cref="RestoredDiagnosis"/>.</summary>
    internal const string RestoredKey = "activity.compile.releasePostCondition.restored";

    /// <summary>Catalog key for <see cref="FailedDiagnosis"/>.</summary>
    internal const string ViolatedKey = "activity.compile.releasePostCondition.violated";

    /// <summary>
    /// The repaired verdict as an activity entry a GERMAN viewer can read.
    ///
    /// <para>🚨 <b>A transcript entry is keyed or it is English forever</b> (<c>LogMessage</c>, #3236;
    /// review on #5057). The entry is written server-side with NO viewer in scope and read later by
    /// viewers whose languages differ, so the sentence must be resolved at RENDER time off
    /// <c>AccessContext.Locale</c> — the English text stays as the fallback, which is what keeps an
    /// old persisted row and a key that later leaves the catalog rendering exactly as they do now.
    /// The ARGUMENTS stay English on purpose: a node path, a release path, a store version and an
    /// exception's own message are not translatable, and inventing German for an exception message
    /// would be worse than leaving it.</para>
    /// </summary>
    /// <param name="violation">The violation, as <see cref="Violation"/> worded it.</param>
    /// <param name="firstAttemptClause"><see cref="FirstAttemptClause"/>.</param>
    /// <param name="restored">Where the re-cut landed.</param>
    internal static LogMessage RestoredEntry(
        string violation, string firstAttemptClause, string restored) =>
        new LogMessage(RestoredDiagnosis(violation, firstAttemptClause, restored), LogLevel.Warning)
            .WithKey(RestoredKey,
                ("violation", violation), ("firstAttempt", firstAttemptClause), ("path", restored));

    /// <summary>
    /// The unrepaired verdict as an activity entry a German viewer can read — same rule as
    /// <see cref="RestoredEntry"/>, and <c>Error</c> because this build has no release.
    /// </summary>
    /// <param name="violation">The violation, as <see cref="Violation"/> worded it.</param>
    /// <param name="firstAttemptClause"><see cref="FirstAttemptClause"/>.</param>
    /// <param name="recut">What the re-cut amounted to.</param>
    internal static LogMessage ViolatedEntry(
        string violation, string firstAttemptClause,
        NodeTypeBuildState.ReleaseCreateOutcome recut) =>
        new LogMessage(FailedDiagnosis(violation, firstAttemptClause, recut), LogLevel.Error)
            .WithKey(ViolatedKey,
                ("violation", violation), ("firstAttempt", firstAttemptClause),
                ("reason", recut.Because));

    /// <summary>
    /// The compile <c>_Activity</c> line for a violation the re-cut could NOT repair — the sentence
    /// issue #5057 is about.
    ///
    /// <para>🚨 It must NAME THE CAUSE of both attempts. The old wording ended at <i>"The re-cut did
    /// not land either"</i>, which reads as a complete report and is not one: it tells the operator
    /// the state (this build has no release) and withholds the only fact that decides what to do
    /// about it. Pure, so that property is asserted rather than hoped for.</para>
    /// </summary>
    /// <param name="violation">The violation, as <see cref="Violation"/> worded it.</param>
    /// <param name="firstAttemptClause"><see cref="FirstAttemptClause"/>.</param>
    /// <param name="recut">What the re-cut amounted to.</param>
    internal static string FailedDiagnosis(
        string violation, string firstAttemptClause, NodeTypeBuildState.ReleaseCreateOutcome recut) =>
        $"Release post-condition (#781) VIOLATED: {violation}.{firstAttemptClause} The re-cut did not "
        + $"land either{recut.Because}. This build has no release.";
}
