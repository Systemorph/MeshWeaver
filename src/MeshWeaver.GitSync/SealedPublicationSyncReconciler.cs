using System.Collections.Concurrent;
using System.Reactive.Linq;
using MeshWeaver.Graph;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.GitSync;

/// <summary>
/// 🚨 <b>The SEAL is the trigger the green-build hook cannot be.</b> Pure decision, per sync
/// source, of what a sealed publication of this instance's identity means for that source.
///
/// <para><b>The two orderings that left memex.systemorph.com dark (2026-09-08).</b> The
/// <c>workflow_run</c> hook fires when a repository's build goes green — which is BEFORE its
/// publish-bake job seals the bundles for this identity. <see cref="SealedSyncGate"/> therefore
/// HOLDS the source ("not sealed for this instance; it advances when it is"), correctly — but
/// nothing re-fired when the seal landed minutes later, so "when it is" was never. And a source
/// whose config already claimed the sealed commit was not necessarily AT it: the importer had
/// answered <c>Skipped</c> against a content marker without reading the partition, while three
/// files the commit deleted were still in the mesh. In both shapes the bytes sealed for this
/// identity were declined on their source fingerprint, every type compiled from whatever the mesh
/// held, and the record of that compile dangled on the next restart.</para>
///
/// <para>So: a source BEHIND the seal is imported at the sealed commit (the gate now says Go — the
/// seal IS the evidence it was waiting for); a source AT the sealed commit whose types were
/// nevertheless declined is re-imported at that commit with the content-skip bypassed
/// (<see cref="ImportConflictPolicy.Reconcile"/> — every conflict protection stays armed). Anything
/// else is left alone, with the reason stated.</para>
/// </summary>
public static class SealedSyncReconcile
{
    /// <summary>What to do with one sync source.</summary>
    public enum Action
    {
        /// <summary>Nothing — the source is not this seal's business, or it is held; see the reason.</summary>
        None = 1,

        /// <summary>The source is behind the seal: import it at the sealed commit.</summary>
        ImportAtSealedCommit = 2,

        /// <summary>The source claims the sealed commit but its types were declined on their
        /// source fingerprint: re-import at that commit, bypassing the content-skip.</summary>
        ReconcileAtSealedCommit = 3,
    }

    /// <summary>The decision and its reason (log copy an operator can act on).</summary>
    public sealed record Plan(Action Action, string? Commit, string Reason);

    /// <summary>
    /// Decides for one sync source of the repository a sealed source belongs to.
    /// </summary>
    /// <param name="sealedSource">The sealed publication (this identity's).</param>
    /// <param name="repo">The repository the seal attributes to.</param>
    /// <param name="spacePath">The Space the sync source configures.</param>
    /// <param name="config">The source's config, or null when unreadable.</param>
    /// <param name="sealedForThisIdentity">Everything sealed under this identity (the gate's input).</param>
    /// <param name="identity">This instance's framework identity — log copy only.</param>
    /// <param name="declinedTypePaths">NodeType paths whose bundle entry was declined on its source
    /// fingerprint during the sweep that read this seal.</param>
    public static Plan Decide(
        SealedSource sealedSource, RepoIdentity repo, string spacePath, GitHubSyncConfig? config,
        IReadOnlyList<SealedSource> sealedForThisIdentity, string identity,
        IReadOnlyCollection<string> declinedTypePaths)
    {
        ArgumentNullException.ThrowIfNull(sealedSource);
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(sealedForThisIdentity);
        ArgumentNullException.ThrowIfNull(declinedTypePaths);

        if (!sealedSource.IsSealed || sealedSource.SourceCommit is not { Length: > 0 } commit)
            return new Plan(Action.None, null,
                $"'{sealedSource.Source}' is not sealed for identity {identity} ({sealedSource.Refusal ?? "no source commit"})");
        if (config is null)
            return new Plan(Action.None, commit, "config content could not be read");
        if (config.Direction == SyncDirection.ExportOnly)
            return new Plan(Action.None, commit, "direction is ExportOnly");

        var at = config.LastSyncCommitSha;
        if (!SameCommit(at, commit))
        {
            var verdict = SealedSyncGate.Decide(repo, commit, at, sealedForThisIdentity, identity);
            return verdict.Proceed
                ? new Plan(Action.ImportAtSealedCommit, commit,
                    $"'{sealedSource.Source}' is sealed at {Short(commit)} for this instance and the "
                    + $"source sits at {(at is null ? "no commit" : Short(at))} — the seal releases the sync")
                : new Plan(Action.None, commit, verdict.HoldReason ?? "held");
        }

        var declinedHere = declinedTypePaths
            .Where(p => p.StartsWith(spacePath + "/", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(p, spacePath, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        if (declinedHere.Count == 0)
            return new Plan(Action.None, commit,
                $"'{sealedSource.Source}' is sealed at {Short(commit)} and the source is at it; nothing was declined");

        return new Plan(Action.ReconcileAtSealedCommit, commit,
            $"'{sealedSource.Source}' is sealed at {Short(commit)} and the source claims that commit, "
            + $"yet {declinedHere.Count} type(s) baked from it were declined on their source fingerprint "
            + $"({string.Join(", ", declinedHere.Take(5))}{(declinedHere.Count > 5 ? ", …" : "")}) — "
            + "the live sources have drifted from the commit they claim; re-importing at it");
    }

    private static bool SameCommit(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
        && (a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase))
        && Math.Min(a.Length, b.Length) >= 7;

    private static string Short(string sha) => sha.Length > 8 ? sha[..8] : sha;
}

/// <summary>
/// The I/O half of <see cref="SealedSyncReconcile"/>, registered as the hosting layer's
/// <see cref="IPublicationSyncReconciler"/>: matches each sealed source to the sync configs of
/// its repository (the same match the webhook uses) and dispatches the imports the decision
/// names. Holds are recorded ON the config (<see cref="GitHubSyncConfig.LastSyncNote"/>) and
/// logged at Warning once per reason, never per delivery.
/// </summary>
internal sealed class SealedPublicationSyncReconciler(
    IMessageHub hub,
    GitHubWebhookProcessor webhooks,
    GitHubSyncService sync,
    ILogger<SealedPublicationSyncReconciler>? logger = null) : IPublicationSyncReconciler
{
    // Instance state on a mesh-scoped singleton: the last hold reason logged per config path, so
    // a hold that persists across sweeps is said once and a CHANGED reason is said again.
    private readonly ConcurrentDictionary<string, string> loggedHolds = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public IObservable<int> Reconcile(
        string identity,
        IReadOnlyList<SealedSource> sealedForThisIdentity,
        IReadOnlyCollection<string> declinedTypePaths)
        => Observable.Defer(() =>
        {
            var attributable = sealedForThisIdentity
                .Where(s => s.IsSealed && s.Repository is { Length: > 0 } && s.SourceCommit is { Length: > 0 })
                .ToList();
            if (attributable.Count == 0)
                return Observable.Return(0);

            return attributable
                .Select(s => ReconcileSource(s, identity, sealedForThisIdentity, declinedTypePaths))
                .Concat()
                .Sum();
        })
        .Catch<int, Exception>(ex =>
        {
            logger?.LogWarning(ex,
                "[SealedSync] reconciling the sync sources against the seals of identity {Identity} "
                + "failed — the sources stay where they are", identity);
            return Observable.Return(0);
        });

    private IObservable<int> ReconcileSource(
        SealedSource sealedSource, string identity,
        IReadOnlyList<SealedSource> sealedForThisIdentity, IReadOnlyCollection<string> declinedTypePaths)
    {
        var repo = SealedSyncGate.Parse(sealedSource.Repository!);
        if (!repo.IsComplete)
            return Observable.Return(0);
        var commit = sealedSource.SourceCommit!;
        return webhooks
            .ConfigsTargeting(repo, $"seal of '{sealedSource.Source}' at {commit[..Math.Min(8, commit.Length)]}")
            .Select(match =>
            {
                var dispatched = 0;
                var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
                foreach (var node in match.Configs)
                {
                    if (GitHubWebhookProcessor.ToPushTarget(node) is not { } target)
                        continue;
                    var config = node.ContentAs<GitHubSyncConfig>(hub.JsonSerializerOptions, logger);
                    var plan = SealedSyncReconcile.Decide(
                        sealedSource, repo, target.SpacePath, config, sealedForThisIdentity, identity,
                        declinedTypePaths);
                    switch (plan.Action)
                    {
                        case SealedSyncReconcile.Action.ImportAtSealedCommit:
                            logger?.LogInformation("[SealedSync] {Space}: {Reason} — importing.", target.SpacePath, plan.Reason);
                            accessService.RunAsSystem(() => hub.UpdateToProvenCommitFromGitHub(
                                    target.SpacePath, target.UserId, commit, sourceId: target.SourceId))
                                .Subscribe(
                                    activity => logger?.LogInformation(
                                        "[SealedSync] seal-triggered import of {Space} at {Sha} completed ({Activity}).",
                                        target.SpacePath, commit, activity),
                                    ex => logger?.LogWarning(ex,
                                        "[SealedSync] seal-triggered import of {Space} at {Sha} failed.",
                                        target.SpacePath, commit));
                            dispatched++;
                            break;
                        case SealedSyncReconcile.Action.ReconcileAtSealedCommit:
                            logger?.LogWarning("[SealedSync] {Space}: {Reason}", target.SpacePath, plan.Reason);
                            accessService.RunAsSystem(() => hub.ReconcileAtProvenCommitFromGitHub(
                                    target.SpacePath, target.UserId, commit, sourceId: target.SourceId))
                                .Subscribe(
                                    activity => logger?.LogInformation(
                                        "[SealedSync] reconciling import of {Space} at {Sha} completed ({Activity}).",
                                        target.SpacePath, commit, activity),
                                    ex => logger?.LogWarning(ex,
                                        "[SealedSync] reconciling import of {Space} at {Sha} failed.",
                                        target.SpacePath, commit));
                            dispatched++;
                            break;
                        default:
                            RecordHold(node.Path, target, plan, config);
                            break;
                    }
                }
                return dispatched;
            });
    }

    /// <summary>A hold is written onto the config and said ONCE at Warning per reason; a source
    /// that is simply at the seal with nothing declined is neither (it is the steady state).</summary>
    private void RecordHold(string configPath, GitHubWebhookProcessor.PushTarget target,
        SealedSyncReconcile.Plan plan, GitHubSyncConfig? config)
    {
        var steadyState = plan.Reason.Contains("nothing was declined", StringComparison.Ordinal);
        if (steadyState)
        {
            loggedHolds.TryRemove(configPath, out _);
            return;
        }
        if (loggedHolds.TryGetValue(configPath, out var previous)
            && string.Equals(previous, plan.Reason, StringComparison.Ordinal))
            return;
        loggedHolds[configPath] = plan.Reason;
        logger?.LogWarning("[SealedSync] {Space}: HELD — {Reason}", target.SpacePath, plan.Reason);
        if (config is null || string.Equals(config.LastSyncNote, plan.Reason, StringComparison.Ordinal))
            return;
        sync.RecordHold(target.SpacePath, target.SourceId, plan.Reason)
            .Subscribe(_ => { }, ex => logger?.LogWarning(ex,
                "[SealedSync] {Space}: could not record the hold on its sync config", target.SpacePath));
    }
}
