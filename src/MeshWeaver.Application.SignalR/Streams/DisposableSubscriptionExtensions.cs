using Orleans.Streams;

namespace MeshWeaver.Application.SignalR.Streams;

public static class DisposableSubscriptionExtensions
{
    public static async Task<IAsyncDisposable> SubscribeDisposableAsync<TMessage>(this IAsyncObservable<TMessage> observable, Func<TMessage, Task> onNextAsync)
    {
        return new DisposableSubscriptionHandle<TMessage>(await observable.SubscribeAsync((msg, _) => onNextAsync(msg)));
    }

    private class DisposableSubscriptionHandle<T>(StreamSubscriptionHandle<T> handle) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await handle.UnsubscribeAsync();
    }
}