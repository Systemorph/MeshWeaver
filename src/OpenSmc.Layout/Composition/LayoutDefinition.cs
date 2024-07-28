using System.Collections.Immutable;
using OpenSmc.Data;
using OpenSmc.Messaging;

namespace OpenSmc.Layout.Composition;

public delegate IEnumerable<(string Area, UiControl Control)> Renderer(LayoutAreaHost host,
    RenderingContext context, EntityStore store);
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
                        renderer) =>
                    s.Update(LayoutAreaReference.Areas,
                        i => i with
                        {
                            Instances = i.Instances
                                .SetItems(
                                    renderer
                                        .Invoke(host, context, store)
                                        .Select(x => new KeyValuePair<object, object>(x.Area, x.Control))
                                )
                        })
            );


    public int Count => Renderers.Count;

}


