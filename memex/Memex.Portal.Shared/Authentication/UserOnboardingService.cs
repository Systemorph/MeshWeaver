using System.Collections.Immutable;
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
/// <para><b>One row, one write:</b> the per-user partition root —
/// <c>{username}.mesh_nodes</c> at <c>(namespace='', id={username})</c>. This is what
/// <c>/{username}</c> resolves to via the standard partition router; it renders the User
/// layout (Activity area) from <see cref="MeshWeaver.Graph.Configuration.UserNodeType"/>'s
/// HubConfiguration. The per-user Postgres schema is created lazily on this first write by
/// the path-routing adapter (<c>public.ensure_partition_schema</c>); no explicit
/// <c>Admin/Partition</c> catalog entry is needed.</para>
///
/// <para><b>Login finds the user via the Auth mirror, not a second write.</b> The login flow
/// runs <c>nodeType:User</c> (routed to the <c>Auth</c> partition by
/// <c>UserNodeType.AddQueryRoutingRule</c>); the V27 trigger
/// <c>mirror_access_object_to_auth_schema</c> copies this partition-root User row into
/// <c>auth.mesh_nodes</c> automatically. There is therefore NO separate
/// <c>(namespace='User', id={username})</c> catalog-mirror write — that legacy write routed
/// to an unregistered <c>User</c> first-segment and lazily provisioned a stray <c>user</c>
/// schema distinct from <c>auth</c> (cleaned up + dropped by migration V31). Non-system writes
/// into the <c>User</c>/<c>Auth</c> mirror are blocked by <c>PartitionWriteGuardValidator</c>.</para>
/// </summary>
public sealed class UserOnboardingService(
    IMeshService meshService,
    AccessService accessService,
    IMessageHub hub,
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
            // The UI language, chosen on the onboarding form and defaulted there from the visitor's
            // own computer language (Accept-Language → AccessContext.Locale). Set HERE, at user
            // creation, rather than left to the browser detector: the detector only runs inside the
            // authenticated portal shell, i.e. AFTER onboarding, so a German-speaking user filled in
            // this form — and saw the first screens — in English. Resolved through Locales.TryMatch
            // so an unshipped tag stores nothing rather than pinning the profile to a language we
            // would render in English anyway.
            Locale = Locales.TryMatch(request.Locale),
            // The SEED — what a profile starts with. It reaches the store on the create leg only:
            // UpsertProfile declares PinnedPaths as create-once, so on a user who already exists
            // this value is discarded and the owner keeps the pins it holds.
            PinnedPaths = UserOnboardingDefaults.PinnedPathsFor(request.IsPlatformBootstrap),
        };

        var partitionRootNode = new MeshNode(username)
        {
            Name = fullDisplayName,
            NodeType = "User",
            State = MeshNodeState.Active,
            Icon = avatarIcon,
            Content = userContent,
        };

        // Single write: the partition-root User node at (namespace='', id={username}).
        // The per-user Postgres schema is auto-created on this first write
        // (ensure_partition_schema), and the V27 auth-mirror trigger copies this User row
        // into auth.mesh_nodes automatically — so login's `nodeType:User` lookup (routed to
        // the Auth partition) finds it WITHOUT a separate catalog-mirror write.
        //
        // The old `new MeshNode(username, "User")` catalog-mirror write is GONE: it routed
        // to the unregistered `User` first-segment and lazily provisioned a stray `user`
        // schema separate from `auth` (migration V31 unifies that back into `auth` and drops
        // it). Writes into the User/Auth mirror by non-system callers are now blocked by
        // PartitionWriteGuardValidator; onboarding stays clean by simply not writing there.
        //
        // Wrap in Observable.Using + ImpersonateAsSystem so onboarding runs as the System
        // identity (Permission.All): the new user / their brand-new partition root don't
        // exist yet, so neither the signed-in admin nor the user-being-onboarded can hold
        // Create on it — the canonical infrastructure-write case (see AccessService.cs).
        // ATOMIC create-or-update — the owning hub decides create-vs-update by existence, so a re-run or
        // a CONCURRENT onboarding (e.g. a lagged user-existence check that re-triggers provisioning) is
        // race-free + idempotent. The old CreateNode().Catch(already-exists → UpdateNode()) split raced:
        // under concurrency the create's exists-check lagged the in-flight create, the catch fell through
        // to UpdateNode, which patched a not-yet-materialized node → "NotFound … for patch apply" →
        // unhandled → error storm → the portal climbed to OutOfMemory and wedged. See /async, CreateOrUpdateNode.
        return Observable.Using(
            () => accessService.ImpersonateAsSystem(),
            _ => UpsertProfile(partitionRootNode)
                .Do(__ => logger?.LogInformation(
                    "Onboarding: upserted partition-root User '{Username}' to {Schema}.mesh_nodes",
                    username, username.ToLowerInvariant())))
            // Best-effort: once the user node exists, generate an inline-SVG avatar in the
            // background (the configurable utility model, via IIconGenerator → NodeInitializer)
            // and stamp it onto the node's Icon — exactly like thread auto-naming runs AFTER a
            // thread is created. Skipped when the user supplied an avatar or no generator is wired.
            .Do(rootNode => MaybeGenerateAvatar(rootNode, fullDisplayName, avatarIcon));
    }

    /// <summary>
    /// The partition-root upsert, carrying the ONE fold onboarding needs: <see cref="User.PinnedPaths"/>
    /// is <b>seeded on the create leg and kept as the owner holds it on the update leg</b>
    /// (<see cref="FoldRule.KeepExisting"/> — the create-once member, #4928).
    ///
    /// <para>🚨 Why a fold and not the plain <see cref="IMeshService.CreateOrUpdateNode"/>. The
    /// full-instance upsert's update leg takes <c>Content</c> WHOLESALE
    /// (<c>UpdateAccordingToSourceNode</c>: <c>Content = sourceNode.Content ?? state.Content</c>), so a
    /// second <see cref="CreateUser"/> for a user who exists replaced their pins with the seed —
    /// <c>[]</c> for the first administrator — and silently erased everything they had pinned since.
    /// The recovery endpoint (<see cref="BootstrapController.FirstAdmin"/>) is documented as
    /// re-runnable, and the onboarding page re-submits after a lagged existence check, so that leg
    /// is not hypothetical. A client-side "exists → <c>stream.Update</c>, else create" split is not
    /// the answer either: that is the exact shape <see cref="CreateUser"/>'s own comment records as
    /// removed (the create's exists-check lagged a concurrent create → a patch onto a not-yet-
    /// materialised node → error storm → OOM wedge). The fold is the owner-side form of that same
    /// Update: the caller states the RULE, the owner applies it against the node as it holds it,
    /// inside the one serialised merge — no read, no decision, no race.</para>
    ///
    /// <para>Every other member still lands wholesale, deliberately: the interactive form's
    /// re-submission carries values the person just typed, and a re-run of the recovery endpoint
    /// repairing a display name is what the existing-user leg is FOR. Only the pins are the user's
    /// own curation after onboarding.</para>
    ///
    /// <para>Issued off the router (<see cref="MeshExtensions.NodeOperationIssuingHub"/>) exactly as
    /// <c>MeshService.CreateOrUpdateNode</c> does, with the caller's identity stamped on the request
    /// (<c>RequestedBy</c>) and on the delivery, because the response's Subscribe lands on an
    /// emission thread where the impersonation scope's AsyncLocal is gone. Caller-read-free is not
    /// cluster-atomic (see <see cref="ContentFolds"/>) — which is exactly enough here: nothing is
    /// counted, a value is merely preserved.</para>
    /// </summary>
    /// <param name="partitionRootNode">The User node carrying the SEED content.</param>
    /// <returns>The node as the owner stored it — created, or merged onto the existing profile.</returns>
    private IObservable<MeshNode> UpsertProfile(MeshNode partitionRootNode)
    {
        // Captured at the call, which CreateUser makes INSIDE its Using scope — so this is the
        // System identity the write runs under, pinned by value before any scheduler hop.
        var captured = accessService.Context;
        var request = new CreateOrUpdateNodeRequest(partitionRootNode)
            .WithFolds<User>(hub.JsonSerializerOptions, f => f.KeepExisting(u => u.PinnedPaths))
            with { RequestedBy = captured?.ObjectId };
        var target = hub.NodeOperationTarget();
        // Defer keeps the post cold — it fires on Subscribe, never on construction.
        return Observable.Defer(() => hub.NodeOperationIssuingHub()
                .Observe(request, o => captured is null
                    ? o.WithTarget(target)
                    : o.WithTarget(target).WithAccessContext(captured))
                .SelectMany(d =>
                {
                    var r = d.Message;
                    if (r.Success && r.Node is not null)
                        return Observable.Return(r.Node);
                    return Observable.Throw<MeshNode>(r.RejectionReason switch
                    {
                        NodeUpsertRejectionReason.Unauthorized or NodeUpsertRejectionReason.ValidationFailed =>
                            new UnauthorizedAccessException(r.Error ?? "Access denied"),
                        _ => new InvalidOperationException(r.Error ?? "Node upsert failed"),
                    });
                }))
            // The SAME identity around every emission that the post was stamped with, so the
            // subscriber's callbacks do not inherit whatever is ambient on the emission thread.
            .CarryAccessContext(hub.ServiceProvider, captured);
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
        // ATOMIC create-or-update (see CreateUser): race-free + idempotent. A retried or concurrent
        // onboarding must not fall into the CreateNode→already-exists→UpdateNode split that patched a
        // not-yet-materialized _Access node ("NotFound … for patch apply" → storm → OOM wedge).
        return Observable.Using(
            () => accessService.ImpersonateAsSystem(),
            _ => meshService.CreateOrUpdateNode(assignment)
                .Do(__ => logger?.LogInformation(
                    "Onboarding: granted self-Admin to '{Username}' at {Path}", username, assignment.Path)));
    }

    /// <summary>
    /// First-user-only: grants the user global admin by making them an admin on the
    /// <b>Admin partition</b> — an <see cref="AccessAssignment"/> at namespace
    /// <c>Admin/_Access</c> with <c>MainNode="Admin"</c>: the scope the path encodes,
    /// which <c>AccessAssignmentGuard</c> enforces at the write boundary. The same shape
    /// <see cref="GlobalAdminSeed"/> writes for config-driven admins. This confers the
    /// platform gates (<c>hub.IsGlobalAdmin()</c> = <see cref="MeshWeaver.Mesh.Security.Permission.All"/>
    /// at scope <c>Admin</c>) — it is deliberately NOT a data superuser; an empty
    /// <c>MainNode</c> would scope the grant to ROOT (every partition) and is refused.
    /// See Doc/Architecture/AccessControl.md → "The scope invariant".
    ///
    /// <para>Caller gates this on the platform-bootstrap check (no admin grant exists yet).
    /// Subscribe to drive — a silent failure would leave the platform with no admins, so
    /// callers must surface OnError.</para>
    /// </summary>
    public IObservable<MeshNode> GrantPlatformAdmin(string username)
    {
        // Global admin = admin on the ADMIN PARTITION: namespace "Admin/_Access", MainNode
        // "Admin". MainNode MUST equal the scope the path encodes — AccessAssignmentGuard
        // refuses the write otherwise (an empty MainNode is a ROOT grant, the 43-superuser
        // incident shape). hub.IsGlobalAdmin() reads Permission.All at scope "Admin"; the
        // platform gates come from this grant, cross-partition data access deliberately
        // does NOT. Mirrors GlobalAdminSeed's config-admin shape. See AccessControl.md.
        var assignment = new MeshNode($"{username}_Access", "Admin/_Access")
        {
            NodeType = "AccessAssignment",
            Name = $"{username} — Admin",
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
        // ATOMIC create-or-update (see GrantSelfAdmin): race-free + idempotent first-user grant.
        return Observable.Using(
            () => accessService.ImpersonateAsSystem(),
            _ => meshService.CreateOrUpdateNode(assignment)
                .Do(__ => logger?.LogInformation(
                    "Onboarding: granted platform Admin (first user) to '{Username}' on the Admin partition (Admin/_Access, MainNode=\"Admin\")", username)));
    }
}

/// <summary>
/// The learning path a new user is SEEDED with — and who is not. A seed, never a value re-imposed:
/// <see cref="UserOnboardingService.CreateUser"/> applies it on the create leg only.
/// </summary>
public static class UserOnboardingDefaults
{
    /// <summary>
    /// The four documentation sections an ordinary new user's Pinned tab opens onto: a clean grid
    /// of doc landing pages, each with its own TOC.
    /// </summary>
    public static readonly ImmutableList<string> LearningPath =
        ["Doc/Architecture", "Doc/DataMesh", "Doc/GUI", "Doc/AI"];

    /// <summary>
    /// What to seed for this user.
    ///
    /// <para>🚨 <b>The first global administrator gets NO learning path.</b> That person is setting
    /// the instance up, not learning it — a Pinned tab full of doc landing pages in front of the
    /// setup work is noise at the one moment it costs most. Ordinary users keep it. Policy
    /// <c>first-admin-no-learning-path</c> (<c>Doc/Architecture/FirstRunSetupOnAProvisionedInstance</c>,
    /// "The first administrator learns nothing here").</para>
    /// </summary>
    /// <param name="isPlatformBootstrap">Whether this user is the instance's first global administrator.</param>
    public static ImmutableList<string> PinnedPathsFor(bool isPlatformBootstrap) =>
        isPlatformBootstrap ? [] : LearningPath;
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
    string? AvatarUrl = null,
    // The BCP-47 UI language for the new user ("de"), from the onboarding form's language picker —
    // which itself defaults to the visitor's own computer language, negotiated from the request's
    // Accept-Language header onto AccessContext.Locale. Resolved through Locales.TryMatch at the
    // point of use, so a tag this deployment does not ship stores nothing rather than pinning the
    // profile to a language we would only ever render in English anyway.
    string? Locale = null)
{
    /// <summary>
    /// True when this user is the instance's FIRST global administrator — the bootstrap path
    /// (<see cref="BootstrapController.FirstAdmin"/>, and the first-user promotion the onboarding
    /// page decides). Such a user is setting the instance up, not learning it, and is
    /// <b>seeded</b> with no learning path (<see cref="UserOnboardingDefaults.PinnedPathsFor"/>).
    ///
    /// <para>🚨 A CALLER's decision, not a form field — which is why it is an init-only property
    /// and not a primary-constructor parameter. The primary constructor mirrors what the person
    /// typed on the onboarding form; this flag is what the code orchestrating the onboarding
    /// knows about the instance. Keeping it out of the constructor also keeps the record's
    /// constructor signature — the one a module compiled earlier calls — exactly as it was
    /// (<c>check-record-signatures.py</c>: adding a parameter, even with a default, replaces
    /// that signature).</para>
    ///
    /// <para>It decides the SEED only. On a user who already exists the flag changes nothing:
    /// <see cref="UserOnboardingService.CreateUser"/> keeps an existing profile's pins as they are
    /// (the <c>KeepExisting</c> fold), so a re-run of the recovery endpoint — which is documented
    /// as re-runnable — cannot erase what the administrator pinned after the original
    /// onboarding.</para>
    /// </summary>
    public bool IsPlatformBootstrap { get; init; }
}
