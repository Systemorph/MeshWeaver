using System.Reactive.Linq;
using System.Text.RegularExpressions;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// Registers MeshWeaver installations and issues their keys — the instance-facing counterpart of
/// <c>ApiTokenService</c>, built on the same mechanism (mint → hash → write the node → write a
/// routing index under System identity) and deliberately NOT on the same node type.
///
/// <para>Registering grants nothing. A new instance authenticates and is entitled to nothing until
/// a platform admin writes it a <see cref="PluginGrant"/> in the Admin partition, which the
/// registering user cannot reach.</para>
/// </summary>
public sealed class MeshWeaverInstanceService(
    IMeshService nodeFactory, IMessageHub hub, ILogger<MeshWeaverInstanceService> logger)
{
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
                return Observable.Throw<InstanceRegistrationResult>(new InvalidOperationException(
                    $"Instance id '{instanceId}' is already registered."));

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

            return nodeFactory.CreateNode(instanceNode)
                .SelectMany(created => WriteIndex(hash, created.Path, instanceId)
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

        return Observable.Defer(() =>
        {
            var disposable = accessService.SwitchAccessContext(
                new AccessContext { ObjectId = WellKnownUsers.System, Name = "system-security" });
            return nodeFactory.CreateNode(indexNode).Finally(() => disposable.Dispose());
        });
    }
}

/// <summary>
/// The outcome of registering an instance. <paramref name="RawKey"/> is the ONLY time the key is
/// available in the clear — only its hash is stored, so it must be shown to the user now or lost.
/// </summary>
public sealed record InstanceRegistrationResult(string RawKey, MeshNode Node, MeshWeaverInstance Instance);
