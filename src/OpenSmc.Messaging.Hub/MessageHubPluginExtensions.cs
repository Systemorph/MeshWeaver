using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenSmc.Messaging.Hub;
using OpenSmc.Reflection;
using System.Reflection;

namespace OpenSmc.Messaging;

public static class MessageHubPluginExtensions
{
    internal static readonly HashSet<Type> HandlerTypes = new() { typeof(IMessageHandler<>), typeof(IMessageHandlerAsync<>) };
    internal static readonly MethodInfo TaskFromResultMethod = ReflectionHelper.GetStaticMethod(() => Task.FromResult<IMessageDelivery>(null));

    public static MessageHubConfiguration AddPlugin<TPlugin>(this MessageHubConfiguration configuration, Func<IMessageHub, Task<TPlugin>> factory)
        where TPlugin : class, IMessageHubPlugin
        => configuration
        .WithServices(s => 
        { 
            s.TryAdd(ServiceDescriptor.Transient<TPlugin, TPlugin>()); 
            return s; 
        })
        .WithBuildupAction(async hub =>
        {
            var plugin = await factory(hub);
            await hub.AddPluginAsync(plugin);
        });

    public static MessageHubConfiguration AddPlugin<TPlugin>(this MessageHubConfiguration configuration) 
        where TPlugin : class, IMessageHubPlugin 
        => configuration.AddPlugin(hub => Task.FromResult(hub.ServiceProvider.GetRequiredService<TPlugin>()));

    public static MessageHubConfiguration AddPlugin<TPlugin>(this MessageHubConfiguration configuration, Func<IMessageHub, TPlugin> factory) 
        where TPlugin : class, IMessageHubPlugin
        => configuration.AddPlugin(hub => Task.FromResult(factory.Invoke(hub)));

}
