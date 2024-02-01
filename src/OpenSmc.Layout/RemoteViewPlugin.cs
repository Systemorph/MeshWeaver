using OpenSmc.Application.Scope;
using OpenSmc.Messaging;
using OpenSmc.Scopes.Synchronization;
using OpenSmc.ServiceProvider;

namespace OpenSmc.Layout;

public class RemoteViewPlugin(IMessageHub hub) : GenericUiControlPlugin<RemoteViewControl>(hub),
    IMessageHandler<DataChanged>,
    IMessageHandler<AreaChangedEvent>,
    IMessageHandler<UpdateRequest<AreaChangedEvent>>,
    IMessageHandler<ScopeExpressionChangedEvent>
{
    [Inject] private IUiControlService uiControlService; // TODO V10: call BuildUp(this) in some base? (2023/12/20, Alexander Yolokhov)

    private ExpressionSynchronizationAddress ExpressionSynchronizationAddress =>
        LayoutExtensions.ExpressionSynchronizationAddress(Hub.Address);

    public override void InitializeState(RemoteViewControl control)
    {
        base.InitializeState(control);
        FullRefreshFromModelHubAsync();
    }

    private void FullRefreshFromModelHubAsync()
    {
        if (State.Message != null)
            Post(State.Message, o => o.WithTarget(State.RedirectAddress));
        if (State.ViewDefinition != null)
            Hub.Post(new SubscribeToEvaluationRequest(nameof(Data), async () =>
                                                                               {
                                                                                   var viewElement = await State.ViewDefinition(State.Options);
                                                                                   return new AreaChangedEvent(viewElement.Area, viewElement.View, viewElement.Options);
                                                                               }),
                o => o.WithTarget(ExpressionSynchronizationAddress));
    }

    private const string Data = nameof(Data);

    public override async Task StartAsync()
    {
        await base.StartAsync();
        UpdateState(s => s with {Data = new AreaChangedEvent(Data, CreateUiControlHub(Controls.Spinner()))});
    }

    private void UpdateView(AreaChangedEvent areaChanged)
    {
        var oldView = State.View;

        if (oldView?.View == null && areaChanged.View == null)
            return;


        if (oldView?.View != null && oldView.View.Equals(areaChanged.View))
            return;


        if (State.View?.View is UiControl existingControl)
            existingControl.Hub?.Dispose();

        
        if (areaChanged.View is not UiControl uiControl)
            areaChanged = areaChanged with { Area = Data };
        else
        {
            uiControl = CreateUiControlHub(uiControl);
            Hub.ConnectTo(uiControl.Hub);
            areaChanged = areaChanged with { Area = Data, View = uiControl };

        }

        UpdateState(s => s with { View = areaChanged });
        Post(State.View, o => o.WithTarget(MessageTargets.Subscribers));
    }

    IMessageDelivery IMessageHandler<DataChanged>.HandleMessage(IMessageDelivery<DataChanged> request)
    {
        if (!request.Sender.Equals(State.RedirectAddress))
            return request.Ignored();

        State.UpdateFunction(request, Hub);
        return request.Processed();
    }

    protected override IMessageDelivery RefreshView(IMessageDelivery<RefreshRequest> request)
    {
        if (request.Message.Area == State.View.Area)
        {
            if (request.Message.Options is RemoteViewRefreshOptions { ForceRefresh: true })
            {
                Hub.Post(new SubscribeToEvaluationRequest(nameof(Data), async () =>
                                                                                   {
                                                                                       var viewElement = await State.ViewDefinition(State.Options);
                                                                                       return new AreaChangedEvent(viewElement.Area, viewElement.View, viewElement.Options);
                                                                                   }),
                                                                                   o => o.WithTarget(ExpressionSynchronizationAddress));

            }
            Post( State.View, o => o.ResponseFor(request));
            return request.Processed();
        }

        Post(new AreaChangedEvent(request.Message.Area, State), o => o.ResponseFor(request));
        return request.Processed();
    }

    public IMessageDelivery HandleMessage(IMessageDelivery<AreaChangedEvent> request)
    {
        if (request.Sender.Equals(State.RedirectAddress) && request.Message.Area == State.RedirectArea)
            UpdateView(request.Message);
        var address = (State.View.View as UiControl)?.Address;
        if (address != null && request.Sender.Equals(address))
        {
            if (request.Message.Area != "")
                Post(new RefreshRequest(), o => o.WithTarget(address));
            else
            {
                UpdateState(s => s with
                                 {
                                     View = s.View with { View = request.Message.View }
                                 });
            }
        }
        return request.Processed();
    }

    IMessageDelivery IMessageHandler<ScopeExpressionChangedEvent>.HandleMessage(IMessageDelivery<ScopeExpressionChangedEvent> request)
    {
        var areaChanged = request.Message.Value is AreaChangedEvent ae
                              ? ae with { Area = Data }
                              : new AreaChangedEvent(Data, request.Message.Status == ExpressionChangedStatus.Evaluating ? CreateUiControlHub(Controls.Spinner()) : request.Message.Value);

        if (areaChanged.View != null && areaChanged.View is not IUiControl)
            areaChanged = areaChanged with { View = uiControlService.GetUiControl(areaChanged.View) };
        
        UpdateView(areaChanged);
        return request.Processed();

    }


    // TODO V10: Not sure what this is ==> should probably be removed. (31.01.2024, Roland Bürgi)
    IMessageDelivery IMessageHandler<UpdateRequest<AreaChangedEvent>>.HandleMessage(IMessageDelivery<UpdateRequest<AreaChangedEvent>> request)
    {
        var oldView = State.View;
        var newView = State.UpdateView(oldView, request.Message.Element);
        if (newView != oldView)
        {
            UpdateView(newView);
        }

        Post(new DataChanged(null), o => o.ResponseFor(request));
        return request.Processed();
    }

    public override void Dispose()
    {
        if (State.ViewDefinition != null)
            Post(new UnsubscribeFromEvaluationRequest(Data), o => o.WithTarget(ExpressionSynchronizationAddress));
        base.Dispose();
    }
}