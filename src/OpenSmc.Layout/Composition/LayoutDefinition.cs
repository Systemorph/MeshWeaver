using System.Collections.Immutable;
using OpenSmc.Data;
using OpenSmc.Messaging;

namespace OpenSmc.Layout.Composition;

public delegate IEnumerable<Func<EntityStore, EntityStore>> Renderer(LayoutAreaHost host, RenderingContext context);
public record LayoutDefinition(IMessageHub Hub)
{
    private ImmutableList<(Func<RenderingContext, bool> Filter, Renderer Renderer)> Renderers { get; init; } = ImmutableList<(Func<RenderingContext, bool> Filter, Renderer Renderer)>.Empty;

    public LayoutDefinition WithRenderer(Func<RenderingContext,bool> filter, Renderer renderer)
        => this with
        {
            Renderers = Renderers.Add((filter, renderer))
        };

    public EntityStore Render(
        LayoutAreaHost host,
        RenderingContext context,
        EntityStore store) =>
        Renderers
            .Where(r => r.Filter(context))
            .Select(x => x.Renderer)
            .Aggregate(store ?? new(),
                (s,
                        renderer) => renderer
                    .Invoke(host, context)
                    .Aggregate(s, (ss, update) => update.Invoke(ss))

            );


    public int Count => Renderers.Count;

}


