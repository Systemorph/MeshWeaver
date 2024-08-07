using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using MeshWeaver.Reflection;

namespace MeshWeaver.Messaging;

public record PluginOptions<TPlugin>(IMessageHub Hub)
    where TPlugin : IMessageHubPlugin
{
    internal Func<TPlugin> Factory { get; init; } =
        () => Hub.ServiceProvider.GetRequiredService<TPlugin>();

    public PluginOptions<TPlugin> WithFactory(Func<TPlugin> factory) =>
        this with
        {
            Factory = factory
        };
}

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
        Func<PluginOptions<TPlugin>, PluginOptions<TPlugin>> options
    )
        where TPlugin : class, IMessageHubPlugin
    {
        if (configuration.PluginFactories.Any(x => x.Type == typeof(TPlugin)))
            return configuration;
        return configuration.WithServices(services => services.AddScoped<TPlugin>()) with
        {
            PluginFactories = configuration.PluginFactories.Add(
                (typeof(TPlugin), h => options.Invoke(new(h)).Factory())
            )
        };
    }

    public static MessageHubConfiguration AddPlugin<TPlugin>(
        this MessageHubConfiguration configuration
    )
        where TPlugin : class, IMessageHubPlugin => AddPlugin<TPlugin>(configuration, x => x);
}
