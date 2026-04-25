using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Blazor.Components;

public partial class ThreadMessageBubbleView : BlazorView<ThreadMessageBubbleControl, ThreadMessageBubbleView>
{
    // Local view state: populated from the bound MeshNode stream when NodePath is set,
    // otherwise from ViewModel.* (legacy callers that pass concrete Text / ToolCalls).
    private string Role = "user";
    private string AuthorName = "";
    private string? ModelName;
    private DateTime? Timestamp;

    private string? messageText;
    private IReadOnlyList<ToolCallEntry>? toolCalls;
    private IReadOnlyList<NodeChangeEntry>? updatedNodes;
    private bool isEditing;

    private bool IsUser => Role.Equals("user", StringComparison.OrdinalIgnoreCase);
    private bool CanEdit => !string.IsNullOrEmpty(ViewModel.ThreadPath) && !string.IsNullOrEmpty(ViewModel.MessageId);
    private bool HasToolCalls => toolCalls is { Count: > 0 };

    /// <summary>
    /// Long-standing per-node MeshNode stream — the same primitive
    /// <see cref="MeshWeaver.AI.ThreadExecution"/> writes through. Subscribed in
    /// BindData via AddBinding so it's auto-disposed on component teardown.
    /// </summary>
    private ISynchronizationStream<MeshNode>? _nodeStream;

    /// <summary>
    /// Shape returned by <see cref="FormatToolCallDisplay"/>: tells the view how
    /// to render the chip — verb text, target path (when the tool modifies a node),
    /// and whether the path should be decorated with Diff + Restore links.
    /// </summary>
    public readonly record struct ToolCallDisplay(string Verb, string? Path, bool IsNodeModifying);

    private MarkdownControl MarkdownVm => new MarkdownControl(messageText ?? "")
        .WithStyle("background: transparent;");

    private void StartEdit() => isEditing = true;

    private void CancelEdit() => isEditing = false;

    protected override void BindData()
    {
        base.BindData();

        // Static metadata that the layout area still passes (ThreadPath, MessageId
        // for cancel / edit / delegation links). Role/AuthorName/ModelName/Timestamp
        // come from the live message when NodePath is set; otherwise from ViewModel.
        if (string.IsNullOrEmpty(ViewModel.NodePath))
        {
            // Legacy path — backend still pushes ThreadMessageViewModel through a
            // layout data section. Bind concrete fields off ViewModel.
            Role = ViewModel.Role ?? "user";
            AuthorName = ViewModel.AuthorName ?? "";
            ModelName = ViewModel.ModelName;
            Timestamp = ViewModel.Timestamp;

            DataBind(ViewModel.Text, x => x.messageText, (val, prev) =>
            {
                var text = val as string ?? "";
                if (text == prev) return prev;
                return text;
            });
            DataBind(ViewModel.ToolCalls, x => x.toolCalls, (val, prev) =>
            {
                IReadOnlyList<ToolCallEntry>? result = val switch
                {
                    null => null,
                    IReadOnlyList<ToolCallEntry> list => list,
                    JsonElement je => je.Deserialize<List<ToolCallEntry>>(Hub.JsonSerializerOptions),
                    _ => null
                };
                if (result == null && prev == null) return prev;
                if (result != null && prev != null && result.SequenceEqual(prev)) return prev;
                return result;
            });
            DataBind(ViewModel.UpdatedNodes, x => x.updatedNodes, (val, prev) =>
            {
                IReadOnlyList<NodeChangeEntry>? result = val switch
                {
                    null => null,
                    IReadOnlyList<NodeChangeEntry> list => list,
                    JsonElement je => je.Deserialize<List<NodeChangeEntry>>(Hub.JsonSerializerOptions),
                    _ => null
                };
                if (result == null && prev == null) return prev;
                if (result != null && prev != null && result.SequenceEqual(prev)) return prev;
                return result;
            });
            return;
        }

        // Canonical path: subscribe to the per-message remote stream — the same
        // primitive ThreadExecution writes through. See
        // Doc/Architecture/ThreadExecutionStreaming.md.
        //
        // We do NOT reference MeshWeaver.AI.ThreadMessage strongly here — the
        // Blazor layer must not depend on the AI layer. Instead we extract
        // Text / ToolCalls / etc. from node.Content as a JsonElement (works
        // whether the content arrives strongly-typed or already serialised).
        try
        {
            _nodeStream = Hub.GetWorkspace().GetRemoteStream<MeshNode, MeshNodeReference>(
                new Address(ViewModel.NodePath), new MeshNodeReference());

            AddBinding(_nodeStream
                .Where(c => c.Value?.Content is not null)
                .Select(c => ToJsonElement(c.Value!.Content!))
                .Subscribe(je =>
                {
                    var changed = false;

                    var role = je.TryGetProperty("role", out var roleProp) && roleProp.ValueKind == JsonValueKind.String
                        ? roleProp.GetString() ?? "user"
                        : "user";
                    if (role != Role) { Role = role; changed = true; }

                    var explicitAuthor = je.TryGetProperty("authorName", out var authorProp) && authorProp.ValueKind == JsonValueKind.String
                        ? authorProp.GetString()
                        : null;
                    var agentName = je.TryGetProperty("agentName", out var agentProp) && agentProp.ValueKind == JsonValueKind.String
                        ? agentProp.GetString()
                        : null;
                    var author = explicitAuthor
                        ?? (role.Equals("user", StringComparison.OrdinalIgnoreCase)
                            ? "You"
                            : agentName ?? "Assistant");
                    if (author != AuthorName) { AuthorName = author; changed = true; }

                    var modelName = je.TryGetProperty("modelName", out var modelProp) && modelProp.ValueKind == JsonValueKind.String
                        ? modelProp.GetString()
                        : null;
                    if (modelName != ModelName) { ModelName = modelName; changed = true; }

                    DateTime? timestamp = je.TryGetProperty("timestamp", out var tsProp) && tsProp.ValueKind == JsonValueKind.String
                        && DateTime.TryParse(tsProp.GetString(), out var parsed)
                            ? parsed
                            : null;
                    if (timestamp != Timestamp) { Timestamp = timestamp; changed = true; }

                    var text = je.TryGetProperty("text", out var textProp) && textProp.ValueKind == JsonValueKind.String
                        ? textProp.GetString()
                        : null;
                    if (text != messageText) { messageText = text; changed = true; }

                    IReadOnlyList<ToolCallEntry>? newToolCalls = je.TryGetProperty("toolCalls", out var tcProp)
                        && tcProp.ValueKind == JsonValueKind.Array
                            ? tcProp.Deserialize<List<ToolCallEntry>>(Hub.JsonSerializerOptions)
                            : null;
                    if (!ToolCallsEqual(newToolCalls, toolCalls))
                    {
                        toolCalls = newToolCalls;
                        changed = true;
                    }

                    IReadOnlyList<NodeChangeEntry>? newUpdated = je.TryGetProperty("updatedNodes", out var unProp)
                        && unProp.ValueKind == JsonValueKind.Array
                            ? unProp.Deserialize<List<NodeChangeEntry>>(Hub.JsonSerializerOptions)
                            : null;
                    if (!UpdatedNodesEqual(newUpdated, updatedNodes))
                    {
                        updatedNodes = newUpdated;
                        changed = true;
                    }

                    if (changed) InvokeAsync(StateHasChanged);
                }));
        }
        catch (Exception ex)
        {
            // Workspace has no MeshNodeReference reducer for this address — fall
            // back to the static ViewModel fields (no live updates).
            var logger = Hub.ServiceProvider.GetService<ILoggerFactory>()
                ?.CreateLogger<ThreadMessageBubbleView>();
            logger?.LogWarning(ex, "ThreadMessageBubbleView could not subscribe to {NodePath}; falling back to ViewModel",
                ViewModel.NodePath);
            Role = ViewModel.Role ?? "user";
            AuthorName = ViewModel.AuthorName ?? "";
            ModelName = ViewModel.ModelName;
            Timestamp = ViewModel.Timestamp;
        }
    }

    private JsonElement ToJsonElement(object content)
        => content is JsonElement je
            ? je
            : JsonSerializer.SerializeToElement(content, Hub.JsonSerializerOptions);

    private static bool ToolCallsEqual(IReadOnlyList<ToolCallEntry>? a, IReadOnlyList<ToolCallEntry>? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return a.SequenceEqual(b);
    }

    private static bool UpdatedNodesEqual(IReadOnlyList<NodeChangeEntry>? a, IReadOnlyList<NodeChangeEntry>? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return a.SequenceEqual(b);
    }

    /// <summary>
    /// Matches a tool-call target path against the message's aggregated
    /// <c>UpdatedNodes</c> so the chip can render Diff / Restore links with the
    /// correct before/after versions.
    /// </summary>
    private NodeChangeEntry? FindChange(string? path)
    {
        if (string.IsNullOrEmpty(path) || updatedNodes is null)
            return null;
        return updatedNodes.FirstOrDefault(n =>
            string.Equals(n.Path, path, StringComparison.Ordinal));
    }

    private static string FormatToolCallSummary(ToolCallEntry call)
    {
        var d = FormatToolCallDisplay(call);
        return d.Path is null ? d.Verb : $"{d.Verb} {d.Path}";
    }

    /// <summary>
    /// Splits the tool call into a verb + target-path + flag. Node-modifying verbs
    /// (Create / Update / Patch / Delete) flow through with <c>IsNodeModifying=true</c>
    /// so the view can render inline Diff + Restore links next to the path.
    /// </summary>
    private static ToolCallDisplay FormatToolCallDisplay(ToolCallEntry call)
    {
        if (!string.IsNullOrEmpty(call.DelegationPath))
        {
            var name = call.DisplayName ?? call.Name;
            if (name.Contains("Delegating to "))
                name = name.Replace("Delegating to ", "").TrimEnd('.', ' ');
            return new ToolCallDisplay(name, null, false);
        }

        // Args come serialized as "path: Org/X\ncontent: ...". Strip the "path: " prefix.
        var rawArgs = call.Arguments ?? "";
        string? path = null;
        foreach (var line in rawArgs.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("path:", StringComparison.OrdinalIgnoreCase))
            {
                path = trimmed["path:".Length..].Trim();
                break;
            }
            if (trimmed.StartsWith("url:", StringComparison.OrdinalIgnoreCase))
            {
                path = trimmed["url:".Length..].Trim();
                break;
            }
            if (trimmed.StartsWith("query:", StringComparison.OrdinalIgnoreCase))
            {
                path = trimmed["query:".Length..].Trim();
                break;
            }
        }
        // Fallback to the first arg line if we couldn't identify the key.
        if (string.IsNullOrEmpty(path))
            path = rawArgs.Split('\n').FirstOrDefault()?.Trim();

        // Agents write references as "@/Foo/Bar" — strip the "@" so href="/{path}"
        // renders as "/Foo/Bar" and not "/@/Foo/Bar".
        if (!string.IsNullOrEmpty(path) && path.StartsWith('@'))
            path = path[1..].TrimStart('/');

        return call.Name switch
        {
            "Get" or "get_node" => new ToolCallDisplay("Reading", path, false),
            "Search" or "search_nodes" => new ToolCallDisplay("Searching", path, false),
            "Create" or "create_node" => new ToolCallDisplay("Created", path, true),
            "Update" or "update_node" => new ToolCallDisplay("Updated", path, true),
            "Patch" or "patch_node" => new ToolCallDisplay("Patched", path, true),
            "Delete" or "delete_node" => new ToolCallDisplay("Deleted", path, true),
            "NavigateTo" or "navigate_to" => new ToolCallDisplay("Navigating to", path, false),
            "SearchWeb" => new ToolCallDisplay("Searching web for", path, false),
            "FetchWebPage" => new ToolCallDisplay("Fetching", path, false),
            "store_plan" => new ToolCallDisplay("Stored plan", null, false),
            _ => new ToolCallDisplay(call.DisplayName ?? call.Name, path, false)
        };
    }
}
