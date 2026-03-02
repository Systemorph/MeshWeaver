using System.Text.RegularExpressions;
using Markdig;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using System.Text.Json;
using MarkdownAnnotationParser = MeshWeaver.Markdown.Collaboration.MarkdownAnnotationParser;

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
    private string? activeAnnotationId;
    private bool jsInitialized;
    private bool commentSelectionInitialized;

    // Parsed data
    private string RenderedHtml = "";
    private List<ParsedAnnotation> Annotations = new();

    private bool HasAnnotations => Annotations.Count > 0;

    private bool HasSideAnnotations =>
        CurrentViewMode == "Markup" && Annotations.Count > 0;

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
        CurrentAuthor = accessService?.Context?.Name ?? "";

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

        ProcessContent();
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

        if (jsModule != null && !string.IsNullOrEmpty(RenderedHtml))
        {
            await jsModule.InvokeVoidAsync("highlightCodeBlocks", contentRef);
        }

        // Enable comment-from-selection when user has comment permission
        if (jsModule != null && BoundCanComment && !commentSelectionInitialized)
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
            RenderedHtml = "";
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
        RenderedHtml = RenderMarkdown(content);
    }

    private static string RenderMarkdown(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "";

        // Transform annotation markers to HTML spans before markdown rendering
        var transformed = AnnotationMarkdownExtension.TransformAnnotations(content);

        // Use the standard pipeline that includes LayoutAreaMarkdownExtension for @@ syntax
        var pipeline = MeshWeaver.Markdown.MarkdownExtensions.CreateMarkdownPipeline(null);
        return Markdig.Markdown.ToHtml(transformed, pipeline);
    }

    // View mode
    private void OnViewModeChanged(string mode)
    {
        CurrentViewMode = mode;
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

        var nodeUpdate = new MeshNode(BoundNodePath ?? "")
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
    /// Finds the selected text in the raw content and inserts comment markers.
    /// </summary>
    [JSInvokable]
    public async Task OnCommentFromSelection(string selectedText)
    {
        if (string.IsNullOrWhiteSpace(selectedText) || string.IsNullOrEmpty(RawContent))
            return;

        // Strip annotation markers to get clean markdown, then search for the selected text
        var cleanContent = MarkdownAnnotationParser.StripAllMarkers(RawContent);
        var idx = cleanContent.IndexOf(selectedText, StringComparison.Ordinal);
        if (idx < 0)
        {
            // Try case-insensitive search as fallback
            idx = cleanContent.IndexOf(selectedText, StringComparison.OrdinalIgnoreCase);
        }

        if (idx < 0)
            return; // Could not locate the selected text in the markdown

        // Map clean-content offsets to annotated-content positions
        var map = BuildCleanToAnnotatedMap();
        var aStart = idx < map.Length ? map[idx] : RawContent.Length;
        var aEnd = (idx + selectedText.Length) < map.Length
            ? map[idx + selectedText.Length]
            : RawContent.Length;

        // Build comment markers with author and date
        var markerId = Guid.NewGuid().ToString("N")[..8];
        var date = DateTime.Now.ToString("MMM d");
        var meta = !string.IsNullOrEmpty(CurrentAuthor) ? $":{CurrentAuthor}:{date}" : "";
        var openTag = $"<!--comment:{markerId}{meta}-->";
        var closeTag = $"<!--/comment:{markerId}-->";

        var newContent = RawContent.Insert(aEnd, closeTag).Insert(aStart, openTag);
        var previousContent = RawContent;
        UpdateContentLocally(newContent);
        if (!await PostContentUpdateAsync(newContent))
            RevertContent(previousContent);
    }

    /// <summary>
    /// Builds a map from clean (marker-stripped) character index to annotated character index.
    /// </summary>
    private int[] BuildCleanToAnnotatedMap()
    {
        var tagRegex = new Regex(@"<!--/?(comment|insert|delete):[^-]+-->");
        var tags = tagRegex.Matches(RawContent).Cast<Match>()
            .OrderBy(m => m.Index).ToList();
        var map = new List<int>();
        int tagIdx = 0;

        for (int i = 0; i < RawContent.Length;)
        {
            if (tagIdx < tags.Count && i == tags[tagIdx].Index)
            {
                i += tags[tagIdx].Length;
                tagIdx++;
            }
            else
            {
                map.Add(i);
                i++;
            }
        }

        return map.ToArray();
    }

    // Helpers
    private string GetCommentAddress(string commentId) =>
        $"{BoundNodePath}/{commentId}";

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
