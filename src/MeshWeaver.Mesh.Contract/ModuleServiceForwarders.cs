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
/// The root's stand-in for a hosted service a module registered (<see cref="ModuleServiceRoute.Hosted"/>):
/// the host starts it at boot, and a live swap STOPS the old generation's instance and STARTS the new
/// one's. A failure costs the module's feature, never the host (#2449).
/// </summary>
internal sealed class ModuleHostedServiceForwarder(
    ModuleContexts contexts, string module, int index, ILogger? logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        contexts.StartModuleHosted(module, index, cancellationToken, logger);

    public Task StopAsync(CancellationToken cancellationToken) =>
        contexts.Current(module)?.Services is { } services
            ? services.StopHosted(cancellationToken, logger)
            : Task.CompletedTask;
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
                case ModuleServiceRoute.Hosted:
                    root.Add(ServiceDescriptor.Singleton<IHostedService>(sp => new ModuleHostedServiceForwarder(
                        sp.GetRequiredService<ModuleContexts>(), module, index,
                        sp.GetService<ILoggerFactory>()?.CreateLogger("MeshWeaver.Mesh.IncompatibleModule"))));
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
            var owned = module.Registrations
                .Where(r => r.Route == ModuleServiceRoute.ModuleOwned)
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
