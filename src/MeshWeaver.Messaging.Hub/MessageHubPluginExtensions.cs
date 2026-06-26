using System.Reflection;
using MeshWeaver.Reflection;

namespace MeshWeaver.Messaging;


/// <summary>
/// Internal reflection helpers for wiring message handler plugins into the hub's
/// reactive rule chain — the set of handler interface types and the
/// <c>Observable.Return</c> method used to lift synchronous handler results into
/// the observable handler shape.
/// </summary>
public static class MessageHubPluginExtensions
{
    internal static readonly HashSet<Type> HandlerTypes =
    [
        typeof(IMessageHandler<>),
        typeof(IMessageHandlerAsync<>)
    ];
    // Wraps a synchronous IMessageHandler<>.HandleMessage result (IMessageDelivery)
    // into the reactive rule-chain shape (IObservable<IMessageDelivery>) — the
    // Observable.Return analogue of the old Task.FromResult bridge.
    internal static readonly MethodInfo ObservableReturnMethod = ReflectionHelper.GetStaticMethod(
        () => System.Reactive.Linq.Observable.Return<IMessageDelivery>(null!)
    );

}
