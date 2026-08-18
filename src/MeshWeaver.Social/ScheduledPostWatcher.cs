using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Social;

/// <summary>
/// Arms the timer that publishes a scheduled post — the half of scheduling that decides WHEN, paired
/// with <see cref="ScheduledSocialPublishHandler"/>, which does the publishing.
///
/// <para>🚨 <b>Why this watches instead of the editor minting on click.</b> A post's <c>status</c> and
/// <c>scheduledAt</c> are ordinary content fields with no write boundary — a script, an agent or an
/// MCP <c>patch</c> writes them straight onto the node, and that is not an edge case: the production
/// incident of 2026-08-18 had the slot moved by an AGENT, not by the workflow button. Minting the
/// subscription inside the Schedule button would therefore have armed nothing for the write that
/// actually happened. Reconciling from the post's stored state covers every writer.</para>
///
/// <para><b>Reconcile, not react.</b> Each emission of the live query re-derives the desired timer set
/// from current state and settles the difference, so a missed change-feed event, a restart, or a slot
/// edited three times all converge on one subscription per post. The subscription id is derived from
/// the post path, which is what makes re-scheduling an UPDATE rather than a second timer.</para>
///
/// <para>🚨 <b>An already-fired timer is never re-armed.</b> The publish continuation is not
/// idempotent — firing it twice posts to LinkedIn twice. So a post whose subscription already reached
/// <see cref="EventSubscriptionStatus.Fired"/> is left alone even if its slot is edited afterwards,
/// and a post that is already <c>Published</c> never arms at all. Re-publishing an edited post is
/// deliberately not a flow here: author a new post instead.</para>
/// </summary>
public sealed class ScheduledPostWatcher(
    IMessageHub hub,
    IMeshService meshService,
    AccessService accessService,
    ILogger<ScheduledPostWatcher>? logger = null) : IHostedService, IDisposable
{
    /// <summary>Live query id — constant, so the workspace keeps ONE registry entry rather than
    /// leaking one per emission.</summary>
    private const string QueryId = "social-scheduled-posts";

    /// <summary>
    /// The candidate set: every post node, narrowed to the ones actually due by
    /// <see cref="IsSchedulablePost"/> in code.
    ///
    /// <para>🚨 <b>Not <c>status:Scheduled</c>.</b> The obvious query — filter on the content field —
    /// silently matches NOTHING: a content-field term returns an empty result while
    /// <c>nodeType:*Post</c> over the same node returns it (measured, and now pinned by
    /// <c>ScheduledPostWatcherTest</c>). A watcher built on that filter would have looked completely
    /// healthy and armed nothing, which is the same silent-failure shape this whole component exists
    /// to remove. The node-type suffix match also keeps this predicate identical to
    /// <see cref="SocialPostMenuProvider"/>'s, so the manual and timed paths agree on what a post is.</para>
    ///
    /// <para>Deliberately NOT capped with <c>limit:</c>: a truncated candidate set drops posts that
    /// were legitimately scheduled, and it drops them invisibly.</para>
    /// </summary>
    private const string Query = "nodeType:*Post select:path,id,namespace,name,nodeType,content";

    /// <summary>The timers already armed. Read as a QUERY rather than one node-stream read per post:
    /// a stream opened on a path that does not exist yet ERRORS (<c>No node found</c>), so the
    /// per-post shape logged a routing warning and a stream fault for every post awaiting its first
    /// timer — noise that would train everyone to ignore this component's logs.</summary>
    private const string SubscriptionQueryId = "social-publish-timers";

    private static readonly string SubscriptionQuery =
        $"path:{EventSubscriptionNodeType.Namespace} scope:children "
        + $"nodeType:{EventSubscriptionNodeType.NodeType} select:path,id,namespace,name,nodeType,content";

    private IDisposable? querySub;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Both sides live: a post being scheduled AND a timer firing re-emit, so the desired set is
        // re-derived from current state either way. That is what makes this restart- and
        // missed-event-safe without any bookkeeping of its own.
        querySub = Observable.CombineLatest(
                AsSystem(() => hub.GetWorkspace().GetQuery(QueryId, Query)),
                AsSystem(() => hub.GetWorkspace().GetQuery(SubscriptionQueryId, SubscriptionQuery)),
                (posts, timers) => (Posts: posts, Timers: timers))
            .Subscribe(
                x => Reconcile(x.Posts ?? [], x.Timers ?? []),
                ex => logger?.LogWarning(ex, "Scheduled-post watch failed; no slots will be armed"));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Settles the timer set against the posts currently claiming to be scheduled. Logs the counts at
    /// Information: a watcher that silently sees nothing is indistinguishable from one that is working
    /// and has nothing to do, and telling those apart in production is the entire point.
    /// </summary>
    private void Reconcile(IEnumerable<MeshNode> nodes, IEnumerable<MeshNode> timerNodes)
    {
        var all = nodes.ToList();
        var posts = all.Where(IsSchedulablePost).ToList();
        var timers = timerNodes
            .Select(n => n.ContentAs<EventSubscription>(hub.JsonSerializerOptions))
            .Where(s => s is { ContinuationType: EventContinuationType.PublishSocialPost })
            .ToDictionary(s => s!.Id, s => s!, StringComparer.Ordinal);

        logger?.LogInformation(
            "Scheduled-post watch: {Scheduled} of {Total} scheduled nodes are publishable posts; "
            + "{Timers} publish timers exist",
            posts.Count, all.Count, timers.Count);

        foreach (var post in posts)
        {
            var slot = SlotOf(post);
            if (slot is null)
                continue;   // "Scheduled" with no time — the workflow blocks this; a raw write can't be honoured
            EnsureTimer(post.Path, slot.Value, post.LastModifiedBy, timers);
        }

        CancelOrphanedTimers(posts, timers);
    }

    /// <summary>
    /// Cancels pending publish timers whose post no longer asks to be published — un-scheduled,
    /// published by hand, or deleted. (NOT emptied: an empty post is still Scheduled, so it keeps
    /// its timer and is refused at publish time with `empty-text` — text can still arrive before
    /// the slot, and cancelling on a momentarily-empty draft would silently unschedule it.)
    ///
    /// <para>🚨 <b>Arming is not the only thing that needs a guard.</b> The re-arm guard stops a
    /// published post from getting a NEW timer, but a timer armed while the post was still Scheduled
    /// outlives that decision: publish by hand at 07:00 and the 08:00 timer still fires, putting the
    /// post on the network twice — the 2026-08-18 incident with the order reversed. The handler
    /// re-checks at fire time too (publishing is irreversible, so it is worth stopping twice), but
    /// leaving armed timers behind also makes the subscription set lie about what is pending.</para>
    /// </summary>
    private void CancelOrphanedTimers(
        IReadOnlyCollection<MeshNode> posts, IReadOnlyDictionary<string, EventSubscription> timers)
    {
        var wanted = posts.Select(p => SubscriptionId(p.Path)).ToHashSet(StringComparer.Ordinal);
        foreach (var (id, timer) in timers)
        {
            if (timer.Status != EventSubscriptionStatus.Pending || wanted.Contains(id))
                continue;
            AsSystem(() => EventSubscriptionOps.SetStatus(
                    hub, EventSubscriptionNodeType.Path(id), EventSubscriptionStatus.Cancelled,
                    $"The post at '{timer.TargetPath}' is no longer awaiting publication."))
                .Subscribe(
                    _ => logger?.LogInformation(
                        "Cancelled publish timer {Id} — {Path} is no longer scheduled",
                        id, timer.TargetPath),
                    ex => logger?.LogWarning(ex, "Could not cancel publish timer {Id}", id));
        }
    }

    /// <summary>
    /// Creates the post's publish timer, or moves it when the slot changed. Reads the existing
    /// subscription FIRST and writes only on a real difference — an unconditional upsert would reset a
    /// <c>Fired</c> subscription to <c>Pending</c> on every emission and republish the post on a loop.
    /// </summary>
    private void EnsureTimer(
        string postPath, DateTimeOffset slot, string? scheduledBy,
        IReadOnlyDictionary<string, EventSubscription> timers)
    {
        var id = SubscriptionId(postPath);
        timers.TryGetValue(id, out var current);

        // Already fired (or cancelled/failed): the post has been published, or a human stopped it.
        // Never re-arm — this continuation posts to a network and cannot be undone.
        if (current is not null && current.Status != EventSubscriptionStatus.Pending)
            return;

        // Unchanged — writing again would churn the node's version for nothing, and every write
        // re-emits the query that brought us here.
        if (current is not null && current.FireAt == slot)
            return;

        var subscription = new EventSubscription
        {
            Id = id,
            TriggerType = EventTriggerType.Timer,
            FireAt = slot,
            ContinuationType = EventContinuationType.PublishSocialPost,
            TargetPath = postPath,
            Status = EventSubscriptionStatus.Pending,
            // WHO the publish runs as. The handler refuses to publish without it rather than
            // falling back to system, because the credential is chosen by the post's authorPath —
            // an un-gated timed publish could use a profile the scheduler may not use.
            // lastModifiedBy is the identity that put the post into this state, the closest thing
            // to "who asked for it" the stored node carries; an existing value is kept so
            // re-slotting never silently changes WHO it goes out as.
            CreatedBy = current?.CreatedBy ?? scheduledBy,
            CreatedAt = current?.CreatedAt ?? DateTimeOffset.UtcNow,
        };
        AsSystem(() => EventSubscriptionOps.CreateSubscription(meshService, subscription))
            .Subscribe(
                _ => logger?.LogInformation(
                    "Armed publish of {Path} for {Slot:o} (subscription {Id})", postPath, slot, id),
                ex => logger?.LogWarning(ex, "Could not arm publish of {Path}", postPath));
    }

    /// <summary>
    /// The post's timer id — derived from its PATH so the same post always addresses the same
    /// subscription. That is what makes re-scheduling move one timer instead of stacking a second one
    /// beside it, and it is why the id must never carry the slot.
    /// </summary>
    public static string SubscriptionId(string postPath) =>
        "publish-" + postPath.Replace('/', '-').Replace('@', '-');

    /// <summary>
    /// Whether this node is a social post that a timer may publish. Mirrors
    /// <see cref="SocialPostMenuProvider"/>'s predicate — the manual and timed paths must agree on what
    /// a post IS, or a post gets a button and no timer (or the reverse).
    ///
    /// <para>A node already carrying a <c>publishedUrn</c> is excluded whatever its status says: it is
    /// on the network, and arming it again would post it twice.</para>
    /// </summary>
    public static bool IsSchedulablePost(MeshNode node)
    {
        if (node.Content is null)
            return false;
        var type = Prop(node, "$type");
        var isPost = string.Equals(type, "SocialMediaPost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "SocialPost", StringComparison.OrdinalIgnoreCase)
            || (node.NodeType?.EndsWith("Post", StringComparison.OrdinalIgnoreCase) ?? false);
        if (!isPost)
            return false;
        if (!string.IsNullOrWhiteSpace(Prop(node, "publishedUrn")))
            return false;
        if (!string.Equals(Prop(node, "status"), "Scheduled", StringComparison.OrdinalIgnoreCase))
            return false;
        var platform = Prop(node, "platform");
        return string.IsNullOrEmpty(platform)
            || string.Equals(platform, "LinkedIn", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The post's slot as an instant, or null when it names none. A bare (unzoned) timestamp is
    /// read as UTC — every stored <c>scheduledAt</c> in the mesh is UTC, and guessing a local zone here
    /// would move a post by hours.</summary>
    public static DateTimeOffset? SlotOf(MeshNode node)
    {
        var raw = Prop(node, "scheduledAt");
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        if (DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal
                | System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed))
            return parsed;
        return null;
    }

    /// <summary>Shape-tolerant content read: the node may hold a typed record OR raw JSON depending on
    /// whether the reading hub knows the type, and camelCase or PascalCase depending on the writer.</summary>
    private static string? Prop(MeshNode node, string name)
    {
        if (node.Content is null)
            return null;
        var je = node.Content is JsonElement e
            ? e
            : JsonSerializer.SerializeToElement(node.Content, node.Content.GetType());
        if (je.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var candidate in new[] { name, char.ToUpperInvariant(name[0]) + name[1..] })
            if (je.TryGetProperty(candidate, out var v))
                return v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString(),
                    JsonValueKind.Null or JsonValueKind.Undefined => null,
                    _ => v.ToString(),
                };
        return null;
    }

    private IObservable<T> AsSystem<T>(Func<IObservable<T>> factory) => accessService.RunAsSystem(factory);

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        querySub?.Dispose();
        querySub = null;
    }
}
