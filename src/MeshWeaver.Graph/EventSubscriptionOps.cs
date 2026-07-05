using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph;

/// <summary>
/// Reactive helpers shared by the callers that create <see cref="EventSubscription"/>s (the invite
/// entry point, and later delegation) and the background <see cref="EventSubscriptionRunner"/>: write
/// the durable subscription node, grant a role, pin a Space, and flip a subscription's lifecycle state.
/// All cold observables — callers Subscribe to drive the write; all run under the caller's
/// <c>AccessContext</c> (the runner wraps them in <c>ImpersonateAsSystem</c>).
/// </summary>
public static class EventSubscriptionOps
{
    /// <summary>Writes the durable <see cref="EventSubscription"/> node at
    /// <c>Admin/EventSubscription/{id}</c>. Upsert by id — a deterministic id (e.g. per invitee+space)
    /// makes a re-registration idempotent instead of piling up duplicates.</summary>
    public static IObservable<MeshNode> CreateSubscription(IMeshService meshService, EventSubscription subscription)
        => meshService.CreateOrUpdateNode(new MeshNode(subscription.Id, EventSubscriptionNodeType.Namespace)
        {
            NodeType = EventSubscriptionNodeType.NodeType,
            Name = $"{subscription.TriggerType} → {subscription.ContinuationType}",
            Content = subscription,
        });

    /// <summary>Grants <paramref name="userId"/> the <paramref name="role"/> on <paramref name="spacePath"/>
    /// by creating the <c>{space}/_Access/{user}_Access</c> assignment (create-or-update = idempotent).</summary>
    public static IObservable<MeshNode> Grant(IMeshService meshService, string userId, string spacePath, string role)
    {
        var idSafe = userId.Replace('/', '_').Replace('@', '_');
        var node = new MeshNode($"{idSafe}_Access", $"{spacePath}/_Access")
        {
            NodeType = Configuration.AccessAssignmentNodeType.NodeType,
            Name = $"{userId} Access",
            MainNode = spacePath,
            Content = new AccessAssignment
            {
                AccessObject = userId,
                DisplayName = userId,
                Roles = [new RoleAssignment { Role = role, Denied = false }],
            },
        };
        return meshService.CreateOrUpdateNode(node);
    }

    /// <summary>Adds <paramref name="path"/> to <paramref name="userId"/>'s pinned dashboard list (idempotent).</summary>
    public static IObservable<MeshNode> Pin(IMessageHub hub, string userId, string path)
        => hub.GetWorkspace().GetMeshNodeStream(userId).Update(node =>
        {
            if (node.Content is not User user)
                return node;
            if (user.PinnedPaths.Contains(path))
                return node;   // already pinned — no-op
            return node with { Content = user with { PinnedPaths = [.. user.PinnedPaths, path] } };
        });

    /// <summary>Flips a subscription to its terminal state (Fired on success, Failed with the error otherwise).</summary>
    public static IObservable<MeshNode> SetStatus(
        IMessageHub hub, string subscriptionPath, EventSubscriptionStatus status, string? error = null)
        => hub.GetWorkspace().GetMeshNodeStream(subscriptionPath).Update(node =>
        {
            if (node.Content is not EventSubscription s)
                return node;
            return node with
            {
                Content = s with
                {
                    Status = status,
                    FiredAt = status == EventSubscriptionStatus.Fired ? DateTimeOffset.UtcNow : s.FiredAt,
                    LastError = error,
                },
            };
        });

    /// <summary>
    /// Reads a content field (e.g. <c>email</c>) off a node's content as a string, case-insensitively —
    /// serializes the (possibly typed) Content to JSON and looks the property up. Used to evaluate a
    /// <see cref="EventSubscription.MatchField"/> against the triggering node.
    /// </summary>
    public static string? ReadContentField(MeshNode node, string field, JsonSerializerOptions options)
    {
        try
        {
            var json = node.Content is JsonElement je
                ? JsonNode.Parse(je.GetRawText())
                : JsonSerializer.SerializeToNode(node.Content, options);
            if (json is not JsonObject obj)
                return null;
            foreach (var (key, value) in obj)
                if (string.Equals(key, field, StringComparison.OrdinalIgnoreCase))
                    return value?.GetValue<string>();
            return null;
        }
        catch
        {
            return null;
        }
    }
}
