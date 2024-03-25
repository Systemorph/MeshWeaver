using System.Collections.Immutable;
using OpenSmc.Data;
using OpenSmc.Messaging;

namespace OpenSmc.Layout.Composition;

public interface ILayout
{
    InstanceCollection Render(AreaReference reference);
}
public class LayoutPlugin(LayoutDefinition layoutDefinition) 
    : MessageHubPlugin(layoutDefinition.Hub), 
    ILayout
{
    private ImmutableDictionary<string, UiControl> Areas { get; set; } = ImmutableDictionary<string, UiControl>.Empty;


    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await base.StartAsync(cancellationToken);

        if (layoutDefinition.InitialState == null)
            return;
        var control = layoutDefinition.InitialState;
        if(control != null)
            RenderControl(new(string.Empty), control);

        foreach (var initialization in layoutDefinition.Initializations)
            await initialization.Invoke(cancellationToken);
    }

    private AreaReference RenderControl(AreaReference reference, UiControl control)
    {
        if (control == null)
            return null;

        if (control is LayoutStackControl stack)
            control = stack with
            {
                Areas = stack.ViewElements
                    .Select(ve => RenderControl(reference with {Id = $"{reference.Id}/{ve.Area}" }, ParseControl(reference, ve))).ToArray()
            };

        Areas = Areas.SetItem(reference.Id.ToString(), control);
        return reference;
    }

    private UiControl ParseControl(AreaReference request, ViewElement a)
        => a switch
        {
            ViewElementWithView ve => layoutDefinition.UiControlsManager.GetUiControl(ve.View),
            ViewElementWithViewDefinition ve => layoutDefinition.UiControlsManager.GetUiControl(ve.ViewDefinition.Invoke(request)),
            _ => throw new NotSupportedException()
        };


    private UiControl GetControl(AreaReference request)
    {
        var generator = layoutDefinition.ViewGenerators.FirstOrDefault(g => g.Filter(request));
        if (generator == null)
            return null;
        var control = layoutDefinition.UiControlsManager.GetUiControl(generator.Generator.Invoke(request));
        return control;
    }

    private void DisposeArea(string area)
    {
        Areas = Areas.RemoveRange(Areas.Keys.Where(a => a.StartsWith(area)));
    }

    public InstanceCollection Render(AreaReference reference)
    {
        var area = reference.Area;
        DisposeArea(area);
        RenderArea(reference);
        return new(Areas.Where(a => a.Key.StartsWith(area)).ToImmutableDictionary(x => (object)x.Key, x => (object)x.Value));
    }

    private void RenderArea(AreaReference reference)
    {
    }
}

internal record ViewGenerator(Func<AreaReference, bool> Filter, ViewDefinition Generator);

public record LayoutDefinition(IMessageHub Hub)
{
    internal LayoutStackControl InitialState { get; init; }
    internal ImmutableList<ViewGenerator> ViewGenerators { get; init; } = ImmutableList<ViewGenerator>.Empty;
    public LayoutDefinition WithInitialState(LayoutStackControl initialState) => this with { InitialState = initialState };
    public LayoutDefinition WithGenerator(Func<AreaReference, bool> filter, ViewDefinition viewGenerator) => this with { ViewGenerators = ViewGenerators.Add(new(filter, viewGenerator)) };

    public LayoutDefinition WithView(string area, Func<AreaReference, object> generator) =>
        WithGenerator(r => r.Area == area, r => UiControlsManager.GetUiControl(generator.Invoke(r)));


    internal ImmutableList<Func<CancellationToken, Task>> Initializations { get; init; } = ImmutableList<Func<CancellationToken, Task>>.Empty;

    public LayoutDefinition WithInitialization(Func<CancellationToken, Task> func)
        => this with { Initializations = Initializations.Add(func) };

    internal UiControlsManager UiControlsManager { get; } = new();

    public LayoutDefinition WithUiControls(Action<UiControlsManager> configuration)
    {
        configuration.Invoke(UiControlsManager);
        return this;
    }
}
