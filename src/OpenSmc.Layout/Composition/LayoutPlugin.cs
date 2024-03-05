using System.Collections.Immutable;
using System.Reflection;
using OpenSmc.Messaging;
using OpenSmc.Reflection;
using OpenSmc.ServiceProvider;

namespace OpenSmc.Layout.Composition;

public class LayoutPlugin(IMessageHub hub) :
    UiControlPlugin<LayoutStackControl>(hub),
    IMessageHandler<SetAreaRequest>

{
    [Inject] private IUiControlService uiControlService;
    private readonly LayoutDefinition layoutDefinition;

    public LayoutPlugin(LayoutDefinition layoutDefinition) : this(layoutDefinition.Hub)
    {
        this.layoutDefinition = layoutDefinition;
    }



    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await base.StartAsync(cancellationToken);

        if (layoutDefinition?.InitialState == null)
            return;
        var control = layoutDefinition.InitialState with { Hub = hub1, Address = hub1.Address };

        var areas = control.ViewElements
            .Select
            (
                a => a is ViewElementWithView { View: not null } vv
                    ? SetAreaImpl(null, vv.View, null, null, vv.Options)
                    : a is ViewElementWithViewDefinition { ViewDefinition: not null } vd
                        ? SetAreaImpl(null, null, vd.ViewDefinition, null, vd.Options)
                        :
                        a is ViewElementWithPath vp
                            ? SetAreaImpl(null, null, null, vp.Path, vp.Options)
                            : new AreaChangedEvent(a.Area, null)

            )
            .ToArray();

        control = control with { Areas = areas };
        InitializeState(control);
        await Task.WhenAll(layoutDefinition.Initializations.Select(i => i.Invoke()));
    }

    private AreaChangedEvent GetArea(string area)
    {
        return Control.Areas.FirstOrDefault(x => x.Area == area);
    }

    protected AreaChangedEvent SetAreaImpl(IMessageDelivery request, object view, ViewDefinition viewDefinition, string path, SetAreaOptions options)
    {
        var area = options.Area;
        var deleteView = view == null && viewDefinition == null && path == null;
        var existing = GetArea(area);
        if (existing != null)
        {
            (existing.View as UiControl)?.Hub?.Dispose();
            UpdateState(s => s with { AreasImpl = s.AreasImpl.RemoveAll(a => a.Area == area) });
        }

        if (deleteView)
            return null;

        viewDefinition ??= view as ViewDefinition;

        if (path != null)
            viewDefinition = GetViewDefinition(request);

        var control = viewDefinition != null
                          ? Controls.RemoteView(viewDefinition, options)
                          : view as UiControl ?? uiControlService.GetUiControl(view);


        control = CreateUiControlHub(control);
        Hub.ConnectTo(control.Hub);
        var ret = new AreaChangedEvent(area, control, options.AreaViewOptions);
        UpdateState(state => state.SetAreaToState(ret));

        return ret;
    }

    private ViewDefinition GetViewDefinition(IMessageDelivery request)
    {
        var generator = layoutDefinition.ViewGenerators.FirstOrDefault(g => g.Filter(request));
        return options => Task.FromResult(new ViewElementWithView(generator?.Generator(request, options), options));
    }




    IMessageDelivery IMessageHandler<SetAreaRequest>.HandleMessage(IMessageDelivery<SetAreaRequest> request)
    {
        return SetArea(request, request.Message.Area, request.Message.View, request.Message.ViewDefinition, request.Message.Path, request.Message.Options);
    }

    private IMessageDelivery SetArea(IMessageDelivery request,  string area, object view, ViewDefinition viewDefinition, string path,
        SetAreaOptions options)
    {
        var areaChanged = SetAreaImpl(request, view, viewDefinition, path, options);
        Post(areaChanged ?? new AreaChangedEvent(area, null), o => o.ResponseFor(request ?? request).WithTarget(MessageTargets.Subscribers));
        return request.Processed();
    }

    protected override IMessageDelivery RefreshView(IMessageDelivery<RefreshRequest> request)
    {
        var areaChanged = string.IsNullOrWhiteSpace(request.Message.Area)
                              ? new AreaChangedEvent(request.Message.Area, State)
                              : State.Areas.FirstOrDefault(x => x.Area == request.Message.Area);

        if (areaChanged == null)
        {
            var view = GetViewDefinition(request);
            if (view == null)
            {
                Post(new AreaChangedEvent(request.Message.Area, null), o => o.ResponseFor(request));
                return request.Processed();
            }

            UpdateState(s => s with { AreasImpl = s.AreasImpl.Add(new(request.Message.Area, Controls.Spinner())) });
            return SetArea(request, view);
        }

        Post(areaChanged, o => o.ResponseFor(request));
        return request.Processed();
    }

    protected IMessageDelivery SetArea(IMessageDelivery<RefreshRequest> request, object view)
        => SetArea(request, request.Message.Area, view, new(request.Message.Area));

    protected IMessageDelivery SetArea(IMessageDelivery request, string area, object view, SetAreaOptions options)
    {

        SetArea(request, area, view, null, null, options);
        return request.Processed();
    }

    private static SetAreaRequest GetSetAreaRequest(IMessageDelivery request, SetAreaOptions options, object view)
    {
        if (view is ViewDefinition viewDef)
            return new SetAreaRequest(options, viewDef) { ForwardedRequest = request };
        if (view is Delegate del)
        {
            var returnType = del.Method.ReturnType;
            ViewDefinition viewDefinition;
            if (returnType.IsGenericType && typeof(Task<>).IsAssignableFrom(returnType.GetGenericTypeDefinition()))
            {
                var viewType = returnType.GetGenericArguments().Single();
                if (viewType == typeof(ViewElementWithView))
                {
                    viewDefinition = del.Method.GetParameters().Length switch
                    {
                        0 => async _ => await ((Func<Task<ViewElementWithView>>)del).Invoke(),
                        1 => async o => await ((Func<SetAreaOptions, Task<ViewElementWithView>>)del).Invoke(o),
                        _ => throw new NotSupportedException()
                    };
                }
                else
                {
                    viewDefinition = del.Method.GetParameters().Length switch
                    {
                        0 => o => (Task<ViewElementWithView>)AwaitTaskMethod.MakeGenericMethod(returnType).InvokeAsFunction(o, del.DynamicInvoke()),
                        1 => o => (Task<ViewElementWithView>)AwaitTaskMethod.MakeGenericMethod(returnType).InvokeAsFunction(o, del.DynamicInvoke(o)),
                        _ => throw new NotSupportedException()
                    };


                }

            }
            else
            {
                if (returnType == typeof(ViewElementWithView))
                    viewDefinition = del.Method.GetParameters().Length switch
                    {
                        0 => _ => Task.FromResult((ViewElementWithView)del.DynamicInvoke()),
                        1 => o => Task.FromResult((ViewElementWithView)del.DynamicInvoke(o)),
                        _ => throw new NotSupportedException()
                    };
                else
                    viewDefinition = del.Method.GetParameters().Length switch
                    {
                        0 => o => Task.FromResult<ViewElementWithView>(new(del.DynamicInvoke(), o)),
                        1 => o => Task.FromResult<ViewElementWithView>(new(del.DynamicInvoke(o), o)),
                        _ => throw new NotSupportedException()
                    };
            }

            return new SetAreaRequest(options, viewDefinition);
        }
        return new SetAreaRequest(options, view) { ForwardedRequest = request };
    }

#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
    private static readonly MethodInfo AwaitTaskMethod = ReflectionHelper.GetStaticMethodGeneric(() => AwaitTask<object>(null, null));
    private readonly IMessageHub hub1 = hub;
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
    // ReSharper disable once UnusedMethodReturnValue.Local
    private static async Task<ViewElementWithView> AwaitTask<T>(SetAreaOptions options, Task<T> task) => new(await task, options);




}

internal record ViewGenerator(Func<IMessageDelivery, bool> Filter, Func<IMessageDelivery, SetAreaOptions, object> Generator);

public record LayoutDefinition(IMessageHub Hub) 
{
    internal LayoutStackControl InitialState { get; init; }
    internal ImmutableList<ViewGenerator> ViewGenerators { get; init; } = ImmutableList<ViewGenerator>.Empty;
    public LayoutDefinition WithInitialState(LayoutStackControl initialState) => this with { InitialState = initialState };
    public LayoutDefinition WithGenerator(Func<IMessageDelivery, bool> filter, Func<IMessageDelivery, SetAreaOptions, object> viewGenerator) => this with { ViewGenerators = ViewGenerators.Add(new(filter, viewGenerator)) };

    public LayoutDefinition WithView(string area, Func<IMessageDelivery, SetAreaOptions, object> generator) =>
        WithGenerator(r => r.Message is IRequestWithArea requestWithArea && requestWithArea.Area == area, generator);


    internal ImmutableList<Func<Task>> Initializations { get; init; } = ImmutableList<Func<Task>>.Empty;

    public LayoutDefinition WithInitialization(Func<Task> func)
        => this with { Initializations = Initializations.Add(func) };
}
