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
        Func<LayoutAreaHost, RenderingContext, IObservable<object>> generator) =>
        WithView(area, generator, x => x);
    public LayoutDefinition WithView(
        string area,
        Func<LayoutAreaHost, RenderingContext, IObservable<object>> generator, 
        Func<LayoutAreaProperties, LayoutAreaProperties> options
    ) =>
        WithViewGenerator(
            r => r.Area == area,
            new ViewElementWithViewStream(area, (a,ctx) => generator(a, ctx),  options.Invoke(new()))
        );

    public LayoutDefinition WithViewDefinition(
        string area,
        IObservable<Func<LayoutAreaHost, object>> generator
    ) =>
    WithViewDefinition(area, generator, x => x);
    public LayoutDefinition WithViewDefinition(
        string area,
        IObservable<Func<LayoutAreaHost, object>> generator,
        Func<LayoutAreaProperties, LayoutAreaProperties> options
    ) =>
        WithViewDefinition(
            area,
            generator.Select(o =>
                (Func<LayoutAreaHost, Task<object>>)(a => Task.FromResult(o.Invoke(a)))
            ),
            options
        );

    public LayoutDefinition WithViewDefinition(
        string area,
        Func<LayoutAreaHost, Task<object>> generator
    ) => WithViewDefinition(area, Observable.Return(generator), x => x);
    public LayoutDefinition WithViewDefinition(
        string area,
        Func<LayoutAreaHost, Task<object>> generator,
        Func<LayoutAreaProperties, LayoutAreaProperties> options
    ) => WithViewDefinition(area, Observable.Return(generator), options);

    public LayoutDefinition WithViewDefinition(
        string area,
        IObservable<Func<LayoutAreaHost, Task<object>>> generator, Func<LayoutAreaProperties, LayoutAreaProperties> options
    ) =>
        WithViewGenerator(
            r => r.Area == area,
            new ViewElementWithViewDefinition(
                area,
                generator.Cast<ViewDefinition>(),
                options.Invoke(new()
            )
        ));


    public LayoutDefinition WithViewDefinition(
        string area,
        Func<LayoutAreaHost, Task<UiControl>> generator
    ) => WithViewDefinition(area, generator, x => x);
    public LayoutDefinition WithViewDefinition(
        string area,
        Func<LayoutAreaHost, Task<UiControl>> generator,
        Func<LayoutAreaProperties, LayoutAreaProperties> options
    ) => WithViewDefinition(area, Observable.Return(generator), options);
    public LayoutDefinition WithViewDefinition(string area, ViewDefinition generator) =>
        WithViewDefinition(
            area,
            generator,
            x => x
        );


    public LayoutDefinition WithViewDefinition(string area, ViewDefinition generator,
        Func<LayoutAreaProperties, LayoutAreaProperties> options) =>
        WithViewDefinition(
            area,
            Observable.Return(generator),
            options
        );

    public LayoutDefinition WithViewDefinition(
        string area,
        IObservable<ViewDefinition> generator
    ) =>
        WithViewDefinition(area, generator, x => x);
    public LayoutDefinition WithViewDefinition(
        string area,
        IObservable<ViewDefinition> generator, 
        Func<LayoutAreaProperties, LayoutAreaProperties> options
    ) => WithViewGenerator(r => r.Area == area, new ViewElementWithViewDefinition(area, generator, options.Invoke(new())));

    public LayoutDefinition WithView(string area, object view) => WithView(area, view, x => x);
    public LayoutDefinition WithView(string area, object view, Func<LayoutAreaProperties, LayoutAreaProperties> options) =>
        WithViewGenerator(r => r.Area == area, new ViewElementWithView(area, view, options.Invoke(new())));

    public LayoutDefinition WithView(
        string area,
        Func<LayoutAreaHost, RenderingContext, CancellationToken, Task<object>> view)
        => WithView(area, view, x => x);

    public LayoutDefinition WithView(
        string area, 
        Func<LayoutAreaHost, RenderingContext, CancellationToken, Task<object>> view, 
        Func<LayoutAreaProperties, LayoutAreaProperties> options)
    {
        return WithViewGenerator(r => r.Area == area,
            new ViewElementWithViewDefinition(area, 
                Observable.Return<ViewDefinition>(view.Invoke), 
                options.Invoke(new())));
    }

    public LayoutDefinition WithView(
        string area,
        Func<LayoutAreaHost, RenderingContext, object> view)
        => WithView(area, view, x => x);
    public LayoutDefinition WithView(
        string area, 
        Func<LayoutAreaHost,RenderingContext, object> view, 
        Func<LayoutAreaProperties, LayoutAreaProperties> options)
        => WithView(area, (a,ctx) => Observable.Return(view(a, ctx)), options);


    internal ViewElement GetViewElement(LayoutAreaReference reference)
    {
        foreach (var viewGenerator in ViewGenerators)
        {
            if (viewGenerator.Filter(reference))
                return viewGenerator.ViewElement;
        }

        return null;
    }

    internal Func<ViewElement, NavMenuControl, ViewElement> MainLayout { get; init; } 

    public LayoutDefinition WithLayout(Func<ViewElement, NavMenuControl, ViewElement> layout)
        => this with { MainLayout = layout };

    internal Func<LayoutAreaReference,NavMenuControl> NavMenu { get; init; }

    public string ToHref(LayoutAreaReference reference)
        => reference.ToHref(Hub.Address);
}


internal record ViewGenerator(Func<LayoutAreaReference, bool> Filter, ViewElement ViewElement);
public record SourceItem(string Assembly, string Type);
