using System.Collections.Generic;
using System.ComponentModel;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph.Logon;

/// <summary>How often a logon action runs for one user.</summary>
public enum LogonActionMode
{
    /// <summary>
    /// Runs at most once per user, ever. The ledger is
    /// <see cref="User.CompletedLogonActions"/> — durable, replicated with the profile, and
    /// written in the SAME patch as the action's profile change, so "it ran" and "what it did"
    /// can never disagree. This is the mode for a one-time migration.
    /// </summary>
    RunOnce,

    /// <summary>
    /// Runs on every logon. No ledger — an every-logon action must decide for itself, cheaply,
    /// that there is nothing to do (the icon-adoption action's <c>NeedsIcon</c> check is the
    /// canonical shape). Use it when new work can appear later: an app installed today needs
    /// adopting tomorrow, which a run-once action would never see.
    /// </summary>
    EveryLogon,
}

/// <summary>
/// A logon action DECLARED AS DATA — a <c>LogonAction</c> node under
/// <see cref="MeshWeaver.Graph.Configuration.LogonActionNodeType.ActionNamespace"/>
/// (<c>Admin/_LogonAction/{id}</c>) that an admin creates in-platform, with no code change and no
/// image roll.
///
/// <para>🚨 <b>This is what makes a logon action DEPLOYMENT-SPECIFIC.</b> The framework ships in
/// core and zero action nodes ship with it, so a portal that never declares one runs nothing. That
/// is the whole reason the concrete pin migration is data rather than a constant in core:
/// memex.meshweaver.cloud carries the agentic-engineering courses and systemorph.com does not, and
/// a hard-coded course path would pin a dangling node on every deployment that lacks it.</para>
///
/// <para>Deliberately NOT a scripting engine. The declarative surface is exactly the profile
/// operations a migration needs — unpin these paths, pin those — because the alternative (a code
/// string in a JSON field) is invisible to the compiler, to <c>dotnet build</c> and to every
/// grep. Anything richer is a code-declared <see cref="ILogonAction"/> instead.</para>
/// </summary>
public record LogonAction
{
    /// <summary>What this action is for. Shown to an admin listing the platform's logon actions.</summary>
    [Description("What this action does, in one line — shown when an admin lists the platform's logon actions.")]
    [Translation("de", "Was diese Aktion tut, in einer Zeile — wird angezeigt, wenn ein Administrator die Anmeldeaktionen der Plattform auflistet.")]
    public string? Description { get; init; }

    /// <summary>Whether this runs once per user, ever, or on every logon.</summary>
    [Description("Whether this runs once per user, ever, or on every logon.")]
    [Translation("de", "Ob dies einmal pro Benutzer oder bei jeder Anmeldung ausgeführt wird.")]
    public LogonActionMode Mode { get; init; } = LogonActionMode.RunOnce;

    /// <summary>Ascending run order. Actions with the same order run in id order, so the sequence is stable.</summary>
    [Description("Run order, ascending. Actions sharing an order run alphabetically by id, so the sequence is always the same.")]
    [Translation("de", "Ausführungsreihenfolge, aufsteigend. Aktionen mit gleicher Reihenfolge laufen alphabetisch nach Id, damit die Abfolge stets gleich ist.")]
    public int Order { get; init; }

    /// <summary>Set false to park the action without deleting it (and without losing the ledger).</summary>
    [Description("Turn the action off without deleting it. The per-user ledger is kept, so re-enabling does not re-run it for users who already had it.")]
    [Translation("de", "Aktion deaktivieren, ohne sie zu löschen. Das Protokoll pro Benutzer bleibt erhalten, ein erneutes Aktivieren führt sie also nicht noch einmal aus.")]
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Node paths to REMOVE from the user's pinned items. Removed whether or not the node still
    /// exists — unpinning a path that has gone away is exactly what you want.
    /// </summary>
    [Description("Node paths to remove from the user's pinned items.")]
    [Translation("de", "Knotenpfade, die aus den angehefteten Elementen des Benutzers entfernt werden.")]
    [Browsable(false)] // list-typed: edited on the node content directly until a list field kind ships (see HomeConfig.DefaultApps)
    public IReadOnlyList<string> UnpinPaths { get; init; } = [];

    /// <summary>
    /// Node paths to ADD to the user's pinned items, in order, after the unpins.
    ///
    /// <para>🚨 Existence-checked before it is written unless <see cref="RequireTargetsExist"/> is
    /// false. A deployment that does not carry these nodes pins NOTHING rather than a dangling
    /// path — the failure mode this check exists for, since the whole point of a data-declared
    /// action is that the same declaration may reach a portal without the targets.</para>
    /// </summary>
    [Description("Node paths to pin, in order, after the unpins. Paths this deployment does not have are skipped.")]
    [Translation("de", "Knotenpfade, die nach dem Lösen in dieser Reihenfolge angeheftet werden. Auf dieser Installation fehlende Pfade werden übersprungen.")]
    [Browsable(false)]
    public IReadOnlyList<string> PinPaths { get; init; } = [];

    /// <summary>
    /// Whether <see cref="PinPaths"/> are checked for existence before being pinned (the default,
    /// and what you want). Set false only for a path that is legitimately resolvable but not
    /// query-visible to the user at logon time.
    /// </summary>
    [Description("Check that each pinned path exists on this deployment before pinning it. Leave on unless you are pinning something the search index cannot see.")]
    [Translation("de", "Vor dem Anheften prüfen, ob der Pfad auf dieser Installation existiert. Eingeschaltet lassen, sofern nicht etwas angeheftet wird, das der Suchindex nicht sieht.")]
    public bool RequireTargetsExist { get; init; } = true;
}

/// <summary>
/// What a running logon action is told about the user it is running for. The identity is the
/// caller's own, resolved at logon — a logon action NEVER runs as <c>system-security</c> or as a
/// hub, because it acts on the user's own nodes. See <c>Doc/Architecture/LogonActions</c>.
/// </summary>
/// <param name="UserPath">The user's partition-root node path, which is also their ObjectId.</param>
/// <param name="Identity">The logging-on user's access context — the identity every write carries.</param>
/// <param name="Hub">The hub to resolve services and compose mesh reads/writes from.</param>
public sealed record LogonActionContext(string UserPath, AccessContext Identity, IMessageHub Hub);

/// <summary>
/// What one logon action's run produced. An action does its own side work reactively inside
/// <see cref="ILogonAction.Run"/>; what it returns here is the part that must land ATOMICALLY with
/// the run-once ledger entry — a pure change to the user's own profile.
///
/// <para>🚨 That atomicity is the whole idempotency guarantee. The profile change and the ledger
/// entry go into ONE <c>stream.Update</c> patch on the user node, so there is no window in which
/// the action has been applied but not recorded (a restart there would re-apply it) or recorded
/// but not applied (the migration would be silently skipped forever).</para>
/// </summary>
public sealed record LogonActionOutcome
{
    /// <summary>
    /// A pure transform of the user's profile, applied in the same patch as the ledger entry, or
    /// null when the action changed nothing about the profile. Must be a pure function of its
    /// argument — the runner may re-run it against fresher state when the owning hub refuses a
    /// stale patch and the write rebases.
    /// </summary>
    public Func<User, User>? ProfileChange { get; init; }

    /// <summary>The action ran and had nothing to change on the profile.</summary>
    public static LogonActionOutcome Nothing { get; } = new();

    /// <summary>The action ran and wants <paramref name="change"/> applied to the user's profile.</summary>
    public static LogonActionOutcome Profile(Func<User, User> change) => new() { ProfileChange = change };
}

/// <summary>
/// A logon action DECLARED IN CODE — the shape for platform behaviour that should run everywhere
/// and needs no per-deployment configuration (the app-icon adoption action is the shipped
/// example). Register with
/// <c>builder.ConfigureServices(s =&gt; s.AddSingleton&lt;ILogonAction, MyAction&gt;())</c>.
///
/// <para>For anything deployment-specific, declare a <see cref="LogonAction"/> NODE instead — a
/// code action ships to every portal by construction, which is precisely wrong for a migration
/// that names content only one portal has.</para>
/// </summary>
public interface ILogonAction
{
    /// <summary>
    /// Stable id — the ledger key for a <see cref="LogonActionMode.RunOnce"/> action. 🚨 Changing
    /// it re-runs the action for every user who already had it; that is the only way to
    /// deliberately re-run one, and the only way to accidentally do so.
    /// </summary>
    string Id { get; }

    /// <summary>Whether this runs once per user or on every logon.</summary>
    LogonActionMode Mode { get; }

    /// <summary>Ascending run order; ties break on <see cref="Id"/> so the sequence is stable.</summary>
    int Order => 0;

    /// <summary>
    /// Do the work, reactively. Runs under the logging-on user's identity — compose
    /// <c>GetMeshNodeStream(...).Update(...)</c> and mesh queries directly; do NOT reach for
    /// impersonation, and never <c>Observable.Using(access.ImpersonateAsSystem, …)</c> (see
    /// <c>ImpersonationScopeExtensions</c>).
    ///
    /// <para>Emit exactly one <see cref="LogonActionOutcome"/>. A cold observable that emits
    /// nothing is treated as "did nothing" and, for a run-once action, is still recorded — a
    /// migration that legitimately found nothing to do has run.</para>
    /// </summary>
    IObservable<LogonActionOutcome> Run(LogonActionContext context);
}
