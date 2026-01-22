using System.Collections.Immutable;
using MeshWeaver.Data;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Layout.Composition;

public delegate EntityStoreAndUpdates Renderer(LayoutAreaHost host, RenderingContext context, EntityStore store);
public delegate Task<EntityStoreAndUpdates> AsyncRenderer(LayoutAreaHost host, RenderingContext context, EntityStore store);
public record LayoutDefinition(IMessageHub Hub)
{
    private ImmutableList<(Func<RenderingContext, bool> Filter, AsyncRenderer Renderer)> AsyncRenderers { get; init; } = [];

    private ImmutableDictionary<string, AsyncRenderer> NamedRenderers { get; init; }
        = ImmutableDictionary<string, AsyncRenderer>.Empty;

    /// <summary>
    /// The default area to display when no area is specified in the URL.
    /// </summary>
    public string? DefaultArea { get; init; }

    /// <summary>
    /// Sets the default area to display when no area is specified in the URL.
    /// </summary>
    public LayoutDefinition WithDefaultArea(string area)
        => this with { DefaultArea = area };

    public LayoutDefinition WithRenderer(Func<RenderingContext, bool> filter, Renderer renderer)
        => this with
        {
            AsyncRenderers = AsyncRenderers.Add((filter, (h,ctx, s) => Task.FromResult(renderer.Invoke(h, ctx, s))))
        };
    public LayoutDefinition WithRenderer(Func<RenderingContext, bool> filter, AsyncRenderer renderer)
        => this with
        {
            AsyncRenderers = AsyncRenderers.Add((filter, renderer))
        };

    public LayoutDefinition WithNamedRenderer(string area, Renderer renderer)
        => this with
        {
            NamedRenderers = NamedRenderers.SetItem(area, (h, ctx, s) =>
                Task.FromResult(renderer.Invoke(h, ctx, s)))
        };

    public LayoutDefinition WithNamedRenderer(string area, AsyncRenderer renderer)
        => this with
        {
            NamedRenderers = NamedRenderers.SetItem(area, renderer)
        };

    public async ValueTask<EntityStoreAndUpdates> RenderAsync(
        LayoutAreaHost host,
        RenderingContext context,
        EntityStore store)
    {
        var result = new EntityStoreAndUpdates(store, [], host.Stream.StreamId);

        // Execute named renderer if one exists for this area
        if (NamedRenderers.TryGetValue(context.Area, out var namedRenderer))
        {
            var ret = await namedRenderer.Invoke(host, context, result.Store);
            result = ret with { Updates = result.Updates.Concat(ret.Updates) };
        }

        // Execute predicate-based renderers (existing behavior)
        await foreach (var x in AsyncRenderers.ToAsyncEnumerable().Where(r => r.Filter(context)))
        {
            var ret = await x.Renderer.Invoke(host, context, result.Store);
            result = ret with { Updates = result.Updates.Concat(ret.Updates) };
        }

        return result;
    }

    public int Count => AsyncRenderers.Count + NamedRenderers.Count;

    public LayoutDefinition AddRendering(Func<object, UiControl?> rule)
    {
        Hub.ServiceProvider.GetRequiredService<IUiControlService>().AddRule(rule);
        return this;
    }

    internal ImmutableDictionary<string, LayoutAreaDefinition> AreaDefinitions { get; init; } = ImmutableDictionary<string, LayoutAreaDefinition>.Empty;
    internal ThumbnailPattern? ThumbnailPattern { get; init; }

    /// <summary>
    /// Configures thumbnails using the default naming convention: {basePath}/{area}.png and {basePath}/{area}-dark.png
    /// </summary>
    public LayoutDefinition WithThumbnailsPath(string basePath)
        => this with { ThumbnailPattern = ThumbnailPattern.FromBasePath(basePath) };

    /// <summary>
    /// Configures thumbnails using lambda expressions to generate URLs from area names.
    /// </summary>
    public LayoutDefinition WithThumbnailPattern(Func<string, string> lightUrlFactory, Func<string, string> darkUrlFactory)
        => this with { ThumbnailPattern = new ThumbnailPattern(lightUrlFactory, darkUrlFactory) };

    /// <summary>
    /// Configures thumbnails using a custom pattern.
    /// </summary>
    public LayoutDefinition WithThumbnailPattern(ThumbnailPattern pattern)
        => this with { ThumbnailPattern = pattern };

    public LayoutDefinition WithAreaDefinition(LayoutAreaDefinition? layoutArea) => 
        layoutArea == null 
            ? this 
            : this with { AreaDefinitions = AreaDefinitions.SetItem(layoutArea.Area, layoutArea) };
}


