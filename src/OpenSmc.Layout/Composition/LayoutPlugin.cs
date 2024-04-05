using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Data;
using OpenSmc.Messaging;

namespace OpenSmc.Layout.Composition;

public interface ILayout : IObservable<EntityStore>
{
    IObservable<EntityStore> Render(IObservable<WorkspaceState> state, LayoutAreaReference reference);
}

public record LayoutAddress(object Host) : IHostedAddress;

public class LayoutPlugin(IMessageHub hub) 
    : MessageHubPlugin(hub), 
    ILayout
{
    //private ImmutableDictionary<string, UiControl> Areas { get; set; } = ImmutableDictionary<string, UiControl>.Empty;
    private static readonly string ControlsCollection = typeof(UiControl).FullName;
    private readonly LayoutDefinition layoutDefinition =
        hub.Configuration.GetListOfLambdas().Aggregate(new LayoutDefinition(hub), (x, y) => y.Invoke(x));

    private readonly IMessageHub layoutHub =
        hub.GetHostedHub(new LayoutAddress(hub.Address));

    private readonly IWorkspace workspace = hub.ServiceProvider.GetRequiredService<IWorkspace>();
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await base.StartAsync(cancellationToken);

        if (layoutDefinition.InitialState == null)
            return;
        var control = layoutDefinition.InitialState;
        if(control != null)
            RenderArea(new EntityStore{Reference = new LayoutAreaReference(string.Empty)}, string.Empty, control);

        foreach (var initialization in layoutDefinition.Initializations)
            await initialization.Invoke(cancellationToken);
    }

    private void RenderArea(IObservable<WorkspaceState> state, LayoutAreaReference reference)
    {
        var ret = new EntityStore{Reference = reference};
        areaSubject.OnNext(RenderArea(ret, reference.Area, layoutDefinition.GetViewElement(reference)));
    }


    private EntityStore RenderArea(EntityStore collection, string area, UiControl control)
    {
        if (control == null)
            return null;

        if (control is LayoutStackControl stack)
        {
            collection = stack.ViewElements.Aggregate(collection, (c, ve) => RenderArea(c, $"{area}/{ve.Area}", ve));
            control = stack with
            {
                Areas = stack.ViewElements
                    .Select(ve => new EntityReference(ControlsCollection, $"{area}/{ve.Area}")).ToArray()
            };
        }
        return collection.UpdateCollection(ControlsCollection, c => c.SetItem(area, control));
    }

    private EntityStore RenderArea(EntityStore collection, string area, ViewElementWithViewDefinition viewDefinition, LayoutAreaReference reference)
    {
        var ret = collection.UpdateCollection(ControlsCollection, i =>  i.SetItem(area, new SpinnerControl() ));
        layoutHub.Schedule(ct =>
        {
            ct.ThrowIfCancellationRequested();
            var stream = viewDefinition.ViewDefinition.Invoke(workspace.Stream, reference with {Area = area});
            stream.Select(view => RenderArea(collection, area, view))
                .DistinctUntilChanged()
                .Subscribe(areaSubject);
            return Task.CompletedTask;
        });
        return ret;
    }


    private EntityStore RenderArea(EntityStore collection, string area, object view)
        => RenderArea(collection, area, layoutDefinition.ControlsManager.Get(view));

    private EntityStore RenderArea(EntityStore collection, string area, ViewElement viewElement)
        => viewElement switch
        {
            ViewElementWithView view => RenderArea(collection, area, layoutDefinition.ControlsManager.Get(view.View)),
            ViewElementWithViewDefinition viewDefinition => RenderArea(collection, area, viewDefinition),
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


    public IObservable<EntityStore> Render(IObservable<WorkspaceState> state, LayoutAreaReference reference)
    {
        RenderArea(state, reference);
        return areaSubject;
    }



    private readonly ReplaySubject<EntityStore> areaSubject = new(1);
    public IDisposable Subscribe(IObserver<EntityStore> observer)
    {
        return areaSubject.Subscribe(observer);
    }
}

