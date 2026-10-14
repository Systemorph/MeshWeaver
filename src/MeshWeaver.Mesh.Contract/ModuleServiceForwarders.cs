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
    private string service = "";
    private Autofac.ILifetimeScope? caller;

    /// <summary>Creates the proxy for <paramref name="serviceType"/>.</summary>
    public static object Create(Type serviceType, ModuleContexts contexts, string module, int index)
        => Create(serviceType, contexts, module, index, null);

    /// <summary>
    /// Creates the proxy for <paramref name="serviceType"/> that forwards for <paramref name="caller"/> —
    /// the root-side scope it was resolved in. A module's SCOPED (or transient) registration of a platform
    /// interface is forwarded by one proxy per caller scope, each answered by that caller's instance in the
    /// module scope the caller owns (MeshWeaver#6584); a singleton's proxy has no caller.
    /// </summary>
    /// <param name="serviceType">The platform interface.</param>
    /// <param name="contexts">The mesh's module registry.</param>
    /// <param name="module">The module's entry-assembly name.</param>
    /// <param name="index">The registration's index.</param>
    /// <param name="caller">The root-side scope the proxy serves, or null.</param>
    public static object Create(Type serviceType, ModuleContexts contexts, string module, int index, Autofac.ILifetimeScope? caller)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        var proxy = (ModuleServiceProxy)DispatchProxy.Create(serviceType, typeof(ModuleServiceProxy));
        proxy.contexts = contexts;
        proxy.module = module;
        proxy.index = index;
        proxy.service = serviceType.Name;
        proxy.caller = caller;
        return proxy;
    }

    /// <summary>
    /// What this proxy forwards to RIGHT NOW, without resolving it: the implementation type the
    /// module's CURRENT generation registered at this position (or the service interface, when that
    /// registration is a factory, which names no type), the module and the position.
    ///
    /// <para>Read from the current generation's registration on every call, never remembered: the
    /// proxy outlives a live swap, and a swap keeps route, service type and lifetime but may change
    /// the implementing class — a name taken at boot would go on naming the retired one.</para>
    /// </summary>
    public override string ToString()
    {
        var descriptor = contexts?.Current(module)?.Services?.Registrations
            .ElementAtOrDefault(index)?.Descriptor;
        var implementation = descriptor is null
            ? null
            : descriptor.IsKeyedService
                ? descriptor.KeyedImplementationType ?? descriptor.KeyedImplementationInstance?.GetType()
                : descriptor.ImplementationType ?? descriptor.ImplementationInstance?.GetType();
        return $"{implementation?.Name ?? service} [module {module}, registration {index}]";
    }

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

    /// <summary>
    /// Whether <paramref name="service"/> is a member a SET of platform services should hold now: true for
    /// an ordinary instance, and for a forwarding proxy whose module the registry still holds; false for a
    /// proxy left behind by a module that was withdrawn at boot or retired.
    ///
    /// <para>🚨 A DI registration is fixed once the container is built; the module set is not. A module
    /// withdrawn at boot (its shared instances could not be built — <see cref="ModuleContexts"/>) keeps
    /// its root forwarders, and every call through one throws <c>Module … holds no service scope</c>. A
    /// consumer that builds an index over <c>IEnumerable&lt;T&gt;</c> of a platform interface — the
    /// security rules (<see cref="Services.NodeTypeAccessRuleSet"/>) — must leave such a member out,
    /// exactly as the installed-module set does (<see cref="InstalledModules"/>): the feature is absent
    /// and the withdrawal is reported, never a fault in every caller of the index. Reads the registry
    /// only; never resolves the instance behind the proxy.</para>
    /// </summary>
    /// <param name="service">A resolved platform service — an instance, or a module's forwarding proxy.</param>
    public static bool Serves(object service)
    {
        ArgumentNullException.ThrowIfNull(service);
        return service is not ModuleServiceProxy proxy
               || proxy.contexts?.Current(proxy.module)?.Services is not null;
    }

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        var target = contexts!.ResolveModuleService(module, index, caller);
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

/// <summary>
/// The root's stand-in for a module's <see cref="IConfigureOptions{TOptions}"/> of a PLATFORM options type —
/// <c>services.Configure&lt;T&gt;(name, …)</c>, <c>AddOptions&lt;T&gt;(name).Configure(…)</c>, and every
/// <c>AddHttpClient(name, …)</c> / <c>AddHttpClient&lt;TClient&gt;(…)</c>, which configure the
/// <c>HttpClientFactoryOptions</c> of their named client exactly that way.
///
/// <para>🚨 <b>Why a plain forwarding proxy is not enough.</b> Those registrations are a
/// <see cref="ConfigureNamedOptions{TOptions}"/>, which ALSO implements <see cref="IConfigureNamedOptions{TOptions}"/>,
/// and the options factory asks for exactly that: a setup that is not an <see cref="IConfigureNamedOptions{TOptions}"/>
/// is applied to the DEFAULT name only. A <see cref="DispatchProxy"/> implements the one interface it was
/// created for, so behind a proxy every NAMED configuration a module made was silently dropped — a module's
/// typed HttpClient was handed out with none of its base address, headers or timeout, and nothing logged.</para>
///
/// <para>So this forwarder implements the named interface and applies the module's CURRENT generation's
/// setup exactly as <see cref="OptionsFactory{TOptions}"/> would apply the setup itself: by name when it is
/// named, to the default name otherwise. Resolved on every call, so a live swap is followed (the root's
/// options CACHE is evicted by <see cref="ModuleContexts.CommitAll"/>).</para>
/// </summary>
/// <typeparam name="TOptions">The platform options type.</typeparam>
internal sealed class ModuleConfigureOptionsForwarder<TOptions>(
    ModuleContexts contexts, string module, int index, Autofac.ILifetimeScope? caller)
    : IConfigureNamedOptions<TOptions>
    where TOptions : class
{
    public void Configure(string? name, TOptions options)
    {
        var target = contexts.ResolveModuleService(module, index, caller);
        if (target is IConfigureNamedOptions<TOptions> named)
            named.Configure(name, options);
        else if (name == Options.DefaultName)
            ((IConfigureOptions<TOptions>)target).Configure(options);
    }

    public void Configure(TOptions options) => Configure(Options.DefaultName, options);

    public override string ToString() => $"IConfigureOptions<{typeof(TOptions).Name}> [module {module}, registration {index}]";
}

/// <summary>Registers the forwarders for one module's root services.</summary>
internal static class ModuleServiceForwarding
{
    /// <summary>The options type <paramref name="type"/> configures when it is
    /// <see cref="IConfigureOptions{TOptions}"/>, else null.</summary>
    internal static Type? ConfiguredOptionsType(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IConfigureOptions<>)
            ? type.GetGenericArguments()[0]
            : null;

    /// <summary>The options type whose root cache a module registration of <paramref name="type"/> feeds —
    /// every options-pipeline interface (<see cref="IConfigureOptions{TOptions}"/>,
    /// <see cref="IPostConfigureOptions{TOptions}"/>, <see cref="IValidateOptions{TOptions}"/>) — else null.</summary>
    internal static Type? OptionsPipelineType(Type type)
    {
        if (!type.IsGenericType)
            return null;
        var definition = type.GetGenericTypeDefinition();
        return definition == typeof(IConfigureOptions<>)
               || definition == typeof(IConfigureNamedOptions<>)
               || definition == typeof(IPostConfigureOptions<>)
               || definition == typeof(IValidateOptions<>)
            ? type.GetGenericArguments()[0]
            : null;
    }

    private static object ConfigureOptionsForwarder(Type options, ModuleContexts contexts, string module, int index, Autofac.ILifetimeScope? caller) =>
        Activator.CreateInstance(typeof(ModuleConfigureOptionsForwarder<>).MakeGenericType(options), contexts, module, index, caller)!;

    /// <summary>
    /// Adds to the ROOT collection the forwarder for every platform-typed registration the module made
    /// — exactly the registrations the root would have held — and nothing that names a module type.
    ///
    /// <para>🚨 Every forwarder is a <see cref="NotOwnedServiceDescriptor"/>: the root scope that resolves it
    /// never disposes what it returns. The instance belongs to the module — its generation's container
    /// (a singleton) or the module scope the caller owns (scoped, MeshWeaver#6584) — and a forwarder the
    /// caller's scope owned disposed a module SINGLETON when the first circuit ended, under every other
    /// user, and a scoped instance twice. A proxy is not owned either: disposing a proxy over an
    /// <see cref="IDisposable"/> interface forwards the call to the module's instance.</para>
    ///
    /// <para>🚨 One exception (MeshWeaver#6557): an <see cref="InstalledModuleAssembly"/> the module
    /// registers (the further reference assemblies a module ships — MeshWeaver.Testing's xunit closure)
    /// gets no plain forwarder. A plain forwarder can only throw once its module is withdrawn, and every
    /// enumeration of the installed-module set — the boot pre-warm sweep's included — would fault on it.
    /// It is declared as an <see cref="InstalledModuleSlot"/> in its place in the set instead, which
    /// <see cref="InstalledModules"/> answers from the module's current generation while the registry
    /// holds the module and leaves out once the module is withdrawn or retired.</para>
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
                    case ModuleServiceRoute.Proxy when registration.Descriptor.Lifetime == ServiceLifetime.Singleton:
                        root.Add(NotOwnedServiceDescriptor.KeyedForwarder(type, key, (sp, _) => ModuleServiceProxy.Create(
                            type, sp.GetRequiredService<ModuleContexts>(), module, index), ServiceLifetime.Singleton));
                        break;
                    case ModuleServiceRoute.Proxy:
                        // Scoped or transient: one proxy per caller, forwarding for it (MeshWeaver#6584).
                        root.Add(NotOwnedServiceDescriptor.KeyedForwarder(type, key, (sp, _) => ModuleServiceProxy.Create(
                            type, sp.GetRequiredService<ModuleContexts>(), module, index,
                            ModuleServiceProvider.CallerOfRootScope(sp)), registration.Descriptor.Lifetime));
                        break;
                    case ModuleServiceRoute.Current:
                        root.Add(NotOwnedServiceDescriptor.KeyedForwarder(type, key, (sp, _) =>
                            sp.GetRequiredService<ModuleContexts>().ResolveModuleService(module, index,
                                ModuleServiceProvider.CallerOfRootScope(sp)), ServiceLifetime.Transient));
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
                case ModuleServiceRoute.Proxy when ConfiguredOptionsType(type) is { } options:
                    // A NAMED configuration must reach the options factory as a named one — a proxy would
                    // drop it (ModuleConfigureOptionsForwarder). Singleton: one forwarder, no caller; otherwise
                    // one per caller scope, as a proxy would be (MeshWeaver#6584).
                    // 🚨 The lambda captures the LIFETIME, never `registration`: a lambda capturing the loop's
                    // registration shares its closure with every other forwarder of the iteration, and the
                    // descriptor names the module's implementation type — pinning generation N for the life
                    // of the root (measured: ModuleRootServicesSwapTest's retention check).
                    var lifetime = registration.Descriptor.Lifetime;
                    root.Add(NotOwnedServiceDescriptor.Forwarder(type, sp => ConfigureOptionsForwarder(
                        options, sp.GetRequiredService<ModuleContexts>(), module, index,
                        lifetime == ServiceLifetime.Singleton ? null : ModuleServiceProvider.CallerOfRootScope(sp)),
                        lifetime));
                    break;
                case ModuleServiceRoute.Proxy when registration.Descriptor.Lifetime == ServiceLifetime.Singleton:
                    // Singleton: ONE stable proxy, so every consumer holds the same forwarder.
                    // Resolving the registry through the container is what attaches it to the root.
                    root.Add(NotOwnedServiceDescriptor.Forwarder(type, sp => ModuleServiceProxy.Create(
                        type, sp.GetRequiredService<ModuleContexts>(), module, index), ServiceLifetime.Singleton));
                    break;
                case ModuleServiceRoute.Proxy:
                    // Scoped or transient: one proxy per caller scope, forwarding to that caller's instance
                    // in the module scope it owns — a singleton proxy would serve every circuit one
                    // instance (MeshWeaver#6584).
                    root.Add(NotOwnedServiceDescriptor.Forwarder(type, sp => ModuleServiceProxy.Create(
                        type, sp.GetRequiredService<ModuleContexts>(), module, index,
                        ModuleServiceProvider.CallerOfRootScope(sp)), registration.Descriptor.Lifetime));
                    break;
                case ModuleServiceRoute.Current when type == typeof(InstalledModuleAssembly):
                    // A member of the installed-module set the module contributes: ABSENT once the
                    // module is withdrawn or retired, never a fault in whoever enumerates the set
                    // (MeshWeaver#6557, InstalledModules).
                    // ONE snapshot of the module's generation: a check of Current and a second lookup
                    // to resolve could see a generation that was disposed in between.
                    root.Add(InstalledModuleSlot.Declare(sp =>
                            (InstalledModuleAssembly?)sp.GetRequiredService<ModuleContexts>()
                                .TryResolveCurrentModuleService(module, index),
                        $"Module {module} is no longer held by this mesh's module registry — the installed module it contributed is gone with it."));
                    break;
                case ModuleServiceRoute.Current:
                    // Resolved for the CALLER — the scope this transient forwarder is resolved in — so a
                    // module's scoped registration is that circuit's / request's / hub's instance, built
                    // against its NavigationManager and IJSRuntime (MeshWeaver#6584).
                    root.Add(NotOwnedServiceDescriptor.Forwarder(type, sp => sp.GetRequiredService<ModuleContexts>()
                        .ResolveModuleService(module, index, ModuleServiceProvider.CallerOfRootScope(sp)),
                        ServiceLifetime.Transient));
                    break;
            }
        }
        return root;
    }
}
