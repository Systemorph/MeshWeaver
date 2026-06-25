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
using TrackedChange = MeshWeaver.Mesh.TrackedChange;
using TrackedChangeType = MeshWeaver.Mesh.TrackedChangeType;
using TrackedChangeStatus = MeshWeaver.Mesh.TrackedChangeStatus;

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

    // The document node's current hub version — stamped onto new comments and used to decide
    // whether a comment's stored offsets are still valid or it must be re-anchored at render time.
    private long _docVersion;

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

    // Interactive markdown (kernel execution). The "kernel address" is a per-view
    // Activity MeshNode path (`{userHome}/_Activity/markdown-{kernelId}`) — its hub
    // hosts the kernel handlers via ActivityNodeType.HubConfiguration. 🚨 Anchored at
    // the VIEWING USER's home partition (KernelOwnerPath), NOT the doc node being
    // viewed: the code blocks execute AS the viewer, so the activity must live where
    // the viewer can legitimately Create (creating it under a read-only doc partition
    // was DENIED and the kernel hung — the bug this fixes).
    private readonly string _kernelId = Guid.NewGuid().AsString();
    private Address? _kernelAddress;
    private Address KernelAddress => _kernelAddress ?? ResolveActivityAddress();
    private bool _codeSubmitted;
    // Flipped (via CreateActivityAndSubmit's onReady) once the per-view Activity is created + routable.
    // Gates the LIVE kernel-area embed in RenderContentHtml so the GUI never subscribes to a
    // not-yet-created {owner}/_Activity/markdown-{id} (the subscribe-before-create NotFound storm).
    private bool _kernelReady;
    private IReadOnlyCollection<SubmitCodeRequest>? _codeSubmissions;

    private Address ResolveActivityAddress()
    {
        var ownerPath = KernelOwnerPath();
        var activityNamespace = string.IsNullOrEmpty(ownerPath)
            ? "_Activity"
            : $"{ownerPath}/_Activity";
        var address = new Address($"{activityNamespace}/markdown-{_kernelId}");
        // Memoise only once a real user home resolves. A prerender pass (no circuit user yet) would
        // otherwise cache an ownerless `_Activity/markdown-{id}` and the deferred live embed would
        // target it forever. Until a real owner is available we recompute cheaply on each call.
        if (!string.IsNullOrEmpty(ownerPath))
            _kernelAddress = address;
        return address;
    }

    // The viewing user's writable home partition — their AccessContext ObjectId (via the durable
    // circuit user, ResolveCircuitUser). The per-view interactive-kernel Activity is anchored here,
    // NOT under the (possibly read-only) doc node being viewed: the code blocks run AS this user, so
    // the activity must live where they can Create. The viewer's OWN partition qualifies —
    // RlsNodeValidator grants any write under `{userId}/…` and PermissionEvaluator auto-grants Admin
    // at scope == userId. Null in SSR/prerender or when no real user is set (system/hub principals are
    // filtered out by ResolveCircuitUser); the kernel result areas then render the non-subscribing
    // "unavailable" notice instead of storming a path nobody can create.
    private string? KernelOwnerPath() => ResolveCircuitUser()?.ObjectId;

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
    // Comments resolved against the CURRENT text (effective ranges set), produced by ProcessContent.
    private IReadOnlyList<Comment> _resolvedComments = Array.Empty<Comment>();

    // Tracked-change satellites (markerId -> change / path), populated by the _Tracking subscription.
    private Dictionary<string, TrackedChange> changeNodes = new();
    private Dictionary<string, string> changePaths = new();
    // Changes resolved against the CURRENT text (effective ranges set), produced by ProcessContent.
    private IReadOnlyList<TrackedChange> _resolvedChanges = Array.Empty<TrackedChange>();

    // Pending tracked changes drive the diff view + Accept All / Reject All.
    private bool HasAnnotations => changeNodes.Values.Any(c => c.Status == TrackedChangeStatus.Pending);

    private bool HasCommentAnnotations => commentNodes.Count > 0;

    // Comments shown in the side panel (anchored comments are sourced from the satellites, not from
    // markers in the text). The status filter mirrors the dropdown.
    private IReadOnlyList<Comment> SidebarComments => CurrentCommentFilter switch
    {
        "None" => Array.Empty<Comment>(),
        "All" => _resolvedComments,
        _ => _resolvedComments.Where(c => c.Status == CommentStatus.Active).ToList()
    };

    // Pending tracked changes shown as cards in the side panel.
    private IReadOnlyList<TrackedChange> SidebarChanges =>
        _resolvedChanges.Where(c => c.Status == TrackedChangeStatus.Pending).ToList();

    private bool HasSideAnnotations => SidebarComments.Count > 0 || SidebarChanges.Count > 0;

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
                    .Subscribe(node =>
                    {
                        // Track the document version so new comments are stamped against it and the
                        // render-time anchoring can tell "still valid" from "re-anchor needed".
                        _docVersion = node!.Version;
                        var content = MarkdownOverviewLayoutArea.GetMarkdownContent(node);
                        var changed = content != RawContent;
                        if (changed)
                            RawContent = content;
                        // Always re-run: ProcessContent is memoised on the decorated output, so a bare
                        // version bump with no content/comment change is a cheap no-op.
                        ProcessContent();
                        if (changed)
                            InvokeAsync(StateHasChanged);
                    },
                    ex => SurfaceError(ex, $"Loading document {BoundNodePath}")));
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
        SubscribeToChanges();

        ProcessContent();
    }

    private void SubscribeToChanges()
    {
        if (string.IsNullOrEmpty(BoundNodePath))
            return;

        var meshQuery = Hub.ServiceProvider.GetService<IMeshService>();
        if (meshQuery == null)
            return;

        var query = MeshQueryRequest.FromQuery(
            $"namespace:{BoundNodePath}/{AnnotationExtensions.TrackingPartition} nodeType:{MeshWeaver.Graph.Configuration.TrackedChangeNodeType.NodeType}");

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
                    .Where(n => n.Content is TrackedChange c && !string.IsNullOrEmpty(c.MarkerId))
                    .DistinctBy(n => ((TrackedChange)n.Content!).MarkerId!);
                changeNodes = withMarker.ToDictionary(
                    n => ((TrackedChange)n.Content!).MarkerId!,
                    n => (TrackedChange)n.Content!);
                changePaths = withMarker.ToDictionary(
                    n => ((TrackedChange)n.Content!).MarkerId!,
                    n => n.Path);
                // Tracked changes are overlaid as a diff view from these satellites — re-derive.
                ProcessContent();
                InvokeAsync(StateHasChanged);
            },
            ex => SurfaceError(ex, "Loading document changes")));
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
                // Comments are overlaid onto the rendered document from these satellites, so a change
                // in the set must re-derive the inline highlights, not just the sidebar status.
                ProcessContent();
                InvokeAsync(StateHasChanged);
            },
            ex => SurfaceError(ex, "Loading document comments")));
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
            var ownerPath = KernelOwnerPath();
            if (string.IsNullOrEmpty(ownerPath))
            {
                // No resolvable viewing user (SSR/prerender, or an unauthenticated/system/hub
                // principal) → nowhere the viewer can legitimately Create the kernel activity. Do NOT
                // fall back to a bare `_Activity/markdown-*` (it would NotFound-storm the router) or to
                // the read-only doc partition (the create is denied — the bug this fixes). The result
                // areas were neutralised into a notice in ProcessContent; log and skip submission.
                Hub.ServiceProvider.GetService<ILoggerFactory>()?
                    .CreateLogger("MarkdownExecution")
                    .LogWarning(
                        "Collaborative markdown view {Kernel}: skipping interactive code execution — no resolvable viewing user to anchor the kernel activity.",
                        _kernelId);
                return;
            }
            var meshService = Hub.ServiceProvider.GetRequiredService<IMeshService>();
            MarkdownViewLogic.CreateActivityAndSubmit(
                Hub, meshService, KernelAddress, ownerPath, _kernelId, _codeSubmissions,
                onReady: OnKernelReady);
        }
    }

    // Invoked (off the Blazor renderer) once the per-view Activity is created + routable. Flip the
    // gate and re-render so RenderContentHtml embeds the LIVE kernel area — the LayoutAreaView(s)
    // then subscribe to an address that already exists (no NotFound storm).
    private void OnKernelReady()
    {
        if (IsViewDisposed || _kernelReady) return;
        InvokeAsync(() =>
        {
            if (IsViewDisposed) return;
            _kernelReady = true;
            StateHasChanged();
        });
    }

    private void ProcessContent()
    {
        // The document text is kept CLEAN. Comments and tracked changes are satellites carrying a
        // captured range (Start/Length/Version/AnchorText); each is recomputed against the current
        // clean text via the version delta and overlaid as a transient span for this render only —
        // comments as highlights, changes as a git-diff view (insertions/deletions/replacements).
        var clean = MarkdownAnnotationParser.StripAllMarkers(RawContent ?? "");
        var commentsForRender = commentNodes.Values
            .Where(c => !string.IsNullOrEmpty(c.MarkerId) && !string.IsNullOrEmpty(c.HighlightedText))
            .ToArray();
        _resolvedComments = commentsForRender.Length > 0
            ? CommentRendering.ResolveAll(commentsForRender, clean, _docVersion)
            : Array.Empty<Comment>();
        var changesForRender = changeNodes.Values
            .Where(c => !string.IsNullOrEmpty(c.MarkerId))
            .ToArray();
        _resolvedChanges = changesForRender.Length > 0
            ? ChangeRendering.ResolveAll(changesForRender, clean, _docVersion)
            : Array.Empty<TrackedChange>();
        var decorated = _resolvedComments.Count > 0 || _resolvedChanges.Any(c => c.Status == TrackedChangeStatus.Pending)
            ? CollaborativeRenderer.Decorate(clean, _resolvedComments, _resolvedChanges)
            : clean;

        if (string.IsNullOrEmpty(decorated))
        {
            _processedHtml = "";
            Annotations = new();
            _lastRenderedContent = "";
            _lastRenderedMode = CurrentViewMode;
            return;
        }

        // Skip the parse when nothing observable to the rendered HTML changed. The memo key is the
        // DECORATED content, so a change in the comment overlay (not just RawContent) re-parses.
        if (decorated == _lastRenderedContent
            && CurrentViewMode == _lastRenderedMode
            && _processedHtml != null)
            return;

        // Extract annotations for the side panel
        Annotations = AnnotationParser.ExtractAnnotations(decorated);

        // Transform content based on view mode
        var content = CurrentViewMode switch
        {
            "HideMarkup" => AnnotationMarkdownExtension.GetAcceptedContent(decorated),
            "Original" => AnnotationMarkdownExtension.GetRejectedContent(decorated),
            _ => decorated
        };

        // Render markdown to HTML with annotation spans. NB: the kernel result-area placeholder is
        // left UNRESOLVED here — the live (subscribing) vs. "starting" placeholder vs. "no owner"
        // notice decision is made at RENDER time in RenderContentHtml (gated on _kernelReady), so
        // flipping _kernelReady after the activity is routable re-renders with the live area without
        // re-parsing. See RenderContentHtml + OnAfterRenderAsync.
        _processedHtml = RenderMarkdown(content);

        _lastRenderedContent = decorated;
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

        // Resolve the kernel result-area placeholder at render time: a live (subscribing) area only
        // once the activity is routable (_kernelReady), a non-subscribing "starting" placeholder
        // until then, or a "no owner" notice. This gate prevents the subscribe-before-create storm.
        var html = _codeSubmissions is { Count: > 0 }
            ? MarkdownViewLogic.RenderKernelResultAreas(
                _processedHtml, KernelOwnerPath(), _kernelReady, KernelAddress)
            : _processedHtml;

        var renderer = new MarkdownHtmlRenderer(Mode, Stream);
        renderer.ShowReferencesSection = true;
        renderer.RenderHtml(builder, html);
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

    // Accept/Reject individual changes — operate on the satellites. Accept applies the suggested
    // text to the document at the change's effective range and drops the satellite; Reject just drops
    // the satellite (the document is left unchanged).
    private void OnAcceptChange(string changeId)
    {
        if (!changeNodes.TryGetValue(changeId, out var change))
            return;
        var clean = MarkdownAnnotationParser.StripAllMarkers(RawContent ?? "");
        var resolved = ChangeRendering.ResolveEffective(change, clean, _docVersion);
        var newClean = ChangeRendering.Apply(clean, resolved);
        ApplyDocContent(newClean, () => DeleteChangeNode(changeId));
    }

    private void OnRejectChange(string changeId) => DeleteChangeNode(changeId);

    // Accept/Reject all pending changes
    private void OnAcceptAll()
    {
        var pending = changeNodes.Values.Where(c => c.Status == TrackedChangeStatus.Pending).ToList();
        if (pending.Count == 0)
            return;
        var clean = MarkdownAnnotationParser.StripAllMarkers(RawContent ?? "");
        var newClean = ChangeRendering.ApplyAll(clean, pending, _docVersion);
        ApplyDocContent(newClean, () =>
        {
            foreach (var c in pending)
                if (c.MarkerId is { } id) DeleteChangeNode(id);
        });
    }

    private void OnRejectAll()
    {
        foreach (var c in changeNodes.Values.Where(c => c.Status == TrackedChangeStatus.Pending).ToList())
            if (c.MarkerId is { } id) DeleteChangeNode(id);
    }

    // Write the document's clean content through the shared stream, then run onApplied (drop the
    // accepted satellite(s)). The doc node keeps its other fields (Name/Authors/Tags/...).
    private void ApplyDocContent(string newClean, Action onApplied)
    {
        if (string.IsNullOrEmpty(BoundNodePath))
            return;
        Hub.GetMeshNodeStream(BoundNodePath).Update(node =>
                node.Content is MarkdownContent md
                    ? node with { Content = md with { Content = newClean } }
                    : node)
            .Subscribe(
                _ => onApplied(),
                ex => Hub.ServiceProvider.GetService<ILoggerFactory>()
                    ?.CreateLogger("MeshWeaver.Blazor.CollaborativeMarkdownView")
                    .LogWarning(ex, "Accept-change content update failed for {Path}", BoundNodePath));
    }

    private void DeleteChangeNode(string changeId)
    {
        if (changePaths.TryGetValue(changeId, out var path))
            Hub.ServiceProvider.GetService<IMeshService>()?.DeleteNode(path).Subscribe(_ => { }, _ => { });
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
        if (string.IsNullOrWhiteSpace(_pendingSelectionText) || string.IsNullOrEmpty(BoundNodePath))
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

        // Capture the selection as a (Start, Length) range in the document's CLEAN text plus the
        // anchor text and version. The document text is never touched — the highlight is recomputed
        // from this capture at display time, so it works for Comment-only users and survives edits.
        var clean = MarkdownAnnotationParser.StripAllMarkers(RawContent ?? "");
        var (start, length) = CommentRendering.Capture(clean, startFragment, endFragment, selectedText);
        var anchored = start >= 0 && length > 0;

        var markerId = Guid.NewGuid().ToString("N")[..8];
        var comment = new Comment
        {
            Id = markerId,
            PrimaryNodePath = BoundNodePath,
            MarkerId = anchored ? markerId : null,
            HighlightedText = anchored ? selectedText : null,
            Author = CurrentAuthor,
            Text = commentText,
            CreatedAt = DateTimeOffset.UtcNow,
            Status = CommentStatus.Active,
            Version = anchored ? _docVersion : 0,
            Start = anchored ? start : -1,
            Length = anchored ? length : 0,
            AnchorText = anchored ? clean : null
        };
        CreateCommentNode(comment);
    }

    /// <summary>
    /// Creates a comment satellite node via <see cref="IMeshService.CreateNode"/> — the canonical
    /// mutation surface, no request/response. Optimistically projects the new comment so its inline
    /// highlight appears immediately; the comment-status subscription reconciles the rest.
    /// </summary>
    private void CreateCommentNode(Comment comment)
    {
        var node = new MeshNode(comment.Id, $"{BoundNodePath}/{CommentsExtensions.CommentPartition}")
        {
            Name = string.IsNullOrEmpty(comment.Author) ? "Comment" : $"Comment by {comment.Author}",
            NodeType = CommentNodeType.NodeType,
            MainNode = BoundNodePath,
            Content = comment
        };

        var meshService = Hub.ServiceProvider.GetRequiredService<IMeshService>();
        meshService.CreateNode(node).Subscribe(
            _ =>
            {
                if (string.IsNullOrEmpty(comment.MarkerId))
                    return;
                // Optimistic overlay for instant feedback (copy-on-write so the comment-status
                // subscription, which replaces the whole dictionary, can't be clobbered by us).
                commentNodes = new Dictionary<string, Comment>(commentNodes) { [comment.MarkerId] = comment };
                commentPaths = new Dictionary<string, string>(commentPaths) { [comment.MarkerId] = node.Path };
                InvokeAsync(() =>
                {
                    if (IsViewDisposed) return;
                    ProcessContent();
                    StateHasChanged();
                });
            },
            ex => Hub.ServiceProvider.GetService<ILoggerFactory>()
                ?.CreateLogger("MeshWeaver.Blazor.CollaborativeMarkdownView")
                .LogWarning(ex, "Comment create failed for {Path}", node.Path));
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
        if (string.IsNullOrWhiteSpace(_pageCommentText) || string.IsNullOrEmpty(BoundNodePath))
            return;

        var text = _pageCommentText;
        _showPageCommentInput = false;
        _pageCommentText = "";

        // Page-level comment: no text anchor (no MarkerId / positions). Shown in the page comments
        // section, not inline. Same canonical CreateNode surface.
        CreateCommentNode(new Comment
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            PrimaryNodePath = BoundNodePath,
            Author = CurrentAuthor,
            Text = text,
            CreatedAt = DateTimeOffset.UtcNow,
            Status = CommentStatus.Active
        });
    }

    private static string ChangeLabel(TrackedChangeType type) => type switch
    {
        TrackedChangeType.Insertion => "Insertion",
        TrackedChangeType.Deletion => "Deletion",
        TrackedChangeType.Replacement => "Replacement",
        _ => "Change"
    };

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
            try
            {
                await jsModule.InvokeVoidAsync("dispose");
                await jsModule.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // Circuit already disconnected — JS teardown is best-effort. The DisposeAsync at :637
                // used to be outside the try, escaping as "Unhandled exception in circuit".
            }
            catch { }
            jsModule = null;
        }
        dotNetRef?.Dispose();
        dotNetRef = null;
        await base.DisposeAsync();
    }
}
