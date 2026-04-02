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
    private bool isEditing;
    private string? editText;

    private bool HasToolCalls => toolCalls is { Count: > 0 };

    private MarkdownControl MarkdownVm => new MarkdownControl(messageText ?? "")
        .WithStyle("background: transparent;");

    private void StartEdit()
    {
        editText = messageText;
        isEditing = true;
    }

    private void CancelEdit()
    {
        isEditing = false;
    }

    private void SubmitEdit()
    {
        if (!CanEdit) return;
        isEditing = false;
        Hub.Post(new ResubmitMessageRequest
        {
            ThreadPath = ViewModel.ThreadPath!,
            MessageId = ViewModel.MessageId!,
            UserMessageText = editText ?? messageText ?? ""
        }, o => o.WithTarget(new Address(ViewModel.ThreadPath!)));
    }

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
    }

    private static string FormatToolCallSummary(ToolCallEntry call)
    {
        if (!string.IsNullOrEmpty(call.DelegationPath))
        {
            // Delegation: extract agent name from DisplayName (e.g., "Delegating to Coder..." → "Coder")
            var name = call.DisplayName ?? call.Name;
            if (name.Contains("Delegating to "))
                name = name.Replace("Delegating to ", "").TrimEnd('.', ' ');
            return name;
        }

        // Regular tool calls: friendly verb
        var target = call.Arguments?.Split('\n').FirstOrDefault()?.Trim() ?? "";
        return call.Name switch
        {
            "Get" or "get_node" => $"Getting {target}",
            "Search" or "search_nodes" => $"Searching {target}",
            "Create" or "create_node" => $"Creating {target}",
            "Update" or "update_node" => $"Updating {target}",
            "Patch" or "patch_node" => $"Patching {target}",
            "Delete" or "delete_node" => $"Deleting {target}",
            "NavigateTo" or "navigate_to" => $"Navigating to {target}",
            "store_plan" => "Storing plan",
            _ => call.DisplayName ?? call.Name
        };
    }
}
