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

    private bool ShowSpinner => isExecuting && string.IsNullOrEmpty(messageText);
    private bool ShowExecutingIndicator => isExecuting && !string.IsNullOrEmpty(messageText);
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
}
