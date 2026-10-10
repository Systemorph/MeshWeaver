using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using MeshWeaver.Data;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// 🚨 THE BROAD-GRANT GUARD: access that reaches many people, or that the platform writes for
/// someone else, is written ONLY by a governed activity — never by a person's standing rights and
/// never by a platform sweep.
///
/// <para><b>The incident that set it (2026-09-29).</b> A package merged into MeshWeaver.Plugins
/// <c>main</c> was published to every instance, and the Store's start-up sweep then wrote a
/// <c>Viewer + Commenter</c> grant for every user on the public instance — 72 grants in three
/// minutes, under <c>system-security</c>, for no one in particular, each one mailed. Every writer
/// involved was individually "allowed". The maintainer's rule (2026-09-30): "i don't want to have
/// this possibility in principle" — so the shape is refused at the write boundary, whoever writes
/// it, the way <see cref="AccessAssignmentGuard"/> refuses a mis-scoped grant.</para>
///
/// <para><b>The three shapes</b> (<see cref="Evaluate"/>):</para>
/// <list type="bullet">
///   <item><see cref="BroadGrantKind.PublicSubject"/> — a non-denied grant to <c>Public</c>
///     (every signed-in user) or <c>Anonymous</c> (every visitor). A <c>Denied</c> assignment only
///     removes access (the Store's gating) and always passes.</item>
///   <item><see cref="BroadGrantKind.SystemForOther"/> — a grant written under the System identity
///     whose subject is not the one user the write is for (<see cref="AccessContext.OnBehalfOf"/>).
///     A user's own acquisition (subscription, coupon, purchase) stamps <c>OnBehalfOf</c> and
///     passes; a sweep, a backfill or an admin enrolling somebody else does not.</item>
///   <item><see cref="BroadGrantKind.AccessPolicy"/> — a <c>PartitionAccessPolicy</c>
///     (<c>{scope}/_Policy</c>) written by anyone: it caps or opens a whole partition at once.</item>
/// </list>
///
/// <para><b>The one way through.</b> The governance executor stamps
/// <see cref="AccessContext.GovernedBy"/> with the activity it is executing, and the node it writes
/// carries <c>governedBy</c> with the same path. The write boundary asks an
/// <see cref="IGovernedActivityVerifier"/> whether that activity is EXECUTING a standard on
/// <see cref="GovernedStandards"/>; only then does a broad grant pass. A person who is a platform
/// admin holds no such context and cannot mint one.</para>
///
/// <para><b>Log-only first</b> (<see cref="ModeKey"/>, default <see cref="BroadGrantMode.LogOnly"/>).
/// Some legitimate System writers do not stamp <c>OnBehalfOf</c> yet — the partition bootstrap's
/// creator grant, invitation acceptance, the Store's root gating. For one week every finding is
/// logged as <c>[BroadGrantGuard] WOULD REFUSE</c> with the writer, the seat and the shape, so
/// the inventory is one grep; each legitimate writer is then fixed to stamp its context, and the
/// setting flips to <see cref="BroadGrantMode.Enforce"/> (maintainer, 2026-09-30).</para>
/// </summary>
public static class BroadGrantGuard
{
    /// <summary>The setting that selects the mode: <c>LogOnly</c> (default), <c>Enforce</c> or <c>Off</c>.</summary>
    public const string ModeKey = "Access:BroadGrantGuard:Mode";

    /// <summary>
    /// The setting that REPLACES the default allowlist of standards a governed broad grant may
    /// come from (an indexed array, <c>Access:BroadGrantGuard:Standards:0</c>, …).
    /// </summary>
    public const string StandardsKey = "Access:BroadGrantGuard:Standards";

    /// <summary>The node type of a partition access policy.</summary>
    public const string AccessPolicyNodeType = "PartitionAccessPolicy";

    /// <summary>
    /// The NUMBER the Governance package's <c>ActivityState.Executing</c> serialises to when a
    /// writer emits the enum as a number rather than its name. Core deliberately does not
    /// reference that package, so the value is mirrored here in ONE place, and the Governance
    /// package's <c>ActivityTests.ActivityState_ExecutingMatchesCoresBroadGrantGuard</c> pins its
    /// enum to the same value: a renumbering there reds there instead of silently failing closed here.
    /// </summary>
    public const int GovernanceActivityStateExecuting = 4;

    /// <summary>The catalog key of the refusal.</summary>
    public const string RefusalKey = "activity.accessAssignment.broadGrant";

    /// <summary>
    /// The governed standards a broad grant may be written by — the Governance package's ids
    /// (<c>Governance/Standards/{id}</c>). Everything else is refused, including a standard that
    /// exists but was never meant to grant broadly.
    /// </summary>
    public static readonly ImmutableHashSet<string> DefaultGovernedStandards = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "access.grant-broad", "access.revoke", "access.policy-change",
        "package.provision", "package.remove",
        // The two existing standards that already write a grant for another user as System.
        "store.enroll", "pr.steward.admin-access",
        // Public links (MeshWeaver#4306): link.publish grants its audience (Anonymous, Public or an
        // org group) Viewer on the LINK node only — never on the target; link.revoke removes that
        // grant; group.org-create writes the org group and its proposer's Admin on that group alone.
        "link.publish", "link.revoke", "group.org-create");

    /// <summary>The allowlist in effect: <see cref="StandardsKey"/> when set, else the default.</summary>
    public static ImmutableHashSet<string> GovernedStandards(IConfiguration? configuration)
    {
        var configured = configuration?.GetSection(StandardsKey).GetChildren()
            .Select(c => c.Value?.Trim())
            .OfType<string>()
            .Where(v => v.Length > 0)
            .ToImmutableHashSet(StringComparer.Ordinal);
        return configured is { Count: > 0 } ? configured : DefaultGovernedStandards;
    }

    /// <summary>The mode in effect. Unreadable or absent → <see cref="BroadGrantMode.LogOnly"/>. Pure over the configuration.</summary>
    public static BroadGrantMode Mode(IConfiguration? configuration) =>
        Enum.TryParse<BroadGrantMode>(configuration?[ModeKey], ignoreCase: true, out var mode)
            ? mode
            : BroadGrantMode.LogOnly;

    /// <summary>
    /// The broad shape this write has, or null when it is an ordinary write. Governed or not is
    /// decided by the caller (<see cref="IGovernedActivityVerifier"/>) — this predicate is pure.
    /// </summary>
    /// <param name="node">The node being written, already normalised.</param>
    /// <param name="assignment">Its content through the typed accessor, when it is an AccessAssignment.</param>
    /// <param name="writer">The context the write is made under.</param>
    public static BroadGrantFinding? Evaluate(MeshNode? node, AccessAssignment? assignment, AccessContext? writer)
    {
        if (node is null)
            return null;

        if (string.Equals(node.NodeType, AccessPolicyNodeType, StringComparison.OrdinalIgnoreCase))
            return new BroadGrantFinding(BroadGrantKind.AccessPolicy, node.Path, Subject: null, Writer(writer));

        if (!string.Equals(node.NodeType, AccessAssignmentGuard.AccessAssignmentNodeType, StringComparison.OrdinalIgnoreCase)
            || AccessAssignmentGuard.ScopeFromPath(node.Path) is null
            || !GrantsAnything(assignment))
            return null;

        var subject = assignment.AccessObject?.Trim() ?? "";
        if (IsBroadSubject(subject))
            return new BroadGrantFinding(BroadGrantKind.PublicSubject, node.Path, subject, Writer(writer));

        if (IsSystem(writer)
            && !string.Equals(subject, WellKnownUsers.System, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(subject, writer.OnBehalfOf?.Trim(), StringComparison.OrdinalIgnoreCase))
            return new BroadGrantFinding(BroadGrantKind.SystemForOther, node.Path, subject, Writer(writer));

        return null;
    }

    /// <summary><c>Public</c> or <c>Anonymous</c> — a subject that is everybody. Pure.</summary>
    public static bool IsBroadSubject(string? subject) =>
        string.Equals(subject, WellKnownUsers.Public, StringComparison.OrdinalIgnoreCase)
        || string.Equals(subject, WellKnownUsers.Anonymous, StringComparison.OrdinalIgnoreCase);

    /// <summary>The write runs as the platform's own identity. Pure.</summary>
    public static bool IsSystem([NotNullWhen(true)] AccessContext? writer) =>
        string.Equals(writer?.ObjectId, WellKnownUsers.System, StringComparison.OrdinalIgnoreCase);

    /// <summary>At least one non-denied role — a grant that ADDS access. Pure.</summary>
    public static bool GrantsAnything([NotNullWhen(true)] AccessAssignment? assignment) =>
        assignment?.Roles is { } roles
        && roles.Any(r => !r.Denied && !string.IsNullOrWhiteSpace(r.Role));

    /// <summary>
    /// The activity path the node itself names as its origin (<c>content.governedBy</c>, the
    /// back-reference the governance executor writes), or null — from the typed
    /// <see cref="AccessAssignment.GovernedBy"/> / <see cref="PartitionAccessPolicy.GovernedBy"/>, or
    /// shape-tolerantly from raw content (any other node type the executor writes).
    /// </summary>
    public static string? GovernedByOf(MeshNode? node, System.Text.Json.JsonSerializerOptions? options)
    {
        if (node?.Content is not { } content)
            return null;
        // Typed first: the content has usually been materialised by the time it reaches a boundary.
        if (content is AccessAssignment { GovernedBy: { Length: > 0 } typedGrant })
            return typedGrant;
        if (content is PartitionAccessPolicy { GovernedBy: { Length: > 0 } typedPolicy })
            return typedPolicy;
        try
        {
            var element = content is System.Text.Json.JsonElement je
                ? je
                : System.Text.Json.JsonSerializer.SerializeToElement(content, content.GetType(), options);
            return element.ValueKind == System.Text.Json.JsonValueKind.Object
                   && element.TryGetProperty("governedBy", out var value)
                   && value.ValueKind == System.Text.Json.JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the write's context and the node agree on ONE governed activity: both name it, and
    /// they name the same path. The caller then asks the verifier whether that activity is
    /// executing an allowlisted standard. Pure.
    /// </summary>
    public static string? ClaimedActivity(MeshNode? node, AccessContext? writer, System.Text.Json.JsonSerializerOptions? options)
    {
        var fromContext = writer?.GovernedBy?.Trim();
        if (string.IsNullOrEmpty(fromContext))
            return null;
        // A policy node or a grant written by the executor carries the back-reference; a node
        // without one cannot be tied to the activity, so the claim is not taken.
        var fromNode = GovernedByOf(node, options)?.Trim();
        return string.Equals(fromContext, fromNode, StringComparison.Ordinal) ? fromContext : null;
    }

    /// <summary>
    /// The governed activity this write NEWLY claims on its node — <c>content.governedBy</c> when
    /// it is set and differs from what <paramref name="existing"/> already carried — or null.
    ///
    /// <para>🚨 <b>A back-reference is a claim, and a claim is checked where it is made.</b> A
    /// downstream reader (the Store's provision control plane, say) treats <c>governedBy</c> as
    /// "this node was written by that signed activity". Until this check, any writer allowed to
    /// create the node could type that field: a standing admin creating
    /// <c>Admin/Provision/{package}</c> with <c>governedBy</c> naming some activity that happened
    /// to be executing read as governed. A claim the node merely KEEPS (the owner's own
    /// bookkeeping rewriting the node) was checked when it was introduced, so it is not a new
    /// claim. Pure.</para>
    /// </summary>
    public static string? IntroducedClaim(MeshNode? node, MeshNode? existing, System.Text.Json.JsonSerializerOptions? options)
    {
        var claim = GovernedByOf(node, options)?.Trim();
        if (string.IsNullOrEmpty(claim))
            return null;
        var kept = GovernedByOf(existing, options)?.Trim();
        return string.Equals(claim, kept, StringComparison.Ordinal) ? null : claim;
    }

    /// <summary>
    /// Whether the writer IS the activity the node claims — its context carries the same
    /// <see cref="AccessContext.GovernedBy"/>. Only the governance executor opens such a context
    /// (<c>ImpersonateAsSystemFor</c>); a person's session never carries one. The caller then asks
    /// the verifier whether that activity is executing. Pure.
    /// </summary>
    public static bool WriterIsClaimedActivity(string claim, AccessContext? writer) =>
        string.Equals(writer?.GovernedBy?.Trim(), claim, StringComparison.Ordinal);

    /// <summary>The refusal for a node claiming a governed activity its writer is not executing. Pure.</summary>
    public static LocalizableText ClaimRefusal(string path, string claim, AccessContext? writer) =>
        LocalizableText.Keyed(
            $"'{path}' claims to be written by the governed activity '{claim}', but '{Writer(writer)}' "
            + "is not that activity executing. Only the governance executor writes a governedBy "
            + "back-reference; propose the change in Governance/Activities instead.",
            ClaimRefusalKey,
            ("path", path), ("claim", claim), ("writer", Writer(writer)));

    /// <summary>The catalog key of <see cref="ClaimRefusal"/>.</summary>
    public const string ClaimRefusalKey = "activity.node.governedClaim";

    /// <summary>The refusal a write boundary returns when the mode is <see cref="BroadGrantMode.Enforce"/>. Pure.</summary>
    public static LocalizableText Refusal(BroadGrantFinding finding) =>
        LocalizableText.Keyed(
            $"'{finding.Path}' is a broad grant ({finding.Kind}) written by '{finding.Writer}'. "
            + "Access for everyone, access the platform writes for someone else, and partition access "
            + "policies are written only by a governed activity (Governance/Activities) executing a "
            + $"standard on the allowlist ({StandardsKey}). Propose one there.",
            RefusalKey,
            ("path", finding.Path), ("kind", finding.Kind.ToString()), ("writer", finding.Writer));

    private static string Writer(AccessContext? writer) =>
        writer?.ObjectId is { } id && !string.IsNullOrWhiteSpace(id) ? id : "(no identity)";
}

/// <summary>How the broad-grant guard acts on a finding.</summary>
public enum BroadGrantMode
{
    /// <summary>Log every finding as <c>[BroadGrantGuard] WOULD REFUSE</c>; refuse nothing. The default for the first week.</summary>
    LogOnly = 0,

    /// <summary>Refuse every finding that is not governed.</summary>
    Enforce = 1,

    /// <summary>Do nothing. For a test harness only.</summary>
    Off = 2,
}

/// <summary>Which broad shape a write has.</summary>
public enum BroadGrantKind
{
    /// <summary>A non-denied grant to Public or Anonymous.</summary>
    PublicSubject,

    /// <summary>A System-context grant for somebody other than the one user the write is for.</summary>
    SystemForOther,

    /// <summary>A partition access policy.</summary>
    AccessPolicy,
}

/// <summary>One finding of the broad-grant guard.</summary>
/// <param name="Kind">The shape.</param>
/// <param name="Path">The node path.</param>
/// <param name="Subject">The grant's subject, when it is a grant.</param>
/// <param name="Writer">The writing identity.</param>
public sealed record BroadGrantFinding(BroadGrantKind Kind, string Path, string? Subject, string Writer);

/// <summary>
/// What a governed activity IS, read from its node: the standard it runs, its state and the inputs
/// two people signed. Core does not reference the Governance package, so the fields are read
/// shape-tolerantly from raw content (<see cref="Read"/>).
///
/// <para>🚨 <b>Two different questions, two different predicates.</b> At the write boundary the
/// executor's own write happens WHILE the activity executes, so <see cref="IsExecuting"/> is the
/// exact test. A control plane that acts on that write LATER (the Store's provision watcher runs
/// after the executor's create returned) must not ask "is it still executing" — the activity may
/// already be <c>Done</c>, and the answer would depend on which of the two writes was seen first.
/// <see cref="HasStarted"/> is monotone: once the executor started, the signatures were consumed
/// and the state never returns to before it, so the verdict cannot race the activity's
/// completion.</para>
/// </summary>
/// <param name="Path">The activity's node path.</param>
/// <param name="Standard">The standard as written — a path or an id.</param>
/// <param name="State">The state's name (<c>Executing</c>, <c>Done</c>, …), normalised from a number when the writer emitted one.</param>
/// <param name="Inputs">The inputs, as signed.</param>
public sealed record GovernedActivityFacts(
    string Path, string Standard, string State, ImmutableDictionary<string, string> Inputs)
{
    /// <summary>The state name of an executing activity.</summary>
    public const string Executing = "Executing";

    /// <summary>The state name of an activity whose executor completed.</summary>
    public const string Done = "Done";

    /// <summary>The state name of an activity whose executor failed.</summary>
    public const string Failed = "Failed";

    /// <summary>
    /// The numbers the Governance package's <c>ActivityState</c> serialises to, mirrored in ONE place
    /// (see <see cref="BroadGrantGuard.GovernanceActivityStateExecuting"/>): <c>Executing = 4</c>,
    /// <c>Done = 5</c>, <c>Failed = 6</c>.
    /// </summary>
    public static readonly ImmutableDictionary<int, string> StateNumbers = ImmutableDictionary.CreateRange(
        new[]
        {
            KeyValuePair.Create(BroadGrantGuard.GovernanceActivityStateExecuting, Executing),
            KeyValuePair.Create(BroadGrantGuard.GovernanceActivityStateExecuting + 1, Done),
            KeyValuePair.Create(BroadGrantGuard.GovernanceActivityStateExecuting + 2, Failed),
        });

    /// <summary>The standard's id — the last segment of <see cref="Standard"/>.</summary>
    public string StandardId
    {
        get
        {
            var id = Standard.Trim().Trim('/');
            var slash = id.LastIndexOf('/');
            return slash < 0 ? id : id[(slash + 1)..];
        }
    }

    /// <summary>The executor is running now.</summary>
    public bool IsExecuting => string.Equals(State, Executing, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The executor has started — <c>Executing</c>, <c>Done</c> or <c>Failed</c>. Reachable only
    /// after every gate, signatures included, was green, and never left again: the monotone fact a
    /// later reader may decide on.
    /// </summary>
    public bool HasStarted => IsExecuting
                              || string.Equals(State, Done, StringComparison.OrdinalIgnoreCase)
                              || string.Equals(State, Failed, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The activity node's own <see cref="MeshNode.NodeType"/> (the Governance package writes
    /// <c>Governance/Activity</c>), or null when the facts were built without one. A reader that
    /// grants authority on the strength of these facts checks it, so a node of another type that
    /// happens to carry a <c>standard</c> field is never read as an activity.
    /// </summary>
    public string? NodeType { get; init; }

    /// <summary>
    /// How many of the activity's signatures were CONSUMED — stamped <c>consumedAt</c> by the
    /// control plane in the same write that made the activity ready, after the own-write check
    /// admitted them. Zero means no signature authorised this activity's execution. How MANY a
    /// standard requires is the standard's gate, judged by the Governance package; this count is
    /// the floor a reader outside that package can still check: at least one person signed.
    /// </summary>
    public int ConsumedSignatures { get; init; }

    /// <summary>The signed input <paramref name="name"/>, trimmed, or null.</summary>
    public string? Input(string name) =>
        Inputs.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    /// <summary>
    /// Reads an activity node's facts from its raw content, or null when it carries none (no node,
    /// no content, no standard). Pure; never throws.
    /// </summary>
    public static GovernedActivityFacts? Read(MeshNode? activity, System.Text.Json.JsonSerializerOptions? options)
    {
        if (activity?.Content is not { } content)
            return null;
        try
        {
            var element = content is System.Text.Json.JsonElement je
                ? je
                : System.Text.Json.JsonSerializer.SerializeToElement(content, content.GetType(), options);
            if (element.ValueKind != System.Text.Json.JsonValueKind.Object
                || !element.TryGetProperty("standard", out var standard)
                || standard.ValueKind != System.Text.Json.JsonValueKind.String
                || standard.GetString() is not { } standardText
                || string.IsNullOrWhiteSpace(standardText))
                return null;
            var state = element.TryGetProperty("state", out var s)
                ? s.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.String => s.GetString() ?? "",
                    System.Text.Json.JsonValueKind.Number when s.TryGetInt32(out var n) =>
                        StateNumbers.TryGetValue(n, out var name) ? name : n.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    _ => "",
                }
                : "";
            var inputs = ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);
            if (element.TryGetProperty("inputs", out var i) && i.ValueKind == System.Text.Json.JsonValueKind.Object)
                inputs = inputs.AddRange(i.EnumerateObject()
                    .Where(p => p.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                    .Select(p => KeyValuePair.Create(p.Name, p.Value.GetString() ?? "")));
            var consumed = element.TryGetProperty("signatures", out var signatures)
                           && signatures.ValueKind == System.Text.Json.JsonValueKind.Array
                ? signatures.EnumerateArray().Count(sig =>
                    sig.ValueKind == System.Text.Json.JsonValueKind.Object
                    && sig.TryGetProperty("consumedAt", out var at)
                    && at.ValueKind == System.Text.Json.JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(at.GetString()))
                : 0;
            return new GovernedActivityFacts(activity.Path, standardText, state, inputs)
            {
                NodeType = activity.NodeType,
                ConsumedSignatures = consumed,
            };
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>
/// Answers whether a governed activity is EXECUTING one of the allowlisted standards — the only
/// state in which its executor's broad write is legitimate. The default implementation reads the
/// activity node; a host may register its own.
/// </summary>
public interface IGovernedActivityVerifier
{
    /// <summary>True when <paramref name="activityPath"/> is executing a standard in <paramref name="allowed"/>. Cold; never throws.</summary>
    IObservable<bool> IsExecuting(string activityPath, IReadOnlySet<string> allowed);
}
