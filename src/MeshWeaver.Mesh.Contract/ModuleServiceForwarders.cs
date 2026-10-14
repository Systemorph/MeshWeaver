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

    /// <summary>
    /// 🚨 The ATOMIC "try current service" (MeshWeaver#6715): <paramref name="service"/> pinned for the
    /// life of the returned lease — or null when it is a forwarding proxy whose module is no longer held
    /// or is retiring. For an ordinary instance the lease is the instance itself and pins nothing.
    ///
    /// <para>For a proxy, <see cref="ModuleServiceLease{T}.Service"/> is the instance of the module's
    /// generation that was current when the lease was taken, and that generation's services are neither
    /// disposed nor its context unloaded until the lease is disposed — a retirement that starts in
    /// between is deferred to the release, never run under the call. This is what a consumer uses
    /// instead of <see cref="Serves"/> followed by a call through the proxy: the check and the call can
    /// no longer be separated by a retirement. Hold it for the call only — a lease that is never
    /// released keeps a retired generation loaded.</para>
    /// </summary>
    /// <typeparam name="T">The platform interface.</typeparam>
    /// <param name="service">A resolved platform service — an instance, or a module's forwarding proxy.</param>
    public static ModuleServiceLease<T>? TryLease<T>(T service) where T : class
    {
        ArgumentNullException.ThrowIfNull(service);
        if (service is not ModuleServiceProxy proxy)
            return new ModuleServiceLease<T>(service, null);
        return proxy.contexts?.TryLeaseModuleService(proxy.module, proxy.index, proxy.caller) is { } leased
            ? new ModuleServiceLease<T>((T)leased.Instance, leased.Release)
            : null;
    }

    /// <summary>
    /// <paramref name="use"/> run against <paramref name="service"/> under ONE lease (<see cref="TryLease{T}"/>)
    /// that spans the whole subscription of what it returns, released when that terminates or is
    /// unsubscribed. Cold: the lease is taken on subscribe. A proxy whose module is no longer held, or is
    /// retiring, errors with <see cref="ModuleRetiredException"/> naming <paramref name="what"/> — a named
    /// verdict, never <c>Module … holds no service scope</c> from inside a forwarded call.
    /// </summary>
    /// <typeparam name="T">The platform interface.</typeparam>
    /// <typeparam name="TResult">What the use emits.</typeparam>
    /// <param name="service">A resolved platform service — an instance, or a module's forwarding proxy.</param>
    /// <param name="use">The call, against the pinned instance.</param>
    /// <param name="what">What the service is, for the retirement's message.</param>
    public static IObservable<TResult> Leased<T, TResult>(T service, Func<T, IObservable<TResult>> use, string what)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(use);
        return System.Reactive.Linq.Observable.Defer(() => TryLease(service) is { } lease
            ? System.Reactive.Linq.Observable.Using(() => lease, held => use(held.Service))
            : System.Reactive.Linq.Observable.Throw<TResult>(ModuleRetiredException.For(service, what)));
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
/// A platform service pinned for the life of this lease (<see cref="ModuleServiceProxy.TryLease{T}"/>):
/// for a module's forwarding proxy, the instance of the generation that was current when it was taken,
/// whose services and load context stay alive until <see cref="Dispose"/>. Release it as soon as the
/// call ends — a retirement waits for it.
/// </summary>
/// <typeparam name="T">The platform interface.</typeparam>
public sealed class ModuleServiceLease<T> : IDisposable where T : class
{
    private IDisposable? release;

    internal ModuleServiceLease(T service, IDisposable? release)
    {
        Service = service;
        this.release = release;
    }

    /// <summary>The pinned instance — call it directly, never through the proxy it was leased from.</summary>
    public T Service { get; }

    /// <summary>Releases the lease; idempotent.</summary>
    public void Dispose() => Interlocked.Exchange(ref release, null)?.Dispose();
}

/// <summary>
/// A module's service was asked for after its module stopped being held — retired by an uninstall or a
/// swap, or withdrawn at boot — between the moment a consumer selected it and the moment it was leased
/// (MeshWeaver#6715). A named, fail-closed verdict: the selection is stale, and asking again decides
/// without that module.
/// </summary>
public sealed class ModuleRetiredException : InvalidOperationException
{
    /// <summary>Creates the exception with its message.</summary>
    /// <param name="message">What was retired, and what that means for the caller.</param>
    public ModuleRetiredException(string message) : base(message)
    {
    }

    internal static ModuleRetiredException For(object service, string what) =>
        new($"{what} ({ModuleServiceProxy.Label(service)}) is no longer served: its module was retired or withdrawn "
            + "after it was selected and before it was called — the selection is stale, and asking again decides without it.");
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
