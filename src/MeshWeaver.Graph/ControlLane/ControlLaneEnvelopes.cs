using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeshWeaver.Graph.Configuration;

namespace MeshWeaver.Graph.ControlLane;

/// <summary>
/// The operations the control→instance lane carries (Doc/Architecture/ControlLane). An OPEN
/// vocabulary (policy <c>open-vocabulary-string-constants</c>): these are the platform's starting
/// set, and a module adds its own by registering an <see cref="IControlLaneOperation"/> that claims a
/// value of its own — no change here. A value no registered operation claims is refused BY NAME,
/// never mapped to a default.
/// </summary>
public static class ControlLaneOperation
{
    /// <summary>Recycle one address on the target, as system — <c>hub.RecycleNode</c>.</summary>
    public const string Recycle = "Recycle";

    /// <summary>Delete one space on the target, as system — <see cref="SpaceDeletion"/>.</summary>
    public const string DeleteSpace = "DeleteSpace";

    /// <summary>Reboot the target — file ONE <c>InstanceReboot</c> request there (<see cref="RebootOperation"/>,
    /// Doc/Architecture/InstanceReboot). The person's request IS the signature: no second approver.</summary>
    public const string Reboot = "Reboot";
}

/// <summary>
/// The states a <see cref="ControlLaneReport"/> announces. Open string constants, like every
/// persisted vocabulary here; <see cref="IsTerminal"/> is the one rule that reads them.
/// </summary>
public static class ControlLaneStatus
{
    /// <summary>The target verified and recorded the request; nothing has run yet.</summary>
    public const string Accepted = "Accepted";

    /// <summary>The target computed its plan (a dry run ends here).</summary>
    public const string Planned = "Planned";

    /// <summary>One line of progress from a running operation.</summary>
    public const string Progress = "Progress";

    /// <summary>The target refused the request before touching anything — terminal.</summary>
    public const string Refused = "Refused";

    /// <summary>The operation completed and verified — terminal.</summary>
    public const string Done = "Done";

    /// <summary>The operation started and failed — terminal.</summary>
    public const string Failed = "Failed";

    /// <summary>Whether <paramref name="status"/> ends the request. A dry run's <see cref="Planned"/>
    /// is terminal FOR A DRY RUN only, so the caller passes that fact. Pure.</summary>
    public static bool IsTerminal(string? status, bool dryRun) =>
        status is Refused or Done or Failed || (dryRun && status == Planned);
}

/// <summary>
/// ONE signed request from the control instance to a target instance: which deployment, which
/// operation on which target, the plan digest an approval bound, who asked and who approved, and
/// how long the request is good for. The body is signed as sent (HMAC-SHA256 over the raw bytes,
/// <c>X-Hub-Signature-256</c>) with the TARGET's per-deployment key; the target verifies the
/// signature, the expiry and single use before it reads anything else.
/// </summary>
public sealed record ControlLaneRequest
{
    /// <summary>The wire kind of a request — a report can never be read as one.</summary>
    public const string RequestKind = "control-lane-request";

    /// <summary>Always <see cref="RequestKind"/>.</summary>
    public string Kind { get; init; } = RequestKind;

    /// <summary>The envelope version. The target refuses any version it does not speak.</summary>
    public int Version { get; init; } = 1;

    /// <summary>Unique per request — the target's single-use ledger key.</summary>
    public string RequestId { get; init; } = "";

    /// <summary>The TARGET deployment id (<c>Hosting:Deployment</c> on the target). A request naming
    /// another deployment is refused.</summary>
    public string Deployment { get; init; } = "";

    /// <summary>The operation (<see cref="ControlLaneOperation"/> or a module's own value).</summary>
    public string Operation { get; init; } = "";

    /// <summary>What the operation acts on — an address to recycle, a space to delete.</summary>
    public string Target { get; init; } = "";

    /// <summary>The target typed again, where the operation requires it (DeleteSpace).</summary>
    public string? Confirmation { get; init; }

    /// <summary>True: compute and report the plan, change nothing.</summary>
    public bool DryRun { get; init; }

    /// <summary>The digest of the plan the approval bound (<c>sha256:…</c>). Required on a real
    /// run: the target executes only when the plan it computes itself has this digest.</summary>
    public string? PlanDigest { get; init; }

    /// <summary>The requester's reason — carried onto the target's audit and dispose reasons.</summary>
    public string Reason { get; init; } = "";

    /// <summary>Who asked, on the control instance.</summary>
    public string? RequestedBy { get; init; }

    /// <summary>Who approved, on the control instance. Required on a real run.</summary>
    public string? ApprovedBy { get; init; }

    /// <summary>When it was approved.</summary>
    public DateTimeOffset? ApprovedAt { get; init; }

    /// <summary>The control instance's action node the reports go back to.</summary>
    public string Action { get; init; } = "";

    /// <summary>When the control instance signed the request.</summary>
    public DateTimeOffset IssuedAt { get; init; }

    /// <summary>After this instant the target refuses the request.</summary>
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>
/// A SET a plan step acts on, stated as an anchored, scoped mesh QUERY and a COUNT — never as an
/// enumeration of its members (a plan never contains a listing). The same shape as MeshWeaver.Plugins'
/// <c>PlanTarget</c>, so the control instance parks the target's plan without translation.
/// </summary>
public sealed record ControlLanePlanTarget
{
    /// <summary>What the set is, as a label KEY the page localizes (<c>grants</c>, <c>nodetypes</c>). Shown, never bound.</summary>
    public string Label { get; init; } = "";

    /// <summary>The set, as a mesh query — anchored, scoped. Bound.</summary>
    public string Query { get; init; } = "";

    /// <summary>How many members the plan read, or null when the set is deliberately not counted. Bound when stated.</summary>
    public int? Count { get; init; }
}

/// <summary>One step of a <see cref="ControlLanePlan"/>: position, name, the exact command, whether
/// it destroys something, and the sets it acts on (<see cref="Targets"/>).</summary>
public sealed record ControlLanePlanStep
{
    /// <summary>1-based position.</summary>
    public int Number { get; init; }

    /// <summary>The step's name.</summary>
    public string Name { get; init; } = "";

    /// <summary>The command, verbatim.</summary>
    public string Command { get; init; } = "";

    /// <summary>True when the step destroys something.</summary>
    public bool Destructive { get; init; }

    /// <summary>The sets this step acts on, each a query with its count. Empty for a step that acts on no set.</summary>
    public ImmutableList<ControlLanePlanTarget> Targets { get; init; } = [];

    /// <summary>Value equality including <see cref="Targets"/> by content.</summary>
    public bool Equals(ControlLanePlanStep? other) =>
        other is not null && Number == other.Number
        && string.Equals(Name, other.Name, StringComparison.Ordinal)
        && string.Equals(Command, other.Command, StringComparison.Ordinal)
        && Destructive == other.Destructive
        && Targets.SequenceEqual(other.Targets);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Number, Name, Command, Destructive, Targets.Count);
}

/// <summary>
/// The plan a target computes for one request — what an approver reads and what the approval
/// binds. The TARGET is the authority: it computes the plan from what it reads, reports it, and
/// on the real run executes only a plan whose <see cref="Digest"/> equals the approved one.
///
/// <para>🚨 <see cref="Digest"/> is the SAME function as MeshWeaver.Plugins' in-mesh
/// <c>ActionPlanSnapshot.Digest</c> with no namespace and no image (<c>action-plan/v1</c>,
/// length-prefixed, injective). It has to be: the control instance parks its action with the
/// reported plan, the approval binds that snapshot's digest, and the target compares against its
/// own. The control side recomputes the digest over the reported steps and refuses a report whose
/// stated digest disagrees, so a drift between the two implementations is a loud refusal, never
/// a silently unexecutable approval.</para>
/// </summary>
public sealed record ControlLanePlan
{
    /// <summary>The executor every lane plan names — where the steps run.</summary>
    public const string LaneExecutor = "control-lane";

    /// <summary>The operation, as the wire spells it.</summary>
    public string Kind { get; init; } = "";

    /// <summary>The target deployment.</summary>
    public string Deployment { get; init; } = "";

    /// <summary>Where the steps run (<see cref="LaneExecutor"/>).</summary>
    public string Executor { get; init; } = LaneExecutor;

    /// <summary>The steps, in order.</summary>
    public ImmutableList<ControlLanePlanStep> Steps { get; init; } = ImmutableList<ControlLanePlanStep>.Empty;

    /// <summary>Prose beside the plan (row counts, warnings). Shown, never bound.</summary>
    public ImmutableList<string> Notes { get; init; } = ImmutableList<string>.Empty;

    /// <summary>Builds a plan from (name, command, destructive) triples, numbering them. Pure.</summary>
    public static ControlLanePlan Of(
        string kind, string deployment, IEnumerable<(string Name, string Command, bool Destructive)> steps,
        IEnumerable<string>? notes = null) =>
        new()
        {
            Kind = kind,
            Deployment = deployment,
            Steps = steps.Select((s, i) => new ControlLanePlanStep
            {
                Number = i + 1, Name = s.Name, Command = s.Command, Destructive = s.Destructive,
            }).ToImmutableList(),
            Notes = (notes ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).ToImmutableList(),
        };

    /// <summary>Builds a plan from structured steps, numbering them in order. Pure.</summary>
    public static ControlLanePlan OfSteps(
        string kind, string deployment, IEnumerable<ControlLanePlanStep> steps, IEnumerable<string>? notes = null) =>
        new()
        {
            Kind = kind,
            Deployment = deployment,
            Steps = steps.Select((s, i) => s with { Number = i + 1 }).ToImmutableList(),
            Notes = (notes ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).ToImmutableList(),
        };

    /// <summary>
    /// <c>sha256:</c> + lower hex over what EXECUTES — kind, deployment, (no namespace), (no image),
    /// executor and every step. A plan with no targets is the <c>action-plan/v1</c> encoding; a plan
    /// whose steps state sets is <c>action-plan/v2</c>, which also binds each target's query and
    /// count (never a member, never the label) — exactly MeshWeaver.Plugins' <c>ActionPlanSnapshot.Digest</c>. Pure.
    /// </summary>
    public string Digest()
    {
        static string F(string? v) => v is null ? "~;" : $"{v.Length.ToString(CultureInfo.InvariantCulture)}:{v};";
        var structured = Steps.Any(s => s.Targets.Count > 0);
        var sb = new StringBuilder(structured ? "action-plan/v2;" : "action-plan/v1;");
        sb.Append(F(Kind)).Append(F(Deployment)).Append(F(null)).Append(F(null)).Append(F(Executor))
          .Append(F(Steps.Count.ToString(CultureInfo.InvariantCulture)));
        foreach (var step in Steps)
        {
            sb.Append(F(step.Number.ToString(CultureInfo.InvariantCulture)))
              .Append(F(step.Destructive ? "destructive" : "safe"))
              .Append(F(step.Name)).Append(F(step.Command));
            if (!structured)
                continue;
            sb.Append(F(step.Targets.Count.ToString(CultureInfo.InvariantCulture)));
            foreach (var target in step.Targets)
                sb.Append(F(target.Query)).Append(F(target.Count?.ToString(CultureInfo.InvariantCulture)));
        }
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }
}

/// <summary>
/// ONE signed report from a target back to the control instance's inbox: the request it answers,
/// its status, the plan the target computed and a line of detail. Signed with the SAME
/// per-deployment key the request was verified with, and posted to the control instance's
/// configured inbox — never to a URL the request named, so a request cannot redirect a report.
/// </summary>
public sealed record ControlLaneReport
{
    /// <summary>The inbox event name of a report.</summary>
    public const string ReportEvent = "control-lane-report";

    /// <summary>Always <see cref="ReportEvent"/> — the control inbox routes on it.</summary>
    public string Event { get; init; } = ReportEvent;

    /// <summary>The reporting deployment (the target). The control instance verifies the report
    /// with THIS deployment's own key and nothing else.</summary>
    public string Deployment { get; init; } = "";

    /// <summary>The request this answers.</summary>
    public string RequestId { get; init; } = "";

    /// <summary>The control instance's action node (echoed from the request).</summary>
    public string Action { get; init; } = "";

    /// <summary>The operation.</summary>
    public string Operation { get; init; } = "";

    /// <summary>What it acted on.</summary>
    public string Target { get; init; } = "";

    /// <summary>Whether the request was a dry run.</summary>
    public bool DryRun { get; init; }

    /// <summary>The <see cref="ControlLaneStatus"/>.</summary>
    public string Status { get; init; } = "";

    /// <summary>The plan the target computed, when it has one.</summary>
    public ControlLanePlan? Plan { get; init; }

    /// <summary>That plan's digest, as the target computed it.</summary>
    public string? PlanDigest { get; init; }

    /// <summary>One line: the progress, the refusal, the failure, or the audit line.</summary>
    public string Message { get; init; } = "";

    /// <summary>Monotonic per request, so the control side can order and de-duplicate.</summary>
    public int Sequence { get; init; }

    /// <summary>When the target wrote it.</summary>
    public DateTimeOffset At { get; init; }
}

/// <summary>
/// The wire half of the lane: canonical bodies, the GitHub-style HMAC signature both directions
/// use, and parsing that never throws. Pure; never logs, stores or returns a key.
/// </summary>
public static class ControlLaneWire
{
    /// <summary>The signature header — the same one the webhook inbox verifies.</summary>
    public const string SignatureHeader = WebhookInbox.SignatureHeader;

    /// <summary>camelCase, nulls omitted — the body is signed exactly as serialised.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The raw body of an envelope. Pure.</summary>
    public static string Body<T>(T envelope) => JsonSerializer.Serialize(envelope, Json);

    /// <summary><c>sha256=</c> + lowercase hex HMAC-SHA256 of the raw body. Pure.</summary>
    public static string Sign(string body, string key)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        return "sha256=" + Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
    }

    /// <summary>Constant-time verification of <see cref="Sign"/>. False for anything absent or malformed. Pure.</summary>
    public static bool Verify(string? signature, string body, string? key) =>
        !string.IsNullOrWhiteSpace(key) && WebhookInbox.VerifyHmacSha256(signature, body, key);

    /// <summary>
    /// The request in <paramref name="body"/>, or null when it is not one. The discriminator must
    /// be PRESENT in the body (<c>"kind": "control-lane-request"</c>) — a defaulted property would
    /// let a report, signed with the same key, parse as a request. Never throws.
    /// </summary>
    public static ControlLaneRequest? ParseRequest(string? body) =>
        Discriminator(body, "kind") == ControlLaneRequest.RequestKind ? Parse<ControlLaneRequest>(body) : null;

    /// <summary>The forwarded event in <paramref name="body"/>, or null when it is not one; the
    /// <c>"kind": "control-lane-event"</c> discriminator must be present, so neither a request nor
    /// a report can be read as one. Never throws.</summary>
    public static ControlLaneEvent? ParseEvent(string? body) =>
        Discriminator(body, "kind") == ControlLaneEvent.EventKind ? Parse<ControlLaneEvent>(body) : null;

    /// <summary>The report in <paramref name="body"/>, or null when it is not one; the
    /// <c>"event": "control-lane-report"</c> discriminator must be present. Never throws.</summary>
    public static ControlLaneReport? ParseReport(string? body) =>
        Discriminator(body, "event") == ControlLaneReport.ReportEvent ? Parse<ControlLaneReport>(body) : null;

    private static string? Discriminator(string? body, string property)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty(property, out var value)
                   && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static T? Parse<T>(string? body) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body!, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
