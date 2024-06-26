using System.Collections.Immutable;
using System.Reactive.Linq;
using OpenSmc.Data;
using OpenSmc.Messaging;

namespace OpenSmc.Layout.Composition;

public record LayoutDefinition(IMessageHub Hub)
{
    internal ImmutableList<ViewGenerator> ViewGenerators { get; init; } =
        ImmutableList<ViewGenerator>.Empty;

    public IWorkspace Workspace => Hub.GetWorkspace();
    public LayoutDefinition WithViewGenerator(
        Func<LayoutAreaReference, bool> filter,
        ViewElement viewElement
    ) => this with { ViewGenerators = ViewGenerators.Add(new(filter, viewElement)) };

    public LayoutDefinition WithView(
        string area,
        Func<LayoutAreaHost, RenderingContext, IObservable<object>> generator
    ) =>
        WithViewGenerator(
            r => r.Area == area,
            new ViewElementWithViewStream(area, (a,ctx) => generator(a, ctx))
        );

    public LayoutDefinition WithViewDefinition(
        string area,
        IObservable<Func<LayoutAreaHost, object>> generator
    ) =>
        WithViewDefinition(
            area,
            generator.Select(o =>
                (Func<LayoutAreaHost, Task<object>>)(a => Task.FromResult(o.Invoke(a)))
            )
        );

    public LayoutDefinition WithViewDefinition(
        string area,
        Func<LayoutAreaHost, Task<object>> generator
    ) => WithViewDefinition(area, Observable.Return(generator));

    public LayoutDefinition WithViewDefinition(
        string area,
        IObservable<Func<LayoutAreaHost, Task<object>>> generator
    ) =>
        WithViewGenerator(
            r => r.Area == area,
            new ViewElementWithViewDefinition(
                area,
                generator.Cast<ViewDefinition>()
            )
        );


    public LayoutDefinition WithViewDefinition(
        string area,
        Func<LayoutAreaHost, Task<UiControl>> generator
    ) => WithViewDefinition(area, Observable.Return(generator));


    public LayoutDefinition WithViewDefinition(string area, ViewDefinition generator) =>
        WithViewDefinition(area, Observable.Return(generator));

    public LayoutDefinition WithViewDefinition(
        string area,
        IObservable<ViewDefinition> generator
    ) => WithViewGenerator(r => r.Area == area, new ViewElementWithViewDefinition(area, generator));

    public LayoutDefinition WithView(string area, object view) =>
        WithViewGenerator(r => r.Area == area, new ViewElementWithView(area, view));
    public LayoutDefinition WithView(string area, Func<LayoutAreaHost,RenderingContext, Task<object>> view)
    {
        return WithViewGenerator(r => r.Area == area,
            new ViewElementWithViewDefinition(area, Observable.Return<ViewDefinition>(view.Invoke)));
    }

    public LayoutDefinition WithView(string area, Func<LayoutAreaHost,RenderingContext, object> view)
        => WithView(area, (a,ctx) => Observable.Return(view(a, ctx)));


    internal ViewElement GetViewElement(LayoutAreaReference reference)
    {
        foreach (var viewGenerator in ViewGenerators)
        {
            if (viewGenerator.Filter(reference))
                return viewGenerator.ViewElement;
        }

        return null;
    }
}

internal record ViewGenerator(Func<LayoutAreaReference, bool> Filter, ViewElement ViewElement);
