using OpenSmc.Layout.Views;
using OpenSmc.Messaging;

namespace OpenSmc.Layout;



public class UiControlPlugin<TControl> : MessageHubPlugin<UiControlPlugin<TControl>, TControl>,
    IMessageHandler<GetRequest<TControl>>,
    IMessageHandler<RefreshRequest>,
    IMessageHandlerAsync<ClickedEvent>
    where TControl : UiControl
{
    private Action scopeDispose;
    protected TControl Control => State;



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
        if (string.IsNullOrWhiteSpace(request.Message.Area))
        {
            Post(new AreaChangedEvent(request.Message.Area, State), o => o.ResponseFor(request));
            return request.Processed();
        }

        return request.NotFound();
    }



    public TControl2 CreateUiControlHub<TControl2>(TControl2 control)
        where TControl2 : UiControl
    {
        if (control == null)
            return null;
        var address = new UiControlAddress(control.Id, Hub.Address);
        control = control with { Address = address };


        var hub = control.CreateHub(Hub.ServiceProvider);

        return control with { Hub = hub, Address = address };
    }


    async Task<IMessageDelivery> IMessageHandlerAsync<ClickedEvent>.HandleMessageAsync(
        IMessageDelivery<ClickedEvent> delivery)
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

    protected UiControlPlugin(IMessageHub hub) : base(hub)
    {
    }
}