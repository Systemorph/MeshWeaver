using System.Collections.Immutable;
using System.Reactive.Linq;
using OpenSmc.Messaging;

namespace OpenSmc.Layout.Composition;

public record LayoutDefinition(IMessageHub Hub)
{
    internal UiControl InitialState { get; init; }
    internal ImmutableList<ViewGenerator> ViewGenerators { get; init; } = ImmutableList<ViewGenerator>.Empty;
    public LayoutDefinition WithInitialState(UiControl initialState) => this with { InitialState = initialState };
    public LayoutDefinition WithViewGenerator(Func<LayoutAreaReference, bool> filter, ViewElement viewElement) => this with { ViewGenerators = ViewGenerators.Add(new(filter, viewElement)) };

    public LayoutDefinition WithView(string area, Func<LayoutAreaReference, Func<LayoutArea, object>> generator)
        => WithView(area, r => Observable.Return(generator.Invoke(r)));

    public LayoutDefinition WithView(string area, Func<LayoutAreaReference, IObservable<Func<LayoutArea, object>>> generator)
    => WithView(area, r => generator.Invoke(r).Select(o => (Func<LayoutArea, Task<object>>)(a => Task.FromResult(o.Invoke(a)))));

    public LayoutDefinition WithView(string area, Func<LayoutArea, Task<object>> generator)
        => WithView(area, Observable.Return(generator));

    public LayoutDefinition WithView(string area, Func<LayoutAreaReference, IObservable<Func<LayoutArea, Task<object>>>> generator)
        => WithViewGenerator(r => r.Area == area, new ViewElementWithViewDefinition(area, r => generator.Invoke(r).Select(x => (Func<LayoutArea, Task<UiControl>>)(async a => ControlsManager.Get(await x(a))))));
    public LayoutDefinition WithView(string area, ViewDefinition generator)
        => WithViewGenerator(r => r.Area == area, new ViewElementWithViewDefinition(area, generator));


    public LayoutDefinition WithView(string area, object view)
        => WithViewGenerator(r => r.Area == area, new ViewElementWithView(area, view));


    internal ImmutableList<Func<CancellationToken, Task>> Initializations { get; init; } = ImmutableList<Func<CancellationToken, Task>>.Empty;

    public LayoutDefinition WithInitialization(Func<CancellationToken, Task> func)
        => this with { Initializations = Initializations.Add(func) };

    internal UiControlsManager ControlsManager { get; } = new();

    public LayoutDefinition WithUiControls(Action<UiControlsManager> configuration)
    {
        configuration.Invoke(ControlsManager);
        return this;
    }

    public ViewElement GetViewElement(LayoutAreaReference reference)
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