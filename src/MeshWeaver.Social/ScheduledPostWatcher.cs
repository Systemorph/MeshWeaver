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
    /// 🚨 <c>lastModifiedBy</c> is listed here but MUST NOT be relied on, and believing it could be
    /// is issue #50. This query is PATH-LESS, so Postgres serves it through the cross-schema fan-out
    /// <c>public.search_across_schemas</c>, whose record shape carries no <c>last_modified_by</c>
    /// and no <c>created_by</c> column at all — a <c>select:</c> cannot add a column the fan-out
    /// never returns. Every node this watcher reads from storage therefore has a null
    /// <c>LastModifiedBy</c>, and a timer armed from it named nobody, so the handler refused every
    /// timed publish hours later with "names no CreatedBy". The identity is resolved by an
    /// authoritative per-node read instead — see <see cref="ResolveScheduler"/>.
    private const string Query =
        "nodeType:*Post select:path,id,namespace,name,nodeType,content,lastModifiedBy";

    /// <summary>The timers already armed. Read as a QUERY rather than one node-stream read per post:
    /// a stream opened on a path that does not exist yet ERRORS (<c>No node found</c>), so the
    /// per-post shape logged a routing warning and a stream fault for every post awaiting its first
    /// timer — noise that would train everyone to ignore this component's logs.</summary>
    private const string SubscriptionQueryId = "social-publish-timers";

    private static readonly string SubscriptionQuery =
        $"path:{EventSubscriptionNodeType.Namespace} scope:children "
        + $"nodeType:{EventSubscriptionNodeType.NodeType} select:path,id,namespace,name,nodeType,content,lastModifiedBy";

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
        var asking = all.Where(IsSchedulablePost).ToList();

        // 🚨 A post that names no author profile CANNOT publish — LinkedInPublishService refuses it
        // with `profile-path-missing`, because the credential is chosen by the post's own authorPath.
        // Arming a timer for one buys nothing and costs the worst failure mode there is: the calendar
        // shows the post as scheduled, the slot passes, and NOTHING happens or is said. So they are
        // separated out here and reported LOUDLY at every reconcile, naming the posts, because the
        // fix (pick the profile) is a human action nobody will take if nobody is told.
        var authorless = asking.Where(p => string.IsNullOrWhiteSpace(AuthorPathOf(p))).ToList();
        if (authorless.Count > 0)
        {
            logger?.LogWarning(
                "{Count} post(s) are marked Scheduled but name NO author profile, so they can never "
                + "publish (the credential is chosen by authorPath). No timer is armed for them. "
                + "Set authorPath, or reset them to Draft: {Paths}",
                authorless.Count, string.Join(", ", authorless.Select(p => p.Path)));
            // …and say it where the person who scheduled it will look. A log line reaches whoever
            // has cluster access; the post reaches its owner (issue #50).
            foreach (var post in authorless)
                ReportProblem(post, PostPublishProblem.Explain("profile-path-missing"));
        }

        var posts = asking.Where(p => !string.IsNullOrWhiteSpace(AuthorPathOf(p))).ToList();
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
            EnsureTimer(post, slot.Value, timers);
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
        MeshNode post, DateTimeOffset slot, IReadOnlyDictionary<string, EventSubscription> timers)
    {
        var postPath = post.Path;
        var id = SubscriptionId(postPath);
        timers.TryGetValue(id, out var current);

        if (!MayArm(current, slot))
            return;

        // Unchanged — writing again would churn the node's version for nothing, and every write
        // re-emits the query that brought us here.
        //
        // 🚨 "Unchanged" includes the IDENTITY, not just the slot. Every timer armed by the build
        // this fixes carries a null CreatedBy (the path-less query could not name one), and such a
        // timer is Pending with exactly the right FireAt — so a slot-only comparison would skip it
        // forever and every post scheduled BEFORE this deploys would still be refused at its slot,
        // silently, with the fix in place. A timer whose identity this deployment would refuse is
        // therefore treated as changed and REPAIRED on sight.
        if (current is { Status: EventSubscriptionStatus.Pending }
            && current.FireAt == slot
            && UsableScheduler(current.CreatedBy) is not null)
            return;

        // WHO the publish runs as, resolved AUTHORITATIVELY — see ResolveScheduler. An existing
        // USABLE value is kept so re-slotting never silently changes WHO the post goes out as; an
        // unusable one (null from the old bug, or a system/hub principal) is replaced rather than
        // preserved, which is what makes the repair above actually land.
        ResolveScheduler(post).Subscribe(
            scheduler => Arm(post, slot, current, id, UsableScheduler(current?.CreatedBy) ?? scheduler),
            ex => logger?.LogWarning(ex, "Could not resolve who scheduled {Path}", postPath));
    }

    /// <summary>
    /// <paramref name="scheduler"/> when this deployment would actually publish as it, else null —
    /// the ONE definition, borrowed from <see cref="ScheduledSocialPublishHandler.UnusableScheduler"/>
    /// so that arming, repairing and firing cannot drift apart on what "usable" means.
    /// </summary>
    /// <param name="scheduler">The identity recorded on an existing timer, or null.</param>
    private static string? UsableScheduler(string? scheduler) =>
        scheduler is not null && ScheduledSocialPublishHandler.UnusableScheduler(scheduler) is null
            ? scheduler
            : null;

    /// <summary>
    /// Whether a post whose current timer is <paramref name="current"/> may be armed for
    /// <paramref name="slot"/>.
    ///
    /// <para>🚨 <b>A <c>Fired</c> or <c>Cancelled</c> timer is NEVER re-armed.</b> The publish
    /// continuation is not idempotent — firing it twice posts to LinkedIn twice — so a post that
    /// has been handed over, or that a human stopped, is left alone whatever its slot says.</para>
    ///
    /// <para><b>A <c>Failed</c> one is re-armed only for a DIFFERENT slot, and that is not a
    /// loosening — it is the exit from a dead end.</b> A failed timer means the continuation threw
    /// BEFORE putting anything on the network (the handler's own refusals and every pre-publish
    /// gate; a publish that succeeded is never reported as a failure — see
    /// <see cref="ScheduledSocialPublishHandler"/>). With the old blanket rule, the first failure
    /// was terminal: fix the credential, re-approve, re-schedule, and the post STILL never went
    /// out, because the subscription id is derived from the post path and that one subscription
    /// was permanently non-Pending. Requiring a different <c>FireAt</c> is what keeps the retry
    /// tied to a new human decision rather than to a reconcile loop — the same emission cannot
    /// retry itself, because the slot it reads has not moved. And the handler re-reads the post at
    /// fire time anyway (<c>StillPublishable</c>), so a post that did reach the network is refused
    /// there too.</para>
    /// </summary>
    /// <param name="current">The post's existing timer, or null when it has none.</param>
    /// <param name="slot">The slot the post is now asking for.</param>
    private static bool MayArm(EventSubscription? current, DateTimeOffset slot) => current?.Status switch
    {
        null or EventSubscriptionStatus.Pending => true,
        EventSubscriptionStatus.Failed => current.FireAt != slot,
        _ => false,
    };

    /// <summary>Writes the post's timer.</summary>
    private void Arm(
        MeshNode post, DateTimeOffset slot, EventSubscription? current, string id, string? scheduler)
    {
        var postPath = post.Path;
        // 🚨 Refuse to arm a timer that provably cannot publish, and SAY SO ON THE POST. Arming one
        // costs the worst failure mode there is — the calendar says scheduled, the slot passes, and
        // nothing happens or is said — which is exactly how this component already treats a post
        // that names no author profile.
        if (ScheduledSocialPublishHandler.UnusableScheduler(scheduler) is { } refusal)
        {
            logger?.LogWarning(
                "No timer armed for {Path}: {Refusal} Set an author profile and re-schedule it from "
                + "the post page so the publish has an identity to run as.", postPath, refusal);
            ReportProblem(post,
                "This post could not be handed over for publishing because the mesh could not tell "
                + "who scheduled it, and the LinkedIn account to post from is chosen per person. "
                + "Open the post and schedule it again from the page.");
            return;
        }

        var subscription = new EventSubscription
        {
            Id = id,
            TriggerType = EventTriggerType.Timer,
            FireAt = slot,
            ContinuationType = EventContinuationType.PublishSocialPost,
            TargetPath = postPath,
            Status = EventSubscriptionStatus.Pending,
            LastError = null,
            CreatedBy = scheduler,
            CreatedAt = current?.CreatedAt ?? DateTimeOffset.UtcNow,
        };
        AsSystem(() => EventSubscriptionOps.CreateSubscription(meshService, subscription))
            .Subscribe(
                _ => logger?.LogInformation(
                    "Armed publish of {Path} for {Slot:o} as {Scheduler} (subscription {Id})",
                    postPath, slot, scheduler, id),
                ex => logger?.LogWarning(ex, "Could not arm publish of {Path}", postPath));
    }

    /// <summary>
    /// WHO the timed publish runs as, resolved from the post itself.
    ///
    /// <para>🚨 <b>This may NOT be read off the query result, and that was the bug (issue #50).</b>
    /// <see cref="Query"/> is PATH-LESS (<c>nodeType:*Post</c> spans every partition), so Postgres
    /// serves it through the cross-schema fan-out <c>public.search_across_schemas</c> — whose record
    /// shape is <c>(id, namespace, name, node_type, category, icon, display_order, last_modified,
    /// version, state, content, desired_id, main_node)</c>. There is no <c>last_modified_by</c> and
    /// no <c>created_by</c> column in it. So <c>MeshNode.LastModifiedBy</c> is ALWAYS null on a node
    /// this watcher read from storage, whatever the query's <c>select:</c> list asks for — the
    /// projection cannot add a column the fan-out never returns. Every timer armed from a storage
    /// read therefore carried no <c>CreatedBy</c>, and
    /// <see cref="ScheduledSocialPublishHandler"/> refused every one of them with "it names no
    /// CreatedBy", hours later, on a post that looked perfectly scheduled. It looked intermittent
    /// only because a timer armed from a LIVE change-feed emission (the post scheduled while this
    /// watcher is running) carries the full node and did have an identity — so it worked when
    /// someone watched it and failed after every restart.</para>
    ///
    /// <para>The cure is to stop inferring the identity from a projection at all: read the post
    /// authoritatively BY PATH, which goes to the owning per-node hub and carries every field.
    /// Only when the emission already has one (the live path) is the read skipped — it is the same
    /// answer, without a round trip per arming. Arming is rare by construction (an unchanged timer
    /// returns before we get here), so this costs one read per real scheduling decision.</para>
    ///
    /// <para>Emits exactly once — null when no identity can be resolved, never an error: a post
    /// that cannot be read is reported by <see cref="Arm"/> rather than faulting the reconcile for
    /// every OTHER post in the emission.</para>
    /// </summary>
    /// <param name="post">The post as the query delivered it.</param>
    private IObservable<string?> ResolveScheduler(MeshNode post) =>
        SchedulerIdentity(post) is { } known
            ? Observable.Return<string?>(known)
            : AsSystem(() => hub.GetMeshNodeStream(post.Path)
                    .Take(1)
                    .Timeout(TimeSpan.FromSeconds(10))
                    .Select(SchedulerIdentity))
                .Catch<string?, Exception>(ex =>
                {
                    logger?.LogWarning(ex,
                        "Could not read {Path} to resolve who scheduled it", post.Path);
                    return Observable.Return<string?>(null);
                })
                .DefaultIfEmpty(null);

    /// <summary>
    /// The identity a node's own metadata names, or null when it names none.
    /// <c>LastModifiedBy</c> is who put the post into this state — the closest thing to "who asked
    /// for it" — and <c>CreatedBy</c> is the fallback for a node written before the mesh recorded
    /// the modifier. Pure.
    /// </summary>
    /// <param name="node">The node to read, in whatever shape it arrived.</param>
    public static string? SchedulerIdentity(MeshNode? node)
    {
        var identity = Trimmed(node?.LastModifiedBy) ?? Trimmed(node?.CreatedBy);
        return identity;

        static string? Trimmed(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// Records a reason on the post so its owner can see why nothing happened. Best effort, and
    /// deliberately not fatal to the reconcile.
    ///
    /// <para>🚨 <b>Never feed a reconcile its own writes.</b> This runs from a live query over the
    /// posts, so every write here re-emits and comes straight back. The post the emission ALREADY
    /// carries is checked first, which makes the steady state cost nothing at all — no write, and
    /// not even a read. <see cref="PostPublishProblem.Record"/> repeats the check against the
    /// authoritative node, so the convergence does not depend on the projection either.</para>
    /// </summary>
    /// <param name="post">The post as the emission delivered it.</param>
    /// <param name="reason">The sentence its owner should read.</param>
    private void ReportProblem(MeshNode post, string reason)
    {
        if (string.Equals(Prop(post, PostPublishProblem.ErrorKey), reason, StringComparison.Ordinal))
            return;
        AsSystem(() => PostPublishProblem.Record(hub, post.Path, reason, DateTimeOffset.UtcNow, logger))
            .Subscribe(
                _ => { },
                ex => logger?.LogWarning(ex, "Could not record the publish problem on {Path}", post.Path));
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

    /// <summary>
    /// The profile this post goes out AS, or null when it names none. Reads both spellings for the
    /// same reason <see cref="LinkedInPublishService"/> does: <c>profilePath</c> is the imported
    /// shape and <c>authorPath</c> the node-native one, and a post naming its profile in the other
    /// spelling must not be mistaken for one naming none.
    /// </summary>
    public static string? AuthorPathOf(MeshNode node) =>
        Prop(node, "profilePath") ?? Prop(node, "authorPath");

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
