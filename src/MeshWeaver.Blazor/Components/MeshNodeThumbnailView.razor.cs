using System.Reactive.Linq;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Blazor.Components;

/// <summary>
/// Code-behind for <see cref="MeshNodeThumbnailView"/>. Subscribes to
/// <c>IMeshNodeStreamCache.GetStream</c> on the bound
/// <see cref="MeshNodeThumbnailControl.NodePath"/> and refreshes title /
/// image-url on every emission.
///
/// <para>Reads go through the process-wide cache — multiple visible cards
/// on the same node share ONE upstream subscription.
/// See <c>Doc/GUI/ItemTemplateMeshNodeStreamBinding</c>.</para>
/// </summary>
public partial class MeshNodeThumbnailView
{
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    private string? Title { get; set; }
    private string? Description { get; set; }
    private string? ImageUrl { get; set; }
    private string NodePath { get; set; } = string.Empty;

    private string Initial => !string.IsNullOrEmpty(Title) ? Title[0].ToString().ToUpper() : "?";

    private string TruncatedDescription => Description?.Length > 120
        ? Description.Substring(0, 117) + "..."
        : Description ?? string.Empty;

    /// <summary>
    /// Seeds title, description, and image URL from the view-model, then subscribes to
    /// <c>Hub.GetMeshNodeStream(NodePath)</c> so the card reflects live node changes
    /// without a full re-render cycle.
    /// </summary>
    protected override void BindData()
    {
        base.BindData();
        // Seed from ViewModel so callers that hand-built the control still render.
        Title = ViewModel.Title;
        Description = ViewModel.Description;
        ImageUrl = ViewModel.ImageUrl;
        NodePath = ViewModel.NodePath;

        // Bind to the per-node stream so the view stays live with the underlying node.
        // Backend layout areas should pass NodePath only; this subscription does the lookup.
        if (string.IsNullOrEmpty(NodePath)) return;

        AddBinding(Hub.GetMeshNodeStream(NodePath)
            .Where(node => node is not null)
            .DistinctUntilChanged()
            .Subscribe(node =>
            {
                if (IsViewDisposed) return;
                Title = node.Name ?? NodePath;
                var img = MeshNodeThumbnailControl.GetImageUrlForNode(node);
                if (!string.IsNullOrEmpty(img)) ImageUrl = img;
                InvokeAsync(StateHasChanged);
            },
            ex =>
            {
                // #434: a subject the viewer cannot read (a peer's user partition — DENIED BY
                // DESIGN) is a benign, expected state, NOT an error. Keep the seeded title /
                // description and render the initials-avatar fallback; log at Debug and do NOT
                // raise an error toast. Only a genuine infrastructure fault surfaces (once) via
                // SurfaceError — the classifier draws the line.
                if (!MeshNodeThumbnailControl.ShouldSurfaceStreamError(ex))
                {
                    Logger.LogDebug(ex,
                        "Thumbnail subject {NodePath} is not readable by the viewer (access-denied) — rendering fallback card",
                        NodePath);
                    return;
                }
                SurfaceError(ex, $"Loading thumbnail for {NodePath}");
            }));
    }

    private void Navigate()
    {
        NavigationManager.NavigateTo($"/{NodePath}");
    }
}
