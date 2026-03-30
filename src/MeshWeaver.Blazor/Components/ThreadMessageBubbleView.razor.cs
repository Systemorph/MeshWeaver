using System.Collections.Immutable;
using MeshWeaver.Layout;

namespace MeshWeaver.Blazor.Components;

public partial class ThreadMessageBubbleView : BlazorView<ThreadMessageBubbleControl, ThreadMessageBubbleView>
{
    private bool IsUser => ViewModel.Role.Equals("user", StringComparison.OrdinalIgnoreCase);

    private string? messageText;
    private ImmutableList<ToolCallEntry>? toolCalls;

    private bool HasToolCalls => toolCalls is { Count: > 0 };

    private MarkdownControl MarkdownVm => new MarkdownControl(messageText ?? "")
        .WithStyle("background: transparent;");

    protected override void BindData()
    {
        base.BindData();
        DataBind(ViewModel.Text, x => x.messageText);
        DataBind(ViewModel.ToolCalls, x => x.toolCalls);
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
