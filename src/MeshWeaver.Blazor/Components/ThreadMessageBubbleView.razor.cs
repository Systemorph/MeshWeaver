using System.Collections.Immutable;
using MeshWeaver.Layout;

namespace MeshWeaver.Blazor.Components;

public partial class ThreadMessageBubbleView : BlazorView<ThreadMessageBubbleControl, ThreadMessageBubbleView>
{
    private bool IsUser => ViewModel.Role.Equals("user", StringComparison.OrdinalIgnoreCase);

    private string? messageText;
    private bool isExecuting;
    private string? executionStatus;
    private ImmutableList<ToolCallEntry>? toolCalls;

    // Derive executing state from content: empty text + no completed tool calls = still generating
    private bool IsEffectivelyExecuting => string.IsNullOrEmpty(messageText)
        && (toolCalls == null || toolCalls.Count == 0 || toolCalls.All(c => c.Result == null));
    private bool ShowSpinner => IsEffectivelyExecuting && !HasToolCalls;
    private bool ShowExecutingIndicator => false; // not needed — tool calls show inline
    private bool HasToolCalls => toolCalls is { Count: > 0 };

    /// <summary>First line of executionStatus (the formatted tool name / delegation status).</summary>
    private string StatusTitle
    {
        get
        {
            if (string.IsNullOrEmpty(executionStatus)) return "Generating response...";
            var nl = executionStatus.IndexOf('\n');
            return nl > 0 ? executionStatus[..nl] : executionStatus;
        }
    }

    /// <summary>Remaining lines of executionStatus (arguments, delegation detail).</summary>
    private string? StatusDetail
    {
        get
        {
            if (string.IsNullOrEmpty(executionStatus)) return null;
            var nl = executionStatus.IndexOf('\n');
            return nl > 0 ? executionStatus[(nl + 1)..] : null;
        }
    }

    private MarkdownControl MarkdownVm => new MarkdownControl(messageText ?? "")
        .WithStyle("background: transparent;");

    protected override void BindData()
    {
        base.BindData();
        DataBind(ViewModel.Text, x => x.messageText);
        DataBind(ViewModel.IsExecuting, x => x.isExecuting);
        DataBind(ViewModel.ExecutionStatus, x => x.executionStatus);
        DataBind(ViewModel.ToolCalls, x => x.toolCalls);
    }

    private void OnCancelClick()
    {
        if (!string.IsNullOrEmpty(ViewModel.ThreadPath) && Stream != null)
        {
            OnClick();
        }
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
