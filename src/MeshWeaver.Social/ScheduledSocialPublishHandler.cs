using System;
using System.Net.Http;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Social;

/// <summary>
/// Publishes a scheduled post when its slot arrives — the continuation behind
/// <see cref="EventContinuationType.PublishSocialPost"/>, and the piece that makes a post's
/// <c>scheduledAt</c> mean something.
///
/// <para>🚨 <b>The defect this closes.</b> Until this existed, scheduling a post wrote a status field
/// and armed NOTHING: <c>scheduledAt</c> was read by the date picker and the timeline sort, and by no
/// other line of code in the mesh, the portal or the plugins. Publishing was a node-menu button
/// (<see cref="SocialPostMenuProvider"/>) and the slot was decoration, so a post sat at
/// <c>Scheduled</c> forever while its timeline card claimed it was on its way out. Observed in
/// production on 2026-08-18 with a post that had been "scheduled" twice and published neither
/// time.</para>
///
/// <para><b>It reuses the manual path exactly</b> — <see cref="LinkedInPublishService.PublishPostAsync"/>,
/// the same call the "Publish to LinkedIn" menu item makes — rather than growing a second publish
/// implementation that could drift from it. The service resolves the author profile's stored
/// credential from the post's own <c>authorPath</c> and writes <c>status</c>/<c>publishedUrn</c>/
/// <c>publishedAt</c> back, so a timed publish and a hand-clicked one leave identical state.</para>
///
/// <para>🚨 <b>Identity — it publishes as the SCHEDULER, not as system.</b> The runner wraps
/// continuations in <c>ImpersonateAsSystem</c>, and running the publish that way would make
/// <see cref="LinkedInPublishService"/>'s two access gates pass unconditionally. That is not a
/// harmless simplification: the credential is chosen by the post's own <c>authorPath</c>, so anyone
/// who can EDIT a post could point it at another member's profile and have the timer publish with
/// that member's LinkedIn credential — an escalation the manual path forbids, because there the
/// caller needs Read on the credential node. So this switches to
/// <see cref="EventSubscription.CreatedBy"/> — the identity that scheduled the post — and the exact
/// same gates then apply to the timed path as to the button. A subscription with no CreatedBy is
/// REFUSED rather than published as system.</para>
///
/// <para>🚨 <b>It re-reads the post before publishing.</b> A timer is armed from a snapshot and fires
/// later; in between the post can be published by hand, un-scheduled, or emptied. Publishing is
/// irreversible, so the state is checked again at the point of no return: anything other than a post
/// still asking to be published aborts. Without this a post published by hand at 07:00 would still be
/// posted a second time by its 08:00 timer — the precise shape of the 2026-08-18 incident.</para>
///
/// <para>🚨 <b>Task-based leaf, reactive surface.</b> <see cref="LinkedInPublishService"/> is HTTP-edge
/// code and returns <c>Task</c>; this is hub-reachable background code and must not. The bridge is
/// <see cref="IIoPool.Invoke{T}"/> on the <see cref="IoPoolNames.Http"/> pool — which also bounds how
/// many posts can be in flight at once, so a hundred slots falling on the same minute cannot open a
/// hundred simultaneous sockets. A bare <c>Observable.FromAsync</c> is forbidden repo-wide.</para>
/// </summary>
public sealed class ScheduledSocialPublishHandler(
    IMessageHub hub,
    IMeshService meshService,
    IHttpClientFactory httpClientFactory,
    AccessService accessService,
    ILogger<ScheduledSocialPublishHandler>? logger = null) : IEventContinuationHandler
{
    private readonly IIoPool _httpPool =
        hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Http) ?? IoPool.Unbounded;

    /// <inheritdoc />
    public EventContinuationType ContinuationType => EventContinuationType.PublishSocialPost;

    /// <summary>
    /// Publishes the post at <see cref="EventSubscription.TargetPath"/> and emits it as stored after
    /// the write-back. Cold: nothing is published until the runner subscribes.
    ///
    /// <para>A refused or failed publish THROWS rather than completing quietly, so the runner records
    /// the reason on the subscription (<c>Failed</c> + <see cref="EventSubscription.LastError"/>)
    /// instead of marking it <c>Fired</c>. A silent failure here would reproduce exactly the bug this
    /// class exists to fix, one layer down.</para>
    /// </summary>
    public IObservable<MeshNode> Execute(EventSubscription subscription, string _)
    {
        var postPath = subscription.TargetPath;
        if (string.IsNullOrWhiteSpace(postPath))
            return Observable.Throw<MeshNode>(new InvalidOperationException(
                $"Event subscription {subscription.Id} publishes a social post but names no TargetPath."));

        var scheduler = subscription.CreatedBy;
        if (UnusableScheduler(scheduler) is { } refusal)
            return Observable.Throw<MeshNode>(new InvalidOperationException(
                $"Event subscription {subscription.Id} cannot publish: {refusal} The credential is "
                + "chosen by the post's authorPath, so an un-gated timed publish could go out through "
                + "a profile whoever scheduled it may not use. Refusing is the only safe answer to "
                + "\"whose account does this post on?\"."));

        var service = new LinkedInPublishService(
            hub, meshService, hub.ServiceProvider.GetService<ILogger<LinkedInPublishService>>());

        // RunAs, never Observable.Using: the impersonation must be established on the SUBSCRIBING
        // thread and torn down on the same logical flow (#1790). Everything inside — the re-read,
        // the permission gates, the credential read and the write-back — then runs as the scheduler.
        return accessService.RunAs(
            // Non-null: UnusableScheduler refused a blank one above.
            new AccessContext { ObjectId = scheduler!, Name = scheduler! },
            () => StillPublishable(postPath!)
            .SelectMany(_ => _httpPool
            .Invoke(ct => service.PublishPostAsync(
                httpClientFactory.CreateClient(),
                postPath!,
                textOverride: null,
                visibility: null,
                apiVersion: LinkedInPostsApi.DefaultApiVersion,
                ct))
            .SelectMany(outcome => outcome.Success
                // 🚨 A publish that SUCCEEDED must never surface as a failure. The runner marks a
                // throwing continuation Failed and RELEASES its at-most-once reservation, so a later
                // pending-set emission can retry it — which for this continuation means posting to
                // LinkedIn a second time. The read-back is therefore best-effort: if the node cannot
                // be re-read (timeout, empty), we still report the success that already happened.
                ? hub.GetMeshNode(postPath!, TimeSpan.FromSeconds(10))
                    .Catch<MeshNode?, Exception>(_ => Observable.Return<MeshNode?>(null))
                    .Select(node => node ?? Placeholder(postPath!))
                : Observable.Throw<MeshNode>(new InvalidOperationException(
                    $"Scheduled publish of '{postPath}' was refused: {outcome.Reason ?? "unknown"}"
                    + (outcome.StatusCode > 0 ? $" (HTTP {outcome.StatusCode})" : string.Empty)
                    + (outcome.HttpAttempted
                        ? string.Empty
                        : " — a pre-publish gate short-circuited before any LinkedIn call."))))))
            .Do(_ => logger?.LogInformation(
                "Scheduled publish of {Path} succeeded (subscription {Id})", postPath, subscription.Id));
    }

    /// <summary>
    /// Re-reads the post and completes only if it STILL wants publishing. Emits nothing useful; it
    /// exists for its refusal.
    ///
    /// <para>The armed timer is a snapshot of intent taken minutes or days earlier. By the time it
    /// fires the post may already be live (someone hit the button), may no longer be Scheduled, or may
    /// have had its text cleared. <see cref="LinkedInPublishService"/> deliberately does not re-check
    /// any of that — it serves a caller who just decided to publish — so the check belongs here, on
    /// the one path where the decision was made in the past.</para>
    /// </summary>
    private IObservable<MeshNode> StillPublishable(string postPath) =>
        hub.GetMeshNode(postPath, TimeSpan.FromSeconds(10))
            .Take(1)
            .SelectMany(node =>
            {
                if (node is null)
                    return Observable.Throw<MeshNode>(new InvalidOperationException(
                        $"Scheduled publish aborted: '{postPath}' no longer exists."));
                if (!ScheduledPostWatcher.IsSchedulablePost(node))
                    return Observable.Throw<MeshNode>(new InvalidOperationException(
                        $"Scheduled publish aborted: '{postPath}' is no longer awaiting publication "
                        + "(already published, un-scheduled, or removed) — publishing now would put it "
                        + "on the network a second time."));
                return Observable.Return(node);
            });

    /// <summary>
    /// Why <paramref name="scheduler"/> may not be published as, or null when it is a real person.
    ///
    /// <para>🚨 <b>A present CreatedBy is not enough.</b> The watcher takes it from the post's
    /// <c>lastModifiedBy</c>, and that is <see cref="WellKnownUsers.System"/> whenever the node was
    /// last written by the platform itself — a GitSync, an import, a migration. Impersonating THAT
    /// makes <see cref="LinkedInPublishService"/>'s two gates pass unconditionally again, which is
    /// the precise bypass this handler exists to close; a blank-check alone would have left it open
    /// for every system-written post. Hub principals (<c>sync/…</c>, <c>mesh/…</c>, address-shaped
    /// and never a user id) are refused for the same reason.</para>
    /// </summary>
    private static string? UnusableScheduler(string? scheduler)
    {
        if (string.IsNullOrWhiteSpace(scheduler))
            return "it names no CreatedBy, so there is no identity to publish as.";
        if (string.Equals(scheduler, WellKnownUsers.System, StringComparison.OrdinalIgnoreCase))
            return $"its CreatedBy is the system identity ('{scheduler}'), which passes every access "
                   + "gate by construction.";
        if (scheduler!.Contains('/'))
            return $"its CreatedBy ('{scheduler}') is a hub address, not a user.";
        return null;
    }

    /// <summary>Stands in for the published post when it cannot be re-read — the runner uses the emitted
    /// node only to log what fired, so identity is all it needs, and inventing it here is what keeps a
    /// read-back hiccup from being reported as a publish failure.</summary>
    private static MeshNode Placeholder(string path)
    {
        var cut = path.LastIndexOf('/');
        return cut > 0
            ? new MeshNode(path[(cut + 1)..], path[..cut])
            : new MeshNode(path);
    }
}
