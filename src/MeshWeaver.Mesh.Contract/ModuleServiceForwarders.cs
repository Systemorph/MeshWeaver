using System.Collections.Immutable;
using System.Reflection;
using MeshWeaver.ServiceProvider;
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
    private string registered = "";

    /// <summary>Creates the proxy for <paramref name="serviceType"/>.</summary>
    public static object Create(Type serviceType, ModuleContexts contexts, string module, int index)
        => Create(serviceType, contexts, module, index, null);

    /// <summary>
    /// Creates the proxy for <paramref name="serviceType"/>, remembering what the module registered
    /// (<paramref name="implementation"/>, when the registration names a type) so the proxy can be
    /// NAMED without resolving the module's instance — see <see cref="Label"/>.
    /// </summary>
    /// <param name="serviceType">The platform interface the proxy stands in for.</param>
    /// <param name="contexts">The mesh's module registry.</param>
    /// <param name="module">The registering module's name.</param>
    /// <param name="index">The registration's position among the module's registrations.</param>
    /// <param name="implementation">The implementation type's name, or null when the registration is a factory.</param>
    public static object Create(Type serviceType, ModuleContexts contexts, string module, int index, string? implementation)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        var proxy = (ModuleServiceProxy)DispatchProxy.Create(serviceType, typeof(ModuleServiceProxy));
        proxy.contexts = contexts;
        proxy.module = module;
        proxy.index = index;
        proxy.registered = implementation ?? serviceType.Name;
        return proxy;
    }

    /// <summary>
    /// What this proxy forwards to, without resolving it: the registered implementation (or the
    /// service interface, for a factory registration), the module and the registration's position.
    /// </summary>
    public override string ToString() => $"{registered} [module {module}, registration {index}]";

    /// <summary>
    /// The name a diagnostic gives <paramref name="service"/>: its own type name, or — for a
    /// module's forwarding proxy — what the proxy forwards to (<see cref="ToString"/>).
    ///
    /// <para>A proxy's runtime type is a generated class whose name says nothing, and the instance
    /// behind it lives in the module's own container. A diagnostic that names a service the caller
    /// is WAITING on must not resolve that instance to name it: the wait is usually that very
    /// resolution (MeshWeaver#6391).</para>
    /// </summary>
    /// <param name="service">The resolved service — an instance, or a module's forwarding proxy.</param>
    public static string Label(object service)
    {
        ArgumentNullException.ThrowIfNull(service);
        return service is ModuleServiceProxy proxy ? proxy.ToString() : service.GetType().Name;
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
            // What the module registered, as far as the registration itself says — a factory names
            // no type. Only the NAME is kept: a Type would pin the module's generation in the root.
            var implementation = registration.Descriptor.IsKeyedService
                ? (registration.Descriptor.KeyedImplementationType
                   ?? registration.Descriptor.KeyedImplementationInstance?.GetType())?.Name
                : (registration.Descriptor.ImplementationType
                   ?? registration.Descriptor.ImplementationInstance?.GetType())?.Name;
            if (registration.Descriptor.IsKeyedService)
            {
                // Forwarded under the module's OWN key, so a consumer asking by key gets the module's.
                var key = registration.Descriptor.ServiceKey;
                switch (registration.Route)
                {
                    case ModuleServiceRoute.Proxy:
                        root.Add(ServiceDescriptor.KeyedSingleton(type, key, (sp, _) => ModuleServiceProxy.Create(
                            type, sp.GetRequiredService<ModuleContexts>(), module, index, implementation)));
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
                        type, sp.GetRequiredService<ModuleContexts>(), module, index, implementation)));
                    break;
                case ModuleServiceRoute.Current:
                    root.Add(ServiceDescriptor.Transient(type, sp => sp.GetRequiredService<ModuleContexts>().ResolveModuleService(module, index)));
                    break;
            }
        }
        return root;
    }
}
