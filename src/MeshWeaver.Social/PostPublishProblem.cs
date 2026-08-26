using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Social;

/// <summary>
/// Recording, ON THE POST, why a publish did not happen — the half of scheduling that makes a
/// failure VISIBLE to the person who scheduled it.
///
/// <para>🚨 <b>The defect this closes (issue #50).</b> Everything about a refused timed publish
/// used to be recorded somewhere the post's owner cannot read: the pre-publish gates
/// (<c>not-connected</c>, <c>profile-path-missing</c>, <c>missing-w_member_social-reconnect</c>,
/// <c>empty-text</c>) returned an outcome to a caller that then vanished, and the handler's own
/// refusals landed on <c>Admin/EventSubscription/{id}</c> — the ADMIN partition, invisible to an
/// ordinary member. From outside, "the timer was never armed", "it fired and the publisher refused"
/// and "it published and the write-back failed" were literally indistinguishable: the node sat at
/// <c>Scheduled</c>, past its slot, saying nothing. That is the shape reported on 2026-08-18 and
/// again on 2026-08-23.</para>
///
/// <para>🌍 <b>The problem is stored as a stable CODE, and that is the localizable datum.</b>
/// <see cref="ErrorCodeKey"/> plus <see cref="ErrorStatusKey"/> is DATA — it can be rendered in
/// whatever language the viewer reads. A stored English sentence is frozen for every viewer
/// forever, which is exactly what the platform's localization rule forbids, and re-migrating stored
/// prose later is far worse than storing the code now.</para>
///
/// <para>The English prose is written ALONGSIDE it (<see cref="ErrorKey"/>) because the page that
/// renders this is the SocialMedia bundle's node content, which reads that field today — dropping
/// it would blank a live UI to fix a future one. So: the data is already correct, and the only
/// change still needed is at the RENDER end. That change is blocked on the platform, not on this
/// module: the localization catalog is a set of embedded resources in
/// <c>MeshWeaver.Messaging.Hub</c>, so a module cannot contribute keys and
/// <c>host.Localize(...)</c> from here would render the raw key. When the catalog opens to modules,
/// the renderer moves to the code and <see cref="ErrorKey"/> can go — and every problem already
/// stored localizes retroactively, because the code was there all along.</para>
///
/// <para><b>Never feed a reconcile its own writes.</b> The watcher reacts to a live query over the
/// posts, so writing a reason onto a post re-emits it and brings the watcher straight back here.
/// <see cref="Record"/> therefore reads the CURRENT problem first and writes only on a real change,
/// which is what makes the loop converge in one write instead of storming. An unchanged problem
/// completes without writing.</para>
/// </summary>
public static class PostPublishProblem
{
    /// <summary>
    /// Content key holding the STABLE CODE for why the last publish attempt did not put this post
    /// on the network — <c>not-connected</c>, <c>empty-text</c>, … — absent or null when the post
    /// published. This is the localizable datum; <see cref="ErrorKey"/> is its English rendering.
    /// </summary>
    public const string ErrorCodeKey = "lastPublishErrorCode";

    /// <summary>
    /// Content key holding the HTTP status LinkedIn answered with, when it answered at all. Null
    /// for a pre-publish refusal, which never reached the network.
    /// </summary>
    public const string ErrorStatusKey = "lastPublishErrorStatus";

    /// <summary>
    /// Content key holding the human-readable reason, in English, rendered from
    /// <see cref="ErrorCodeKey"/> at write time for the bundle's post page. Cleared on a successful
    /// publish.
    ///
    /// <para>🚨 Declared on the <c>SocialPost</c> record too. Anything that record does not declare
    /// is silently DROPPED the next time the node round-trips through it — the same trap its
    /// <c>PublishedUrn</c> doc comment describes — so the reason would survive exactly until the
    /// post was next read as a typed post, i.e. until the page that must show it renders. The same
    /// applies to <see cref="ErrorCodeKey"/> and <see cref="ErrorStatusKey"/>: they reach the page
    /// only once the bundle's record declares them.</para>
    /// </summary>
    public const string ErrorKey = "lastPublishError";

    /// <summary>Content key holding when that attempt was made.</summary>
    public const string AttemptedAtKey = "lastPublishAttemptAt";

    /// <summary>
    /// The problem code for "the mesh cannot tell who scheduled this post". Raised at TWO points
    /// that must agree — the watcher refusing to arm a timer it knows would be refused, and the
    /// handler refusing one that was armed anyway — so the sentence lives in <see cref="Explain"/>
    /// once instead of being written out at each of them.
    /// </summary>
    public const string SchedulerUnknownCode = "scheduler-unknown";

    /// <summary>
    /// The problem code for a refusal that names no reason of its own. 🚨 It exists because null
    /// means CLEAR everywhere in this class: a refusal whose reason happened to be null must record
    /// "something went wrong" rather than silently wiping the post's problem and reporting success
    /// to the only person who could act on it.
    /// </summary>
    public const string UnknownCode = "unknown";

    /// <summary>
    /// <paramref name="reasonCode"/> as a code safe to RECORD — never null, so it can never be
    /// mistaken for a clear. Pure.
    /// </summary>
    /// <param name="reasonCode">A publisher's refusal reason, possibly null or blank.</param>
    public static string CodeOf(string? reasonCode) =>
        string.IsNullOrWhiteSpace(reasonCode) ? UnknownCode : reasonCode!.Trim();

    /// <summary>
    /// The <c>$type</c> a node-native <c>SocialMedia/Post</c> carries — the FALLBACK only, used
    /// when the stored content names no type of its own (the imported <c>SocialMediaPost</c> shape
    /// keeps its own).
    /// </summary>
    public const string PostContentType = "SocialPost";

    /// <summary>
    /// Records the problem <paramref name="reasonCode"/> on the post at <paramref name="postPath"/>,
    /// or CLEARS a recorded problem when <paramref name="reasonCode"/> is null (a publish that
    /// succeeded). Cold — nothing is written until the caller subscribes. Emits the stored node;
    /// completes without writing when the post already says exactly this.
    /// </summary>
    /// <param name="hub">The hub whose workspace owns the write.</param>
    /// <param name="postPath">Path of the post node.</param>
    /// <param name="reasonCode">The problem code to record, or null to clear it.</param>
    /// <param name="statusCode">The HTTP status LinkedIn answered with, or 0 when it did not.</param>
    /// <param name="now">The attempt's timestamp.</param>
    /// <param name="logger">Optional logger for the diagnostic.</param>
    public static IObservable<MeshNode> Record(
        IMessageHub hub, string postPath, string? reasonCode, int statusCode, DateTimeOffset now,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(hub);
        var handle = hub.GetMeshNodeStream(postPath);
        return handle
            .Take(1)
            .Timeout(ReadBudget)
            .SelectMany(node =>
            {
                if (node is null)
                    return Observable.Empty<MeshNode>();
                if (AlreadySays(node.Content, reasonCode, statusCode))
                    // Already says this. Writing again would re-emit the query the watcher reacts
                    // to, which would bring us straight back here — a self-feeding write storm.
                    return Observable.Empty<MeshNode>();

                logger?.LogInformation(
                    "Recording publish problem on {Path}: {Reason}", postPath, reasonCode ?? "(cleared)");
                return handle.Update(current => current with
                {
                    Content = Apply(current.Content, reasonCode, statusCode, now),
                });
            });
    }

    /// <summary>
    /// Whether <paramref name="content"/> already records exactly this problem, so that writing it
    /// again would only churn the node and re-feed the watcher.
    ///
    /// <para>🚨 <b>CLEARING is NOT symmetric with recording, and treating it as such leaves stale
    /// state behind.</b> A matching code is enough to skip a RECORD — the post already explains
    /// itself, and refreshing the timestamp on every reconcile emission is the storm this check
    /// exists to prevent. A CLEAR must additionally see the residue gone: a post whose
    /// <see cref="ErrorCodeKey"/> is already absent can still carry a stale
    /// <see cref="AttemptedAtKey"/>, a leftover status, or an English <see cref="ErrorKey"/> written
    /// by an older build — and skipping on the code alone would leave a successfully published post
    /// permanently claiming a failed attempt. Pure.</para>
    /// </summary>
    /// <param name="content">The post's content as stored, in any shape.</param>
    /// <param name="reasonCode">The problem code being recorded, or null to clear.</param>
    /// <param name="statusCode">The HTTP status being recorded, or 0.</param>
    public static bool AlreadySays(object? content, string? reasonCode, int statusCode = 0)
    {
        var current = NodeContentJson.ToJsonObject(content);
        if (!string.Equals(Text(current, ErrorCodeKey), reasonCode, StringComparison.Ordinal))
            return false;
        if (reasonCode is not null)
            return Number(current, ErrorStatusKey) == (statusCode > 0 ? statusCode : null);
        // Clearing: every trace has to be gone already, not merely the code.
        return Text(current, ErrorKey) is null
            && !HasValue(current, ErrorStatusKey)
            && !HasValue(current, AttemptedAtKey);
    }

    /// <summary>
    /// The post's content with the publish problem applied — pure, so the merge rules are testable
    /// without a mesh. A null <paramref name="reasonCode"/> writes JSON nulls, which is how the
    /// fields are CLEARED (omitting them would leave a stale reason on a post that has since
    /// published). The content's <c>$type</c> is preserved; see <see cref="NodeContentJson"/> for
    /// why that has to be said out loud.
    /// </summary>
    /// <param name="content">The post's content as stored, in any shape.</param>
    /// <param name="reasonCode">The problem code to record, or null to clear.</param>
    /// <param name="statusCode">The HTTP status LinkedIn answered with, or 0 when it did not.</param>
    /// <param name="now">The attempt's timestamp; ignored when clearing.</param>
    public static JsonObject Apply(object? content, string? reasonCode, int statusCode, DateTimeOffset now) =>
        NodeContentJson.Merge(content, PostContentType, Fields(reasonCode, statusCode, now));

    /// <summary>
    /// The four content fields a publish problem writes — the ONE place their shape is defined, so
    /// a caller that assembles its own update bag (the publisher's write-back, which also sets
    /// <c>status</c> and <c>publishedUrn</c> in the same write) cannot record a problem in a
    /// different shape from <see cref="Apply"/>. Every value is null when
    /// <paramref name="reasonCode"/> is, because a null WRITES a JSON null: that is how the fields
    /// are CLEARED, and omitting them would leave a stale reason on a post that has since
    /// published. Pure.
    /// </summary>
    /// <param name="reasonCode">The problem code to record, or null to clear.</param>
    /// <param name="statusCode">The HTTP status LinkedIn answered with, or 0 when it did not.</param>
    /// <param name="now">The attempt's timestamp; ignored when clearing.</param>
    public static IReadOnlyList<KeyValuePair<string, object?>> Fields(
        string? reasonCode, int statusCode, DateTimeOffset now) =>
    [
        new(ErrorCodeKey, reasonCode),
        new(ErrorStatusKey, reasonCode is not null && statusCode > 0 ? statusCode : null),
        // The English rendering, for the bundle's post page — see the class remarks.
        new(ErrorKey, reasonCode is null ? null : Explain(reasonCode, statusCode)),
        new(AttemptedAtKey, reasonCode is null ? null : (object)now),
    ];

    /// <summary>How long a read may take before the record attempt gives up. A diagnostic write
    /// must never outlive the thing it is describing.</summary>
    private static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The sentence a member reads for one of <see cref="LinkedInPublishService"/>'s short refusal
    /// codes — the codes are wire-shaped (<c>not-connected</c>) and mean nothing to the person who
    /// scheduled the post, while the remedy is always a concrete human action.
    ///
    /// <para>🌍 English only, and that is a PLATFORM limitation rather than a choice here: the
    /// localization catalog is a set of embedded resources in <c>MeshWeaver.Messaging.Hub</c>, so a
    /// module cannot contribute keys to it and <c>host.Localize(...)</c> from this assembly would
    /// render the raw key to every viewer. This is exactly why the CODE is what gets STORED (see
    /// the class remarks) — the recorded data is already language-neutral, so opening the catalog
    /// to modules is the only change needed to localize every message, including the ones already
    /// on disk.</para>
    /// </summary>
    /// <param name="reasonCode">The publisher's short refusal code.</param>
    /// <param name="statusCode">The HTTP status, when LinkedIn answered.</param>
    public static string Explain(string? reasonCode, int statusCode = 0) => reasonCode switch
    {
        "post-not-found" => "This post could not be read when its slot came round.",
        "access-denied" => "Whoever scheduled this post may no longer edit it, so it was not published.",
        "profile-path-missing" =>
            "This post names no author profile, and the LinkedIn account to post from is chosen by "
            + "that profile. Pick the profile, then approve and schedule it again.",
        "empty-text" => "This post has no text, so there was nothing to publish.",
        "not-connected" =>
            "The author profile has no connected LinkedIn account. Open the profile, connect "
            + "LinkedIn, then schedule this post again.",
        "missing-w_member_social-reconnect" =>
            "The author profile's LinkedIn connection does not grant permission to post. Reconnect "
            + "LinkedIn on that profile, then schedule this post again.",
        SchedulerUnknownCode =>
            "This post could not be published because the mesh could not tell who scheduled it, and "
            + "the LinkedIn account to post from is chosen per person. Open the post and schedule "
            + "it again from the page.",
        null or "" or UnknownCode => "LinkedIn refused this post" + Status(statusCode) + ".",
        _ => "LinkedIn refused this post" + Status(statusCode) + ": " + reasonCode,
    };

    private static string Status(int statusCode) =>
        statusCode > 0 ? $" (HTTP {statusCode})" : string.Empty;

    /// <summary>The string at <paramref name="key"/>, or null when absent, null, or not a string.</summary>
    private static string? Text(JsonObject content, string key) =>
        content.TryGetPropertyValue(key, out var value) && value?.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : null;

    /// <summary>The number at <paramref name="key"/>, or null when absent, null, or not a number.</summary>
    private static int? Number(JsonObject content, string key) =>
        content.TryGetPropertyValue(key, out var value) && value?.GetValueKind() == JsonValueKind.Number
            ? value.GetValue<int>()
            : null;

    /// <summary>Whether <paramref name="key"/> is present with a value that is not JSON null.</summary>
    private static bool HasValue(JsonObject content, string key) =>
        content.TryGetPropertyValue(key, out var value)
        && value is not null
        && value.GetValueKind() != JsonValueKind.Null;
}
