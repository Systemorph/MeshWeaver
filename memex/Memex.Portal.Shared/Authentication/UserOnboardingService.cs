using System;
using System.Reactive.Disposables;
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
/// <para><b>Two rows, one onboarding write:</b>
/// <list type="number">
///   <item>Per-user partition root — <c>{username}.mesh_nodes</c> at
///         <c>(namespace='', id={username})</c>. This is what <c>/{username}</c>
///         resolves to via the standard partition router; renders the User layout
///         (Activity area) from <see cref="MeshWeaver.Graph.Configuration.UserNodeType"/>'s
///         HubConfiguration. The per-user Postgres schema is created lazily on
///         this first write by the path-routing adapter (calls
///         <c>public.ensure_partition_schema</c>); no explicit
///         <c>Admin/Partition</c> catalog entry needed.</item>
///   <item>User-catalog mirror — <c>user.mesh_nodes</c> at
///         <c>(namespace='User', id={username})</c>. The login flow runs
///         <c>nodeType:User content.email:X</c> and scans the <c>user</c> schema.
///         Without this mirror, the catalog query finds nothing and every signed-in
///         user bounces back to <c>/onboarding</c>.</item>
/// </list>
/// </para>
///
/// <para>Sequencing is expressed reactively via <c>SelectMany</c>: the
/// catalog-mirror subscribe is triggered by the partition-root emission. The
/// partition-root is the canonical row (V27 mirror trigger copies User rows from
/// the per-user partition into <c>auth.mesh_nodes</c> automatically).</para>
/// </summary>
public sealed class UserOnboardingService(
    IMeshService meshService,
    AccessService accessService,
    ILogger<UserOnboardingService>? logger = null,
    IIconGenerator? iconGenerator = null)
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
            // Pin the four documentation sections so a new user's Pinned tab opens
            // onto a clean grid of doc landing pages (each with its own TOC).
            PinnedPaths = ["Doc/Architecture", "Doc/DataMesh", "Doc/GUI", "Doc/AI"],
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

        // SelectMany sequences the two writes — partition-root first (auto-creates
        // the per-user schema via ensure_partition_schema), then the catalog mirror.
        // The outer observable emits ONLY the partition-root node (the canonical
        // identity) so callers can treat the return value as `the User node`.
        //
        // Wrap in Observable.Using + ImpersonateAsSystem so the whole onboarding
        // chain runs as the System identity (Permission.All unconditionally).
        // Reason: the new user does not yet exist, the partition root they
        // would own doesn't yet exist either, and the caller (signed-in admin
        // OR the user-being-onboarded themselves during first-login) can't
        // have Create permission on a brand-new top-level partition. This is
        // the canonical "infrastructure operation" use case ImpersonateAsSystem
        // was built for — explicitly documented in AccessService.cs.
        return Observable.Using(
            () => accessService.ImpersonateAsSystem(),
            _ => meshService.CreateNode(partitionRootNode)
                .Do(__ => logger?.LogInformation(
                    "Onboarding: wrote partition-root User '{Username}' to {Schema}.mesh_nodes",
                    username, username.ToLowerInvariant()))
                .SelectMany(rootNode => meshService.CreateNode(userCatalogMirror)
                    .Do(__ => logger?.LogInformation(
                        "Onboarding: wrote login-catalog mirror at user.mesh_nodes (namespace=User, id={Username})",
                        username))
                    .Select(__ => rootNode)))
            // Best-effort: once the user node exists, generate an inline-SVG avatar in the
            // background (the configurable utility model, via IIconGenerator → NodeInitializer)
            // and stamp it onto the node's Icon — exactly like thread auto-naming runs AFTER a
            // thread is created. Skipped when the user supplied an avatar or no generator is wired.
            .Do(rootNode => MaybeGenerateAvatar(rootNode, fullDisplayName, avatarIcon));
    }

    /// <summary>
    /// Fire-and-forget avatar generation for a freshly-created User node. Reuses the existing
    /// <see cref="IIconGenerator"/> (the <c>NodeInitializer</c> agent on the configurable utility
    /// model) to produce an inline SVG, then writes it to the node's <see cref="MeshNode.Icon"/>
    /// as System (the brand-new partition root has no usable caller identity on this background
    /// callback). Best-effort: bounded by a timeout and swallows failures so a missing/un-configured
    /// utility model never blocks onboarding — the user simply keeps the initials fallback avatar.
    /// </summary>
    private void MaybeGenerateAvatar(MeshNode userNode, string displayName, string? providedIcon)
    {
        if (iconGenerator is null || !string.IsNullOrWhiteSpace(providedIcon))
            return;

        iconGenerator
            .GenerateSvgAsync(displayName, $"A friendly, minimal circular profile avatar for {displayName}")
            .Timeout(TimeSpan.FromSeconds(45))
            .Subscribe(
                svg =>
                {
                    using (accessService.ImpersonateAsSystem())
                        meshService.UpdateNode(userNode with { Icon = svg })
                            .Subscribe(
                                _ => logger?.LogInformation("Onboarding: generated avatar for '{User}'", userNode.Id),
                                ex => logger?.LogWarning(ex, "Onboarding: avatar Icon write failed for '{User}'", userNode.Id));
                },
                ex => logger?.LogInformation(
                    ex, "Onboarding: avatar generation skipped for '{User}' (no utility model or error)", userNode.Id));
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
        // Self-impersonate as System: granting a brand-new user access to their own (possibly
        // just-created) partition is an infrastructure write — the caller may have no usable
        // identity (server-side bootstrap) or only hub identity (interactive onboarding). Same
        // justification + pattern as CreateUser above. PostPipeline fails closed without a
        // context, so this MUST set one explicitly rather than rely on the caller.
        return Observable.Using(
            () => accessService.ImpersonateAsSystem(),
            _ => meshService.CreateNode(assignment)
                .Do(__ => logger?.LogInformation(
                    "Onboarding: granted self-Admin to '{Username}' at {Path}", username, assignment.Path)));
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
        // Self-impersonate as System (see GrantSelfAdmin) — the first-user platform-Admin grant
        // is an infrastructure write that must succeed even when no user identity is on the
        // caller (server-side bootstrap) or only a hub identity (interactive onboarding).
        return Observable.Using(
            () => accessService.ImpersonateAsSystem(),
            _ => meshService.CreateNode(assignment)
                .Do(__ => logger?.LogInformation(
                    "Onboarding: granted platform Admin (first user) to '{Username}' at Admin/_Access", username)));
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
