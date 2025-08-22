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

    public ValueTask<EntityStoreAndUpdates> RenderAsync(
        LayoutAreaHost host,
        RenderingContext context,
        EntityStore store) =>
        AsyncRenderers
            .ToAsyncEnumerable()
            .Where(r => r.Filter(context))
            .AggregateAwaitAsync(new EntityStoreAndUpdates(store, [], host.Stream.StreamId),
                async (r,x) =>
            {
                var ret = await x.Renderer.Invoke(host, context, r.Store);
                return ret with{
                    Updates = r.Updates.Concat(ret.Updates)
                };
            });

    public int Count => AsyncRenderers.Count;

    public LayoutDefinition AddRendering(Func<object, UiControl?> rule)
    {
        Hub.ServiceProvider.GetRequiredService<IUiControlService>().AddRule(rule);
        return this;
    }

    internal ImmutableDictionary<string, LayoutAreaDefinition> AreaDefinitions { get; init; } = ImmutableDictionary<string, LayoutAreaDefinition>.Empty;
    public LayoutDefinition WithAreaDefinition(LayoutAreaDefinition? layoutArea) => 
        layoutArea == null 
            ? this 
            : this with { AreaDefinitions = AreaDefinitions.SetItem(layoutArea.Area, layoutArea) };
}


