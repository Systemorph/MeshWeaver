using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh;

/// <summary>How one root-service registration of a module is served — an OPEN vocabulary (policy
/// <c>open-vocabulary-string-constants</c>).</summary>
public static class ModuleServiceRoute
{
    /// <summary>A platform INTERFACE: the root holds one stable forwarding proxy that calls the
    /// module's CURRENT generation's instance, so a consumer that cached it follows a swap.</summary>
    public const string Proxy = "Proxy";

    /// <summary>A platform CLASS: the root resolves the current generation's instance on every
    /// request (a class cannot be proxied). A consumer that caches it keeps the old instance.</summary>
    public const string Current = "Current";

    /// <summary>A hosted service: started at boot, and STOPPED and RE-STARTED from the new generation
    /// on a swap.</summary>
    public const string Hosted = "Hosted";

    /// <summary>A service type the module itself declares: never registered in the root (a root
    /// registration of a collectible type pins its context forever); served to every per-node hub
    /// from the current generation, and to the module's own code from its scope.</summary>
    public const string ModuleOwned = "ModuleOwned";

    /// <summary>Only the module's scope sees it (an open generic, or infrastructure the module added).</summary>
    public const string Private = "Private";
}

/// <summary>One registration a module added, and how it is served.</summary>
/// <param name="Index">Its position among the module's added registrations — the scope key.</param>
/// <param name="Descriptor">The registration as the module made it.</param>
/// <param name="Route">One of <see cref="ModuleServiceRoute"/>.</param>
public sealed record ModuleServiceRegistration(int Index, ServiceDescriptor Descriptor, string Route)
{
    /// <summary>The scope key a forwarded registration is held under in the module's scope.</summary>
    public string Key => $"mw-module-service:{Index}";

    /// <summary>What has to stay the same across generations for the root's forwarders to keep
    /// meaning the same thing: route, service type and lifetime — by NAME for a module-owned type,
    /// which is a different <see cref="Type"/> in every generation.</summary>
    public string Shape => Route == ModuleServiceRoute.ModuleOwned || Route == ModuleServiceRoute.Private
        ? $"{Route}:{Descriptor.ServiceType.FullName}"
        : $"{Route}:{Descriptor.ServiceType.AssemblyQualifiedName}:{Descriptor.Lifetime}"
          + (Descriptor.IsKeyedService ? $":key={Descriptor.ServiceKey}" : "");
}

/// <summary>
/// The ROOT services of one module generation, held in a scope of their own instead of the mesh's
/// root container — the conversion that makes a module that registers root services live-updatable
/// (policy <c>module-live-update-default</c>, <c>Doc/Architecture/LiveModuleUpdate</c> → "Root services").
///
/// <para><b>How the boot behaviour is preserved.</b> The module's registration delegates run against a
/// COPY of the root collection as it stands when the module installs — so <c>TryAdd</c>,
/// <c>TryAddEnumerable</c> and every other "already there?" decision is taken exactly as it would have
/// been — and what they ADDED is the module's set. The root receives forwarders for exactly the
/// platform-typed registrations it would have had; the module's own types and its private
/// infrastructure live only in the module's scope, a child of the root, so a module service still
/// resolves every platform dependency.</para>
///
/// <para><b>What cannot be converted</b> (<see cref="Blockers"/>): a delegate that removed or replaced a
/// registration it did not add, a keyed registration, a class-typed platform service the module itself
/// implements (a cached instance would pin the old generation and keep serving it), an interface whose
/// module implementation also implements another platform interface (a forwarding proxy would hide it),
/// and an open generic the module implements. Such a module keeps the old path — straight into the root
/// — and stays restart-required, said by name.</para>
/// </summary>
public sealed class ModuleServices : IDisposable
{
    private readonly object gate = new();
    private IServiceProvider? scope;
    private ImmutableList<(int Index, IHostedService Instance)> started = [];
    private int disposed;

    private ModuleServices(
        string moduleName,
        ImmutableList<ModuleServiceRegistration> registrations,
        ImmutableList<ServiceDescriptor> prefix,
        ImmutableList<string> blockers)
    {
        ModuleName = moduleName;
        Registrations = registrations;
        Prefix = prefix;
        Blockers = blockers;
    }

    /// <summary>The module's entry-assembly name.</summary>
    public string ModuleName { get; }

    /// <summary>The generation's load context — the scope is a load-context scope of it.</summary>
    internal AssemblyLoadContext? LoadContext { get; private init; }

    /// <summary>Every registration the module added, routed.</summary>
    public ImmutableList<ModuleServiceRegistration> Registrations { get; }

    /// <summary>The root collection as the module saw it — what a later generation's delegates run
    /// against, so its TryAdd decisions are the boot ones.</summary>
    internal ImmutableList<ServiceDescriptor> Prefix { get; }

    /// <summary>Why these services cannot be served from a module scope — empty when they can.</summary>
    public ImmutableList<string> Blockers { get; }

    /// <summary>The shape a swap must preserve (see <see cref="ModuleServiceRegistration.Shape"/>).</summary>
    public ImmutableList<string> Shape => Registrations
        // Hosted services are NOT part of the shape: the root holds no per-registration forwarder for
        // them — one ModuleHostedServicesHost starts whatever the CURRENT generation registers — so a
        // generation may add or drop a background service and still swap live (measured: the AI
        // update behind the 2026-10-05 incident added exactly one hosted service and nothing else).
        .Where(r => r.Route is ModuleServiceRoute.Proxy or ModuleServiceRoute.Current)
        .Select(r => r.Shape)
        .ToImmutableList();

    /// <summary>
    /// Runs <paramref name="configurations"/> against a copy of <paramref name="prefix"/> and routes what
    /// they added. Throws what a delegate throws — the caller treats that as the generation failing.
    /// </summary>
    /// <param name="moduleName">The module's entry-assembly name.</param>
    /// <param name="moduleContext">The generation's load context — what "the module's own type" means.</param>
    /// <param name="configurations">The module's root-service delegates, in node order.</param>
    /// <param name="prefix">The root collection as it stands when the module installs.</param>
    public static ModuleServices Probe(
        string moduleName,
        AssemblyLoadContext moduleContext,
        IReadOnlyList<Func<IServiceCollection, IServiceCollection>> configurations,
        IReadOnlyList<ServiceDescriptor> prefix)
    {
        var probe = new ServiceCollection();
        foreach (var descriptor in prefix)
            ((ICollection<ServiceDescriptor>)probe).Add(descriptor);
        IServiceCollection result = probe;
        foreach (var configure in configurations)
            result = configure(result) ?? result;

        var blockers = ImmutableList.CreateBuilder<string>();
        if (!ReferenceEquals(result, probe))
            blockers.Add("a root-service delegate returned a different collection than it was given");
        var intact = probe.Count >= prefix.Count;
        for (var i = 0; intact && i < prefix.Count; i++)
            intact = ReferenceEquals(probe[i], prefix[i]);
        if (!intact)
            blockers.Add("a root-service delegate removed or replaced a registration it did not add");

        var registrations = ImmutableList.CreateBuilder<ModuleServiceRegistration>();
        var added = intact ? probe.Skip(prefix.Count).ToArray() : [];
        for (var i = 0; i < added.Length; i++)
        {
            var descriptor = added[i];
            var route = Route(descriptor, moduleContext, out var blocker);
            if (blocker is not null)
                blockers.Add($"{descriptor.ServiceType.Name}: {blocker}");
            registrations.Add(new ModuleServiceRegistration(i, descriptor, route));
        }
        return new ModuleServices(moduleName, registrations.ToImmutable(), [.. prefix], blockers.ToImmutable())
        {
            LoadContext = moduleContext.IsCollectible ? moduleContext : null,
        };
    }

    private static string Route(ServiceDescriptor descriptor, AssemblyLoadContext module, out string? blocker)
    {
        blocker = null;
        var type = descriptor.ServiceType;
        // A keyed registration is forwarded under its OWN key — unless the key itself is a module
        // object, which a root registration would then hold (and pin) for the life of the process.
        if (descriptor.IsKeyedService && descriptor.ServiceKey is { } key && IsOwned(key.GetType(), module))
        {
            blocker = $"its service key is a module type ({key.GetType().Name}) — the root would hold it";
            return ModuleServiceRoute.Private;
        }
        var implementation = ImplementationTypeOf(descriptor);
        if (type.IsGenericTypeDefinition)
        {
            if (IsOwned(implementation, module))
                blocker = "the module implements an open generic root service";
            return ModuleServiceRoute.Private;
        }
        if (IsOwned(type, module))
            return ModuleServiceRoute.ModuleOwned;
        if (type == typeof(IHostedService))
            return ModuleServiceRoute.Hosted;
        if (type.IsInterface)
        {
            if (implementation is not null && IsOwned(implementation, module))
            {
                var hidden = implementation.GetInterfaces()
                    .Where(i => i != type && !type.GetInterfaces().Contains(i)
                                && !IsOwned(i, module)
                                && i != typeof(IDisposable) && i != typeof(IAsyncDisposable))
                    .Select(i => i.Name)
                    .ToArray();
                if (hidden.Length > 0)
                    blocker = $"its implementation {implementation.Name} also implements {string.Join(", ", hidden)}, which a forwarding proxy would hide";
            }
            return ModuleServiceRoute.Proxy;
        }
        if (implementation is not null && IsOwned(implementation, module))
            blocker = $"a class-typed root service implemented by the module ({implementation.Name}) — a cached instance would keep serving, and pin, the old generation";
        return ModuleServiceRoute.Current;
    }

    private static Type? ImplementationTypeOf(ServiceDescriptor descriptor) =>
        descriptor.IsKeyedService
            ? descriptor.KeyedImplementationType
              ?? descriptor.KeyedImplementationInstance?.GetType()
              ?? descriptor.KeyedImplementationFactory?.Method.ReturnType
            : descriptor.ImplementationType
              ?? descriptor.ImplementationInstance?.GetType()
              ?? descriptor.ImplementationFactory?.Method.ReturnType;

    /// <summary>Whether <paramref name="type"/> (or a generic argument of it) comes from
    /// <paramref name="module"/>.</summary>
    public static bool IsOwned(Type? type, AssemblyLoadContext module) =>
        type is not null
        && (ReferenceEquals(AssemblyLoadContext.GetLoadContext(type.Assembly), module)
            || (type.IsGenericType && type.GetGenericArguments().Any(a => IsOwned(a, module)))
            || (type.HasElementType && IsOwned(type.GetElementType(), module)));

    /// <summary>
    /// The module's scope — a child of <paramref name="root"/> holding every registration the module
    /// added, the forwarded ones under their <see cref="ModuleServiceRegistration.Key"/> so the root can
    /// ask for exactly one. Built once, on first use.
    /// </summary>
    public IServiceProvider Scope(IServiceProvider root)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            if (scope is not null)
                return scope;
            IServiceCollection services = new ServiceCollection();
            foreach (var registration in Registrations)
            {
                var d = registration.Descriptor;
                // A keyed registration keeps its own key — the root forwards by that key; an unkeyed
                // forwarded one is held under its index key so the root can ask for exactly it.
                if (registration.Route is ModuleServiceRoute.Proxy or ModuleServiceRoute.Current or ModuleServiceRoute.Hosted
                    && !d.IsKeyedService)
                    services.Add(Keyed(d, registration.Key));
                else
                    services.Add(d);
            }
            // A container of its OWN, reaching the root only for types that do not name the module —
            // a child scope of the root would pin the generation (MeshWeaver.ServiceProvider.ModuleServiceProvider).
            var context = LoadContext;
            return scope = ModuleServiceProvider.Create(
                services, root,
                type => context is not null && IsOwned(type, context),
                Prefix.Where(d => d.ServiceType.IsGenericTypeDefinition));
        }
    }

    private static ServiceDescriptor Keyed(ServiceDescriptor d, string key) =>
        d.ImplementationInstance is { } instance
            ? new ServiceDescriptor(d.ServiceType, key, instance)
            : d.ImplementationFactory is { } factory
                ? new ServiceDescriptor(d.ServiceType, key, (sp, _) => factory(sp), d.Lifetime)
                : new ServiceDescriptor(d.ServiceType, key, d.ImplementationType!, d.Lifetime);

    /// <summary>The instance behind forwarded registration <paramref name="index"/>.</summary>
    public object Resolve(IServiceProvider root, int index)
    {
        var registration = Registrations[index];
        var descriptor = registration.Descriptor;
        return Scope(root).GetRequiredKeyedService(
            descriptor.ServiceType, descriptor.IsKeyedService ? descriptor.ServiceKey : registration.Key);
    }

    /// <summary>Starts hosted registration <paramref name="index"/> from this generation and records
    /// it, so a swap can stop it. A failure is reported and costs the module's feature, never the
    /// host (#2449). Task-shaped because <see cref="IHostedService"/> is — no <c>await</c>.</summary>
    public Task StartHosted(IServiceProvider root, int index, CancellationToken ct, ILogger? logger)
    {
        IHostedService instance;
        Task starting;
        try
        {
            instance = (IHostedService)Resolve(root, index);
            starting = instance.StartAsync(ct);
        }
        catch (Exception exception)
        {
            ReportStartFailure(exception, logger);
            return Task.CompletedTask;
        }
        return starting.ContinueWith(
            t =>
            {
                if (t.IsFaulted)
                    ReportStartFailure(t.Exception!, logger);
                else
                    lock (gate)
                        started = started.Add((index, instance));
            },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void ReportStartFailure(Exception exception, ILogger? logger) =>
        logger?.LogError(exception,
            "[MeshWeaver.Mesh.IncompatibleModule] a hosted service of module {Module} could not start — its feature is absent",
            ModuleName);

    /// <summary>Starts EVERY hosted registration of this generation — the host at boot, and the swap
    /// for the generation it puts in service.</summary>
    public Task StartAllHosted(IServiceProvider root, CancellationToken ct, ILogger? logger) =>
        Task.WhenAll(Registrations
            .Where(r => r.Route == ModuleServiceRoute.Hosted)
            .Select(r => StartHosted(root, r.Index, ct, logger)));

    /// <summary>The hosted registrations this generation has started.</summary>
    public ImmutableList<int> StartedHosted
    {
        get { lock (gate) return started.Select(s => s.Index).ToImmutableList(); }
    }

    /// <summary>Stops every hosted service this generation started — before it is retired. A stop
    /// that faults is logged; it never keeps the others running.</summary>
    public Task StopHosted(CancellationToken ct, ILogger? logger)
    {
        ImmutableList<(int Index, IHostedService Instance)> running;
        lock (gate)
        {
            running = started;
            started = [];
        }
        return Task.WhenAll(running.Select(r =>
        {
            Task stopping;
            try
            {
                stopping = r.Instance.StopAsync(ct);
            }
            catch (Exception exception)
            {
                logger?.LogWarning(exception, "[ModuleLiveUpdate] stopping a hosted service of {Module} faulted", ModuleName);
                return Task.CompletedTask;
            }
            return stopping.ContinueWith(
                t =>
                {
                    if (t.IsFaulted)
                        logger?.LogWarning(t.Exception, "[ModuleLiveUpdate] stopping a hosted service of {Module} faulted", ModuleName);
                },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }));
    }

    /// <summary>Disposes the scope — this generation's singletons with it.</summary>
    public void Dispose()
    {
        IServiceProvider? toDispose;
        lock (gate)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;
            toDispose = scope;
            scope = null;
        }
        (toDispose as IDisposable)?.Dispose();
    }
}
