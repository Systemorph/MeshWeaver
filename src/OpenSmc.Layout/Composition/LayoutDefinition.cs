using System.Collections.Immutable;
using System.Reactive.Linq;
using OpenSmc.Data;
using OpenSmc.Messaging;

namespace OpenSmc.Layout.Composition;

public record LayoutDefinition(IMessageHub Hub)
{
    internal LayoutStackControl InitialState { get; init; }
    internal ImmutableList<ViewGenerator> ViewGenerators { get; init; } = ImmutableList<ViewGenerator>.Empty;
    public LayoutDefinition WithInitialState(LayoutStackControl initialState) => this with { InitialState = initialState };
    public LayoutDefinition WithGenerator(Func<LayoutAreaReference, bool> filter, ViewElement viewElement) => this with { ViewGenerators = ViewGenerators.Add(new(filter, viewElement)) };

    public LayoutDefinition WithView(LayoutAreaReference reference, Func<IObservable<WorkspaceState>, LayoutAreaReference, IObservable<object>> generator) =>
        WithGenerator(reference.Equals,  new ViewElementWithViewDefinition(reference,(stream,r) => generator.Invoke(stream, r).Select(x => ControlsManager.Get(x))));

    public LayoutDefinition WithView(string area, Func<IObservable<WorkspaceState>, LayoutAreaReference, IObservable<object>> generator) =>
        WithView(new LayoutAreaReference(area), generator);

    public LayoutDefinition WithView(string area, object view)
        => WithView(new LayoutAreaReference(area), view);
    public LayoutDefinition WithView(LayoutAreaReference reference, object view)
        => WithView(reference, (_, _) => Observable.Return<object>(ControlsManager.Get(view)));


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