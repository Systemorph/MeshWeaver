using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.ControlLane;

/// <summary>
/// The sources a forwarded event names. An OPEN vocabulary (policy
/// <c>open-vocabulary-string-constants</c>): the platform's starting set; a caller forwarding events
/// of another origin names its own value, and the target stores it as it came.
/// </summary>
public static class ControlLaneEventSource
{
    /// <summary>A GitHub webhook delivery the control instance's inbox verified.</summary>
    public const string GitHub = "github";
}

/// <summary>
/// ONE signed FORWARDED EVENT from the control instance to a target instance — the lane's third
/// kind beside a request and a report (Doc/Architecture/ControlLane → "Forwarded events"). The
/// control instance received and verified a delivery (a GitHub webhook event on the ONE organisation
/// hook), and hands it to the instance that consumes it, signed with that instance's OWN key. The
/// target stores it verbatim into ONE local inbox (<c>{target}/_Inbox/{eventId}</c>) that its own
/// configuration names (<see cref="ControlLaneEvents.TargetsSection"/>); a consumer there treats it
/// as a trigger and re-reads the live state.
///
/// <para>🚨 It carries NO operation. The target runs nothing for it — no plan, no approval, no
/// report — so it needs none of the request's approval binding: its whole effect is one node in an
/// inbox this instance declared open to the lane. Everything else about it is the request's rules:
/// the signature before a single field is read, this deployment by name, a validity window of at most
/// <see cref="ControlLaneAdmission.MaxLifetime"/>, single use (the inbox node's CREATION is the
/// claim).</para>
/// </summary>
public sealed record ControlLaneEvent
{
    /// <summary>The wire kind of a forwarded event — neither a request nor a report can be read as one.</summary>
    public const string EventKind = "control-lane-event";

    /// <summary>Always <see cref="EventKind"/>.</summary>
    public string Kind { get; init; } = EventKind;

    /// <summary>The envelope version. The target refuses any version it does not speak.</summary>
    public int Version { get; init; } = 1;

    /// <summary>Unique per event (16–64 letters, digits or dashes) — the stored inbox node's id, so a replay's create fails.</summary>
    public string EventId { get; init; } = "";

    /// <summary>The TARGET deployment id (<c>Hosting:Deployment</c> on the target).</summary>
    public string Deployment { get; init; } = "";

    /// <summary>Where the event came from (<see cref="ControlLaneEventSource"/>).</summary>
    public string Source { get; init; } = "";

    /// <summary>The source's own event name (for GitHub, the <c>X-GitHub-Event</c> header: <c>pull_request</c>, <c>check_suite</c>, …).</summary>
    public string Name { get; init; } = "";

    /// <summary>The target's local inbox owner — must be one of the target's <see cref="ControlLaneEvents.TargetsSection"/>.</summary>
    public string Target { get; init; } = "";

    /// <summary>The event body, verbatim as the control instance received and verified it.</summary>
    public string Payload { get; init; } = "";

    /// <summary>When the control instance signed it.</summary>
    public DateTimeOffset IssuedAt { get; init; }

    /// <summary>After this instant the target refuses it.</summary>
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>
/// The forwarded-event half of the lane: the target's allowlist, pure admission, and the store.
/// </summary>
public static class ControlLaneEvents
{
    /// <summary>
    /// The configuration list naming the local inboxes a target accepts forwarded events into
    /// (<c>ControlLane:EventTargets:0</c>, …). Empty — the default — accepts none: an armed lane
    /// takes forwarded events only where the instance said so. Deliberately NOT the public webhook
    /// inbox list: a lane target need not be reachable from the internet at all.
    /// </summary>
    public const string TargetsSection = "ControlLane:EventTargets";

    /// <summary>The header a stored forwarded event carries its lane id under — how a consumer tells it from a direct delivery.</summary>
    public const string EventIdHeader = "X-Control-Lane-Event";

    /// <summary>The header carrying the forwarded event's source.</summary>
    public const string SourceHeader = "X-Control-Lane-Source";

    /// <summary>The header carrying a GitHub event's name, as GitHub itself sends it.</summary>
    public const string GitHubEventHeader = "X-GitHub-Event";

    /// <summary>The longest payload forwarded — the webhook inbox's own cap.</summary>
    public const int MaxPayloadBytes = WebhookInbox.MaxBodyBytes;

    private static readonly Regex EventIdPattern = new("^[A-Za-z0-9-]{16,64}$", RegexOptions.Compiled);
    private static readonly Regex NamePattern = new("^[A-Za-z0-9_.-]{1,64}$", RegexOptions.Compiled);

    /// <summary>The local inboxes this instance accepts forwarded events into. Pure over the configuration.</summary>
    /// <param name="configuration">The configuration.</param>
    public static ImmutableArray<string> Targets(IConfiguration? configuration) =>
        configuration is null
            ? []
            : configuration.GetSection(TargetsSection).GetChildren()
                .Select(c => WebhookInbox.NormalizeTarget(c.Value))
                .Where(t => t is not null)
                .Select(t => t!)
                .Distinct(StringComparer.Ordinal)
                .ToImmutableArray();

    /// <summary>
    /// The verdict and the reason for a VERIFIED forwarded event, or <c>(Accepted, null)</c>: a
    /// well-formed envelope, this deployment, inside its window, a target this instance declared, a
    /// payload within the cap. Pure.
    /// </summary>
    /// <param name="evt">The parsed event, or null when the body is not one.</param>
    /// <param name="self">This deployment's id.</param>
    /// <param name="now">Now.</param>
    /// <param name="targets">The declared targets (<see cref="Targets"/>).</param>
    public static (string Verdict, string? Why) Admit(
        ControlLaneEvent? evt, string self, DateTimeOffset now, IReadOnlyCollection<string> targets)
    {
        if (evt is null)
            return (ControlLaneVerdict.Malformed, "the body is not a forwarded control-lane event");
        if (evt.Version != ControlLaneAdmission.Version)
            return (ControlLaneVerdict.Malformed, $"envelope version {evt.Version} is not spoken here (this target speaks {ControlLaneAdmission.Version})");
        if (!EventIdPattern.IsMatch(evt.EventId ?? ""))
            return (ControlLaneVerdict.Malformed, "the event id is not 16–64 letters, digits or dashes");
        if (!NamePattern.IsMatch(evt.Source ?? "") || !NamePattern.IsMatch(evt.Name ?? ""))
            return (ControlLaneVerdict.Malformed, "the event's source or name is not a plain token");
        if (!string.Equals(evt.Deployment, self, StringComparison.Ordinal))
            return (ControlLaneVerdict.WrongDeployment, $"the event names deployment '{evt.Deployment}', and this instance is '{self}'");
        if (evt.ExpiresAt <= evt.IssuedAt || evt.ExpiresAt - evt.IssuedAt > ControlLaneAdmission.MaxLifetime)
            return (ControlLaneVerdict.Expired,
                $"the validity window {evt.IssuedAt:O} → {evt.ExpiresAt:O} is empty or longer than {ControlLaneAdmission.MaxLifetime.TotalMinutes:0} minutes");
        if (evt.IssuedAt > now + ControlLaneAdmission.ClockSkew)
            return (ControlLaneVerdict.Expired, $"the event was issued in the future ({evt.IssuedAt:O}; now {now:O})");
        if (now >= evt.ExpiresAt)
            return (ControlLaneVerdict.Expired, $"the event expired at {evt.ExpiresAt:O} (now {now:O})");
        var target = WebhookInbox.NormalizeTarget(evt.Target);
        if (target is null || !targets.Contains(target, StringComparer.Ordinal))
            return (ControlLaneVerdict.Refused,
                $"'{evt.Target}' is not an inbox this instance accepts forwarded events into ({TargetsSection}: "
                + (targets.Count == 0 ? "none declared" : string.Join(", ", targets)) + ")");
        if (System.Text.Encoding.UTF8.GetByteCount(evt.Payload ?? "") > MaxPayloadBytes)
            return (ControlLaneVerdict.Refused, $"the payload is larger than {MaxPayloadBytes} bytes");
        return (ControlLaneVerdict.Accepted, null);
    }

    /// <summary>The stored inbox node of an admitted event: <c>{target}/_Inbox/{eventId}</c>, a <see cref="WebhookEvent"/> carrying the payload verbatim. Pure.</summary>
    /// <param name="evt">The admitted event.</param>
    /// <param name="now">When it was received.</param>
    public static MeshNode InboxNode(ControlLaneEvent evt, DateTimeOffset now)
    {
        var target = WebhookInbox.NormalizeTarget(evt.Target)!;
        var headers = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        headers[EventIdHeader] = evt.EventId;
        headers[SourceHeader] = evt.Source;
        if (evt.Source == ControlLaneEventSource.GitHub)
            headers[GitHubEventHeader] = evt.Name;
        return new MeshNode(evt.EventId, $"{target}/{WebhookInbox.InboxContainer}")
        {
            Name = $"Forwarded {evt.Source} {evt.Name} {evt.EventId}",
            NodeType = WebhookInbox.NodeType,
            MainNode = target,
            Content = new WebhookEvent
            {
                ReceivedAt = now,
                ContentType = "application/json",
                Headers = headers.ToImmutable(),
                Body = evt.Payload ?? "",
            },
        };
    }

    /// <summary>
    /// Stores an ADMITTED event: the target node must exist (the inbox anchors under a real owner),
    /// then the inbox node is CREATED — its creation is the single-use claim, so a replay is
    /// <see cref="ControlLaneVerdict.Replayed"/>. Existence is read as a LISTING of the target's
    /// parent's children (never a point read of a path whose existence is the question), and a
    /// listing that is not an ANSWER refuses by name rather than reading as "absent". As system.
    /// Cold; never errors — a refusal is data.
    /// </summary>
    /// <param name="hub">The target's mesh hub.</param>
    /// <param name="evt">The admitted event.</param>
    /// <param name="now">When it was received.</param>
    public static IObservable<ControlLaneReceipt> Store(IMessageHub hub, ControlLaneEvent evt, DateTimeOffset now)
    {
        var mesh = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var target = WebhookInbox.NormalizeTarget(evt.Target)!;
        return SpaceDeletion.AsSystem(hub, () => MeshReading.Read(mesh, ExistenceQuery(target)))
            .SelectMany(reading =>
            {
                if (!reading.IsAnswer)
                    return Observable.Return(new ControlLaneReceipt(ControlLaneVerdict.Refused,
                        $"whether the target '{target}' exists could not be established ({reading.WhyNotAnAnswer}), so the event was not stored",
                        evt.EventId));
                if (!reading.Rows.Any(n => n.Path == target))
                    return Observable.Return(new ControlLaneReceipt(ControlLaneVerdict.Refused,
                        $"the target '{target}' does not exist on this instance, so there is no inbox to store the event in", evt.EventId));
                return SpaceDeletion.AsSystem(hub, () => mesh.CreateNode(InboxNode(evt, now)))
                    .Take(1)
                    .Timeout(MeshReading.DefaultBudget)
                    .Select(_ => new ControlLaneReceipt(ControlLaneVerdict.Accepted, null, evt.EventId));
            })
            .Catch((Exception ex) => Observable.Return(ex.IsNodeAlreadyExists()
                ? new ControlLaneReceipt(ControlLaneVerdict.Replayed, $"event {evt.EventId} was already received — a forwarded event is single use", evt.EventId)
                : new ControlLaneReceipt(ControlLaneVerdict.Refused,
                    $"the event could not be stored ({ex.GetType().Name}: {ex.Message})", evt.EventId)));
    }

    /// <summary>
    /// The listing that establishes whether <paramref name="target"/> exists: its parent's
    /// children, or — for a partition root, which has no parent — the root's own path. Pure.
    /// </summary>
    /// <param name="target">A normalized target path.</param>
    public static string ExistenceQuery(string target)
    {
        var slash = target.LastIndexOf('/');
        return slash > 0 ? $"path:{target[..slash]} scope:children" : $"path:{target}";
    }
}
