using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
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
    private Type? serviceType;
    private MeshWeaver.ServiceProvider.MeshServiceProvider? caller;

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
    public static object Create(Type serviceType, ModuleContexts contexts, string module, int index, MeshWeaver.ServiceProvider.MeshServiceProvider? caller)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        var proxy = (ModuleServiceProxy)DispatchProxy.Create(serviceType, typeof(ModuleServiceProxy));
        proxy.contexts = contexts;
        proxy.module = module;
        proxy.index = index;
        proxy.service = serviceType.Name;
        proxy.serviceType = serviceType;
        proxy.caller = caller;
        // A proxy over a disposable interface forwards Dispose to the module's instance, so only the module
        // may end it: a scope that disposes the proxy it was forwarded is the defect of MeshWeaver#6588, and
        // the ownership diagnostics name it (MeshWeaver.ServiceProvider.ModuleScopeOwnership).
        if (typeof(IDisposable).IsAssignableFrom(serviceType) || typeof(IAsyncDisposable).IsAssignableFrom(serviceType))
            contexts.RootService<MeshWeaver.ServiceProvider.ModuleInstanceOwners>()?.Record(proxy, null,
                $"module {module} (the proxy forwards Dispose to the module's {serviceType.Name})");
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
    /// The generation of the module behind <paramref name="service"/> that is current NOW — or null for an
    /// ordinary instance, or for a proxy whose module is not held. Reads the registry only; never resolves
    /// the instance. A consumer that remembers something it learned from a leased instance (the query
    /// fan-out remembers a provider's name) keys it to the generation current when it learned it, and
    /// trusts it only while that same generation is current: a live swap may change what a registration
    /// position holds (MeshWeaver#7218).
    /// </summary>
    /// <param name="service">A resolved platform service — an instance, or a module's forwarding proxy.</param>
    public static ModuleGeneration? CurrentGeneration(object service)
    {
        ArgumentNullException.ThrowIfNull(service);
        return service is ModuleServiceProxy proxy ? proxy.contexts?.Current(proxy.module) : null;
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
    ///
    /// <para>False, too, for a registration the module's current generation was found unable to produce
    /// (<see cref="ModuleServices.Unservable"/>, MeshWeaver#6847) — a member that can only fault is absent.</para>
    /// </summary>
    /// <param name="service">A resolved platform service — an instance, or a module's forwarding proxy.</param>
    public static bool Serves(object service)
    {
        ArgumentNullException.ThrowIfNull(service);
        return service is not ModuleServiceProxy proxy
               || proxy.contexts?.Current(proxy.module)?.Services is { } services
                  && !services.Unservable.ContainsKey(proxy.index);
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
    ///
    /// <para>Null, too, when the current generation cannot produce this registration — it does not
    /// resolve, or resolves to something that is not <typeparamref name="T"/> — which is recorded and
    /// reported once and makes the module's feature absent (<see cref="ModuleContexts.TryLeaseModuleService(string, int, MeshWeaver.ServiceProvider.MeshServiceProvider?, System.Type?)"/>,
    /// MeshWeaver#6847): an optional module's broken service never faults a consumer of the set it
    /// belongs to.</para>
    /// </summary>
    /// <typeparam name="T">The platform interface.</typeparam>
    /// <param name="service">A resolved platform service — an instance, or a module's forwarding proxy.</param>
    public static ModuleServiceLease<T>? TryLease<T>(T service) where T : class
    {
        ArgumentNullException.ThrowIfNull(service);
        if (service is not ModuleServiceProxy proxy)
            return new ModuleServiceLease<T>(service, null);
        return proxy.contexts?.TryLeaseModuleService(proxy.module, proxy.index, proxy.caller, proxy.serviceType ?? typeof(T)) is { } leased
            ? new ModuleServiceLease<T>((T)leased.Instance, leased.Release)
            : null;
    }

    /// <summary>As <see cref="TryLease{T}(T)"/> — a distinct name, so no published <c>cref</c> to <c>TryLease</c>
    /// turns ambiguous — saying why a null answer is null (<see cref="ModuleLeaseFault"/>):
    /// a module that is not held is ABSENT, while one that is held and cannot produce the service FAULTED —
    /// which a consumer whose safe reading differs from absence (an access rule) must not read as absence.</summary>
    /// <typeparam name="T">The platform interface.</typeparam>
    /// <param name="service">A resolved platform service — an instance, or a module's forwarding proxy.</param>
    /// <param name="fault">Why the answer is null; <see cref="ModuleLeaseFault.None"/> otherwise.</param>
    internal static ModuleServiceLease<T>? TryLeaseOrFault<T>(T service, out ModuleLeaseFault fault) where T : class
    {
        ArgumentNullException.ThrowIfNull(service);
        fault = ModuleLeaseFault.None;
        if (service is not ModuleServiceProxy proxy)
            return new ModuleServiceLease<T>(service, null);
        if (proxy.contexts is not { } contexts)
            return null;
        return contexts.TryLeaseModuleService(proxy.module, proxy.index, proxy.caller, proxy.serviceType ?? typeof(T), out fault) is { } leased
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
/// <para>So the root holds, for every check the module declares, a registration that names NO module
/// type — its name, tags, failure status, timeout, delay and period — and whose factory, at every
/// activation, takes ONE snapshot of the module's CURRENT generation and runs THAT generation's own
/// factory for the check of that name in THAT generation's container.</para>
///
/// <para>🚨 <b>Why the factory is the current generation's, not the boot one's</b> (MeshWeaver#6766).
/// <c>IOptions&lt;HealthCheckServiceOptions&gt;</c> is built once per process, so a factory captured
/// while it was built is generation N's <c>GetServiceOrCreateInstance&lt;TCheck&gt;</c> — <c>TCheck</c>
/// is N's type, and so are its constructor's parameter types. Rebinding only the PROVIDER to the
/// current scope then asked generation N+1's container, after a live swap, for N's dependency type,
/// which it does not hold: <c>Unable to resolve service for type 'MeshWeaver.AI.ProviderCatalogConvergence'
/// while attempting to activate 'MeshWeaver.AI.ProviderCatalogHealthCheck'</c> on every <c>/health</c>
/// until the pod restarted — and the captured factory pinned generation N's collectible context.</para>
///
/// <para>A module that is no longer held (retired by an uninstall, or withdrawn at boot), or a current
/// generation that no longer declares the check, is a NAMED <see cref="HealthStatus.Degraded"/> verdict
/// for that check — never an exception out of the health middleware: the feature is absent, and the
/// probe says which and why.</para>
///
/// <para>🚨 It is an <see cref="IConfigureNamedOptions{TOptions}"/>, as <see cref="ModuleConfigureOptionsForwarder{TOptions}"/>
/// is, and applies the module's setup by name exactly as <see cref="OptionsFactory{TOptions}"/> would: a module's
/// <c>Configure&lt;HealthCheckServiceOptions&gt;("name", …)</c> must reach <c>IOptionsMonitor.Get("name")</c>, and a
/// setup that is not named reaches the default name only (MeshWeaver#6749). The per-generation snapshot below is
/// therefore taken PER OPTIONS NAME.</para>
/// </summary>
internal sealed class ModuleHealthCheckOptionsForwarder(ModuleContexts contexts, string module, int index)
    : IConfigureNamedOptions<HealthCheckServiceOptions>
{
    // The checks each GENERATION declares, per options name, by check name — keyed weakly by that
    // generation's services, so a retired generation is never pinned by this table (an ephemeron: the
    // value lives only while the key does). Mesh-scoped instance state on a singleton, never static
    // (NoStaticState).
    private readonly ConditionalWeakTable<ModuleServices, ConcurrentDictionary<string, ImmutableDictionary<string, HealthCheckRegistration>>> declared = new();

    public void Configure(HealthCheckServiceOptions options) => Configure(Options.DefaultName, options);

    public void Configure(string? name, HealthCheckServiceOptions options)
    {
        var optionsName = name ?? Options.DefaultName;
        if (contexts.Current(module)?.Services is not { } services)
        {
            // Withdrawn before the options were first read: its checks cannot even be named. One
            // named verdict stands for them (the withdrawal itself is an IncompatibleModule record) —
            // on the default name only, where a setup that cannot say its name lands.
            if (optionsName == Options.DefaultName)
                options.Registrations.Add(new HealthCheckRegistration(module,
                    _ => Absent($"module {module} is not held by this mesh's module registry — it was withdrawn, so its health checks cannot run"),
                    HealthStatus.Degraded, null));
            return;
        }
        foreach (var check in Declared(services, optionsName).Values.OrderBy(r => r.Name, StringComparer.Ordinal))
        {
            var checkName = check.Name;
            options.Registrations.Add(new HealthCheckRegistration(checkName, _ => Activate(optionsName, checkName), check.FailureStatus, check.Tags, check.Timeout)
            {
                Delay = check.Delay,
                Period = check.Period,
            });
        }
    }

    // ONE snapshot of the current generation: the factory and the container are the same generation's.
    private IHealthCheck Activate(string optionsName, string name)
    {
        if (contexts.Current(module)?.Services is not { } services
            || contexts.TryScopeOf(services) is not { } scope)
            return Absent($"module {module} is no longer held by this mesh's module registry (retired or withdrawn) — its health check '{name}' cannot run");
        return Declared(services, optionsName).TryGetValue(name, out var check)
            ? check.Factory(scope)
            : Absent($"the current generation of module {module} no longer declares the health check '{name}'");
    }

    // What `services`' own configuration adds to an EMPTY options instance of `optionsName`: that
    // generation's checks, with that generation's factories — applied by name exactly as the options
    // factory would (#6749). Run once per generation and name.
    private ImmutableDictionary<string, HealthCheckRegistration> Declared(ModuleServices services, string optionsName) =>
        declared.GetValue(services, _ => new ConcurrentDictionary<string, ImmutableDictionary<string, HealthCheckRegistration>>(StringComparer.Ordinal))
            .GetOrAdd(optionsName, n =>
            {
                var probe = new HealthCheckServiceOptions();
                var target = contexts.ResolveIn(services, index);
                if (target is IConfigureNamedOptions<HealthCheckServiceOptions> named)
                    named.Configure(n, probe);
                else if (n == Options.DefaultName)
                    ((IConfigureOptions<HealthCheckServiceOptions>)target).Configure(probe);
                return probe.Registrations.ToImmutableDictionary(r => r.Name, StringComparer.Ordinal);
            });

    private static IHealthCheck Absent(string why) => new AbsentModuleHealthCheck(why);

    /// <summary>The verdict for a check whose module (or whose declaration) is gone.</summary>
    private sealed class AbsentModuleHealthCheck(string why) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HealthCheckResult(HealthStatus.Degraded, why));
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
    ModuleContexts contexts, string module, int index, MeshWeaver.ServiceProvider.MeshServiceProvider? caller)
    : IConfigureNamedOptions<TOptions>
    where TOptions : class
{
    // 🚨 LEASED, and a registration the module cannot serve contributes NOTHING (MeshWeaver#6847). The
    // options factory runs EVERY setup of TOptions for EVERY name, so a module's setup is called for
    // names it never configured — a WhatsApp AddHttpClient's forwarder runs when the sign-in's OIDC
    // handler builds its own HttpClientFactoryOptions. A forwarder that threw there made one optional
    // module's broken registration a fault in every named options build of that type, sign-in's
    // included. A module that is not held (withdrawn, retired) or whose current generation cannot
    // produce this registration (recorded and reported by TryLeaseModuleService) configures nothing:
    // its feature is absent, every other name is built as if it had never registered.
    //
    // 🚨 A TEMPORARY construction fault — one attempt, one caller's scope — configures nothing for THIS
    // build too, so every other name keeps building (review of #6854), but the build must not be cached
    // as complete: IOptionsMonitor would hand out options lacking the module's setup until something
    // invalidated them. The monitor's cache inserts the entry BEFORE it builds the value, so the entry
    // this build will fill is removed here; this one call gets the options without the module's setup,
    // and the next Get of that name builds afresh — with the module's setup once the fault has cleared.
    // (Throwing instead would be worse: the cache keeps a build that threw, and would rethrow it for that
    // name until invalidated — the sign-in's own name included.)
    public void Configure(string? name, TOptions options)
    {
        if (contexts.TryLeaseModuleService(module, index, caller, typeof(IConfigureOptions<TOptions>), out ModuleLeaseFault fault) is not { } leased)
        {
            if (fault == ModuleLeaseFault.Temporary)
                contexts.RootService<IOptionsMonitorCache<TOptions>>()?.TryRemove(name ?? Options.DefaultName);
            return;
        }
        using var release = leased.Release;
        if (leased.Instance is IConfigureNamedOptions<TOptions> named)
            named.Configure(name, options);
        else if (name == Options.DefaultName)
            ((IConfigureOptions<TOptions>)leased.Instance).Configure(options);
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

    private static object ConfigureOptionsForwarder(Type options, ModuleContexts contexts, string module, int index, MeshWeaver.ServiceProvider.MeshServiceProvider? caller) =>
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
