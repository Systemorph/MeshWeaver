using System.Reflection;
using MeshWeaver.Reflection;

namespace MeshWeaver.Messaging;


public static class MessageHubPluginExtensions
{
    internal static readonly HashSet<Type> HandlerTypes =
    [
        typeof(IMessageHandler<>),
        typeof(IMessageHandlerAsync<>)
    ];
    internal static readonly MethodInfo TaskFromResultMethod = ReflectionHelper.GetStaticMethod(
        () => Task.FromResult<IMessageDelivery>(null!)
    );

}
