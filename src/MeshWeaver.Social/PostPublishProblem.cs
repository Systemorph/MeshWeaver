using System;
using System.Collections.Generic;
using System.Reactive.Linq;
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
/// <para><b>Never feed a reconcile its own writes.</b> The watcher reacts to a live query over the
/// posts, so writing a reason onto a post re-emits it and brings the watcher straight back here.
/// <see cref="Record"/> therefore reads the CURRENT reason first and writes only on a real change,
/// which is what makes the loop converge in one write instead of storming. An unchanged reason
/// completes without writing.</para>
/// </summary>
public static class PostPublishProblem
{
    /// <summary>
    /// Content key holding the human-readable reason the last publish attempt did not put this
    /// post on the network. Cleared on a successful publish.
    ///
    /// <para>🚨 Declared on the <c>SocialPost</c> record too. Anything that record does not declare
    /// is silently DROPPED the next time the node round-trips through it — the same trap its
    /// <c>PublishedUrn</c> doc comment describes — so the reason would survive exactly until the
    /// post was next read as a typed post, i.e. until the page that must show it renders.</para>
    /// </summary>
    public const string ErrorKey = "lastPublishError";

    /// <summary>Content key holding when that attempt was made.</summary>
    public const string AttemptedAtKey = "lastPublishAttemptAt";

    /// <summary>
    /// Records <paramref name="reason"/> on the post at <paramref name="postPath"/>, or CLEARS the
    /// recorded reason when <paramref name="reason"/> is null (a publish that succeeded). Cold —
    /// nothing is written until the caller subscribes. Emits the stored node; completes without
    /// writing when the post already says exactly this.
    /// </summary>
    /// <param name="hub">The hub whose workspace owns the write.</param>
    /// <param name="postPath">Path of the post node.</param>
    /// <param name="reason">The reason to record, or null to clear it.</param>
    /// <param name="now">The attempt's timestamp.</param>
    /// <param name="logger">Optional logger for the diagnostic.</param>
    public static IObservable<MeshNode> Record(
        IMessageHub hub, string postPath, string? reason, DateTimeOffset now, ILogger? logger = null)
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
                var current = NodeContentJson.ToJsonObject(node.Content);
                var recorded = current.TryGetPropertyValue(ErrorKey, out var value)
                    && value?.GetValueKind() == System.Text.Json.JsonValueKind.String
                        ? value.GetValue<string>()
                        : null;
                if (string.Equals(recorded, reason, StringComparison.Ordinal))
                    // Already says this. Writing again would re-emit the query the watcher reacts
                    // to, which would bring us straight back here — a self-feeding write storm.
                    return Observable.Empty<MeshNode>();

                logger?.LogInformation(
                    "Recording publish problem on {Path}: {Reason}", postPath, reason ?? "(cleared)");
                return handle.Update(current => current with
                {
                    Content = Apply(current.Content, reason, now),
                });
            });
    }

    /// <summary>
    /// The post's content with the publish problem applied — pure, so the merge rules are testable
    /// without a mesh. A null <paramref name="reason"/> writes JSON nulls, which is how the fields
    /// are CLEARED (omitting them would leave a stale reason on a post that has since published).
    /// The content's <c>$type</c> is preserved; see <see cref="NodeContentJson"/> for why that has
    /// to be said out loud.
    /// </summary>
    /// <param name="content">The post's content as stored, in any shape.</param>
    /// <param name="reason">The reason to record, or null to clear.</param>
    /// <param name="now">The attempt's timestamp; ignored when clearing.</param>
    public static System.Text.Json.Nodes.JsonObject Apply(object? content, string? reason, DateTimeOffset now) =>
        NodeContentJson.Merge(content, PostContentType,
        [
            new(ErrorKey, reason),
            new(AttemptedAtKey, reason is null ? null : now),
        ]);

    /// <summary>
    /// The <c>$type</c> a node-native <c>SocialMedia/Post</c> carries — the FALLBACK only, used
    /// when the stored content names no type of its own (the imported <c>SocialMediaPost</c> shape
    /// keeps its own).
    /// </summary>
    public const string PostContentType = "SocialPost";

    /// <summary>How long a read may take before the record attempt gives up. A diagnostic write
    /// must never outlive the thing it is describing.</summary>
    private static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The sentence a member reads for one of <see cref="LinkedInPublishService"/>'s short refusal
    /// codes — the codes are wire-shaped (<c>not-connected</c>) and mean nothing to the person who
    /// scheduled the post, while the remedy is always a concrete human action.
    ///
    /// <para>🌍 English only, matching every other user-visible string this module ships (the post
    /// workflow's reasons, the layout areas). The platform's localization catalog is a set of
    /// embedded resources in <c>MeshWeaver.Messaging.Hub</c>, so a module cannot contribute keys to
    /// it; giving these sentences <c>host.Localize(...)</c> keys would render the raw key to every
    /// viewer. Translating the module's strings needs the catalog opened to modules first — one
    /// change, in the platform, for all of them.</para>
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
        null or "" => "LinkedIn refused this post" + Status(statusCode) + ".",
        _ => "LinkedIn refused this post" + Status(statusCode) + ": " + reasonCode,
    };

    private static string Status(int statusCode) =>
        statusCode > 0 ? $" (HTTP {statusCode})" : string.Empty;
}
