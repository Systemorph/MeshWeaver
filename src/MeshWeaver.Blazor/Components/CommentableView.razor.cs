using System.Reactive.Linq;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Layout;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace MeshWeaver.Blazor.Components;

/// <summary>
/// Blazor view for <see cref="CommentableControl"/>: renders the wrapped content and adds the
/// select-to-comment affordance to it, so ANY rendered text — a social post, an HTML block, a
/// composed stack — gets the anchored comments that used to be markdown-only.
///
/// <para>The selection listener and the anchoring are both shared, not re-implemented: the
/// floating button comes from <c>selectionComment.js</c> (the module extracted from
/// <c>CollaborativeMarkdownView</c>) and the satellite from
/// <see cref="AnchoredComment.Build"/>.</para>
/// </summary>
public partial class CommentableView
{
    [Inject] private IJSRuntime JS { get; set; } = null!;

    private ElementReference containerRef;
    private IJSObjectReference? jsModule;
    private IJSObjectReference? selectionHandle;
    private DotNetObjectReference<CommentableView>? dotNetRef;
    private bool selectionInitialized;

    // Bound members must be PROPERTIES — DataBind resolves the selector as a MemberExpression
    // onto the view's property, not a field.
    private string? BoundNodePath { get; set; }
    private string? BoundAnchorText { get; set; }
    private bool BoundCanComment { get; set; }
    private long BoundVersion { get; set; }
    private string currentAuthor = "";

    private bool showCommentInput;
    private string pendingSelectionText = "";
    private string pendingStartFragment = "";
    private string pendingEndFragment = "";
    private string pendingCommentText = "";

    /// <inheritdoc />
    protected override void BindData()
    {
        base.BindData();
        DataBind(ViewModel.NodePath, x => x.BoundNodePath);
        DataBind(ViewModel.AnchorText, x => x.BoundAnchorText);
        DataBind(ViewModel.CanComment, x => x.BoundCanComment);

        var accessService = Hub.ServiceProvider.GetService<AccessService>();
        currentAuthor = (accessService?.Context ?? accessService?.CircuitContext)?.Name ?? "";

        // Track the node's LIVE version: an anchored comment is stamped with the version it was
        // captured against, which is what lets CommentRendering tell "the capture still holds"
        // from "re-anchor against the newer text". Stamping 0 (the unset default) makes every
        // anchored comment look like it was never versioned. The markdown view tracks it the same
        // way — no .Take(1): the binding must follow later edits, not freeze on the first value.
        if (!string.IsNullOrEmpty(BoundNodePath))
            AddBinding(Hub.GetMeshNodeStream(BoundNodePath)
                .Where(node => node is not null)
                .Subscribe(node => BoundVersion = node!.Version));
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await base.OnAfterRenderAsync(firstRender);

        // Nothing to anchor to, or the viewer may not comment ⇒ the content renders untouched:
        // no module load, no listener, no button.
        if (!BoundCanComment || string.IsNullOrEmpty(BoundNodePath) || selectionInitialized)
            return;

        selectionInitialized = true;
        jsModule ??= await JS.InvokeAsync<IJSObjectReference>(
            "import", "./_content/MeshWeaver.Blazor/Components/selectionComment.js");
        dotNetRef = DotNetObjectReference.Create(this);
        selectionHandle = await jsModule.InvokeAsync<IJSObjectReference>(
            "enable", containerRef, ".commentable-content", dotNetRef);
    }

    /// <summary>
    /// Called from JS when the reader picks "Comment" on a selection: opens the input dialog.
    /// The comment is only created on submit — see <see cref="SubmitComment"/>.
    /// </summary>
    /// <param name="selectedText">The selected text as rendered.</param>
    /// <param name="startFragment">Leading words of the selection.</param>
    /// <param name="endFragment">Trailing words of the selection.</param>
    [JSInvokable]
    public Task OnCommentFromSelection(string selectedText, string startFragment, string endFragment)
    {
        if (string.IsNullOrWhiteSpace(selectedText))
            return Task.CompletedTask;

        pendingSelectionText = selectedText;
        pendingStartFragment = startFragment ?? "";
        pendingEndFragment = endFragment ?? "";
        pendingCommentText = "";
        showCommentInput = true;
        InvokeAsync(StateHasChanged);
        return Task.CompletedTask;
    }

    private void CancelComment()
    {
        showCommentInput = false;
        pendingSelectionText = "";
        pendingCommentText = "";
    }

    private void SubmitComment()
    {
        if (string.IsNullOrWhiteSpace(pendingSelectionText) || string.IsNullOrEmpty(BoundNodePath))
            return;

        var node = AnchoredComment.Build(
            BoundNodePath, BoundAnchorText, pendingSelectionText,
            pendingStartFragment, pendingEndFragment,
            currentAuthor, pendingCommentText, BoundVersion);

        showCommentInput = false;
        pendingSelectionText = "";
        pendingCommentText = "";
        pendingStartFragment = "";
        pendingEndFragment = "";

        // The canonical mutation surface — no request/response. The node's Comments area is
        // subscribed to the satellite partition, so the new comment appears there on its own.
        Hub.ServiceProvider.GetRequiredService<IMeshService>().CreateNode(node)
            .Subscribe(
                _ => { },
                ex => Logger.LogWarning(ex, "Failed to create comment on {Path}", BoundNodePath));
    }

    private static string Truncate(string text, int max) =>
        string.IsNullOrEmpty(text) || text.Length <= max ? text : text[..max] + "…";

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (jsModule is not null && selectionHandle is not null)
        {
            // Best-effort: the circuit may already be gone (navigation, disconnect), in which case
            // the DOM this would clean up went with it.
            try { await jsModule.InvokeVoidAsync("disable", selectionHandle); }
            catch (JSDisconnectedException) { }
            catch (ObjectDisposedException) { }
        }
        dotNetRef?.Dispose();
        await base.DisposeAsync();
    }
}
