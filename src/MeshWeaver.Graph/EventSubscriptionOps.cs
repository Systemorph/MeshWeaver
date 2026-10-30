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

    /// <summary>
    /// Advances a REPEATING timer to its next slot, leaving it <c>Pending</c>. The counterpart of
    /// <see cref="SetStatus"/> for a subscription that has no terminal state.
    ///
    /// <para>Writing the next slot is what makes a repeater survive a restart honestly: the pending-set
    /// reconcile re-schedules from the STORED <c>FireAt</c>, so a fire that did not record its next
    /// occurrence would be replayed on the next boot — a nightly job re-running on every pod restart.</para>
    /// </summary>
    public static IObservable<MeshNode> RearmTimer(
        IMessageHub hub, string subscriptionPath, DateTimeOffset nextFireAt)
        => hub.GetWorkspace().GetMeshNodeStream(subscriptionPath).Update(node =>
        {
            if (node.Content is not EventSubscription s)
                return node;
            return node with
            {
                Content = s with
                {
                    FireAt = nextFireAt,
                    Status = EventSubscriptionStatus.Pending,
                    FiredAt = DateTimeOffset.UtcNow,
                    LastError = null,
                }
            };
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

    /// <summary>Adds <paramref name="userId"/> to the group at <paramref name="groupPath"/> by creating the
    /// <c>{groupPath}/{user}_Membership</c> <see cref="GroupMembership"/> node (create-or-update = idempotent).
    /// The membership node is a child of the group so it lands in the group's partition schema, where the
    /// permission matview's group-expansion CTE reads it (<c>content.member</c> + <c>content.groups[].group</c>)
    /// and joins the group's <c>AccessAssignment</c> — so the user inherits whatever the group is granted.</summary>
    public static IObservable<MeshNode> AddToGroup(IMeshService meshService, string userId, string groupPath)
    {
        var idSafe = userId.Replace('/', '_').Replace('@', '_');
        var node = new MeshNode($"{idSafe}_Membership", groupPath)
        {
            NodeType = GroupMembershipNodeType.NodeType,
            Name = $"{userId} — {groupPath.Split('/').LastOrDefault()}",
            Content = new GroupMembership
            {
                Member = userId,
                DisplayName = userId,
                Groups = [new MembershipEntry { Group = groupPath }],
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

    /// <summary>
    /// Delivers a firing to the node at <paramref name="path"/>: writes <paramref name="delivery"/> onto
    /// its content as the <c>eventDelivery</c> field (<see cref="WithDelivery"/>), through the node's
    /// own stream, so the write routes to the node's owning hub and that hub's watcher acts on it.
    /// Every other field of the content is left as it was.
    /// </summary>
    /// <param name="hub">The hub whose workspace issues the write.</param>
    /// <param name="path">The node named by <see cref="EventSubscription.DeliverTo"/>.</param>
    /// <param name="delivery">What fired, for whom, and on whose request.</param>
    /// <returns>The node after the write. Cold.</returns>
    public static IObservable<MeshNode> Deliver(IMessageHub hub, string path, EventDelivery delivery)
        => hub.GetWorkspace().GetMeshNodeStream(path).Update(node =>
            node with { Content = WithDelivery(node.Content, delivery, hub.JsonSerializerOptions) });

    /// <summary>
    /// <paramref name="content"/> as JSON with <paramref name="delivery"/> set on its
    /// <c>eventDelivery</c> field (named through <paramref name="options"/>' naming policy). The content
    /// may arrive typed or as a <see cref="JsonElement"/>; either way its other fields, its
    /// <c>$type</c> included, are kept, so the owning hub re-types it on the way in. Pure.
    /// </summary>
    /// <param name="content">The node's current content, or null.</param>
    /// <param name="delivery">The delivery to set.</param>
    /// <param name="options">The mesh's serializer options.</param>
    /// <returns>The content with the delivery set.</returns>
    public static JsonObject WithDelivery(object? content, EventDelivery delivery, JsonSerializerOptions options)
    {
        var json = content switch
        {
            null => null,
            JsonElement element => JsonNode.Parse(element.GetRawText()),
            JsonNode node => node.DeepClone(),
            _ => JsonSerializer.SerializeToNode(content, options),
        };
        var obj = json as JsonObject ?? new JsonObject();
        obj[DeliveryField(options)] = JsonSerializer.SerializeToNode(delivery, options);
        return obj;
    }

    /// <summary>The content field a delivery is written to: <c>EventDelivery</c> through the options'
    /// naming policy (<c>eventDelivery</c> on the mesh). Pure.</summary>
    /// <param name="options">The mesh's serializer options.</param>
    /// <returns>The field name.</returns>
    public static string DeliveryField(JsonSerializerOptions options)
        => options.PropertyNamingPolicy?.ConvertName(nameof(EventDelivery)) ?? nameof(EventDelivery);

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
