using System.Collections.Immutable;
using OpenSmc.Data;
using OpenSmc.Messaging;

namespace OpenSmc.Layout.Composition;

public interface ILayout
{
    LayoutAreaCollection Render(WorkspaceState state, LayoutAreaReference reference);
}

public record LayoutAddress(object Host) : IHostedAddress;

public class LayoutPlugin(IMessageHub hub) 
    : MessageHubPlugin(hub), 
    ILayout
{
    private ImmutableDictionary<LayoutAreaReference, UiControl> Areas { get; set; } = ImmutableDictionary<LayoutAreaReference, UiControl>.Empty;

    private readonly LayoutDefinition layoutDefinition =
        hub.Configuration.GetListOfLambdas().Aggregate(new LayoutDefinition(hub), (x, y) => y.Invoke(x));

    private readonly IMessageHub layoutHub =
        hub.GetHostedHub(new LayoutAddress(hub.Address));


    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await base.StartAsync(cancellationToken);

        if (layoutDefinition.InitialState == null)
            return;
        var control = layoutDefinition.InitialState;
        if(control != null)
            RenderArea(new(string.Empty), control);

        foreach (var initialization in layoutDefinition.Initializations)
            await initialization.Invoke(cancellationToken);
    }

    private LayoutAreaReference RenderArea(LayoutAreaReference reference)
    {
        return RenderArea(reference, layoutDefinition.GetViewElement(reference));
    }


    private LayoutAreaReference RenderArea(LayoutAreaReference reference, UiControl control)
    {
        if (control == null)
            return null;

        if (control is LayoutStackControl stack)
            control = stack with
            {
                Areas = stack.ViewElements.Select(ve => RenderArea(reference with {Area = $"{reference.Area}/{ve.Reference.Area}"}, ve)).ToArray()
            };

        Areas = Areas.SetItem(reference, control);
        return reference;
    }

    private LayoutAreaReference RenderArea(LayoutAreaReference reference, ViewElementWithViewDefinition viewDefinition)
    {
        layoutHub.Schedule(ct =>
        {
            ct.ThrowIfCancellationRequested();
            var view = viewDefinition.ViewDefinition.Invoke(reference);
            var control = layoutDefinition.ControlsManager.Get(view);
            Areas = Areas.SetItem(reference, control);
            return Task.CompletedTask;
        });

        return reference;
    }
    private LayoutAreaReference RenderArea(LayoutAreaReference reference, ViewElement viewElement) =>
        viewElement switch
        {
            ViewElementWithView view => RenderArea(reference, layoutDefinition.ControlsManager.Get(view.View)),
            ViewElementWithViewDefinition viewDefinition => RenderArea(reference, viewDefinition),
            _ => throw new NotSupportedException($"Unknown type: {viewElement.GetType().FullName}")
        };



    //private UiControl GetControl(LayoutAreaReference request)
    //{
    //    var generator = layoutDefinition.ViewGenerators.FirstOrDefault(g => g.Filter(request));
    //    if (generator == null)
    //        return null;
    //    var control = layoutDefinition.ControlsManager.Get(generator.Generator.Invoke(request));
    //    return control;
    //}

    private void DisposeArea(string area)
    {
        Areas = Areas.RemoveRange(Areas.Keys.Where(a => a.Area.StartsWith(area)));
    }

    public LayoutAreaCollection Render(WorkspaceState state, LayoutAreaReference reference)
    {
        var area = reference.Area;
        DisposeArea(area);
        RenderArea(reference);
        return new(Areas.Where(a => a.Key.Area.StartsWith(area)).ToImmutableDictionary());
    }

}

internal record ViewGenerator(Func<LayoutAreaReference, bool> Filter, ViewElement ViewElement);

public record LayoutDefinition(IMessageHub Hub)
{
    internal LayoutStackControl InitialState { get; init; }
    internal ImmutableList<ViewGenerator> ViewGenerators { get; init; } = ImmutableList<ViewGenerator>.Empty;
    public LayoutDefinition WithInitialState(LayoutStackControl initialState) => this with { InitialState = initialState };
    public LayoutDefinition WithGenerator(Func<LayoutAreaReference, bool> filter, ViewElement viewElement) => this with { ViewGenerators = ViewGenerators.Add(new(filter, viewElement)) };

    public LayoutDefinition WithView(LayoutAreaReference reference, Func<LayoutAreaReference, object> generator) =>
        WithGenerator(reference.Equals,  new ViewElementWithViewDefinition(reference,r => ControlsManager.Get(generator.Invoke(reference))));

    public LayoutDefinition WithView(string area, Func<LayoutAreaReference, object> generator) =>
        WithView(new LayoutAreaReference(area), generator);

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
