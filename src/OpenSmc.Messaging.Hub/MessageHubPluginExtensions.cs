using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace OpenSmc.Messaging.Hub;

public static class MessageHubPluginExtensions
{
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
        });
    public static MessageHubConfiguration AddPlugin<TPlugin>(this MessageHubConfiguration configuration, Func<IMessageHub, Task<TPlugin>> factory)
        => configuration.WithBuildupAction(factory);
    public static MessageHubConfiguration AddPlugin<TPlugin>(this MessageHubConfiguration configuration, Func<IMessageHub, TPlugin> factory)
        => configuration.WithBuildupAction(hub => Task.FromResult(factory(hub)));

}
