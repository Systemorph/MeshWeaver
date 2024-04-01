using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Data;
using OpenSmc.Messaging;

namespace OpenSmc.Layout.Composition;

public interface ILayout : IObservable<LayoutAreaCollection>
{
    IObservable<LayoutAreaCollection> Render(IObservable<WorkspaceState> state, LayoutAreaReference reference);
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

    private readonly IWorkspace workspace = hub.ServiceProvider.GetRequiredService<IWorkspace>();
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

    private LayoutAreaReference RenderArea(IObservable<WorkspaceState> state, LayoutAreaReference reference)
    {
        return RenderArea(reference, layoutDefinition.GetViewElement(reference));
    }


    private UiControl RenderArea(LayoutAreaReference reference, UiControl control)
    {
        if (control == null)
            return null;

        if (control is LayoutStackControl stack)
            control = stack with
            {
                Areas = stack.ViewElements.Select(ve => RenderArea(reference with {Area = $"{reference.Area}/{ve.Reference.Area}"}, ve)).ToArray()
            };

        Areas = Areas.SetItem(reference, control);
        return control;
    }

    private UiControl RenderArea(LayoutAreaReference reference, ViewElementWithViewDefinition viewDefinition)
    {
        layoutHub.Schedule(ct =>
        {
            ct.ThrowIfCancellationRequested();
            var stream = viewDefinition.ViewDefinition.Invoke(workspace.Stream, reference);
            stream.Select(view => 
                    new LayoutAreaCollection(
                        reference, 
                        Areas = Areas.SetItem(reference, layoutDefinition.ControlsManager.Get(view)).Where(a => a.Key.Area.StartsWith(reference.Area)).ToImmutableDictionary()))
                .DistinctUntilChanged()
                .Subscribe(areaSubject);
            return Task.CompletedTask;
        });

        return new SpinnerControl();
    }




    private LayoutAreaReference RenderArea(LayoutAreaReference reference, ViewElement viewElement)
    {
        Areas = Areas.SetItem(reference, viewElement switch
        {
            ViewElementWithView view => RenderArea(reference, layoutDefinition.ControlsManager.Get(view.View)),
            ViewElementWithViewDefinition viewDefinition => RenderArea(reference, viewDefinition),
            _ => throw new NotSupportedException($"Unknown type: {viewElement.GetType().FullName}")
        });
        return reference;
    }


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

    public IObservable<LayoutAreaCollection> Render(IObservable<WorkspaceState> state, LayoutAreaReference reference)
    {
        DisposeArea(reference.Area);
        RenderArea(state, reference);
        return areaSubject.Where(a => a.Reference.Equals(reference));
    }



    private readonly ReplaySubject<LayoutAreaCollection> areaSubject = new(1);
    public IDisposable Subscribe(IObserver<LayoutAreaCollection> observer)
    {
        return areaSubject.Subscribe(observer);
    }
}

