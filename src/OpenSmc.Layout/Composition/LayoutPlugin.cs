using OpenSmc.Data;
using OpenSmc.Data.Serialization;
using OpenSmc.Messaging;

namespace OpenSmc.Layout.Composition;

public record LayoutExecutionAddress(object Host) : IHostedAddress;

public class LayoutManager
{
    private readonly LayoutArea layoutArea;
    private readonly IMessageHub layoutHub;

    public LayoutDefinition LayoutDefinition { get; }

    public LayoutManager(LayoutArea layoutArea, LayoutDefinition layoutDefinition)
    {
        this.layoutArea = layoutArea;
        LayoutDefinition = layoutDefinition;
        var hub = layoutDefinition.Hub;
        layoutHub = hub.GetHostedHub(new LayoutExecutionAddress(hub.Address));
    }

    private void RenderArea(string area, UiControl control)
    {
        if (control == null)
            return;

        if (control is LayoutStackControl stack)
        {
            foreach (var ve in stack.ViewElements)
                RenderArea($"{area}/{ve.Area}", ve);
            control = stack with
            {
                Areas = stack
                    .ViewElements.Select(ve => new EntityReference(
                        LayoutArea.ControlsCollection,
                        $"{area}/{ve.Area}"
                    ))
                    .ToArray()
            };
        }

        if (control.DataContext != null)
            control = control with { DataContext = layoutArea.UpdateData(control.DataContext) };

        layoutArea.UpdateView(area, control);
    }

    private void RenderArea(string area, ViewElementWithViewDefinition viewDefinition)
    {
        var stream = viewDefinition.ViewDefinition;
        layoutArea.UpdateView(area, new SpinnerControl());
        _ = stream.Subscribe(f =>
            layoutHub.Schedule(async ct =>
            {
                ct.ThrowIfCancellationRequested();
                var control = await f.Invoke(layoutArea);
                RenderArea(area, control);
            })
        );
    }

    private void RenderArea(string area, ViewElement viewElement)
    {
        switch (viewElement)
        {
            case ViewElementWithView view:
                RenderArea(area, LayoutDefinition.ControlsManager.Get(view.View));
                break;
            case ViewElementWithViewDefinition viewDefinition:
                RenderArea(area, viewDefinition);
                break;
            case ViewElementWithViewStream s:
                s.Stream.Invoke(layoutArea).Subscribe(c => RenderArea(area, c));
                break;
            default:
                throw new NotSupportedException($"Unknown type: {viewElement.GetType().FullName}");
        }
    }

    public IObservable<ChangeItem<EntityStore>> Render(LayoutAreaReference reference)
    {
        var viewElement = LayoutDefinition.GetViewElement(reference);
        RenderArea(reference.Area, viewElement);
        return layoutArea.Stream;
    }
}

public interface ILayout
{
    IObservable<ChangeItem<EntityStore>> Render(LayoutAreaReference reference);
}

public class LayoutPlugin : MessageHubPlugin, ILayout
{
    private readonly LayoutDefinition layoutDefinition;

    public LayoutPlugin(IMessageHub hub)
        : base(hub)
    {
        layoutDefinition = Hub
            .Configuration.GetListOfLambdas()
            .Aggregate(new LayoutDefinition(Hub), (x, y) => y.Invoke(x));
    }

    public IObservable<ChangeItem<EntityStore>> Render(LayoutAreaReference reference) =>
        new LayoutManager(new(reference, Hub), layoutDefinition).Render(reference);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await base.StartAsync(cancellationToken);

        foreach (var initialization in layoutDefinition.Initializations)
            await initialization.Invoke(cancellationToken);
    }
}
