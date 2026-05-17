using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// Materialises a new user's identity in all the places login + routing + partition
/// activation need to find them. Extracted from <c>Onboarding.razor</c> so the dual-write
/// shape is unit-testable end-to-end (see <c>UserOnboardingServiceTests</c>).
///
/// <para><b>100% reactive — IObservable&lt;T&gt; end-to-end.</b> Every method returns a
/// cold observable. Callers Subscribe (and may chain). No <c>await</c>, no
/// <c>FirstAsync().ToTask()</c> — those would block hub message processing on the
/// publishing thread and deadlock under load.
/// See <c>Doc/Architecture/AsynchronousCalls.md</c>.</para>
///
/// <para><b>Three rows, one onboarding write:</b>
/// <list type="number">
///   <item>Per-user partition root — <c>{username}.mesh_nodes</c> at
///         <c>(namespace='', id={username})</c>. This is what <c>/{username}</c>
///         resolves to via the standard partition router; renders the User layout
///         (Activity area) from <see cref="MeshWeaver.Graph.Configuration.UserNodeType"/>'s
///         HubConfiguration.</item>
///   <item>User-catalog mirror — <c>user.mesh_nodes</c> at
///         <c>(namespace='User', id={username})</c>. The login flow runs
///         <c>nodeType:User content.email:X</c> and scans the <c>user</c> schema.
///         Without this mirror, the catalog query finds nothing and every signed-in
///         user bounces back to <c>/onboarding</c>.</item>
///   <item>Admin/Partition catalog entry — <c>admin.mesh_nodes</c> at
///         <c>(namespace='Admin/Partition', id={username})</c>. Registers the
///         per-user partition with the storage provider so the routing layer's
///         first-segment lookup matches <c>{username}</c>.</item>
/// </list>
/// </para>
///
/// <para>Ordering matters — <c>Admin/Partition</c> is created FIRST so the per-user
/// partition root's first-segment lookup matches when the partition-root write
/// arrives. The user-catalog mirror is independent (lives in the pre-existing
/// <c>user</c> schema). Sequencing is expressed reactively via
/// <c>SelectMany</c>: the partition-root subscribe is triggered by the
/// Admin/Partition emission; the catalog-mirror subscribe by the partition-root
/// emission.</para>
/// </summary>
public sealed class UserOnboardingService(
    IMeshService meshService,
    ILogger<UserOnboardingService>? logger = null)
{
    /// <summary>
    /// Drives the full dual-write. Returns a cold observable that, on Subscribe,
    /// creates the three rows in order and emits the per-user-partition-root
    /// <see cref="MeshNode"/> (path = <c>{username}</c>) as its single value
    /// before completing. Errors surface via OnError — callers wrap in a UI
    /// Catch block. <b>Subscribe to drive.</b>
    /// </summary>
    public IObservable<MeshNode> CreateUser(UserOnboardingRequest request)
    {
        var username = request.Username;
        var fullDisplayName = string.IsNullOrWhiteSpace(request.FullName) ? username : request.FullName!;
        var avatarIcon = string.IsNullOrWhiteSpace(request.AvatarUrl) ? null : request.AvatarUrl!.Trim();

        var userContent = new User
        {
            FullName = string.IsNullOrWhiteSpace(request.FullName) ? null : request.FullName!.Trim(),
            Email = request.Email.Trim(),
            Bio = string.IsNullOrWhiteSpace(request.Bio) ? null : request.Bio!.Trim(),
            Role = string.IsNullOrWhiteSpace(request.Role) ? null : request.Role!.Trim(),
            PinnedPaths = ["Doc"],
        };

        var partitionCatalogEntry = new MeshNode(username, "Admin/Partition")
        {
            Name = username,
            NodeType = "Partition",
            State = MeshNodeState.Active,
            Content = new PartitionDefinition
            {
                Namespace = username,
                DataSource = "default",
                Schema = username.ToLowerInvariant(),
                Table = "mesh_nodes",
                TableMappings = PartitionDefinition.StandardTableMappings,
                Versioned = true,
                Description = $"User partition for {fullDisplayName}",
            }
        };

        var partitionRootNode = new MeshNode(username)
        {
            Name = fullDisplayName,
            NodeType = "User",
            State = MeshNodeState.Active,
            Icon = avatarIcon,
            Content = userContent,
        };

        var userCatalogMirror = new MeshNode(username, "User")
        {
            Name = fullDisplayName,
            NodeType = "User",
            State = MeshNodeState.Active,
            Icon = avatarIcon,
            Content = userContent,
        };

        // SelectMany sequences the three writes — Admin/Partition first so the
        // partition-root write can route, then the user-catalog mirror. The
        // outer observable emits ONLY the partition-root node (the canonical
        // identity) so callers can treat the return value as `the User node`.
        return meshService.CreateNode(partitionCatalogEntry)
            .Do(_ => logger?.LogInformation(
                "Onboarding: registered partition '{Username}' via Admin/Partition catalog", username))
            .SelectMany(_ => meshService.CreateNode(partitionRootNode))
            .Do(_ => logger?.LogInformation(
                "Onboarding: wrote partition-root User '{Username}' to {Schema}.mesh_nodes",
                username, username.ToLowerInvariant()))
            .SelectMany(rootNode => meshService.CreateNode(userCatalogMirror)
                .Do(_ => logger?.LogInformation(
                    "Onboarding: wrote login-catalog mirror at user.mesh_nodes (namespace=User, id={Username})",
                    username))
                .Select(_ => rootNode));
    }

    /// <summary>
    /// Self-AccessAssignment write — the new user gets Admin on their own scope.
    /// Lives in the per-user partition's <c>access</c> satellite. Without this,
    /// the user can read their own partition root (public read on User nodes)
    /// but every subsequent write ("Create permission required") fails.
    /// Returns a cold observable that emits the created AccessAssignment node;
    /// subscribe to drive.
    /// </summary>
    public IObservable<MeshNode> GrantSelfAdmin(string username)
    {
        var assignment = new MeshNode($"{username}_Access", $"{username}/_Access")
        {
            NodeType = "AccessAssignment",
            Name = $"{username} Access",
            MainNode = username,
            Content = new AccessAssignment
            {
                AccessObject = username,
                DisplayName = username,
                Roles = [new RoleAssignment { Role = Role.Admin.Id, Denied = false }]
            }
        };
        return meshService.CreateNode(assignment)
            .Do(_ => logger?.LogInformation(
                "Onboarding: granted self-Admin to '{Username}' at {Path}", username, assignment.Path));
    }

    /// <summary>
    /// First-user-only: grants the user global Admin at <c>Admin/_Access</c>. Caller
    /// gates this on the "no existing User nodes" check. Subscribe to drive — a
    /// silent failure would leave the platform with no admins, so callers must
    /// surface OnError.
    /// </summary>
    public IObservable<MeshNode> GrantPlatformAdmin(string username)
    {
        var assignment = new MeshNode($"{username}_Access", "Admin/_Access")
        {
            NodeType = "AccessAssignment",
            Name = $"{username} Access",
            MainNode = "Admin",
            Content = new AccessAssignment
            {
                AccessObject = username,
                DisplayName = username,
                Roles = [new RoleAssignment { Role = Role.Admin.Id, Denied = false }]
            }
        };
        return meshService.CreateNode(assignment)
            .Do(_ => logger?.LogInformation(
                "Onboarding: granted platform Admin (first user) to '{Username}' at Admin/_Access", username));
    }
}

/// <summary>
/// Input shape for <see cref="UserOnboardingService.CreateUser"/>. Mirrors the form
/// model in <c>Onboarding.razor</c>; kept in this assembly so unit tests can
/// construct it without taking a dependency on the Blazor page.
/// </summary>
public sealed record UserOnboardingRequest(
    string Username,
    string Email,
    string? FullName = null,
    string? Bio = null,
    string? Role = null,
    string? AvatarUrl = null);
