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

namespace MeshWeaver.Blazor.Components;

public partial class CollaborativeMarkdownView
{
    [Inject] private IJSRuntime JS { get; set; } = null!;

    private ElementReference containerRef;
    private ElementReference contentRef;
    private ElementReference sidebarRef;

    private IJSObjectReference? jsModule;

    // Bound properties
    private string RawContent = "";
    private string? BoundNodePath;
    private string? BoundHubAddress;

    // View state (local Blazor state)
    private string CurrentViewMode = "Markup";
    private HashSet<string> ExpandedAnnotationIds = new();

    // Parsed data
    private string RenderedHtml = "";
    private List<ParsedAnnotation> Annotations = new();

    private bool HasAnnotations => Annotations.Count > 0;

    private bool HasSideAnnotations =>
        CurrentViewMode == "Markup" && Annotations.Count > 0;

    private List<ParsedAnnotation> TrackChangeAnnotations =>
        Annotations.Where(a => a.Type != AnnotationType.Comment).ToList();

    private List<ParsedAnnotation> CommentAnnotations =>
        Annotations.Where(a => a.Type == AnnotationType.Comment).ToList();

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

        if (jsModule != null && HasSideAnnotations)
        {
            await jsModule.InvokeVoidAsync("positionAnnotations", contentRef, sidebarRef);
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
        return Markdig.Markdown.ToHtml(transformed);
    }

    // View mode
    private void OnViewModeChanged(string mode)
    {
        CurrentViewMode = mode;
        ProcessContent();
        StateHasChanged();
    }

    // Accept/Reject individual changes
    private void OnAcceptChange(string changeId)
    {
        var newContent = AnnotationMarkdownExtension.AcceptChange(RawContent, changeId);
        UpdateContentLocally(newContent);
        PostContentUpdate(newContent);
    }

    private void OnRejectChange(string changeId)
    {
        var newContent = AnnotationMarkdownExtension.RejectChange(RawContent, changeId);
        UpdateContentLocally(newContent);
        PostContentUpdate(newContent);
    }

    // Accept/Reject all
    private void OnAcceptAll()
    {
        var newContent = AnnotationMarkdownExtension.GetAcceptedContent(RawContent);
        UpdateContentLocally(newContent);
        PostContentUpdate(newContent);
    }

    private void OnRejectAll()
    {
        var newContent = AnnotationMarkdownExtension.GetRejectedContent(RawContent);
        UpdateContentLocally(newContent);
        PostContentUpdate(newContent);
    }

    // Resolve comment (remove marker, keep text)
    private void OnResolveComment(string annotationId)
    {
        var newContent = AnnotationMarkdownExtension.ResolveComment(RawContent, annotationId);
        UpdateContentLocally(newContent);
        PostContentUpdate(newContent);
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

    // Post content update to hub
    private void PostContentUpdate(string newContent)
    {
        if (string.IsNullOrEmpty(BoundHubAddress))
            return;

        var nodeUpdate = new MeshNode(BoundNodePath ?? "")
        {
            NodeType = "Markdown",
            Content = new MarkdownContent { Content = newContent }
        };

        // Set ChangedBy to the stream's ClientId so the echo-filter suppresses
        // our own change from being pushed back (avoiding unnecessary re-render).
        Hub.Post(
            new DataChangeRequest { ChangedBy = Stream?.ClientId }.WithUpdates(nodeUpdate),
            o => o.WithTarget(new Address(BoundHubAddress)));
    }

    // Highlight an annotation (scroll-into-view via JS)
    private async Task HighlightAnnotation(string annotationId)
    {
        if (jsModule != null)
            await jsModule.InvokeVoidAsync("highlightAnnotation", annotationId);
    }

    // Expand/collapse
    private void ToggleExpanded(string id)
    {
        if (!ExpandedAnnotationIds.Add(id))
            ExpandedAnnotationIds.Remove(id);
        StateHasChanged();
    }

    private bool IsExpanded(string id) => ExpandedAnnotationIds.Contains(id);

    // Helpers
    private static string GetInitial(string? author) =>
        !string.IsNullOrEmpty(author) ? author[0].ToString().ToUpper() : "?";

    private static string GetTypeColor(AnnotationType type) => type switch
    {
        AnnotationType.Comment => "#3b82f6",
        AnnotationType.Insert => "#22c55e",
        AnnotationType.Delete => "#ef4444",
        _ => "#6b7280"
    };

    private static string GetTypeLabel(AnnotationType type) => type switch
    {
        AnnotationType.Comment => "Comment",
        AnnotationType.Insert => "Inserted",
        AnnotationType.Delete => "Deleted",
        _ => "Change"
    };

    private static string GetTypeBg(AnnotationType type) => type switch
    {
        AnnotationType.Insert => "rgba(34, 197, 94, 0.15)",
        AnnotationType.Delete => "rgba(239, 68, 68, 0.15)",
        _ => "rgba(59, 130, 246, 0.1)"
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
            await jsModule.DisposeAsync();
            jsModule = null;
        }
        await base.DisposeAsync();
    }
}
