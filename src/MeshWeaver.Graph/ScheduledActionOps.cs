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
/// Reactive helpers shared by the invite entry point and the
/// background <see cref="ScheduledActionRunner"/>: create the durable action node, grant a role,
/// pin a Space to a user's dashboard, and flip an action's lifecycle state. All cold observables —
/// callers Subscribe to drive the write; all run under the caller's <c>AccessContext</c> (the
/// runner wraps them in <c>ImpersonateAsSystem</c>).
/// </summary>
public static class ScheduledActionOps
{
    /// <summary>Writes the durable <see cref="ScheduledAction"/> node at <c>Admin/ScheduledAction/{id}</c>.
    /// Upsert by id — a deterministic id (e.g. per invitee+space) makes a re-invite idempotent instead
    /// of piling up duplicate actions.</summary>
    public static IObservable<MeshNode> CreateAction(IMeshService meshService, ScheduledAction action)
        => meshService.CreateOrUpdateNode(new MeshNode(action.Id, ScheduledActionNodeType.Namespace)
        {
            NodeType = ScheduledActionNodeType.NodeType,
            Name = $"{action.ActionKind} → {action.TargetPath}",
            Content = action,
        });

    /// <summary>Grants <paramref name="userId"/> the <paramref name="role"/> on <paramref name="spacePath"/>
    /// by creating the <c>{space}/_Access/{user}_Access</c> assignment (create-or-update = idempotent).
    /// Builds the node the same way <c>AccessControlLayoutArea.CreateAssignment</c> does.</summary>
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

    /// <summary>Flips an action to its terminal state (Fired on success, Failed with the error otherwise).</summary>
    public static IObservable<MeshNode> SetStatus(
        IMessageHub hub, string actionId, ScheduledActionStatus status, string? error = null)
        => hub.GetWorkspace().GetMeshNodeStream(ScheduledActionNodeType.Path(actionId)).Update(node =>
        {
            if (node.Content is not ScheduledAction a)
                return node;
            return node with
            {
                Content = a with
                {
                    Status = status,
                    FiredAt = status == ScheduledActionStatus.Fired ? DateTimeOffset.UtcNow : a.FiredAt,
                    LastError = error,
                },
            };
        });

    /// <summary>
    /// Reads a content field (e.g. <c>email</c>) off a node's content as a string, case-insensitively —
    /// serializes the (possibly typed) Content to JSON and looks the property up. Used to evaluate a
    /// <see cref="ScheduledAction.MatchField"/> against the triggering node.
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
