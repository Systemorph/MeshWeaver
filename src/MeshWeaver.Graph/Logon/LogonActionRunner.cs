using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Logon;

/// <summary>
/// Runs the platform's logon actions for one user, at logon, under THAT USER's identity.
///
/// <para>The framework this type is the engine of exists because the platform had no way to give an
/// EXISTING user something new. <c>INodePostCreationHandler</c> seeds a user at account creation and
/// can never fire again, so every "existing users need this too" ended up as a hand-written SQL
/// backfill in <c>Memex.Database.Migration</c> (<c>V29_PinDocsForExistingUsers</c>,
/// <c>V33_SeedChatInputForExistingUsers</c>) — raw <c>UPDATE</c>s that bypass the workspace cache,
/// run once per DEPLOYMENT rather than per user, and cannot be expressed by anyone who is not
/// shipping a new database version. A logon action is the per-user sibling of the post-creation
/// handler. See <c>Doc/Architecture/LogonActions</c>.</para>
///
/// <para><b>Mesh-scoped singleton</b> — no static state, so its lifetime is the mesh's and nothing
/// bleeds between tests or between portals in one process (<c>Doc/Architecture/NoStaticState</c>).
/// It holds no per-user memory at all: the run-once ledger is
/// <see cref="User.CompletedLogonActions"/> on the durable profile, which is what makes the guard
/// survive a restart and hold across replicas.</para>
/// </summary>
public sealed class LogonActionRunner(IMessageHub hub, ILogger<LogonActionRunner>? logger = null)
{
    /// <summary>How long the whole per-logon run may take before it is abandoned. A logon action is
    /// background work on the authentication path: it must never hold a login open, and a portal
    /// whose mesh is slow must still log people in.</summary>
    private static readonly TimeSpan RunBudget = TimeSpan.FromSeconds(30);

    /// <summary>Bound on reading the user's own profile node — the one read the run cannot proceed without.</summary>
    private static readonly TimeSpan ProfileReadBound = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Runs every pending logon action for <paramref name="identity"/>, in order, one at a time.
    /// Cold: <b>subscribe to drive</b>.
    ///
    /// <para>Sequential by <c>Concat</c>, never <c>Merge</c>: the actions write to the same profile
    /// node, and two concurrent cross-hub patches against the same base is the shape the owning hub
    /// refuses as stale. One at a time costs nothing here (this runs once per logon session) and
    /// removes the retry entirely.</para>
    ///
    /// <para>One action's failure is logged and the rest still run — a broken migration must not
    /// take the others down with it, and it must not fail the logon at all.</para>
    /// </summary>
    public IObservable<Unit> RunFor(AccessContext identity) => RunFor(identity, actions: null);

    /// <summary>
    /// The same run against an EXPLICIT action set, bypassing discovery.
    ///
    /// <para>Use it to apply a known action on demand — an admin's "run this for me now", a
    /// deployment script, and the tests, which need each case to see exactly its own action rather
    /// than every action the mesh happens to have registered. Discovery (<see cref="Resolve"/>) is
    /// the only thing skipped: ordering, the ledger, the identity scope and the atomic commit are
    /// identical, so this exercises the real path.</para>
    /// </summary>
    /// <param name="identity">The user to run for.</param>
    /// <param name="actions">The actions to run, or null to discover them as usual.</param>
    public IObservable<Unit> RunFor(AccessContext identity, IReadOnlyCollection<ILogonAction>? actions)
    {
        // 🚨 IsAuthenticated, never !IsNullOrEmpty: an unauthenticated caller is not nameless, it is
        // named "Anonymous" — a perfectly non-empty string, and a partition nobody's profile lives
        // in. Running migrations for it would write a User node for a visitor. System is excluded
        // for the same reason, one level up: it is infrastructure, not a person logging on.
        var userPath = identity.ObjectId;
        if (!WellKnownUsers.IsAuthenticated(userPath)
            || string.Equals(userPath, WellKnownUsers.System, StringComparison.OrdinalIgnoreCase))
            return Observable.Return(Unit.Default);

        var access = hub.ServiceProvider.GetService<AccessService>();
        var context = new LogonActionContext(userPath, identity, hub);

        // 🚨 The identity is scoped with RunAs, NEVER Observable.Using(access.ImpersonateAsSystem, …).
        // Impersonation is an AsyncLocal store/restore pair; Observable.Using opens it on the
        // SUBSCRIBING thread and disposes it when the inner observable TERMINATES — for a cross-hub
        // write, the owning hub's response thread — so the two halves land on different threads and
        // the subscriber is left latched as the impersonated identity (issue #1790). RunAs opens and
        // closes inside one synchronous Subscribe. Each individual write re-establishes it too (see
        // Commit), because Concat subscribes a later action on whichever thread the previous one
        // completed on, which is not this one.
        var resolved = actions is null
            ? Resolve(context)
            : Observable.Return(actions.OrderBy(a => a.Order).ThenBy(a => a.Id, StringComparer.Ordinal).ToArray());

        return access.RunAs(identity, () => resolved
                .SelectMany(ordered => ordered.Length == 0
                    ? Observable.Return(Unit.Default)
                    : ReadProfile(userPath)
                        .SelectMany(user => ordered
                            .Where(a => IsPending(a, user))
                            .Select(a => RunOne(a, context, access))
                            .Concat()
                            .DefaultIfEmpty(Unit.Default))))
            .TakeLast(1)
            .Timeout(RunBudget)
            .Catch<Unit, Exception>(ex =>
            {
                // Never faults the caller: this is invoked from the authentication path, where a
                // slow or unreachable mesh must cost a missed migration, never a failed login.
                logger?.LogWarning(ex, "Logon actions did not complete for {User}", userPath);
                return Observable.Return(Unit.Default);
            });
    }

    /// <summary>Whether an action still has to run for this user: every-logon always, run-once only
    /// while its id is absent from the profile's ledger.</summary>
    private static bool IsPending(ILogonAction action, User? user) =>
        action.Mode != LogonActionMode.RunOnce
        || user is null
        || !user.CompletedLogonActions.ContainsKey(action.Id);

    /// <summary>
    /// The user's own profile node. Read through the shared per-node handle — never
    /// <c>QueryAsync</c>, which is the lagged index and would answer with pre-migration state
    /// (<c>Doc/Architecture/CqrsAndContentAccess</c>).
    /// </summary>
    private IObservable<User?> ReadProfile(string userPath) =>
        hub.GetWorkspace().GetMeshNodeStream(userPath)
            .Where(node => node is not null)
            .Take(1)
            .Timeout(ProfileReadBound)
            .Select(node => node.ContentAs<User>(hub.JsonSerializerOptions, logger))
            .Catch<User?, Exception>(ex =>
            {
                logger?.LogWarning(ex, "Logon actions: could not read profile {User}", userPath);
                return Observable.Return<User?>(null);
            });

    /// <summary>
    /// Every action that applies to this deployment: the ones registered in code, plus the ones
    /// DECLARED AS DATA in the Admin partition. Ordered by <c>Order</c> then <c>Id</c> so the
    /// sequence is identical on every replica and every run.
    /// </summary>
    private IObservable<ILogonAction[]> Resolve(LogonActionContext context) =>
        ReadDeclaredActions()
            .Select(declared => hub.ServiceProvider.GetServices<ILogonAction>()
                .Concat(declared)
                .GroupBy(a => a.Id)
                // A code action and a declared node sharing an id: the code one wins, because it is
                // the one a reviewer can see. Silently running both would double-apply.
                .Select(g => g.First())
                .OrderBy(a => a.Order)
                .ThenBy(a => a.Id, StringComparer.Ordinal)
                .ToArray())
            .Do(actions => logger?.LogDebug(
                "Logon actions resolved for {User}: {Ids}",
                context.UserPath, string.Join(", ", actions.Select(a => a.Id))));

    /// <summary>
    /// The data-declared actions under <c>Admin/_LogonAction</c>.
    ///
    /// <para>🚨 Read as SYSTEM, and this is the one place the runner does not use the user's
    /// identity — deliberately, and it is a READ of platform configuration, never a write and never
    /// a touch of user data. The declarations live in the Admin partition; an ordinary user has no
    /// standing grant there, and an RLS-filtered read would come back EMPTY rather than denied — so
    /// running this as the user would silently disable the whole framework for exactly the users it
    /// exists to serve. Everything the actions then DO runs as the user (see
    /// <see cref="RunFor(AccessContext)"/>).</para>
    ///
    /// <para>An absent namespace, an unreadable partition or a malformed node yields an empty set,
    /// never an exception: a deployment that declares no logon actions is the normal case.</para>
    /// </summary>
    private IObservable<IEnumerable<ILogonAction>> ReadDeclaredActions()
    {
        var mesh = hub.ServiceProvider.GetService<IMeshService>();
        if (mesh is null)
            return Observable.Return(Enumerable.Empty<ILogonAction>());
        var access = hub.ServiceProvider.GetService<AccessService>();

        return access.RunAsSystem(() => mesh
                .Query<MeshNode>(MeshQueryRequest.FromQuery(
                    $"path:{LogonActionNodeType.ActionNamespace} scope:children "
                    + $"nodeType:{LogonActionNodeType.NodeType}"))
                .Where(change => change.ChangeType == QueryChangeType.Initial)
                .Select(change => change.Items)
                .Take(1))
            .Select(nodes => nodes
                .Select(node => (node, declaration: node.ContentAs<LogonAction>(hub.JsonSerializerOptions, logger)))
                .Where(pair => pair.declaration is { Enabled: true })
                .Select(pair => (ILogonAction)new PinMigrationLogonAction(pair.node.Id, pair.declaration!))
                .ToArray()
                .AsEnumerable())
            .Timeout(ProfileReadBound)
            .Catch<IEnumerable<ILogonAction>, Exception>(ex =>
            {
                logger?.LogDebug(ex, "No declared logon actions could be read");
                return Observable.Return(Enumerable.Empty<ILogonAction>());
            });
    }

    /// <summary>Runs one action and commits its outcome. Never faults: a failing action is logged
    /// and skipped, and — crucially — is NOT recorded, so it is retried on the next logon.</summary>
    private IObservable<Unit> RunOne(ILogonAction action, LogonActionContext context, AccessService? access) =>
        Observable.Defer(() => action.Run(context))
            .Take(1)
            .DefaultIfEmpty(LogonActionOutcome.Nothing)
            .SelectMany(outcome => Commit(action, outcome, context, access))
            .Catch<Unit, Exception>(ex =>
            {
                logger?.LogWarning(ex,
                    "Logon action {Action} failed for {User}; it will be retried on the next logon",
                    action.Id, context.UserPath);
                return Observable.Return(Unit.Default);
            });

    /// <summary>
    /// The single write that makes a run-once action idempotent: the action's profile change and its
    /// ledger entry go into ONE <c>stream.Update</c> patch on the user's own node.
    ///
    /// <para><b>Why one patch, and why that is the guard.</b> Split across two writes there is a
    /// window where the change landed and the record did not (a restart re-applies the migration,
    /// clobbering whatever the user did in between) or the record landed and the change did not (the
    /// migration is skipped forever). One patch has neither.</para>
    ///
    /// <para><b>Under concurrency</b> — two tabs, two replicas — the owning hub serialises the
    /// patches and refuses one whose base has moved, and the losing writer's lambda is re-run
    /// against the fresher state. That re-run sees the ledger key present and returns the node
    /// untouched, so the effect is applied exactly once no matter how many logons race. This is why
    /// the ledger check is INSIDE the lambda and not only in <see cref="IsPending"/>: the outer
    /// check is the cheap fast path, the inner one is the guard.</para>
    /// </summary>
    private IObservable<Unit> Commit(
        ILogonAction action, LogonActionOutcome outcome, LogonActionContext context, AccessService? access)
    {
        var once = action.Mode == LogonActionMode.RunOnce;
        if (!once && outcome.ProfileChange is null)
            return Observable.Return(Unit.Default);

        var ranAt = DateTimeOffset.UtcNow;
        var options = hub.JsonSerializerOptions;

        // Re-establish the identity AT THE WRITE. Concat subscribes this on whichever thread the
        // previous action completed on, so the ambient context from RunFor's Subscribe is not
        // guaranteed to still be here — and a write with no identity fails CLOSED in the post
        // pipeline (Doc/Architecture/AccessContextPropagation).
        return access.RunAs(context.Identity, () => hub.GetWorkspace()
                .GetMeshNodeStream(context.UserPath)
                .Update(node =>
                {
                    // Bad-data tolerance, the ThreadInput rule (Doc/Architecture/RequestViaStreamUpdate):
                    // content that EXISTS but cannot be read as a User is left alone — replacing it
                    // with a fresh User would erase a real profile the moment a $type failed to
                    // resolve. Content that is absent entirely is a bare User node (the shape
                    // TestUsers seeds, and any node created without content), and seeding it is safe
                    // — otherwise a run-once action would never be recordable for that user and
                    // would re-run on every single logon, forever.
                    var user = node.ContentAs<User>(options, logger);
                    if (node.Content is not null && user is null)
                        return node;
                    user ??= new User();
                    if (once && user.CompletedLogonActions.ContainsKey(action.Id))
                        return node;

                    var updated = outcome.ProfileChange?.Invoke(user) ?? user;
                    if (once)
                        updated = updated with
                        {
                            CompletedLogonActions = updated.CompletedLogonActions
                                .ToImmutableDictionary()
                                .SetItem(action.Id, ranAt),
                        };
                    return ReferenceEquals(updated, user) ? node : node with { Content = updated };
                }))
            .Select(_ => Unit.Default)
            .Do(_ => logger?.LogInformation(
                "Logon action {Action} applied for {User} ({Mode})", action.Id, context.UserPath, action.Mode));
    }
}
