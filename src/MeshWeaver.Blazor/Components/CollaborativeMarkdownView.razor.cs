using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Kernel;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using System.Reactive.Linq;
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

    // Interactive markdown (kernel execution). The "kernel address" is now a
    // per-view Activity MeshNode path (`{owner}/_Activity/markdown-{kernelId}`)
    // — its hub hosts the kernel handlers via ActivityNodeType.HubConfiguration.
    private readonly string _kernelId = Guid.NewGuid().AsString();
    private Address? _kernelAddress;
    private Address KernelAddress => _kernelAddress ??= ResolveActivityAddress();
    private bool _codeSubmitted;
    private IReadOnlyCollection<SubmitCodeRequest>? _codeSubmissions;

    private Address ResolveActivityAddress()
    {
        var ownerPath = BoundNodePath ?? Stream?.Owner?.Path;
        var activityNamespace = string.IsNullOrEmpty(ownerPath)
            ? "_Activity"
            : $"{ownerPath}/_Activity";
        return new Address($"{activityNamespace}/markdown-{_kernelId}");
    }

    // Parsed data
    private string? _processedHtml;
    private List<ParsedAnnotation> Annotations = new();

    // Memoize the last successful render so ProcessContent is idempotent — repeated
    // calls with the same (RawContent, CurrentViewMode) skip the Markdig parse entirely.
    // Saves ~1 ms per redundant call on a medium-sized doc.
    private string? _lastRenderedContent;
    private string? _lastRenderedMode;

    // Reads + writes go through IMeshNodeStreamCache — process-wide shared
    // handle per path. SaveContentAsync calls _cache.Update(BoundNodePath, fn)
    // to push edits through the same handle the read subscription is on. The
    // cache stays alive for the process; this view just holds a reference to
    // call Update later. See Doc/GUI/ItemTemplateMeshNodeStreamBinding.
    private IMeshNodeStreamCache? _cache;

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

        // Long-standing subscription to the per-node stream via the process-wide
        // IMeshNodeStreamCache. Hold the cache reference so SaveContentAsync can
        // call _cache.Update(BoundNodePath, fn) — writes go through the same
        // shared handle the read subscription is on, so every reader observes
        // the patch in order. See Doc/GUI/ItemTemplateMeshNodeStreamBinding.
        if (!string.IsNullOrEmpty(BoundNodePath))
        {
            try
            {
                _cache = Hub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
                AddBinding(Hub.GetMeshNodeStream(BoundNodePath)
                    .Where(node => node is not null)
                    .Select(node => MarkdownOverviewLayoutArea.GetMarkdownContent(node))
                    .DistinctUntilChanged()
                    .Subscribe(content =>
                    {
                        if (content != null && content != RawContent)
                        {
                            RawContent = content;
                            ProcessContent();
                            InvokeAsync(StateHasChanged);
                        }
                    }));
            }
            catch
            {
                // Cache service unavailable — fall back to one-time bind from
                // the ViewModel. No live updates in this mode.
                DataBind(ViewModel.Value, x => x.RawContent, defaultValue: "");
            }
        }
        else
        {
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

        AddBinding(meshQuery.Query<MeshNode>(query)
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

        // Submit code to the per-view Activity hub (which hosts the kernel).
        if (!_codeSubmitted && _codeSubmissions is { Count: > 0 })
        {
            _codeSubmitted = true;
            var meshService = Hub.ServiceProvider.GetRequiredService<IMeshService>();
            var ownerPath = BoundNodePath ?? Stream?.Owner?.Path;
            MarkdownViewLogic.CreateActivityAndSubmit(
                Hub, meshService, KernelAddress, ownerPath, _kernelId, _codeSubmissions);
        }
    }

    private void ProcessContent()
    {
        if (string.IsNullOrEmpty(RawContent))
        {
            _processedHtml = "";
            Annotations = new();
            _lastRenderedContent = "";
            _lastRenderedMode = CurrentViewMode;
            return;
        }

        // Skip the parse when nothing observable to the rendered HTML changed.
        // Filter changes route through OnCommentFilterChanged → StateHasChanged
        // (no re-parse), but other lifecycle events can still call here.
        if (RawContent == _lastRenderedContent
            && CurrentViewMode == _lastRenderedMode
            && _processedHtml != null)
            return;

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

        // Replace kernel address placeholder with actual kernel address
        if (_processedHtml != null && _codeSubmissions is { Count: > 0 })
            _processedHtml = MarkdownViewLogic.ReplaceKernelPlaceholder(_processedHtml, KernelAddress);

        _lastRenderedContent = RawContent;
        _lastRenderedMode = CurrentViewMode;
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
        {
            _codeSubmissions = null;
            return "";
        }

        // Transform annotation markers to HTML spans before markdown rendering,
        // then render with source-position data attributes (data-start/data-end) so JS
        // can map text selections back to markdown source positions.
        var transformed = AnnotationMarkdownExtension.TransformAnnotations(content);
        var pipeline = MarkdownExtensions.CreateMarkdownPipeline(null, BoundNodePath);
        var document = Markdig.Markdown.Parse(transformed, pipeline);

        _codeSubmissions = MarkdownViewLogic.ExtractCodeSubmissions(document);

        return SourceMapHtmlRenderer.RenderWithSourceMap(document, pipeline);
    }

    // View mode
    private void OnViewModeChanged(string mode)
    {
        CurrentViewMode = mode;
        ProcessContent();
        StateHasChanged();
    }

    // Comment filter only affects the FilteredAnnotations computed property
    // (side panel) — the rendered HTML is independent of it, so no re-parse.
    private void OnCommentFilterChanged(string filter)
    {
        CurrentCommentFilter = filter;
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

    // Post content update by syncing the full MeshNode via the remote stream and
    // editing its Content field directly — this preserves Name/Icon/Description/etc.
    // Using a partial MeshNode + DataChangeRequest fails key-mapping validation on the
    // hosting hub ("No key mapping is defined for type MeshNode").
    private Task<bool> PostContentUpdateAsync(string newContent)
    {
        if (string.IsNullOrEmpty(BoundHubAddress) || string.IsNullOrEmpty(BoundNodePath))
            return Task.FromResult(false);

        try
        {
            // Push the edit through the process-wide IMeshNodeStreamCache.Update.
            // The cache routes the write through the SAME shared handle the read
            // subscription is on, so the echo flows back to this view's Subscribe
            // and re-renders without an extra read. Other GUIs watching the same
            // path see the patch through their own subscriptions on the same handle.
            if (_cache == null || string.IsNullOrEmpty(BoundNodePath)) return Task.FromResult(false);
            Hub.GetMeshNodeStream(BoundNodePath).Update(current =>
                current with { Content = new MarkdownContent { Content = newContent } })
                .Subscribe(
                    _ => { },
                    ex => Hub.ServiceProvider.GetService<ILoggerFactory>()
                        ?.CreateLogger("MeshWeaver.Blazor.CollaborativeMarkdownView")
                        .LogWarning(ex, "Content save failed for {Path}", BoundNodePath));
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
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
    private string _pendingStartFragment = "";
    private string _pendingEndFragment = "";

    [JSInvokable]
    public Task OnCommentFromSelection(string selectedText, string startFragment, string endFragment)
    {
        if (string.IsNullOrWhiteSpace(selectedText) || string.IsNullOrEmpty(RawContent))
            return Task.CompletedTask;

        _pendingSelectionText = selectedText;
        _pendingStartFragment = startFragment ?? "";
        _pendingEndFragment = endFragment ?? "";
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
        var startFragment = _pendingStartFragment;
        var endFragment = _pendingEndFragment;
        _showCommentInput = false;
        _pendingSelectionText = "";
        _pendingCommentText = "";
        _pendingStartFragment = "";
        _pendingEndFragment = "";

        // Fire-and-forget: Post + RegisterCallback (never await — deadlocks in Orleans)
        var delivery = Hub.Post(
            new CreateCommentRequest
            {
                DocumentId = BoundNodePath ?? "",
                SelectedText = selectedText,
                StartFragment = startFragment,
                EndFragment = endFragment,
                CommentText = commentText,
                Author = CurrentAuthor
            },
            o => o.WithTarget(new Address(BoundHubAddress)));

        if (delivery != null)
        {
            Hub.Observe(delivery)
                .Subscribe(
                    response =>
                    {
                        if (response.Message is CreateCommentResponse { Success: false } resp)
                        {
                            var logger = Hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("MeshWeaver.Blazor.CollaborativeMarkdownView");
                            logger?.LogWarning("[SubmitComment] FAILED: {Error}", resp.Error);
                        }
                    },
                    ex =>
                    {
                        var logger = Hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("MeshWeaver.Blazor.CollaborativeMarkdownView");
                        logger?.LogWarning(ex, "[SubmitComment] FAILED");
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
            Hub.Observe(delivery)
                .Subscribe(
                    response =>
                    {
                        if (response.Message is CreateCommentResponse { Success: false } resp)
                        {
                            var logger = Hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("MeshWeaver.Blazor.CollaborativeMarkdownView");
                            logger?.LogWarning("[SubmitPageComment] FAILED: {Error}", resp.Error);
                        }
                    },
                    ex =>
                    {
                        var logger = Hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("MeshWeaver.Blazor.CollaborativeMarkdownView");
                        logger?.LogWarning(ex, "[SubmitPageComment] FAILED");
                    });
        }
    }

    // Comment helpers
    private Comment? GetCommentData(string markerId) =>
        commentNodes.TryGetValue(markerId, out var c) ? c : null;

    private bool IsResolved(string markerId) =>
        commentNodes.TryGetValue(markerId, out var c) && c.Status == CommentStatus.Resolved;

    private void ResolveComment(string markerId)
    {
        if (!commentPaths.TryGetValue(markerId, out var path))
            return;
        // Write through the shared cache — the lambda fires against the live
        // MeshNode the cache holds, no separate Read → Post round-trip needed.
        Hub.GetMeshNodeStream(path).Update(n =>
        {
            if (n.Content is not Comment c) return n;
            return n with { Content = c with { Status = CommentStatus.Resolved } };
        }).Subscribe(
            _ => { },
            ex => Hub.ServiceProvider.GetService<ILoggerFactory>()
                ?.CreateLogger("MeshWeaver.Blazor.CollaborativeMarkdownView")
                .LogWarning(ex, "Comment resolve failed for {Path}", path));
    }

    private void DeleteComment(string markerId)
    {
        if (!commentPaths.TryGetValue(markerId, out var path))
            return;
        var meshQuery = Hub.ServiceProvider.GetService<IMeshService>();
        meshQuery?.DeleteNode(path).Subscribe(
            _ => { },
            _ => { });
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
