using OpenSmc.Layout.Views;
using OpenSmc.Messaging;

namespace OpenSmc.Layout;



public class UiControlPlugin<TControl> : MessageHubPlugin<TControl>,
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
            Post(new LayoutArea(request.Message.Area, State), o => o.ResponseFor(request));
            return request.Processed();
        }

        return request.NotFound();
    }





    async Task<IMessageDelivery> IMessageHandlerAsync<ClickedEvent>.HandleMessageAsync(IMessageDelivery<ClickedEvent> delivery, CancellationToken cancellationToken)
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