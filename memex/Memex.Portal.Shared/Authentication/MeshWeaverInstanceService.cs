using System.Reactive;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// Registers MeshWeaver installations and issues their keys — the instance-facing counterpart of
/// <c>ApiTokenService</c>, built on the same mechanism (mint → hash → write the node → write a
/// routing index under System identity) and deliberately NOT on the same node type.
///
/// <para>Registering grants nothing — unless the registry operator has opted specific sources into
/// every new registration via <c>PluginCatalog:DefaultGrants</c> (a list of <c>Source/Package</c>
/// entries, e.g. <c>Plugins/*</c>). Registration then seeds those entries into the instance's
/// <see cref="PluginGrant"/> node in the Admin partition — the grant node stays the single
/// authority, so a platform admin can still revoke or extend per instance, and anything NOT in the
/// default list (private/paid sources) remains admin-granted as before. The registering user still
/// cannot reach the grant node either way.</para>
/// </summary>
public sealed class MeshWeaverInstanceService(
    IMeshService nodeFactory, IMessageHub hub, ILogger<MeshWeaverInstanceService> logger,
    IConfiguration? configuration = null)
{
    /// <summary>Config key listing the <c>Source/Package</c> entries every new registration is
    /// seeded with (<c>PluginCatalog:DefaultGrants:0</c>, …). Absent/empty → registration grants
    /// nothing, the strict default.</summary>
    public const string DefaultGrantsConfigKey = "PluginCatalog:DefaultGrants";

    /// <summary>Namespace holding a user's registered instances, inside their own partition.</summary>
    public const string InstanceNamespace = "MeshWeaverInstance";

    /// <summary>
    /// A logical App ID: lowercase letters, digits and single hyphens, 3–48 chars, not
    /// hyphen-terminated. Constrained because the id becomes a node id AND the key of the grant node
    /// in the Admin partition — a loose id would let a registration collide with, or visually
    /// impersonate, another instance's grant.
    /// </summary>
    private static readonly Regex InstanceIdPattern =
        new("^[a-z0-9](?:[a-z0-9]|-(?=[a-z0-9])){2,47}$", RegexOptions.Compiled);

    /// <summary>Whether <paramref name="instanceId"/> is a well-formed logical App ID.</summary>
    public static bool IsValidInstanceId(string? instanceId) =>
        !string.IsNullOrWhiteSpace(instanceId) && InstanceIdPattern.IsMatch(instanceId);

    /// <summary>
    /// Registers an installation and mints its key. Cold — the writes happen on Subscribe.
    ///
    /// <para>🚨 The instance id is claimed GLOBALLY, not per user: grants are keyed on it in the
    /// Admin partition, so two users must never hold the same id, and an id must not be silently
    /// re-registered after deletion or the newcomer would inherit the old grant. The index write
    /// is what makes the claim visible; <see cref="IsIdAvailable"/> is checked first so a
    /// collision is a clean error rather than a half-written registration.</para>
    /// </summary>
    public IObservable<InstanceRegistrationResult> Register(
        string userId, string userName, string userEmail,
        string instanceId, string displayName, string description = "", string homeUrl = "")
    {
        if (!IsValidInstanceId(instanceId))
            return Observable.Throw<InstanceRegistrationResult>(new ArgumentException(
                $"'{instanceId}' is not a valid instance id: 3–48 chars, lowercase letters, digits "
                + "and single hyphens, not starting or ending with a hyphen.", nameof(instanceId)));

        var rawKey = InstanceKeys.Generate();
        var hash = InstanceKeys.Hash(rawKey);

        return IsIdAvailable(instanceId).SelectMany(available =>
        {
            if (!available)
                return Observable.Throw<InstanceRegistrationResult>(
                    new InstanceIdTakenException(instanceId));

            var instance = new MeshWeaverInstance
            {
                InstanceId = instanceId,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? instanceId : displayName,
                Description = description,
                HomeUrl = homeUrl,
                OwnerUserId = userId,
                OwnerUserName = userName,
                OwnerUserEmail = userEmail,
                KeyHash = hash,
                KeyIssuedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            var instanceNode = new MeshNode(instanceId, $"{userId}/{InstanceNamespace}")
            {
                Name = instance.DisplayName,
                NodeType = MeshWeaverInstanceNodeType.NodeType,
                State = MeshNodeState.Active,
                MainNode = userId,
                Content = instance,
            };

            // The IsIdAvailable pre-check reads the eventually-consistent query index, so a
            // just-registered id can still look available — the CREATE is the real serialization
            // point. When it fails and an authoritative read shows a node at the claimed path,
            // surface the same typed conflict the pre-check would have raised, not a raw storage
            // error (the endpoint's 409 depends on the type).
            return nodeFactory.CreateNode(instanceNode)
                .Catch((Exception ex) => Observable.Using(
                        () => hub.ServiceProvider.GetRequiredService<AccessService>().ImpersonateAsSystem(),
                        _ => hub.GetMeshNode(instanceNode.Path, TimeSpan.FromSeconds(10)))
                    .Take(1)
                    .SelectMany(existing => Observable.Throw<MeshNode>(
                        existing is not null ? new InstanceIdTakenException(instanceId) : ex)))
                .SelectMany(created => WriteIndex(hash, created.Path, instanceId)
                    .SelectMany(_ => SeedDefaultGrants(instanceId))
                    .Select(_ =>
                    {
                        logger.LogInformation(
                            "Registered MeshWeaver instance {InstanceId} for {UserId} (key hash prefix {Prefix})",
                            instanceId, userId, InstanceKeys.HashPrefix(hash));
                        return new InstanceRegistrationResult(rawKey, created, instance);
                    }));
        });
    }

    /// <summary>
    /// Registers an installation presenting a registration bootstrap key (<c>mwr_…</c>) — the
    /// first-startup auto-registration flow behind <c>POST /api/instances/register</c>. The
    /// bootstrap key IS the authentication: it resolves to its minting admin
    /// (<see cref="RegistrationKeyService.Resolve"/>, revocation + expiry enforced) and the
    /// registration runs under THAT identity, so the instance lands in the minter's partition
    /// exactly like a hand registration — including the <see cref="DefaultGrantsConfigKey"/> seed.
    /// Errors are typed: <see cref="InvalidBootstrapKeyException"/> (→ 401) and
    /// <see cref="InstanceIdTakenException"/> (→ 409).
    /// </summary>
    public IObservable<InstanceRegistrationResult> RegisterWithBootstrapKey(
        string rawBootstrapKey, string instanceId,
        string displayName = "", string description = "", string homeUrl = "")
    {
        var keys = hub.ServiceProvider.GetRequiredService<RegistrationKeyService>();
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();

        return keys.Resolve(rawBootstrapKey)
            .SelectMany(resolved =>
            {
                if (resolved is null)
                    return Observable.Throw<InstanceRegistrationResult>(new InvalidBootstrapKeyException());

                return Observable.Defer(() =>
                    {
                        var disposable = accessService.SwitchAccessContext(new AccessContext
                        {
                            ObjectId = resolved.Key.OwnerUserId,
                            Name = resolved.Key.OwnerUserName,
                            Email = resolved.Key.OwnerUserEmail,
                        });
                        return Register(
                                resolved.Key.OwnerUserId, resolved.Key.OwnerUserName,
                                resolved.Key.OwnerUserEmail, instanceId,
                                displayName, description, homeUrl)
                            .Finally(() => disposable.Dispose());
                    })
                    .SelectMany(registration => keys.StampUse(resolved.KeyPath)
                        .Select(_ =>
                        {
                            logger.LogInformation(
                                "Instance '{InstanceId}' auto-registered via bootstrap key of {Owner}",
                                registration.Instance.InstanceId, resolved.Key.OwnerUserId);
                            return registration;
                        }));
            });
    }

    /// <summary>The default grant entries the operator configured, parsed and de-blanked.
    /// Empty when no <see cref="DefaultGrantsConfigKey"/> list is set — the strict default.</summary>
    private IReadOnlyList<PluginGrantEntry> DefaultGrantEntries() =>
        configuration is null
            ? []
            : configuration.GetSection(DefaultGrantsConfigKey).GetChildren()
                .Select(c => PluginGrantEntry.TryParse(c.Value))
                .Where(e => e is not null)
                .Select(e => e!)
                .ToList();

    /// <summary>
    /// Seeds the operator-configured default grant entries into
    /// <c>Admin/_PluginGrant/{instanceId}</c> as part of registration. No defaults configured →
    /// no write at all, preserving "registering is identity, not entitlement" exactly.
    ///
    /// <para>Runs as System on BOTH the read and the write: the grant lives in the Admin partition,
    /// which the registering user cannot reach — that unreachability is the security model, and the
    /// seed is registry policy, not a user action. Read-then-<c>CreateOrUpdateNode</c>, never
    /// <c>stream.Update</c>: the node does not exist yet for a fresh id, and Update aborts on a
    /// missing node (the first-grant failure observed live 2026-08-06). Existing entries — e.g. an
    /// admin re-seeding after an orphan cleanup — are preserved; defaults merge in idempotently.</para>
    /// </summary>
    private IObservable<Unit> SeedDefaultGrants(string instanceId)
    {
        var defaults = DefaultGrantEntries();
        if (defaults.Count == 0)
            return Observable.Return(Unit.Default);

        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        var path = MeshWeaverInstanceNodeType.GrantPath(instanceId);

        return Observable.Using(
                () => accessService.ImpersonateAsSystem(),
                _ => hub.GetMeshNode(path, TimeSpan.FromSeconds(10)))
            .Take(1)
            .SelectMany(existing => Observable.Defer(() =>
            {
                var grant = existing?.ContentAs<PluginGrant>(hub.JsonSerializerOptions)
                            ?? new PluginGrant { InstanceId = instanceId };
                var missing = defaults
                    .Where(d => !grant.Entries.Any(e =>
                        string.Equals(e.Source, d.Source, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(e.PackageId, d.PackageId, StringComparison.Ordinal)))
                    .ToList();
                // Nothing to add → no write at all. Writing a no-op would still re-stamp
                // GrantedByUserId/UpdatedAt to System/now, silently erasing WHO last decided an
                // existing admin-written grant — and a grant is an access decision, so the
                // attribution is part of the record.
                if (missing.Count == 0)
                    return Observable.Return(Unit.Default);
                var entries = grant.Entries.Concat(missing).ToList();

                var node = new MeshNode(instanceId, MeshWeaverInstanceNodeType.GrantNamespace)
                {
                    Name = $"Plugin grant: {instanceId}",
                    NodeType = MeshWeaverInstanceNodeType.GrantNodeType,
                    State = MeshNodeState.Active,
                    Content = grant with
                    {
                        InstanceId = instanceId,
                        Entries = entries,
                        GrantedByUserId = WellKnownUsers.System,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    },
                };

                // Same Defer/Finally discipline as WriteIndex: the System context must be active
                // when CreateOrUpdateNode captures it, and the SelectMany continuation may land on
                // a thread the earlier Using scope never touched.
                var disposable = accessService.ImpersonateAsSystem();
                return nodeFactory.CreateOrUpdateNode(node)
                    .Select(_ =>
                    {
                        logger.LogInformation(
                            "Seeded default plugin grants [{Entries}] for instance {InstanceId}",
                            string.Join(", ", missing), instanceId);
                        return Unit.Default;
                    })
                    .Finally(() => disposable.Dispose());
            }));
    }

    /// <summary>
    /// Mints a replacement key for an already-registered instance, writing the new index entry
    /// before the instance node so there is no window where a valid key resolves to nothing.
    /// The OLD index entry is left to be cleaned up separately — the instance node's hash is the
    /// authority, so the stale entry stops authenticating the moment this completes.
    /// </summary>
    public IObservable<string> ReissueKey(string instancePath)
    {
        var rawKey = InstanceKeys.Generate();
        var hash = InstanceKeys.Hash(rawKey);
        var workspace = hub.GetWorkspace();

        return workspace.GetMeshNodeStream(instancePath)
            .Where(node => node is not null)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(10))
            .SelectMany(node =>
            {
                var instance = node!.ContentAs<MeshWeaverInstance>(hub.JsonSerializerOptions)
                    ?? throw new InvalidOperationException($"Node {instancePath} is not a MeshWeaverInstance.");
                return WriteIndex(hash, instancePath, instance.InstanceId)
                    .SelectMany(_ => workspace.GetMeshNodeStream(instancePath)
                        .Update(current => current with
                        {
                            Content = (current.ContentAs<MeshWeaverInstance>(hub.JsonSerializerOptions)
                                       ?? instance) with
                            {
                                KeyHash = hash,
                                KeyIssuedAt = DateTimeOffset.UtcNow,
                            },
                        }))
                    .Select(_ =>
                    {
                        logger.LogInformation("Re-issued key for instance {InstanceId}", instance.InstanceId);
                        return rawKey;
                    });
            });
    }

    /// <summary>Enables or disables an instance. A disabled instance fails authentication while its
    /// grants stay intact, so access can be cut without losing the grant history.</summary>
    public IObservable<MeshNode> SetDisabled(string instancePath, bool disabled) =>
        hub.GetWorkspace().GetMeshNodeStream(instancePath)
            .Update(current => current with
            {
                Content = current.ContentAs<MeshWeaverInstance>(hub.JsonSerializerOptions) is { } instance
                    ? instance with { IsDisabled = disabled }
                    : current.Content,
            });

    /// <summary>
    /// Whether no instance currently claims <paramref name="instanceId"/>. An existence check is one
    /// of the valid uses of the (eventually consistent) query index — reading the grant path would
    /// not do, since the claim lives on the instance nodes.
    ///
    /// <para><see cref="IMeshService.Query(MeshQueryRequest)"/> is reactive by contract (there is no
    /// async surface), so this needs no bridge at all — take the first live snapshot and read its
    /// membership. Running as System: the claim is global, and a caller must not be told "available"
    /// merely because someone else's instance is invisible to them.</para>
    /// </summary>
    public IObservable<bool> IsIdAvailable(string instanceId)
    {
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        var request = new MeshQueryRequest
        {
            Query = $"nodeType:{MeshWeaverInstanceNodeType.NodeType} id:{instanceId}",
        };
        return Observable.Using(
                () => accessService.ImpersonateAsSystem(),
                _ => meshService.Query(request))
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(10))
            .Select(results => results.Count == 0);
    }

    // The global index namespace is not user-writable — the same System-scoped write ApiTokenService
    // does for ApiTokenIndex. The Defer/Finally layout matters: the System context must be active
    // during CaptureContext at Subscribe time, not merely when the using-block was constructed.
    //
    // 🚨 The index lives at the TOP-LEVEL path MeshWeaverInstance/{keyHashPrefix}, and a top-level
    // segment is its own Postgres schema. The router does NOT lazy-create schemas, so without the
    // provisioning step the FIRST registration dies with
    //   42P01: relation "meshweaverinstance.mesh_nodes" does not exist
    // (observed on memex-cloud 2026-08-06). Provisioned HERE rather than declared in
    // DefaultPartitionProvider on purpose: an eager declaration provisions the schema at EVERY
    // mesh boot — including every test mesh — for a partition that is written only when someone
    // registers an installation. ApiToken/VUser are declared eagerly because they sit on a
    // per-request path and cannot afford a first-request provision; registration is rare and
    // deliberate, so it takes the same lazy route PackageInstaller uses for its target partition.
    // EnsurePartitionProvisioned is idempotent and promise-cached; non-Postgres providers no-op.
    private IObservable<MeshNode> WriteIndex(string hash, string instancePath, string instanceId)
    {
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        var indexNode = new MeshNode(
            InstanceKeys.HashPrefix(hash), MeshWeaverInstanceNodeType.IndexNamespace)
        {
            Name = $"Instance key: {instanceId}",
            NodeType = MeshWeaverInstanceNodeType.NodeType,
            State = MeshNodeState.Active,
            Content = new MeshWeaverInstanceIndex
            {
                KeyHash = hash,
                InstancePath = instancePath,
                InstanceId = instanceId,
            },
        };

        var providers = hub.ServiceProvider.GetServices<IPartitionStorageProvider>().ToArray();
        var provisioned = providers.Length == 0
            ? Observable.Return(Unit.Default)
            : Observable.Merge(providers.Select(p =>
                    p.EnsurePartitionProvisioned(MeshWeaverInstanceNodeType.IndexNamespace)))
                .DefaultIfEmpty(Unit.Default)
                .LastAsync();

        return provisioned.SelectMany(_ => Observable.Defer(() =>
        {
            var disposable = accessService.SwitchAccessContext(
                new AccessContext { ObjectId = WellKnownUsers.System, Name = "system-security" });
            return nodeFactory.CreateNode(indexNode).Finally(() => disposable.Dispose());
        }));
    }
}

/// <summary>
/// The outcome of registering an instance. <paramref name="RawKey"/> is the ONLY time the key is
/// available in the clear — only its hash is stored, so it must be shown to the user now or lost.
/// </summary>
public sealed record InstanceRegistrationResult(string RawKey, MeshNode Node, MeshWeaverInstance Instance);

/// <summary>The requested instance id is already claimed. Typed so callers (the registration
/// endpoint's 409) can branch on it without matching message text.</summary>
public sealed class InstanceIdTakenException(string instanceId)
    : InvalidOperationException($"Instance id '{instanceId}' is already registered.")
{
    /// <summary>The id that was requested.</summary>
    public string InstanceId { get; } = instanceId;
}

/// <summary>The presented registration bootstrap key is unknown, revoked or expired — the
/// registration endpoint's 401. The message deliberately does not say which.</summary>
public sealed class InvalidBootstrapKeyException()
    : InvalidOperationException("A valid registration bootstrap key is required (mwr_…).");
