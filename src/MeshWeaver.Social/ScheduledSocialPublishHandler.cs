using System;
using System.Net.Http;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
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
/// <para><b>Identity.</b> The runner has no ambient <c>AccessContext</c> and wraps this in
/// <c>ImpersonateAsSystem</c>, so the publish service's two access gates pass by construction. That is
/// the intended reading, not a bypass: the credential is selected by the POST's own author profile,
/// never by the caller, so a timer can only ever publish as the member whose post it is. Who armed the
/// timer stays on the subscription's <see cref="EventSubscription.CreatedBy"/>.</para>
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
    public IObservable<MeshNode> Execute(EventSubscription subscription, string subjectId)
    {
        var postPath = subscription.TargetPath;
        if (string.IsNullOrWhiteSpace(postPath))
            return Observable.Throw<MeshNode>(new InvalidOperationException(
                $"Event subscription {subscription.Id} publishes a social post but names no TargetPath."));

        var service = new LinkedInPublishService(
            hub, meshService, hub.ServiceProvider.GetService<ILogger<LinkedInPublishService>>());

        return _httpPool
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
                        : " — a pre-publish gate short-circuited before any LinkedIn call."))))
            .Do(_ => logger?.LogInformation(
                "Scheduled publish of {Path} succeeded (subscription {Id})", postPath, subscription.Id));
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
