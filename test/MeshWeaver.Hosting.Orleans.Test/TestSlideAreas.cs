using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// A TEST-LOCAL Slide node type whose Content area mirrors the composition shape the live slide
/// views use — stage + presenter bar (Prev / "Slide n / N" / Next) derived from
/// <see cref="IDeckSlidesCache.GetOrderedSlides"/> in ONE <c>CombineLatest</c>, so the first
/// emitted frame either carries the complete deck position or does not emit at all.
///
/// <para>The production slide views live in the Publish pack's IN-MESH source
/// (<c>Publish/Slide/Source/SlideLayoutAreas.cs</c>) since the core Slide/Deck types were retired
/// (#1589), which a hermetic platform test cannot install. What this fixture pins is the PLATFORM
/// half of the first-frame contract on the production storage shape (Orleans + partitioned PG):
/// <see cref="DeckSlidesCache"/>'s first emission must be the COMPLETE ordered deck (the
/// "Slide 1 / 1 then re-render" flicker regression), and the warm second subscription must replay
/// synchronously. The pack's own in-mesh tests pin the area half.</para>
/// </summary>
public static class TestSlideAreas
{
    public const string ContentArea = "Content";
    public const string PresenterBarArea = "PresenterBar";
    public const string CounterArea = "Counter";
    public const string PrevButtonArea = "Prev";
    public const string NextButtonArea = "Next";

    /// <summary>The test-local Slide MeshNode registration (NodeType <c>Slide</c>).</summary>
    public static MeshNode CreateTestSlideNode() => new(SlideNodeType.NodeType)
    {
        Name = "Slide (test-local areas)",
        HubConfiguration = config => config
            .AddMeshDataSource(s => s.WithContentType<SlideContent>())
            .AddLayout(layout => layout
                .WithDefaultArea(ContentArea)
                .WithView(ContentArea, Content)),
    };

    private static IObservable<UiControl?> Content(LayoutAreaHost host, RenderingContext _)
        => host.Workspace.GetMeshNodeStream()
            .CombineLatest(ObserveDeckSlides(host),
                (node, slides) => (UiControl?)BuildContent(host, node, slides));

    private static IObservable<IReadOnlyList<MeshNode>> ObserveDeckSlides(LayoutAreaHost host)
    {
        var hubPath = host.Hub.Address.ToString();
        var cut = hubPath.LastIndexOf('/');
        if (cut <= 0)
            return Observable.Return<IReadOnlyList<MeshNode>>([]);
        var cache = host.Hub.ServiceProvider.GetRequiredService<IDeckSlidesCache>();
        return cache.GetOrderedSlides(hubPath[..cut]);
    }

    private static UiControl BuildContent(LayoutAreaHost host, MeshNode? node, IReadOnlyList<MeshNode> slides)
    {
        var hubPath = host.Hub.Address.ToString();
        var slide = node.ContentAs<SlideContent>(host.Hub.JsonSerializerOptions);
        var index = slides.ToList().FindIndex(s => s.Path == hubPath);
        var prev = index > 0 ? slides[index - 1] : null;
        var next = index >= 0 && index < slides.Count - 1 ? slides[index + 1] : null;

        var bar = Controls.Stack.WithOrientation(Orientation.Horizontal);
        if (prev is not null)
            bar = bar.WithView(Controls.Button("◀").WithNavigateToHref($"/{prev.Path}"), PrevButtonArea);
        bar = bar.WithView(
            Controls.Label($"Slide {index + 1} / {slides.Count}"), CounterArea);
        if (next is not null)
            bar = bar.WithView(Controls.Button("▶").WithNavigateToHref($"/{next.Path}"), NextButtonArea);

        return Controls.Stack
            .WithView(Controls.Markdown(slide?.Content ?? string.Empty), "Stage")
            .WithView(bar, PresenterBarArea);
    }
}
