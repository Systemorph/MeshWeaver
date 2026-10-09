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
/// <para><b>EveryLogon</b>, with a cheap "nothing to do": one anchored query of the user's own
/// <c>_Access</c> namespace, which holds the self-grant on every healthy account.</para>
/// </summary>
public sealed class RestoreSelfGrantLogonAction : ILogonAction
{
    /// <inheritdoc />
    public string Id => "platform.restore-self-grant";

    /// <inheritdoc />
    public LogonActionMode Mode => LogonActionMode.EveryLogon;

    /// <summary>First of all: every action that writes into the user's home as the user (default
    /// apps, the Inbox tile) needs the grant this restores.</summary>
    public int Order => -1000;

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
    /// signed-in person, never System, Anonymous/Public or a service principal. Pure.
    /// </summary>
    internal static bool IsEligiblePerson(AccessContext identity) =>
        WellKnownUsers.IsAuthenticated(identity.ObjectId)
        && !string.Equals(identity.ObjectId, WellKnownUsers.System, StringComparison.OrdinalIgnoreCase)
        && !ServiceIdentity.IsServiceObjectId(identity.ObjectId)
        && !identity.ObjectId.Contains('/');

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

        // The reads run as System: a user without the grant cannot read their own _Access, and an
        // RLS-filtered read answers EMPTY, not denied — read as the user, every healthy account
        // would look broken and every broken one would look the same as an absent one.
        IObservable<IReadOnlyList<MeshNode>> Read(string query) =>
            access.RunAsSystem(() => mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(query))
                .Where(change => change.ChangeType == QueryChangeType.Initial)
                .Select(change => (IReadOnlyList<MeshNode>)change.Items.ToArray())
                .Take(1))
                .Timeout(ReadBound);

        return Read($"path:{userId}")
            .Zip(Read($"namespace:{userId}/_Access nodeType:{AssignmentNodeType}"), (home, grants) => (home, grants))
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
                        if (exception.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
                            return Observable.Return(LogonActionOutcome.Nothing);
                        logger?.LogWarning(exception,
                            "[RestoreSelfGrant] Could not restore the self-grant of '{User}' at {Path}",
                            userId, grant.Path);
                        return Observable.Throw<LogonActionOutcome>(exception);
                    });
            });
    }
}
