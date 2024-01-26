using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Application.Scope;
using OpenSmc.Layout.Composition;
using OpenSmc.Layout.Views;
using OpenSmc.Messaging;
using OpenSmc.Scopes.Synchronization;
using OpenSmc.ServiceProvider;

namespace OpenSmc.Layout;



public class UiControlPlugin<TControl> : IMessageHandler<GetRequest<TControl>>,
                                                  IMessageHandler<RefreshRequest>,
                                                  IMessageHandlerAsync<ClickedEvent>
    where TControl : UiControl
{
    private Action scopeDispose;
    protected TControl State { get; private set; }
    protected void UpdateState(Func<TControl, TControl> changes) => State = changes.Invoke(State);
    protected TControl Control => State;
    protected ApplicationVariable Application { get; set; }


    protected IMessageHub Hub { get; set; }
    public virtual Task InitializeAsync(IMessageHub hub, TControl control)
    {
        Hub = hub;
        hub.ServiceProvider.Buildup(this);
        hub.RegisterHandlersFromInstance(this);
        Application = Hub.ServiceProvider.GetRequiredService<ApplicationVariable>();
        State = control;
        var address = new ApplicationScopeAddress(Application.Host.Address);
        var internalMutableScopes = TypeScanner.ScanFor<IInternalMutableScope>(control.DataContext).ToArray();

        if (internalMutableScopes.Any())
        {
            foreach (var internalMutableScope in internalMutableScopes)
                Post(new SubscribeScopeRequest(internalMutableScope), o => o.WithTarget(address));


            scopeDispose = () =>
            {
                foreach (var internalMutableScope in internalMutableScopes)
                    Post(new UnsubscribeScopeRequest(internalMutableScope), o => o.WithTarget(address));
            };

        }

        return Task.FromResult(this);
    }

    public IMessageDelivery HandleMessage(IMessageDelivery<GetRequest<TControl>> request)
    {
        Post(State, o => o.ResponseFor(request));
        return request.Processed();
    }

    public virtual void Dispose()
    {
        ((IDisposable)State).Dispose();
        scopeDispose?.Invoke();
    }

    IMessageDelivery IMessageHandler<RefreshRequest>.HandleMessage(IMessageDelivery<RefreshRequest> request)
    {
        return RefreshView(request);
    }

    protected virtual IMessageDelivery RefreshView(IMessageDelivery<RefreshRequest> request)
    {
        Post(new AreaChangedEvent(request.Message.Area, State), o => o.ResponseFor(request));
        return request.Processed();
    }



    public async Task<(TControl2 Control, IMessageHub Hub)> CreateUiControlHub<TControl2>(TControl2 control, string area)
    where TControl2 : UiControl
    {
        if (control == null)
            return default;
        var address = new UiControlAddress(control.Id, Hub.Address);
        control = control with { Address = address };


        var hub = control.GetHub(Hub.ServiceProvider);

        var dataChanged = await hub.AwaitResponse<DataChanged>(new CreateRequest<UiControl>(control) { Options = area });
        return ((TControl2)dataChanged.Changes, hub);
    }


    async Task<IMessageDelivery> IMessageHandlerAsync<ClickedEvent>.HandleMessageAsync(IMessageDelivery<ClickedEvent> delivery)
    {
        try
        {
            await State.ClickAsync(new UiActionContext(delivery.Message.Payload, Hub));
        }
        catch (Exception e)
        {
            // TODO V10: How to fault it? (2023/06/25, Roland Buergi)
            return delivery.Failed($"Exception in click action: \n{e}");
            //errorDelivery.ChangeDeliveryState(MessageDeliveryState.Failed);
        }
        return delivery.Processed();
    }

    public IMessageDelivery<TMessage> Post<TMessage>(TMessage message, Func<PostOptions, PostOptions> options = null)
    {
        return Hub.Post(message, options);
    }

}