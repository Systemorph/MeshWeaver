using OpenSmc.Data;
using OpenSmc.Data.Serialization;
using OpenSmc.Layout.Views;
using OpenSmc.Messaging;

namespace OpenSmc.Layout.Composition;

public record LayoutExecutionAddress(object Host) : IHostedAddress;

public class LayoutManager
{
    private readonly LayoutArea layoutArea;
    private readonly IChangeStream<EntityStore, LayoutAreaReference> changeStream;
    private readonly IMessageHub layoutHub;

    public LayoutDefinition LayoutDefinition { get; }

    public LayoutManager(
        LayoutArea layoutArea,
        LayoutDefinition layoutDefinition,
        IChangeStream<EntityStore, LayoutAreaReference> changeStream
    )
    {
        this.layoutArea = layoutArea;
        LayoutDefinition = layoutDefinition;
        this.changeStream = changeStream;
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
                Areas = stack.ViewElements.Select(ve => $"{area}/{ve.Area}").ToArray()
            };
        }

        if (control.DataContext != null)
            control = control with { DataContext = layoutArea.UpdateData(control.DataContext) };

        layoutArea.Update(area, control);
    }

    private void RenderArea(string area, ViewElementWithViewDefinition viewDefinition)
    {
        var stream = viewDefinition.ViewDefinition;
        layoutArea.Update(area, new SpinnerControl());
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

    public IChangeStream<EntityStore, LayoutAreaReference> Render(LayoutAreaReference reference)
    {
        var viewElement = LayoutDefinition.GetViewElement(reference);
        if (viewElement == null)
            return changeStream;
        RenderArea(reference.Area, viewElement);
        changeStream.AddDisposable(layoutArea.Stream.Subscribe(changeStream));
        changeStream.AddDisposable(
            changeStream.Hub.Register<ClickedEvent>(
                (request) =>
                {
                    var control = changeStream.Current.Value.GetControl(request.Message.Area);
                    if (control == null)
                        return request.Ignored();
                    try
                    {
                        control.ClickAction.Invoke(
                            new(request.Message.Payload, LayoutDefinition.Hub, layoutArea)
                        );
                    }
                    catch (Exception e)
                    {
                        request.Failed(e.Message);
                    }
                    return request.Processed();
                },
                d =>
                    changeStream.Id.Equals(d.Message.Id)
                    && changeStream.Reference.Equals(d.Message.Reference)
            )
        );

        return changeStream;
    }
}

public interface ILayout
{
    IChangeStream<EntityStore, LayoutAreaReference> Render(
        IChangeStream<EntityStore, LayoutAreaReference> changeStream,
        LayoutAreaReference reference
    );
}

public sealed class LayoutPlugin : MessageHubPlugin, ILayout
{
    private readonly LayoutDefinition layoutDefinition;

    public LayoutPlugin(IMessageHub hub)
        : base(hub)
    {
        layoutDefinition = Hub
            .Configuration.GetListOfLambdas()
            .Aggregate(new LayoutDefinition(Hub), (x, y) => y.Invoke(x));
    }

    public IChangeStream<EntityStore, LayoutAreaReference> Render(
        IChangeStream<EntityStore, LayoutAreaReference> changeStream,
        LayoutAreaReference reference
    ) => new LayoutManager(new(reference, Hub), layoutDefinition, changeStream).Render(reference);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await base.StartAsync(cancellationToken);

        foreach (var initialization in layoutDefinition.Initializations)
            await initialization.Invoke(cancellationToken);
    }
}
