using Markdig;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Markdown.Collaboration;
using AnnotationType = MeshWeaver.Markdown.AnnotationType;

namespace MeshWeaver.Blazor.Components;

public partial class CollaborativeMarkdownView
{
    [Inject] private IJSRuntime JS { get; set; } = null!;

    private ElementReference containerRef;
    private ElementReference contentRef;

    private IJSObjectReference? jsModule;
    private DotNetObjectReference<CollaborativeMarkdownView>? dotNetRef;

    // Bound properties
    private string RawContent = "";
    private string? BoundNodePath;
    private string? BoundHubAddress;
    private bool BoundCanComment;
    private bool BoundCanEdit;

    // Current user info
    private string CurrentAuthor = "";

    // View state (local Blazor state)
    private string CurrentViewMode = "Markup";
    private string CurrentCommentFilter = "Unresolved";
    private string? activeAnnotationId;
    private bool jsInitialized;
    private bool commentSelectionInitialized;

    // Comment input state
    private bool _showCommentInput;
    private string _pendingSelectionText = "";
    private string _pendingCommentText = "";
    private bool _showPageCommentInput;
    private string _pageCommentText = "";

    // Parsed data
    private string? _processedHtml;
    private List<ParsedAnnotation> Annotations = new();

    // Comment data cache (markerId -> Comment), populated by mesh query subscription
    private Dictionary<string, Comment> commentNodes = new();
    // Comment path cache (markerId -> MeshNode path), for resolve/delete operations
    private Dictionary<string, string> commentPaths = new();

    private bool HasAnnotations => Annotations.Any(a => a.Type != AnnotationType.Comment);

    private bool HasCommentAnnotations => Annotations.Any(a => a.Type == AnnotationType.Comment);

    private List<ParsedAnnotation> FilteredAnnotations => CurrentCommentFilter switch
    {
        "None" => Annotations.Where(a => a.Type != AnnotationType.Comment).ToList(),
        "All" => Annotations,
        // "Unresolved" (default): show non-comment annotations + active comments only
        _ => Annotations.Where(a =>
            a.Type != AnnotationType.Comment ||
            !commentNodes.TryGetValue(a.Id, out var comment) ||
            comment.Status == CommentStatus.Active).ToList()
    };

    private bool HasSideAnnotations =>
        CurrentViewMode == "Markup" && FilteredAnnotations.Count > 0;

    private string ViewModeClass => CurrentViewMode switch
    {
        "HideMarkup" => "annotations-hide-markup",
        "Original" => "annotations-original-view",
        _ => ""
    };

    protected override void BindData()
    {
        base.BindData();

        BoundNodePath = ViewModel.NodePath;
        BoundHubAddress = ViewModel.HubAddress;
        BoundCanComment = ViewModel.CanComment;
        BoundCanEdit = ViewModel.CanEdit;

        // Resolve current user for comment metadata
        var accessService = Hub.ServiceProvider.GetService<AccessService>();
        CurrentAuthor = (accessService?.Context ?? accessService?.CircuitContext)?.Name ?? "";

        // Subscribe to value changes reactively so we re-process content
        // when the hub updates (e.g., after accept/reject)
        var valuePointer = ViewModel.Value as JsonPointerReference;
        if (Stream != null && valuePointer != null)
        {
            AddBinding(Stream
                .DataBind<string>(valuePointer, DataContext)
                .Subscribe(value =>
                {
                    if (value != null && value != RawContent)
                    {
                        RawContent = value;
                        ProcessContent();
                        InvokeAsync(StateHasChanged);
                    }
                }));
        }
        else
        {
            // Fallback: one-time bind
            DataBind(ViewModel.Value, x => x.RawContent, defaultValue: "");
        }

        // Subscribe to comment nodes to track resolved/active status for filtering
        SubscribeToCommentStatuses();

        ProcessContent();
    }

    private void SubscribeToCommentStatuses()
    {
        if (string.IsNullOrEmpty(BoundNodePath))
            return;

        var meshQuery = Hub.ServiceProvider.GetService<IMeshService>();
        if (meshQuery == null)
            return;

        var query = MeshQueryRequest.FromQuery(
            $"namespace:{BoundNodePath}/_Comment nodeType:Comment");

        AddBinding(meshQuery.ObserveQuery<MeshNode>(query)
            .Scan(new List<MeshNode>(), (list, change) =>
            {
                if (change.ChangeType == QueryChangeType.Initial || change.ChangeType == QueryChangeType.Reset)
                    return change.Items.ToList();
                foreach (var item in change.Items)
                {
                    if (change.ChangeType == QueryChangeType.Added)
                        list.Add(item);
                    else if (change.ChangeType == QueryChangeType.Removed)
                        list.RemoveAll(n => n.Path == item.Path);
                    else if (change.ChangeType == QueryChangeType.Updated)
                    {
                        list.RemoveAll(n => n.Path == item.Path);
                        list.Add(item);
                    }
                }
                return list;
            })
            .Subscribe(list =>
            {
                var withMarker = list
                    .Where(n => n.Content is Comment c && !string.IsNullOrEmpty(c.MarkerId))
                    .DistinctBy(n => ((Comment)n.Content!).MarkerId!);
                commentNodes = withMarker.ToDictionary(
                    n => ((Comment)n.Content!).MarkerId!,
                    n => (Comment)n.Content!);
                commentPaths = withMarker.ToDictionary(
                    n => ((Comment)n.Content!).MarkerId!,
                    n => n.Path);
                InvokeAsync(StateHasChanged);
            }));
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            jsModule = await JS.InvokeAsync<IJSObjectReference>(
                "import", "./_content/MeshWeaver.Blazor/Components/CollaborativeMarkdownView.razor.js");
        }

        if (jsModule != null && !jsInitialized)
        {
            jsInitialized = true;
            await jsModule.InvokeVoidAsync("init", containerRef);
        }
        else if (jsModule != null && jsInitialized && HasSideAnnotations)
        {
            await jsModule.InvokeVoidAsync("positionCards");
        }

        // Enable comment-from-selection — always initialize so the floating button appears.
        // Permissions are checked server-side when actually creating the comment.
        if (jsModule != null && !commentSelectionInitialized)
        {
            commentSelectionInitialized = true;
            dotNetRef = DotNetObjectReference.Create(this);
            await jsModule.InvokeVoidAsync("enableCommentSelection", containerRef, dotNetRef);
        }
    }

    private void ProcessContent()
    {
        if (string.IsNullOrEmpty(RawContent))
        {
            _processedHtml = "";
            Annotations = new();
            return;
        }

        // Extract annotations for the side panel
        Annotations = AnnotationParser.ExtractAnnotations(RawContent);

        // Transform content based on view mode
        var content = CurrentViewMode switch
        {
            "HideMarkup" => AnnotationMarkdownExtension.GetAcceptedContent(RawContent),
            "Original" => AnnotationMarkdownExtension.GetRejectedContent(RawContent),
            _ => RawContent
        };

        // Render markdown to HTML with annotation spans
        _processedHtml = RenderMarkdown(content);
    }

    /// <summary>
    /// Renders the processed HTML using the shared MarkdownHtmlRenderer,
    /// which dispatches to Blazor components for UCR links, layout areas,
    /// code blocks, mermaid diagrams, math blocks, and SVG.
    /// </summary>
    private void RenderContentHtml(RenderTreeBuilder builder)
    {
        if (string.IsNullOrEmpty(_processedHtml))
            return;

        var renderer = new MarkdownHtmlRenderer(Mode, Stream);
        renderer.ShowReferencesSection = true;
        renderer.RenderHtml(builder, _processedHtml);
    }

    private string RenderMarkdown(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "";

        // Transform annotation markers to HTML spans before markdown rendering
        var transformed = AnnotationMarkdownExtension.TransformAnnotations(content);

        // Use the standard pipeline that includes LayoutAreaMarkdownExtension for @@ syntax.
        // Pass BoundNodePath so relative @references resolve correctly.
        var pipeline = MeshWeaver.Markdown.MarkdownExtensions.CreateMarkdownPipeline(null, BoundNodePath);
        return Markdig.Markdown.ToHtml(transformed, pipeline);
    }

    // View mode
    private void OnViewModeChanged(string mode)
    {
        CurrentViewMode = mode;
        ProcessContent();
        StateHasChanged();
    }

    // Comment filter
    private void OnCommentFilterChanged(string filter)
    {
        CurrentCommentFilter = filter;
        ProcessContent();
        StateHasChanged();
    }

    // Accept/Reject individual changes
    private async Task OnAcceptChange(string changeId)
    {
        var newContent = AnnotationMarkdownExtension.AcceptChange(RawContent, changeId);
        var previousContent = RawContent;
        UpdateContentLocally(newContent);
        if (!await PostContentUpdateAsync(newContent))
            RevertContent(previousContent);
    }

    private async Task OnRejectChange(string changeId)
    {
        var newContent = AnnotationMarkdownExtension.RejectChange(RawContent, changeId);
        var previousContent = RawContent;
        UpdateContentLocally(newContent);
        if (!await PostContentUpdateAsync(newContent))
            RevertContent(previousContent);
    }

    // Accept/Reject all
    private async Task OnAcceptAll()
    {
        var newContent = AnnotationMarkdownExtension.GetAcceptedContent(RawContent);
        var previousContent = RawContent;
        UpdateContentLocally(newContent);
        if (!await PostContentUpdateAsync(newContent))
            RevertContent(previousContent);
    }

    private async Task OnRejectAll()
    {
        var newContent = AnnotationMarkdownExtension.GetRejectedContent(RawContent);
        var previousContent = RawContent;
        UpdateContentLocally(newContent);
        if (!await PostContentUpdateAsync(newContent))
            RevertContent(previousContent);
    }

    /// <summary>
    /// Update local state immediately for responsive UI, then let the hub echo back.
    /// </summary>
    private void UpdateContentLocally(string newContent)
    {
        RawContent = newContent;
        ProcessContent();
        StateHasChanged();
    }

    // Post content update to hub and return success/failure
    private async Task<bool> PostContentUpdateAsync(string newContent)
    {
        if (string.IsNullOrEmpty(BoundHubAddress))
            return false;

        // Split path into Id + Namespace so the workspace matches the existing node by key (Id).
        var path = BoundNodePath ?? "";
        var lastSlash = path.LastIndexOf('/');
        var (id, ns) = lastSlash > 0
            ? (path[(lastSlash + 1)..], path[..lastSlash])
            : (path, (string?)null);

        var nodeUpdate = new MeshNode(id, ns)
        {
            NodeType = "Markdown",
            Content = new MarkdownContent { Content = newContent }
        };

        try
        {
            var response = await Hub.AwaitResponse(
                new DataChangeRequest { ChangedBy = Stream?.ClientId }.WithUpdates(nodeUpdate),
                o => o.WithTarget(new Address(BoundHubAddress)),
                default);

            if (response.Message is DataChangeResponse dcr && dcr.Status != DataChangeStatus.Committed)
                return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    private void RevertContent(string previousContent)
    {
        RawContent = previousContent;
        ProcessContent();
        StateHasChanged();
    }

    // Highlight an annotation (Blazor state for card, JS for inline span)
    private async Task HighlightAnnotation(string annotationId)
    {
        activeAnnotationId = annotationId;
        StateHasChanged();
        if (jsModule != null)
            await jsModule.InvokeVoidAsync("highlightAnnotation", annotationId);
    }

    /// <summary>
    /// Called from JS when user selects text and clicks the "Comment" button.
    /// Shows the comment input form instead of creating immediately.
    /// </summary>
    [JSInvokable]
    public Task OnCommentFromSelection(string selectedText)
    {
        if (string.IsNullOrWhiteSpace(selectedText) || string.IsNullOrEmpty(RawContent))
            return Task.CompletedTask;

        _pendingSelectionText = selectedText;
        _pendingCommentText = "";
        _showCommentInput = true;
        InvokeAsync(StateHasChanged);
        return Task.CompletedTask;
    }

    private void CancelSelectionComment()
    {
        _showCommentInput = false;
        _pendingSelectionText = "";
        _pendingCommentText = "";
    }

    private void SubmitSelectionComment()
    {
        if (string.IsNullOrWhiteSpace(_pendingSelectionText) || string.IsNullOrEmpty(BoundHubAddress))
            return;

        var selectedText = _pendingSelectionText;
        var commentText = _pendingCommentText;
        _showCommentInput = false;
        _pendingSelectionText = "";
        _pendingCommentText = "";

        // Fire-and-forget: Post + RegisterCallback (never await — deadlocks in Orleans)
        var delivery = Hub.Post(
            new CreateCommentRequest
            {
                DocumentId = BoundNodePath ?? "",
                SelectedText = selectedText,
                CommentText = commentText,
                Author = CurrentAuthor
            },
            o => o.WithTarget(new Address(BoundHubAddress)));

        if (delivery != null)
        {
            Hub.RegisterCallback<CreateCommentResponse>(delivery, response =>
            {
                if (!response.Message.Success)
                {
                    var logger = Hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("MeshWeaver.Blazor.CollaborativeMarkdownView");
                    logger?.LogWarning("[SubmitComment] FAILED: {Error}", response.Message.Error);
                }
                return response;
            });
        }
    }

    // Page-level comment (no text selection)
    private void StartPageComment()
    {
        _showPageCommentInput = true;
        _pageCommentText = "";
    }

    private void CancelPageComment()
    {
        _showPageCommentInput = false;
        _pageCommentText = "";
    }

    private void SubmitPageComment()
    {
        if (string.IsNullOrWhiteSpace(_pageCommentText) || string.IsNullOrEmpty(BoundHubAddress))
            return;

        var text = _pageCommentText;
        _showPageCommentInput = false;
        _pageCommentText = "";

        // Fire-and-forget: Post + RegisterCallback (never await)
        var delivery = Hub.Post(
            new CreateCommentRequest
            {
                DocumentId = BoundNodePath ?? "",
                CommentText = text,
                Author = CurrentAuthor
            },
            o => o.WithTarget(new Address(BoundHubAddress)));

        if (delivery != null)
        {
            Hub.RegisterCallback<CreateCommentResponse>(delivery, response =>
            {
                if (!response.Message.Success)
                {
                    var logger = Hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("MeshWeaver.Blazor.CollaborativeMarkdownView");
                    logger?.LogWarning("[SubmitPageComment] FAILED: {Error}", response.Message.Error);
                }
                return response;
            });
        }
    }

    // Comment helpers
    private Comment? GetCommentData(string markerId) =>
        commentNodes.TryGetValue(markerId, out var c) ? c : null;

    private bool IsResolved(string markerId) =>
        commentNodes.TryGetValue(markerId, out var c) && c.Status == CommentStatus.Resolved;

    private async Task ResolveComment(string markerId)
    {
        if (!commentPaths.TryGetValue(markerId, out var path) || string.IsNullOrEmpty(BoundHubAddress))
            return;
        var meshQuery = Hub.ServiceProvider.GetService<IMeshService>();
        if (meshQuery == null) return;
        var node = await meshQuery.QueryAsync<MeshNode>($"path:{path}").FirstOrDefaultAsync();
        if (node?.Content is Comment comment)
        {
            var updated = node with { Content = comment with { Status = CommentStatus.Resolved } };
            Hub.Post(new UpdateNodeRequest(updated), o => o.WithTarget(new Address(BoundHubAddress)));
        }
    }

    private async Task DeleteComment(string markerId)
    {
        if (!commentPaths.TryGetValue(markerId, out var path))
            return;
        var meshQuery = Hub.ServiceProvider.GetService<IMeshService>();
        if (meshQuery == null) return;
        await meshQuery.DeleteNodeAsync(path);
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "…";

    private static string GetTypeLabel(AnnotationType type) => type switch
    {
        AnnotationType.Comment => "Comment",
        AnnotationType.Insert => "Inserted",
        AnnotationType.Delete => "Deleted",
        _ => "Change"
    };

    internal static string FormatTimeAgo(DateTimeOffset dateTime)
    {
        var timeSpan = DateTimeOffset.UtcNow - dateTime;
        if (timeSpan.TotalMinutes < 1) return "just now";
        if (timeSpan.TotalMinutes < 60) return $"{(int)timeSpan.TotalMinutes}m ago";
        if (timeSpan.TotalHours < 24) return $"{(int)timeSpan.TotalHours}h ago";
        if (timeSpan.TotalDays < 7) return $"{(int)timeSpan.TotalDays}d ago";
        return dateTime.ToString("MMM d, yyyy");
    }

    public override async ValueTask DisposeAsync()
    {
        if (jsModule != null)
        {
            try { await jsModule.InvokeVoidAsync("dispose"); } catch { }
            await jsModule.DisposeAsync();
            jsModule = null;
        }
        dotNetRef?.Dispose();
        dotNetRef = null;
        await base.DisposeAsync();
    }
}
