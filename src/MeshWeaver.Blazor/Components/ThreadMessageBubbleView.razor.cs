using MeshWeaver.Layout;

namespace MeshWeaver.Blazor.Components;

public partial class ThreadMessageBubbleView : BlazorView<ThreadMessageBubbleControl, ThreadMessageBubbleView>
{
    private bool IsUser => ViewModel.Role.Equals("user", StringComparison.OrdinalIgnoreCase);

    private string? messageText;
    private bool isExecuting;
    private string? executionStatus;

    private bool ShowSpinner => isExecuting && string.IsNullOrEmpty(messageText);
    private bool ShowExecutingIndicator => isExecuting && !string.IsNullOrEmpty(messageText);

    private MarkdownControl MarkdownVm => new MarkdownControl(messageText ?? "")
        .WithStyle("background: transparent;");

    protected override void BindData()
    {
        base.BindData();
        DataBind(ViewModel.Text, x => x.messageText);
        DataBind(ViewModel.IsExecuting, x => x.isExecuting);
        DataBind(ViewModel.ExecutionStatus, x => x.executionStatus);
    }

    private void OnCancelClick()
    {
        if (!string.IsNullOrEmpty(ViewModel.ThreadPath) && Stream != null)
        {
            OnClick();
        }
    }
}
