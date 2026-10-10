using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Logon;

/// <summary>
/// Re-establishes a user's own <c>Admin</c> grant on their home partition —
/// <c>{user}/_Access/{user}_Access</c>, the assignment account creation writes — when it is
/// MISSING, at the user's logon.
///
/// <para><b>Why it is needed (MeshWeaver#5225).</b> The self-grant is always a SEPARATE write after
/// the user root (the <c>User</c> post-creation handler, onboarding's <c>GrantSelfAdmin</c>, the
/// legacy-partition repair), and nothing ever checked it again. A user whose grant write failed —
/// or was lost — existed with a home they could not read: every install into it failed its viewer
/// verify, and no retry could help, because a logon action runs as the user and the user had no
/// right to write the grant back. Policy <c>self-grant-restored-at-logon</c>.</para>
///
/// <para><b>Authorize as caller, execute as System</b> (policy
/// <c>authorize-as-caller-execute-as-system</c>). The authorization is explicit and fails closed:
/// the logging-on identity must be a real, signed-in person (never Anonymous/Public, System or a
/// service principal), and the home must be a <c>User</c> node whose path IS that identity's id —
/// the only partition the rule ever touches. Only then is the grant written, as System on behalf of
/// that one user (<c>RunAsSystemFor(onBehalfOf: user)</c>), so the broad-grant guard sees a write
/// for its own subject.</para>
///
/// <para>🚨 <b>It never widens anything and never overrides a decision.</b> It writes exactly the
/// shape account creation writes, and only when the user's home holds NO assignment naming them at
/// all: an existing assignment — including one whose roles are DENIED, which is how an admin
/// removes a user's own rights — means the state is somebody's decision, and the action leaves it
/// alone. The write is a create, never a create-or-update: an assignment that appeared in between
/// (a concurrent logon, a lagging index) makes it refuse with "already exists", which is success.
/// Every restore is logged at Warning, naming the user and the path — access appearing silently is
/// its own bug class.</para>
///
/// <para><b>The "nothing names them" check reads the AUTHORITATIVE store</b> (<see cref="ReadAssignments"/>),
/// never the query index, whose negative can lag a write: a deny written seconds ago must still stop
/// the restore. A deny that lands in the instant between that read and the create cannot widen access
/// either — the permission fold subtracts a scope's denied roles from its granted ones, so an Admin
/// deny beside the restored Admin grant still wins.</para>
///
/// <para><b>EveryLogon</b>, with a cheap "nothing to do": one listing of the user's own
/// <c>_Access</c> namespace, which holds the self-grant on every healthy account.</para>
/// </summary>
public sealed class RestoreSelfGrantLogonAction : ILogonAction
{
    /// <inheritdoc />
    public string Id => "platform.restore-self-grant";

    /// <inheritdoc />
    public LogonActionMode Mode => LogonActionMode.EveryLogon;

    /// <summary>The first slot of every logon run, reserved for this repair: every action that writes
    /// into the user's home as the user (default apps, the Inbox tile, a declared pin migration)
    /// needs the grant it restores. Data-declared actions are clamped above it
    /// (<see cref="PinMigrationLogonAction.Order"/>), so no declaration can sort ahead.</summary>
    internal const int ReservedOrder = int.MinValue;

    /// <inheritdoc />
    public int Order => ReservedOrder;

    /// <summary>Bound on each read. A slow store costs this logon's check — it runs again on the
    /// next one — never the logon.</summary>
    private static readonly TimeSpan ReadBound = TimeSpan.FromSeconds(20);

    /// <summary>The node type of an access assignment.</summary>
    internal const string AssignmentNodeType = "AccessAssignment";

    /// <summary>The self-grant node, exactly as account creation writes it.</summary>
    internal static MeshNode SelfGrant(string userId) =>
        new($"{userId}_Access", $"{userId}/_Access")
        {
            NodeType = AssignmentNodeType,
            Name = $"{userId} Access",
            MainNode = userId,
            Content = new AccessAssignment
            {
                AccessObject = userId,
                DisplayName = userId,
                Roles = ImmutableList<RoleAssignment>.Empty.Add(new RoleAssignment { Role = Role.Admin.Id }),
            },
        };

    /// <summary>
    /// Whether <paramref name="identity"/> may have its home's self-grant restored at all — a real,
    /// signed-in person: never a virtual, hub or service identity (by the context's own kind flags),
    /// and never System, Anonymous/Public or a service principal by id. Pure.
    /// </summary>
    internal static bool IsEligiblePerson(AccessContext identity) =>
        // The identity-KIND flags first: a virtual, hub or service identity is not a person, whatever
        // its object id happens to look like.
        !identity.IsVirtual
        && !identity.IsHub
        && !identity.IsService
        && WellKnownUsers.IsAuthenticated(identity.ObjectId)
        && !string.Equals(identity.ObjectId, WellKnownUsers.System, StringComparison.OrdinalIgnoreCase)
        && !ServiceIdentity.IsServiceObjectId(identity.ObjectId)
        && !identity.ObjectId.Contains('/');

    /// <summary>
    /// The assignments in the user's own <c>_Access</c> namespace, read from the AUTHORITATIVE store
    /// (<see cref="IStorageAdapter"/>) — never the query index, whose negative can lag a write by
    /// minutes. A freshly written deny that the index has not seen yet must still stop the restore.
    /// A store that cannot list or read faults the read, and the action is retried at the next logon.
    /// </summary>
    internal static IObservable<IReadOnlyList<MeshNode>> ReadAssignments(IMessageHub hub, string userId)
    {
        var storage = hub.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var options = hub.JsonSerializerOptions;
        return storage.ListChildPaths($"{userId}/_Access")
            .Take(1)
            .SelectMany(listing => listing.NodePaths
                .Select(path => storage.Read(path, options).Take(1).DefaultIfEmpty())
                .Concat()
                .Where(node => node is not null
                               && string.Equals(node.NodeType, AssignmentNodeType, StringComparison.OrdinalIgnoreCase))
                .Select(node => node!)
                .ToList())
            .Select(nodes => (IReadOnlyList<MeshNode>)nodes.ToArray())
            .Timeout(ReadBound);
    }

    /// <inheritdoc />
    public IObservable<LogonActionOutcome> Run(LogonActionContext context)
    {
        var hub = context.Hub;
        var mesh = hub.ServiceProvider.GetService<IMeshService>();
        var access = hub.ServiceProvider.GetService<AccessService>();
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.Logon.RestoreSelfGrant");
        var userId = context.Identity.ObjectId;

        // AUTHORIZE (1/2): the caller is a person, and the home is theirs by id — nothing else.
        if (mesh is null
            || !IsEligiblePerson(context.Identity)
            || !string.Equals(context.UserPath, userId, StringComparison.Ordinal))
            return Observable.Return(LogonActionOutcome.Nothing);

        var options = hub.JsonSerializerOptions;

        // The home is read as System: a user without the grant cannot rely on reading even their own
        // partition root through RLS.
        IObservable<IReadOnlyList<MeshNode>> Query(string query) =>
            access.RunAsSystem(() => mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(query))
                .Where(change => change.ChangeType == QueryChangeType.Initial)
                .Select(change => (IReadOnlyList<MeshNode>)change.Items.ToArray())
                .Take(1))
                .Timeout(ReadBound);

        return Query($"path:{userId}")
            .Zip(ReadAssignments(hub, userId), (home, grants) => (home, grants))
            .SelectMany(read =>
            {
                // AUTHORIZE (2/2): the home is a User node at exactly the identity's path.
                var home = read.home.FirstOrDefault(n => string.Equals(n.Path, userId, StringComparison.Ordinal));
                if (home is null || !string.Equals(home.NodeType, UserNodeType.NodeType, StringComparison.Ordinal))
                    return Observable.Return(LogonActionOutcome.Nothing);

                // Any assignment naming the user — allowing OR denied — is a decision; leave it.
                var named = read.grants.Any(n =>
                    string.Equals(n.ContentAs<AccessAssignment>(options, logger)?.AccessObject?.Trim(),
                        userId, StringComparison.OrdinalIgnoreCase));
                if (named)
                    return Observable.Return(LogonActionOutcome.Nothing);

                var grant = SelfGrant(userId);
                // EXECUTE as System, on behalf of this one user. A create, never an upsert.
                return access.RunAsSystemFor(governedBy: null, onBehalfOf: userId, () => mesh.CreateNode(grant))
                    .Do(_ => logger?.LogWarning(
                        "[RestoreSelfGrant] Restored the missing self-grant of '{User}' at {Path} (role Admin on "
                        + "their own home, written as System on their behalf at logon) — their home held no "
                        + "assignment naming them", userId, grant.Path))
                    .Select(_ => LogonActionOutcome.Nothing)
                    .Catch((Exception exception) =>
                    {
                        if (exception.IsNodeAlreadyExists())
                            return Observable.Return(LogonActionOutcome.Nothing);
                        logger?.LogWarning(exception,
                            "[RestoreSelfGrant] Could not restore the self-grant of '{User}' at {Path}",
                            userId, grant.Path);
                        return Observable.Throw<LogonActionOutcome>(exception);
                    });
            });
    }
}
