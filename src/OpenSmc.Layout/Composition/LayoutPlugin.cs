using OpenSmc.Data;
using OpenSmc.Messaging;

namespace OpenSmc.Layout.Composition;

public interface ILayout
{
    IObservable<EntityStore> Render(LayoutAreaReference reference);
}

public record LayoutAddress(object Host) : IHostedAddress;

public class LayoutPlugin(IMessageHub hub) 
    : MessageHubPlugin(hub), 
    ILayout
{

    //private ImmutableDictionary<string, UiControl> Areas { get; set; } = ImmutableDictionary<string, UiControl>.Empty;
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
            RenderArea(new(new(string.Empty)), string.Empty, control);

        foreach (var initialization in layoutDefinition.Initializations)
            await initialization.Invoke(cancellationToken);
    }



    private LayoutArea RenderArea(LayoutArea layoutArea, string area, UiControl control)
    {
        if (control == null)
            return null;

        if (control is LayoutStackControl stack)
        {
            layoutArea = stack.ViewElements.Aggregate(layoutArea, (c, ve) => RenderArea(c, $"{area}/{ve.Area}", ve));
            control = stack with
            {
                Areas = stack.ViewElements
                    .Select(ve => new EntityReference(LayoutArea.ControlsCollection, $"{area}/{ve.Area}")).ToArray()
            };
        }

        layoutArea.UpdateView(area, control);
        return layoutArea;
    }

    private LayoutArea RenderArea(LayoutArea layoutArea, string area, ViewElementWithViewDefinition viewDefinition)
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
        return layoutArea;
    }


    //private LayoutArea RenderArea(LayoutArea collection, string area, object view)
    //    => RenderArea(collection, area, layoutDefinition.ControlsManager.Get(view));

    private LayoutArea RenderArea(LayoutArea collection, string area, ViewElement viewElement)
        => viewElement switch
        {
            ViewElementWithView view => RenderArea(collection, area, layoutDefinition.ControlsManager.Get(view.View)),
            ViewElementWithViewDefinition viewDefinition => RenderArea(collection, area, viewDefinition),
            _ => throw new NotSupportedException($"Unknown type: {viewElement.GetType().FullName}")
        };







    public IObservable<EntityStore> Render(LayoutAreaReference reference)
    {
        var ret = RenderArea(new LayoutArea(reference), reference.Area, layoutDefinition.GetViewElement(reference));
        return ret.Stream;
    }
}

