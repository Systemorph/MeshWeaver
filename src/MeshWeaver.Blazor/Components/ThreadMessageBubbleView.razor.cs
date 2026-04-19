using System.Collections.Immutable;
using System.Text.Json;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Blazor.Components;

public partial class ThreadMessageBubbleView : BlazorView<ThreadMessageBubbleControl, ThreadMessageBubbleView>
{
    private bool IsUser => ViewModel.Role.Equals("user", StringComparison.OrdinalIgnoreCase);
    private bool CanEdit => !string.IsNullOrEmpty(ViewModel.ThreadPath) && !string.IsNullOrEmpty(ViewModel.MessageId);

    private string? messageText;
    private IReadOnlyList<ToolCallEntry>? toolCalls;
    private IReadOnlyList<NodeChangeEntry>? updatedNodes;
    private bool isEditing;

    private bool HasToolCalls => toolCalls is { Count: > 0 };

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
        DataBind(ViewModel.Text, x => x.messageText, (val, prev) =>
        {
            var text = val as string ?? "";
            if (text == prev) return prev; // skip if unchanged
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
