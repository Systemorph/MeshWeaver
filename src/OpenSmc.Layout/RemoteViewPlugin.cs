using Microsoft.Extensions.Logging;
using OpenSmc.Application.Scope;
using OpenSmc.Messaging;
using OpenSmc.Scopes.Synchronization;
using OpenSmc.ServiceProvider;

namespace OpenSmc.Layout;

public class RemoteViewPlugin(IMessageHub hub) : GenericUiControlPlugin<RemoteViewControl>(hub),
    IMessageHandler<DataChangedEvent>,
    IMessageHandler<AreaChangedEvent>,
    IMessageHandler<ScopeExpressionChangedEvent>
{
    [Inject] private IUiControlService uiControlService; // TODO V10: call BuildUp(this) in some base? (2023/12/20, Alexander Yolokhov)


    private const string Data = nameof(Data);
    [Inject] private ILogger<RemoteViewPlugin> logger;

    private ApplicationScopeAddress ApplicationScopeAddress => new(LayoutExtensions.FindLayoutHost(Hub.Address));

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await base.StartAsync(cancellationToken);
        FullRefreshFromModelHubAsync();
    }
    private void FullRefreshFromModelHubAsync()
    {
        UpdateState(s => s with { Data = new AreaChangedEvent(Data, CreateUiControlHub(Controls.Spinner())) });
        if (State.Message != null)
            Post(State.Message, o => o.WithTarget(State.RedirectAddress));
        if (State.ViewDefinition != null)
            Hub.Post(new SubscribeToEvaluationRequest(nameof(Data), async () =>
                {
                    var viewElement = await State.ViewDefinition(State.Options);
                    return new AreaChangedEvent(viewElement.Area, viewElement.View, viewElement.Options);
                }),
                o => o.WithTarget(ApplicationScopeAddress));
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

    IMessageDelivery IMessageHandler<DataChangedEvent>.HandleMessage(IMessageDelivery<DataChangedEvent> request)
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
                                                                                   o => o.WithTarget(ApplicationScopeAddress));

            }
            Post( State.View, o => o.ResponseFor(request));
            return request.Processed();
        }


        return base.RefreshView(request);
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

        logger.LogDebug($"Received Changed Expression in area: {areaChanged}");
        UpdateView(areaChanged);
        return request.Processed();

    }


    // TODO V10: Not sure what this is ==> should probably be removed. (31.01.2024, Roland Bürgi)
    //IMessageDelivery IMessageHandler<UpdateRequest<AreaChangedEvent>>.HandleMessage(IMessageDelivery<UpdateRequest<AreaChangedEvent>> request)
    //{
    //    var oldView = State.View;
    //    var newView = State.UpdateView(oldView, request.Message.Element);
    //    if (newView != oldView)
    //    {
    //        UpdateView(newView);
    //    }

    //    // TODO V10: Why should remote view issue data changed?! (09.02.2024, Roland Bürgi)
    //    Post(new DataChangedEvent(hub.Version), o => o.ResponseFor(request));
    //    return request.Processed();
    //}

    public override void Dispose()
    {
        if (State.ViewDefinition != null)
            Post(new UnsubscribeFromEvaluationRequest(Data), o => o.WithTarget(ApplicationScopeAddress));
        base.Dispose();
    }
}