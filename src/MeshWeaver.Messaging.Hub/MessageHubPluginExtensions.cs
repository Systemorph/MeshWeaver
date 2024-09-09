using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
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
        () => Task.FromResult<IMessageDelivery>(null)
    );

    public static MessageHubConfiguration AddPlugin<TPlugin>(
        this MessageHubConfiguration configuration,
        Func<IMessageHub, TPlugin> factory)
        where TPlugin : class, IMessageHubPlugin
    {
        if (configuration.PluginFactories.Any(x => x.Type == typeof(TPlugin)))
            return configuration;
        return configuration.WithServices(services => services.AddScoped<TPlugin>()) with
        {
            PluginFactories = configuration.PluginFactories.Add(
                (typeof(TPlugin), factory)
            )
        };
    }

}
