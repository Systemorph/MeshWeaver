using System.Collections.Immutable;
using MeshWeaver.Data;
using MeshWeaver.Messaging;

namespace MeshWeaver.Layout.Composition;

public delegate EntityStoreAndUpdates Renderer(LayoutAreaHost host, RenderingContext context, EntityStore store);
public record LayoutDefinition(IMessageHub Hub)
{
    private ImmutableList<(Func<RenderingContext, bool> Filter, Renderer Renderer)> Renderers { get; init; } = ImmutableList<(Func<RenderingContext, bool> Filter, Renderer Renderer)>.Empty;

    public LayoutDefinition WithRenderer(Func<RenderingContext,bool> filter, Renderer renderer)
        => this with
        {
            Renderers = Renderers.Add((filter, renderer))
        };

    public EntityStoreAndUpdates Render(
        LayoutAreaHost host,
        RenderingContext context,
        EntityStore store) =>
        Renderers
            .Where(r => r.Filter(context))
            .Aggregate(new EntityStoreAndUpdates(store, [], host.Reference.HostId),(r,x) =>
            {
                var ret = x.Renderer.Invoke(host, context, r.Store);
                return ret with{
                    Updates = r.Updates.Concat(ret.Updates)
                };
            });

    public int Count => Renderers.Count;

}


