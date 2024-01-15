using OpenSmc.ServiceProvider;

namespace OpenSmc.Messaging.Hub
{
    public interface IMessageHubPlugin
    {
        Task InitializeAsync(IMessageHub hub);
    }

    public class MessageHubPlugin<TPlugin> : IMessageHubPlugin
        where TPlugin : MessageHubPlugin<TPlugin>
    {
        protected object Address => Hub.Address;
        protected IMessageHub Hub { get; private set; }
        public virtual Task InitializeAsync(IMessageHub hub)
        {
            Hub = hub;
            hub.ServiceProvider.Buildup(this);
            hub.RegisterHandlersFromInstance(this);
            return Task.CompletedTask;
        }

        protected IMessageDelivery<TMessage> Post<TMessage>(TMessage message, Func<PostOptions, PostOptions> options) => Hub.Post(message, options);

        protected void PluginStarted()
        {
            // TODO V10: Implement mechanism which releases a deferral here (2023/12/25, Roland Buergi)
        }
    }


    public class MessageHubPlugin<TPlugin, TState> : MessageHubPlugin<TPlugin>, IAsyncDisposable
        where TPlugin : MessageHubPlugin<TPlugin, TState>
    {
        public TState State { get; private set; }
        protected TPlugin This => (TPlugin)this;

        protected TPlugin UpdateState(Func<TState, TState> changes)
        {
            State = changes.Invoke(State);
            return This;
        }

        public override async Task InitializeAsync(IMessageHub hub)
        {
            await base.InitializeAsync(hub);
            if (State == null)
            {
                var constructor = typeof(TState).GetConstructor(Array.Empty<Type>());
                if (constructor != null)
                    InitializeState(Activator.CreateInstance<TState>());
            }
        }

        public virtual TPlugin InitializeState(TState state)
        {
            State = state;
            return This;
        }

        public virtual ValueTask DisposeAsync()
        {
            return default;
        }
    }
}
