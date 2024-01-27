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

    public static MessageHubConfiguration AddPlugin<TPlugin>(this MessageHubConfiguration configuration)
        where TPlugin : class, IMessageHubPlugin
        => configuration
        .WithServices(s => 
        { 
            s.TryAdd(ServiceDescriptor.Transient<TPlugin, TPlugin>()); 
            return s; 
        })
        .WithBuildupAction(async hub =>
        {
            var plugin = hub.ServiceProvider.GetRequiredService<TPlugin>();
            await plugin.InitializeAsync(hub);
            hub.Set(plugin);
        })
        .WithDisposeAction(hub => hub.Get<TPlugin>().DisposeAsync());
    public static MessageHubConfiguration AddPlugin<TPlugin>(this MessageHubConfiguration configuration, Func<IMessageHub, Task<TPlugin>> factory)
        => configuration.WithBuildupAction(factory);
    public static MessageHubConfiguration AddPlugin<TPlugin>(this MessageHubConfiguration configuration, Func<IMessageHub, TPlugin> factory)
        => configuration.WithBuildupAction(hub => Task.FromResult(factory(hub)));

}
