using OpenSmc.Data;
using OpenSmc.Data.Serialization;
using OpenSmc.Messaging;

namespace OpenSmc.Layout.Composition;

public interface ILayout
{
    IMessageHub Hub { get; }
    IObservable<ChangeItem<EntityStore>> Render(LayoutAreaReference reference);
    internal void RenderArea(LayoutArea layoutArea, string area, UiControl control);

}

public record LayoutExecutionAddress(object Host) : IHostedAddress;

public class LayoutPlugin(IMessageHub hub) 
    : MessageHubPlugin(hub), 
    ILayout
{

    //private ImmutableDictionary<string, UiControl> Areas { get; set; } = ImmutableDictionary<string, UiControl>.Empty;
    private readonly LayoutDefinition layoutDefinition =
        hub.Configuration.GetListOfLambdas().Aggregate(new LayoutDefinition(hub), (x, y) => y.Invoke(x));

    private readonly IMessageHub layoutHub =
        hub.GetHostedHub(new LayoutExecutionAddress(hub.Address));

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await base.StartAsync(cancellationToken);

        if (layoutDefinition.InitialState == null)
            return;
        var control = layoutDefinition.InitialState;
        if(control != null)
            RenderArea(new(this,new(string.Empty)), string.Empty, control);

        foreach (var initialization in layoutDefinition.Initializations)
            await initialization.Invoke(cancellationToken);
    }

    void ILayout.RenderArea(LayoutArea layoutArea, string area, UiControl control)
        => RenderArea(layoutArea, area, control);

    private void RenderArea(LayoutArea layoutArea, string area, UiControl control)
    {
        if (control == null) return;

        if (control is LayoutStackControl stack)
        {
            foreach (var ve in stack.ViewElements)
                RenderArea(layoutArea, $"{area}/{ve.Area}", ve);
            control = stack with
            {
                Areas = stack.ViewElements
                    .Select(ve => new EntityReference(LayoutArea.ControlsCollection, $"{area}/{ve.Area}")).ToArray()
            };
        }

        if (control.DataContext != null) 
            control = control with { DataContext = layoutArea.UpdateData(control.DataContext) };

        layoutArea.UpdateView(area, control);
    }

    private void RenderArea(LayoutArea layoutArea, string area, ViewElementWithViewDefinition viewDefinition)
    {
        var stream = viewDefinition.ViewDefinition;
        layoutArea.UpdateView(area, new SpinnerControl());
        stream.Subscribe(
            f => layoutHub.Schedule(async ct =>
            {
                ct.ThrowIfCancellationRequested();
                var control = await f.Invoke(layoutArea);
                RenderArea(layoutArea, area, control);
            })
        );
    }

    private void RenderArea(LayoutArea layoutArea, string area, ViewElement viewElement)
    {
         switch(viewElement)
        {
            case ViewElementWithView view:
                RenderArea(layoutArea, area,
                    layoutDefinition.ControlsManager.Get(view.View));
                break;
            case ViewElementWithViewDefinition viewDefinition:
                RenderArea(layoutArea, area, viewDefinition);
                    break;
            case ViewElementWithViewStream s:
                s.Stream.Invoke(layoutArea)
                    .Subscribe(c => RenderArea(layoutArea, area, c));
                break;
            default: throw new NotSupportedException($"Unknown type: {viewElement.GetType().FullName}");

        }

    }






    public IObservable<ChangeItem<EntityStore>> Render(LayoutAreaReference reference)
    {
        var viewElement = layoutDefinition.GetViewElement(reference);
        var area = new LayoutArea(this, reference);
        RenderArea(area, reference.Area, viewElement);
        return area.Stream;
    }
}

