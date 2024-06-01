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
        Func<LayoutArea, IObservable<object>> generator
    ) =>
        WithViewGenerator(
            r => r.Area == area,
            new ViewElementWithViewStream(area, a => generator(a))
        );

    public LayoutDefinition WithViewDefinition(
        string area,
        IObservable<Func<LayoutArea, object>> generator
    ) =>
        WithViewDefinition(
            area,
            generator.Select(o =>
                (Func<LayoutArea, Task<object>>)(a => Task.FromResult(o.Invoke(a)))
            )
        );

    public LayoutDefinition WithViewDefinition(
        string area,
        Func<LayoutArea, Task<object>> generator
    ) => WithViewDefinition(area, Observable.Return(generator));

    public LayoutDefinition WithViewDefinition(
        string area,
        IObservable<Func<LayoutArea, Task<object>>> generator
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
        Func<LayoutArea, Task<UiControl>> generator
    ) => WithViewDefinition(area, Observable.Return(generator));


    public LayoutDefinition WithViewDefinition(string area, ViewDefinition generator) =>
        WithViewDefinition(area, Observable.Return(generator));

    public LayoutDefinition WithViewDefinition(
        string area,
        IObservable<ViewDefinition> generator
    ) => WithViewGenerator(r => r.Area == area, new ViewElementWithViewDefinition(area, generator));

    public LayoutDefinition WithView(string area, object view) =>
        WithViewGenerator(r => r.Area == area, new ViewElementWithView(area, view));
    public LayoutDefinition WithView(string area, Func<LayoutArea, Task<object>> view)
    {
        return WithViewGenerator(r => r.Area == area,
            new ViewElementWithViewDefinition(area, Observable.Return<ViewDefinition>(view.Invoke)));
    }


    internal ImmutableList<Func<CancellationToken, Task>> Initializations { get; init; } =
        ImmutableList<Func<CancellationToken, Task>>.Empty;

    public LayoutDefinition WithInitialization(Func<CancellationToken, Task> func) =>
        this with
        {
            Initializations = Initializations.Add(func)
        };


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
