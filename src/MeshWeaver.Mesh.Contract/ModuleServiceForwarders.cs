using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
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
///
/// <para>Start and stop are SEQUENTIAL, as the generic host runs root hosted services and as the
/// per-registration forwarders this replaced inherited (#6128 review): modules in name order, each
/// module's services in registration order, each started once the previous start completed; stop is
/// the exact reverse.</para>
/// </summary>
internal sealed class ModuleHostedServicesHost(ModuleContexts contexts, ILogger? logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        ModuleServices.InSequence(contexts.Generations
            .Where(g => g.Services is not null)
            .OrderBy(g => g.Name, StringComparer.Ordinal)
            .Select(g => (Func<Task>)(() => contexts.StartAllModuleHosted(g, cancellationToken, logger))));

    public Task StopAsync(CancellationToken cancellationToken) =>
        ModuleServices.InSequence(contexts.Generations
            .OrderByDescending(g => g.Name, StringComparer.Ordinal)
            .Select(g => (Func<Task>)(() => g.Services?.StopHosted(cancellationToken, logger) ?? Task.CompletedTask)));
}

/// <summary>
/// The root's stand-in for a module's <c>AddHealthChecks().AddCheck&lt;T&gt;()</c> — a platform-typed
/// <see cref="IConfigureOptions{TOptions}"/> of <see cref="HealthCheckServiceOptions"/>, so it is forwarded,
/// and the ROOT's health-check service then activates each check it adds.
///
/// <para>🚨 <b>Why a plain forwarding proxy is not enough.</b> <c>AddCheck&lt;T&gt;</c> stores a
/// <see cref="HealthCheckRegistration"/> whose factory is
/// <c>s =&gt; ActivatorUtilities.GetServiceOrCreateInstance&lt;T&gt;(s)</c>, and the root's
/// <c>HealthCheckService</c> calls it with a ROOT scope. A check whose constructor takes a type the
/// module itself declares — <c>FleetWatchHealthCheck(FleetWatchHeartbeat)</c> in
/// MeshWeaver.SelfUpdate.Aks — then cannot be activated: module-owned types live only in the module's
/// scope (<see cref="ModuleServiceRoute.ModuleOwned"/>), never in the root. Measured on the control
/// image from core #6127 on: every <c>/health</c> answered 500 (<c>Unable to resolve service for type
/// 'MeshWeaver.SelfUpdate.Aks.FleetWatchHeartbeat' while attempting to activate
/// 'MeshWeaver.SelfUpdate.Aks.FleetWatchHealthCheck'</c>) and the control image never passed acceptance.</para>
///
/// <para>So this forwarder runs the module's CURRENT generation's configuration and rebinds every
/// registration it added to activate in that module's CURRENT scope — the same scope the check's
/// dependencies (and the hosted service that feeds them) are served from, read at every activation so a
/// live swap is followed.</para>
/// </summary>
internal sealed class ModuleHealthCheckOptionsForwarder(ModuleContexts contexts, string module, int index)
    : IConfigureOptions<HealthCheckServiceOptions>
{
    public void Configure(HealthCheckServiceOptions options)
    {
        var target = (IConfigureOptions<HealthCheckServiceOptions>)contexts.ResolveModuleService(module, index);
        var before = options.Registrations.ToHashSet(ReferenceEqualityComparer.Instance);
        target.Configure(options);
        foreach (var registration in options.Registrations.Where(r => !before.Contains(r)).ToArray())
        {
            var activate = registration.Factory;
            registration.Factory = _ => activate(contexts.ModuleScope(module)
                ?? throw new InvalidOperationException(
                    $"Module {module} holds no service scope — its health check '{registration.Name}' cannot be activated."));
        }
    }
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
                case ModuleServiceRoute.Proxy when type == typeof(IConfigureOptions<HealthCheckServiceOptions>):
                    // A module's health check must activate in the MODULE's scope, not the root's.
                    root.Add(ServiceDescriptor.Singleton(type, sp => new ModuleHealthCheckOptionsForwarder(
                        sp.GetRequiredService<ModuleContexts>(), module, index)));
                    break;
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
