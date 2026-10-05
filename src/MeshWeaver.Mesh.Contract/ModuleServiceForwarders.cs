using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh;

/// <summary>
/// The root's stand-in for a platform INTERFACE a module registered (<see cref="ModuleServiceRoute.Proxy"/>):
/// one stable instance per registration, every call forwarded to the module's CURRENT generation — so a
/// platform singleton that resolved it once and cached it follows a live swap, and holds no reference
/// into any generation.
/// </summary>
public class ModuleServiceProxy : DispatchProxy
{
    private ModuleContexts? contexts;
    private string module = "";
    private int index;

    /// <summary>Creates the proxy for <paramref name="serviceType"/>.</summary>
    public static object Create(Type serviceType, ModuleContexts contexts, string module, int index)
    {
        var proxy = (ModuleServiceProxy)DispatchProxy.Create(serviceType, typeof(ModuleServiceProxy));
        proxy.contexts = contexts;
        proxy.module = module;
        proxy.index = index;
        return proxy;
    }

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        var target = contexts!.ResolveModuleService(module, index);
        try
        {
            return targetMethod.Invoke(target, args);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }
}

/// <summary>
/// The ONE hosted service the root holds for every module held in its own load context: at host start
/// it starts every hosted registration of each module's CURRENT generation; a live swap stops the old
/// generation's and starts the new one's (<see cref="ModuleContexts.HandOverHosted"/>). Because the root
/// holds no per-registration forwarder, a generation may add or drop a background service and still swap
/// live. A failure costs the module's feature, never the host (#2449).
/// </summary>
internal sealed class ModuleHostedServicesHost(ModuleContexts contexts, ILogger? logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(contexts.Generations
            .Where(g => g.Services is not null)
            .Select(g => contexts.StartAllModuleHosted(g, cancellationToken, logger)));

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(contexts.Generations
            .Select(g => g.Services?.StopHosted(cancellationToken, logger) ?? Task.CompletedTask));
}

/// <summary>Registers the forwarders for one module's root services.</summary>
internal static class ModuleServiceForwarding
{
    /// <summary>
    /// Adds to the ROOT collection the forwarder for every platform-typed registration the module made
    /// — exactly the registrations the root would have held — and nothing that names a module type.
    /// </summary>
    public static IServiceCollection AddForwarders(IServiceCollection root, ModuleServices services)
    {
        var module = services.ModuleName;
        foreach (var registration in services.Registrations)
        {
            var index = registration.Index;
            var type = registration.Descriptor.ServiceType;
            if (registration.Descriptor.IsKeyedService)
            {
                // Forwarded under the module's OWN key, so a consumer asking by key gets the module's.
                var key = registration.Descriptor.ServiceKey;
                switch (registration.Route)
                {
                    case ModuleServiceRoute.Proxy:
                        root.Add(ServiceDescriptor.KeyedSingleton(type, key, (sp, _) => ModuleServiceProxy.Create(
                            type, sp.GetRequiredService<ModuleContexts>(), module, index)));
                        break;
                    case ModuleServiceRoute.Current:
                        root.Add(ServiceDescriptor.KeyedTransient(type, key, (sp, _) =>
                            sp.GetRequiredService<ModuleContexts>().ResolveModuleService(module, index)));
                        break;
                }
                continue;
            }
            switch (registration.Route)
            {
                case ModuleServiceRoute.Proxy:
                    // Singleton: ONE stable proxy, so every consumer holds the same forwarder.
                    // Resolving the registry through the container is what attaches it to the root.
                    root.Add(ServiceDescriptor.Singleton(type, sp => ModuleServiceProxy.Create(
                        type, sp.GetRequiredService<ModuleContexts>(), module, index)));
                    break;
                case ModuleServiceRoute.Current:
                    root.Add(ServiceDescriptor.Transient(type, sp => sp.GetRequiredService<ModuleContexts>().ResolveModuleService(module, index)));
                    break;
            }
        }
        return root;
    }

    /// <summary>
    /// Adds to a PER-NODE hub's scope a forwarder for every service type the modules held in their own
    /// contexts DECLARE themselves (<see cref="ModuleServiceRoute.ModuleOwned"/>) — so module code
    /// resolving its own service from <c>hub.ServiceProvider</c> finds it — and for the options of
    /// every options type a module owns. Read from the CURRENT generation when the hub is built; a
    /// swap recycles the hubs, which re-bind the new generation's types. Never in the root: a root
    /// registration of a collectible type would pin its generation for the life of the process.
    /// </summary>
    public static IServiceCollection AddModuleOwned(IServiceCollection services, ModuleContexts contexts)
    {
        foreach (var generation in contexts.Generations)
        {
            if (generation.Services is not { } module)
                continue;
            var name = generation.Name;
            foreach (var keyed in module.Registrations
                         .Where(r => r.Route == ModuleServiceRoute.ModuleOwned && r.Descriptor.IsKeyedService))
            {
                var keyedType = keyed.Descriptor.ServiceType;
                var key = keyed.Descriptor.ServiceKey;
                services.Add(ServiceDescriptor.KeyedTransient(keyedType, key, (_, _) =>
                    (contexts.ModuleScope(name) ?? throw new InvalidOperationException($"Module {name} holds no service scope."))
                    .GetRequiredKeyedService(keyedType, key)));
            }
            var owned = module.Registrations
                .Where(r => r.Route == ModuleServiceRoute.ModuleOwned && !r.Descriptor.IsKeyedService)
                .Select(r => r.Descriptor.ServiceType)
                .Distinct()
                .ToArray();
            foreach (var type in owned)
            {
                services.Add(ServiceDescriptor.Transient(type, _ =>
                    (contexts.ModuleScope(name) ?? throw new InvalidOperationException($"Module {name} holds no service scope."))
                    .GetRequiredService(type)));
                // An options type the module owns: IOptions<T>, IOptionsMonitor<T>, IOptionsSnapshot<T>
                // close over it in the MODULE's scope, where its configure registrations live.
                if (type.IsGenericType && type.GetGenericArguments() is [var optionsType]
                    && type.GetGenericTypeDefinition().FullName == "Microsoft.Extensions.Options.IConfigureOptions`1")
                    foreach (var open in OptionsInterfaces)
                    {
                        var closed = open.MakeGenericType(optionsType);
                        services.Add(ServiceDescriptor.Transient(closed, _ =>
                            (contexts.ModuleScope(name) ?? throw new InvalidOperationException($"Module {name} holds no service scope."))
                            .GetRequiredService(closed)));
                    }
            }
        }
        return services;
    }

    private static readonly System.Collections.Immutable.ImmutableArray<Type> OptionsInterfaces =
    [
        typeof(Microsoft.Extensions.Options.IOptions<>),
        typeof(Microsoft.Extensions.Options.IOptionsMonitor<>),
        typeof(Microsoft.Extensions.Options.IOptionsSnapshot<>),
    ];
}
