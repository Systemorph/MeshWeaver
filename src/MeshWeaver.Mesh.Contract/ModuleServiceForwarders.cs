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
                case ModuleServiceRoute.Hosted:
                    root.Add(ServiceDescriptor.Singleton<IHostedService>(sp => new ModuleHostedServiceForwarder(
                        sp.GetRequiredService<ModuleContexts>(), module, index,
                        sp.GetService<ILoggerFactory>()?.CreateLogger<ModuleHostedServiceForwarder>())));
                    break;
            }
        }
        return root;
    }
}
