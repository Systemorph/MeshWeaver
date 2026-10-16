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

    /// <summary>A service type ANOTHER module declares — this module contributes to that module's list
    /// (every AI provider module registers MeshWeaver.AI's catalog sources and chat-client factories).
    /// Never registered in the root: a root registration would pin the declaring module's generation, and a
    /// root forwarding proxy cannot be built over a collectible interface at all ("A non-collectible
    /// assembly may not reference a collectible assembly"). Served instead to the DECLARING module's own
    /// container, after its own registrations, and to every per-node hub.</summary>
    public const string Contributed = "Contributed";
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

    private ImmutableDictionary<int, string> unservable = ImmutableDictionary<int, string>.Empty;

    /// <summary>
    /// The forwarded registrations THIS generation could not produce when a consumer leased them, by
    /// index, each with why (MeshWeaver#6847). A verdict about this generation, never about the module:
    /// a later generation starts with none. Every consumer that leases a module's service — the query
    /// fan-out, the access-rule set, the options forwarders — reads such a registration as ABSENT
    /// (<see cref="ModuleContexts.TryLeaseModuleService"/>), so an optional module that cannot serve one
    /// of its services costs exactly that feature and never a caller that never asked for it.
    /// </summary>
    public ImmutableDictionary<int, string> Unservable => Volatile.Read(ref unservable);

    /// <summary>Records registration <paramref name="index"/> as unservable in this generation; true the
    /// first time, so the verdict is reported once.</summary>
    internal bool MarkUnservable(int index, string reason) =>
        ImmutableInterlocked.TryAdd(ref unservable, index, reason);

    private ImmutableHashSet<(int Index, string Cause)> reported = ImmutableHashSet<(int Index, string Cause)>.Empty;

    /// <summary>Notes that a construction fault of registration <paramref name="index"/> with
    /// <paramref name="cause"/> (the innermost exception type) was reported; true the first time. Not a
    /// verdict — the registration stays servable — only what keeps a fault that repeats on every lease
    /// from being reported on every lease (review of #6854).</summary>
    internal bool MarkReported(int index, string cause) =>
        ImmutableInterlocked.Update(ref reported, set => set.Add((index, cause)));

    /// <summary>
    /// Why registration <paramref name="index"/> is not LAID OUT in this generation the way the root's
    /// forwarder for that index asks for it — this generation has no registration at the index, or its
    /// container holds nothing under the key the root resolves — or null when it is (MeshWeaver#6847,
    /// review of #6854). A fault every caller of this generation shares, so it may be recorded as a
    /// verdict; asked only after a resolve FAILED, so a registration that resolves is never judged by it.
    /// Null, too, when the container cannot say — then the fault is not known to be generation-wide.
    /// </summary>
    /// <param name="root">The mesh's root provider.</param>
    /// <param name="index">The registration's index.</param>
    internal string? NotLaidOut(IServiceProvider root, int index)
    {
        if (index < 0 || index >= Registrations.Count)
            return $"this generation has no registration #{index} — it holds {Registrations.Count}";
        var registration = Registrations[index];
        var descriptor = registration.Descriptor;
        var key = descriptor.IsKeyedService ? descriptor.ServiceKey : registration.Key;
        var container = TryScope(root);
        var isKeyed = container as IServiceProviderIsKeyedService ?? container?.GetService<IServiceProviderIsKeyedService>();
        return isKeyed is not null && !isKeyed.IsKeyedService(descriptor.ServiceType, key)
            ? $"'{key} ({descriptor.ServiceType.Name})' has not been registered in this generation's container"
            : null;
    }

    /// <summary>The shape a swap must preserve (see <see cref="ModuleServiceRegistration.Shape"/>), each
    /// forwarded registration prefixed with its <see cref="ModuleServiceRegistration.Index"/>.</summary>
    public ImmutableList<string> Shape => Registrations
        // Hosted services are NOT part of the shape: the root holds no per-registration forwarder for
        // them — one ModuleHostedServicesHost starts whatever the CURRENT generation registers — so a
        // generation may add or drop a background service and still swap live (measured: the AI
        // update behind the 2026-10-05 incident added exactly one hosted service and nothing else),
        // as long as it does so AFTER the forwarded registrations (see the index below).
        .Where(r => r.Route is ModuleServiceRoute.Proxy or ModuleServiceRoute.Current)
        // 🚨 The INDEX is part of the shape (MeshWeaver#6847). Every root forwarder captured the boot
        // generation's index (ModuleServiceForwarders.AddForwarders) and resolves THAT index in whatever
        // generation is current. A generation that inserts ANY registration — a module-owned one, a
        // private one, a hosted service — ahead of a forwarded one keeps the forwarded subsequence
        // identical but moves every index after it, and the root then asks the new generation for the
        // wrong registration: WhatsApp 1.3.2 → 1.4.2 went live that way on 2026-10-16, a root forwarder
        // laid out at boot for index 10 resolved the new generation's index 10 — the module-owned
        // IWhatsAppRecentChats, held unkeyed, so "mw-module-service:10 has not been registered" — and every
        // hub activation that leased it (API-token validation among them) faulted until the pods
        // restarted. Such a generation is restart-required, never a live swap.
        .Select(r => $"#{r.Index} {r.Shape}")
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
        // What the probe holds at each prefix position: the root's own descriptor, except a
        // registration-time registry, which the module writes into and so gets a copy of its own
        // (IsolateRegistrationTimeRegistry).
        var held = new ServiceDescriptor[prefix.Count];
        for (var i = 0; i < prefix.Count; i++)
            ((ICollection<ServiceDescriptor>)probe).Add(held[i] = IsolateRegistrationTimeRegistry(prefix[i]));
        IServiceCollection result = probe;
        foreach (var configure in configurations)
            result = configure(result) ?? result;

        var blockers = ImmutableList.CreateBuilder<string>();
        if (!ReferenceEquals(result, probe))
            blockers.Add("a root-service delegate returned a different collection than it was given");
        var intact = probe.Count >= prefix.Count;
        for (var i = 0; intact && i < prefix.Count; i++)
            intact = ReferenceEquals(probe[i], held[i]);
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

    /// <summary>The full name of <c>Microsoft.Extensions.Http</c>'s registration-time client registry
    /// (internal to that package, so it is recognised by name).</summary>
    internal const string HttpClientMappingRegistryTypeName = "Microsoft.Extensions.DependencyInjection.HttpClientMappingRegistry";

    /// <summary>
    /// 🚨 A prefix registration whose INSTANCE is a registry that registration code WRITES INTO
    /// (MeshWeaver.Plugins#3518). <c>AddHttpClient&lt;T&gt;()</c> records "client name → T" in the
    /// <c>HttpClientMappingRegistry</c> instance registered in the collection, and refuses a second
    /// type under the same name. The probe copies the root's descriptors BY REFERENCE, so without this
    /// every generation's <c>AddHttpClient&lt;WebSearchPlugin&gt;()</c> wrote into the ROOT's one
    /// registry: generation N's type stayed there (pinning its collectible context for the life of the
    /// process), and generation N+1's — the same name, a different <see cref="Type"/> from a different
    /// load context — was refused, failing the live swap. The probe therefore gets its own copy of the
    /// registry, seeded with what the root holds (so a module that collides with a PLATFORM client is
    /// refused exactly as at boot), and what the module writes stays with its probe.
    ///
    /// <para>Matched by name because the type is internal to <c>Microsoft.Extensions.Http</c>; its
    /// name and shape are pinned by <c>ALiveSwapReRegistersATypedHttpClientTest</c>: a package version
    /// that renames it fails that test, and one that reshapes it fails here by name, rather than
    /// silently sharing the root's instance again.</para>
    /// </summary>
    private static ServiceDescriptor IsolateRegistrationTimeRegistry(ServiceDescriptor descriptor)
    {
        if (descriptor.IsKeyedService
            || descriptor.ImplementationInstance is not { } registry
            || descriptor.ServiceType.FullName != HttpClientMappingRegistryTypeName
            || registry.GetType() != descriptor.ServiceType)
            return descriptor;
        const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var type = registry.GetType();
        var copy = Activator.CreateInstance(type, nonPublic: true)
                   ?? throw new InvalidOperationException($"{type.FullName} could not be instantiated to isolate a module's HttpClient registrations.");
        foreach (var name in new[] { "NamedClientRegistrations", "KeyedLifetimeMap" })
        {
            if (type.GetProperty(name, Members)?.GetValue(registry) is not System.Collections.IDictionary from
                || type.GetProperty(name, Members)?.GetValue(copy) is not System.Collections.IDictionary to)
                throw new InvalidOperationException(
                    $"{type.FullName} no longer exposes the dictionary {name}: a module's HttpClient registrations cannot be isolated from the root's (MeshWeaver.Plugins#3518).");
            foreach (System.Collections.DictionaryEntry entry in from)
                to[entry.Key] = entry.Value;
        }
        var defaultLifetime = type.GetProperty("DefaultKeyedLifetime", Members)
            ?? throw new InvalidOperationException(
                $"{type.FullName} no longer exposes DefaultKeyedLifetime: a module's HttpClient registrations cannot be isolated from the root's (MeshWeaver.Plugins#3518).");
        defaultLifetime.SetValue(copy, defaultLifetime.GetValue(registry));
        return new ServiceDescriptor(descriptor.ServiceType, copy);
    }

    private static string Route(ServiceDescriptor descriptor, AssemblyLoadContext module, out string? blocker)
    {
        blocker = null;
        var type = descriptor.ServiceType;
        // A keyed registration is forwarded under its OWN key — unless the key pins the module: a key
        // that is a module object, or a key that IS a module type (the keyed-by-marker-type shape,
        // `AddKeyedSingleton<IService>(typeof(SomeModuleType), …)`, whose runtime type is CoreLib's
        // RuntimeType and so passes an instance check). A root registration would hold either for the
        // life of the process, and with it the module's collectible load context. Not only THIS
        // module's: a key typed from ANOTHER collectible context (a module this one depends on, a
        // compiled NodeType) would pin that context after it swaps, and its owner's own probe cannot
        // see a registration it did not make.
        if (descriptor.IsKeyedService && descriptor.ServiceKey is { } key
            && (PinsACollectibleContext(key.GetType(), module)
                || (key is Type keyType && PinsACollectibleContext(keyType, module))))
        {
            var named = key is Type t ? t.Name : key.GetType().Name;
            blocker = $"its service key is a module type ({named}) — the root would hold it";
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
        if (IsOwnedByAnotherModule(type, module))
            return ModuleServiceRoute.Contributed;
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

    /// <summary>Whether a root registration holding <paramref name="type"/> would pin a collectible load
    /// context: the type (or a generic argument or element type of it) comes from
    /// <paramref name="module"/> or from ANY collectible assembly.</summary>
    private static bool PinsACollectibleContext(Type? type, AssemblyLoadContext module) =>
        type is not null
        && (IsOwned(type, module)
            || type.Assembly.IsCollectible
            || (type.IsGenericType && type.GetGenericArguments().Any(a => PinsACollectibleContext(a, module)))
            || (type.HasElementType && PinsACollectibleContext(type.GetElementType(), module)));

    /// <summary>Whether <paramref name="type"/> (or a generic argument of it) comes from a module context
    /// OTHER than <paramref name="module"/> — a type a module this one depends on declares.</summary>
    public static bool IsOwnedByAnotherModule(Type? type, AssemblyLoadContext module) =>
        type is not null
        && ((AssemblyLoadContext.GetLoadContext(type.Assembly) is ModuleLoadContext other && !ReferenceEquals(other, module))
            || (type.IsGenericType && type.GetGenericArguments().Any(a => IsOwnedByAnotherModule(a, module)))
            || (type.HasElementType && IsOwnedByAnotherModule(type.GetElementType(), module)));

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
                // forwarded or contributed one is held under its index key so the root can ask for exactly it.
                if (registration.Route is ModuleServiceRoute.Proxy or ModuleServiceRoute.Current or ModuleServiceRoute.Hosted
                        or ModuleServiceRoute.Contributed
                    && !d.IsKeyedService)
                    services.Add(Keyed(d, registration.Key));
                else
                    services.Add(d);
            }
            // A container of its OWN, reaching the root only for types that do not name the module —
            // a child scope of the root would pin the generation (MeshWeaver.ServiceProvider.ModuleServiceProvider).
            var context = LoadContext;
            var name = ModuleName;
            var contexts = root.GetService<ModuleContexts>();
            // A STAGED generation's container sees the other generations of its swap in place of the
            // current ones — it is built (BuildBeforePublish) before the swap commits, and must bind
            // what it will serve with once it has. A released stage answers nothing, so once the commit
            // has made every generation of the swap current this is the current generations, exactly as
            // for a generation loaded at boot.
            var stage = Stage;
            return scope = ModuleServiceProvider.CreateForCallers(
                services, root,
                type => context is not null && IsOwned(type, context),
                Prefix.Where(d => d.ServiceType.IsGenericTypeDefinition),
                type => contexts is null ? NoneElsewhere : ElsewhereSeenFrom(contexts, name, type, null, stage));
        }
    }

    /// <summary>As <see cref="Scope"/>, or null once this generation's services are disposed (a live
    /// uninstall, a swap's retirement, a boot withdrawal) — the disposal is an answer here, not a fault.</summary>
    /// <param name="root">The mesh's root provider.</param>
    internal IServiceProvider? TryScope(IServiceProvider root)
    {
        lock (gate)
            return disposed != 0 ? null : Scope(root);
    }

    /// <summary>The live swap this generation was staged in, or null (a generation loaded at boot).</summary>
    internal ModuleSwapStage? Stage { get; set; }

    /// <summary>
    /// 🚨 <b>Built before published</b> (MeshWeaver#6542, MeshWeaver#6556): builds every
    /// process-lifetime instance the ROOT can reach in this generation's container — each SINGLETON the
    /// root reaches through a forwarder (<see cref="ModuleServiceRoute.Proxy"/>, which carries the
    /// health-check and options forwarders, and <see cref="ModuleServiceRoute.Current"/>) or a
    /// module-type stand-in of <see cref="ModuleOwnedRootSource"/> (<see cref="ModuleServiceRoute.ModuleOwned"/>,
    /// <see cref="ModuleServiceRoute.Contributed"/>), and what their constructors take — BEFORE the
    /// generation can be reached from the root: at boot, before the root provider is handed out; on a
    /// swap, before the swap commits. The root then only ever reads built instances, so its lock never
    /// waits on this container's.
    ///
    /// <para>NOT built: a SCOPED registration, and a singleton whose constructor graph reaches one
    /// (<c>ModuleServiceProvider.BuildRootReachableSingletons</c>) — their instances belong to a
    /// circuit or a request, and the one a boot would build runs against scoped state nobody initialised
    /// (Radzen's <c>DialogService</c> and its <c>NavigationManager</c>, MeshWeaver#6556); a module's
    /// PRIVATE registration unless a built one takes it; and a module's HOSTED services, which are left
    /// to their own host — the root never reaches them, and a hosted service that cannot be built costs
    /// its feature, never the module (#2449). Throws what a constructor throws.</para>
    /// </summary>
    /// <param name="root">The mesh's root provider.</param>
    internal void BuildBeforePublish(IServiceProvider root)
    {
        var reachable = Registrations
            .Where(r => r.Route is ModuleServiceRoute.Proxy or ModuleServiceRoute.Current
                or ModuleServiceRoute.ModuleOwned or ModuleServiceRoute.Contributed)
            .Select(RootReachesItAs)
            .ToImmutableHashSet();
        var contexts = root.GetService<ModuleContexts>();
        var stage = Stage;
        ModuleServiceProvider.BuildRootReachableSingletons(Scope(root), root, reachable.Contains,
            type => contexts is null ? [] : RegistriesSeenFrom(contexts, type, stage));
    }

    /// <summary>
    /// EVERY module container that registers module-declared <paramref name="type"/> (unkeyed), and the
    /// service each holds it under — the registrations <see cref="ElsewhereSeenFrom"/> resolves, in the
    /// same order: the declaring module's own (under the type), then the contributions modules made of it
    /// (under their index key). What the boot-time scoped-dependency walk reads, so a singleton over
    /// another module's SCOPED type is never built at boot (<c>ModuleServiceProvider.BuildRootReachableSingletons</c>).
    ///
    /// <para>🚨 No module is excluded HERE. Which container is "elsewhere" changes as the walk recurses:
    /// once it enters module B's singleton, B's fallback resolves module A's contributions, so a walk that
    /// started in A must see A's registrations from inside B (A singleton → B singleton → A's scoped
    /// contribution). The walk excludes only the container it is currently in, by reference
    /// (<c>ScopedDependencyWalk.RegistrationsOf</c>).</para>
    /// </summary>
    internal static IEnumerable<(Autofac.Core.IComponentRegistry Registry, Autofac.Core.Service Service)> RegistriesSeenFrom(
        ModuleContexts contexts, Type type, ModuleSwapStage? stage)
    {
        if (type.IsGenericTypeDefinition)
            yield break;
        var generations = contexts.GenerationsSeenFrom(stage)
            .Where(g => g.Services is not null)
            .OrderBy(g => g.Name, StringComparer.Ordinal)
            .ToArray();
        var owner = generations.FirstOrDefault(g => IsOwned(type, g.Context));
        if (owner is null)
            yield break;
        if (RegistryOf(contexts, owner.Name, stage) is { } ownerRegistry)
            yield return (ownerRegistry, new Autofac.Core.TypedService(type));
        foreach (var contributor in generations.Where(g => g.Name != owner.Name))
        {
            var contributions = contributor.Services!.Registrations
                .Where(r => r.Route == ModuleServiceRoute.Contributed && r.Descriptor.ServiceType == type
                            && !r.Descriptor.IsKeyedService)
                .ToArray();
            if (contributions.Length == 0 || RegistryOf(contexts, contributor.Name, stage) is not { } registry)
                continue;
            foreach (var registration in contributions)
                yield return (registry, new Autofac.Core.KeyedService(registration.Key, type));
        }
    }

    private static Autofac.Core.IComponentRegistry? RegistryOf(ModuleContexts contexts, string module, ModuleSwapStage? stage) =>
        contexts.ModuleScopeSeenFrom(module, stage)?.GetService<Autofac.ILifetimeScope>()?.ComponentRegistry;

    // The Autofac service a registration is held under in the module container (see Scope): a keyed
    // registration under its own key, an unkeyed forwarded or contributed one under its index key, an
    // unkeyed module-owned one under its type.
    private static Autofac.Core.Service RootReachesItAs(ModuleServiceRegistration registration)
    {
        var descriptor = registration.Descriptor;
        if (descriptor.IsKeyedService)
            return new Autofac.Core.KeyedService(descriptor.ServiceKey!, descriptor.ServiceType);
        return registration.Route == ModuleServiceRoute.ModuleOwned
            ? new Autofac.Core.TypedService(descriptor.ServiceType)
            : new Autofac.Core.KeyedService(registration.Key, descriptor.ServiceType);
    }

    /// <summary>
    /// The registrations of <paramref name="type"/> held OUTSIDE <paramref name="requester"/>'s container,
    /// when <paramref name="type"/> is a type a module declares — each a resolver of its own registration,
    /// from the CURRENT generations, in a stable order: the declaring module's own registrations (unless it
    /// is the requester), then the contributions other modules made of it
    /// (<see cref="ModuleServiceRoute.Contributed"/>, module name then registration order; never the
    /// requester's own, which its container registers itself). Empty for a type no module declares.
    ///
    /// <para>This is how a module type is served wherever it is asked: to the root (and through it the
    /// mesh hub and every per-node hub — <see cref="ModuleOwnedRootSource"/>), and to another module's
    /// container (<c>ModuleFallbackSource</c>). Before it, MeshWeaver.AI's catalog never saw a provider
    /// module's sources or chat-client factories, and a provider module's code — which reads the AI
    /// module's <c>ChatClientCredentialResolver</c> off the mesh hub — could not resolve it at all.</para>
    /// </summary>
    /// <param name="contexts">The mesh's module registry.</param>
    /// <param name="requester">The module whose container asks, or null for the root.</param>
    /// <param name="type">The service type asked for.</param>
    public static IReadOnlyList<Func<object>> RegistrationsElsewhere(ModuleContexts contexts, string? requester, Type type)
        => WithoutCaller(ElsewhereSeenFrom(contexts, requester, type, null, null));

    /// <summary>
    /// As <see cref="RegistrationsElsewhere(ModuleContexts, string?, Type)"/>, each resolver told the CALLER
    /// it resolves for — the root-side scope (a Blazor circuit, a request, a hub scope), or null — so a
    /// scoped registration is that caller's instance, in the module scope the caller owns (MeshWeaver#6584).
    /// </summary>
    /// <param name="contexts">The mesh's module registry.</param>
    /// <param name="requester">The module whose container asks, or null for the root.</param>
    /// <param name="type">The service type asked for.</param>
    public static IReadOnlyList<Func<Autofac.ILifetimeScope?, object>> RegistrationsElsewhereForCallers(
        ModuleContexts contexts, string? requester, Type type)
        => ElsewhereSeenFrom(contexts, requester, type, null, null);

    /// <summary>
    /// As <see cref="RegistrationsElsewhere(ModuleContexts, string?, Type)"/>, for the registrations of
    /// <paramref name="type"/> under service key <paramref name="key"/>: a module's keyed registration
    /// keeps its own key in its container, so a consumer asking by that key — on the mesh hub or a per-node
    /// hub (<see cref="ModuleOwnedRootSource"/>) — gets the module's.
    /// </summary>
    /// <param name="contexts">The mesh's module registry.</param>
    /// <param name="requester">The module whose container asks, or null for the root.</param>
    /// <param name="type">The service type asked for.</param>
    /// <param name="key">The service key asked for.</param>
    public static IReadOnlyList<Func<object>> KeyedRegistrationsElsewhere(ModuleContexts contexts, string? requester, Type type, object key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return WithoutCaller(ElsewhereSeenFrom(contexts, requester, type, key, null));
    }

    /// <summary>
    /// As <see cref="KeyedRegistrationsElsewhere(ModuleContexts, string?, Type, object)"/>, each resolver told
    /// the CALLER it resolves for (see <see cref="RegistrationsElsewhereForCallers"/>).
    /// </summary>
    /// <param name="contexts">The mesh's module registry.</param>
    /// <param name="requester">The module whose container asks, or null for the root.</param>
    /// <param name="type">The service type asked for.</param>
    /// <param name="key">The service key asked for.</param>
    public static IReadOnlyList<Func<Autofac.ILifetimeScope?, object>> KeyedRegistrationsElsewhereForCallers(
        ModuleContexts contexts, string? requester, Type type, object key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return ElsewhereSeenFrom(contexts, requester, type, key, null);
    }

    private static readonly IReadOnlyList<Func<Autofac.ILifetimeScope?, object>> NoneElsewhere = [];

    private static IReadOnlyList<Func<object>> WithoutCaller(IReadOnlyList<Func<Autofac.ILifetimeScope?, object>> resolvers) =>
        resolvers.Select(resolve => (Func<object>)(() => resolve(null))).ToImmutableList();

    // key null = the unkeyed registrations. An unkeyed request never sees a keyed registration and a
    // keyed one sees only the registrations under its own key: the module container holds a keyed
    // registration under that key, so counting it among the unkeyed positions would point ResolveAt
    // past the end of the type's registrations.
    // stage non-null = asked from a container of that swap's STAGED generations: the stage's generations
    // stand in for the current ones of the same name, while the stage is open (BuildBeforePublish).
    internal static IReadOnlyList<Func<Autofac.ILifetimeScope?, object>> ElsewhereSeenFrom(
        ModuleContexts contexts, string? requester, Type type, object? key, ModuleSwapStage? stage)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(type);
        if (type.IsGenericTypeDefinition)
            return [];
        var generations = contexts.GenerationsSeenFrom(stage)
            .Where(g => g.Services is not null)
            .OrderBy(g => g.Name, StringComparer.Ordinal)
            .ToArray();
        var owner = generations.FirstOrDefault(g => IsOwned(type, g.Context));
        if (owner is null)
            return [];
        var resolvers = ImmutableList.CreateBuilder<Func<Autofac.ILifetimeScope?, object>>();
        if (owner.Name != requester)
        {
            var ownerName = owner.Name;
            if (key is not null)
            {
                if (owner.Services!.Registrations.Any(r => r.Route == ModuleServiceRoute.ModuleOwned
                        && r.Descriptor.ServiceType == type && Matches(r.Descriptor, key)))
                    resolvers.Add(caller => ModuleServiceProvider.InCallerScope(
                            contexts.ModuleScopeSeenFrom(ownerName, stage)
                            ?? throw new InvalidOperationException($"Module {ownerName} holds no service scope."), caller)
                        .GetRequiredKeyedService(type, key));
            }
            else
            {
                var count = owner.Services!.Registrations.Count(r => r.Route == ModuleServiceRoute.ModuleOwned
                    && r.Descriptor.ServiceType == type && !r.Descriptor.IsKeyedService);
                for (var position = 0; position < count; position++)
                {
                    var at = position;
                    resolvers.Add(caller => ModuleServiceProvider.ResolveAt(
                        contexts.ModuleScopeSeenFrom(ownerName, stage) ?? throw new InvalidOperationException($"Module {ownerName} holds no service scope."),
                        type, at, caller));
                }
            }
        }
        foreach (var contributor in generations.Where(g => g.Name != requester && g.Name != owner.Name))
        {
            var name = contributor.Name;
            foreach (var registration in contributor.Services!.Registrations
                         .Where(r => r.Route == ModuleServiceRoute.Contributed && r.Descriptor.ServiceType == type
                             && (key is null ? !r.Descriptor.IsKeyedService : Matches(r.Descriptor, key))))
            {
                var index = registration.Index;
                resolvers.Add(caller => contexts.ResolveModuleServiceSeenFrom(name, index, stage, caller));
            }
        }
        return resolvers.ToImmutable();
    }

    private static bool Matches(ServiceDescriptor descriptor, object key) =>
        descriptor.IsKeyedService && Equals(descriptor.ServiceKey, key);

    private static ServiceDescriptor Keyed(ServiceDescriptor d, string key) =>
        d.ImplementationInstance is { } instance
            ? new ServiceDescriptor(d.ServiceType, key, instance)
            : d.ImplementationFactory is { } factory
                ? new ServiceDescriptor(d.ServiceType, key, (sp, _) => factory(sp), d.Lifetime)
                : new ServiceDescriptor(d.ServiceType, key, d.ImplementationType!, d.Lifetime);

    /// <summary>The instance behind forwarded registration <paramref name="index"/>.</summary>
    public object Resolve(IServiceProvider root, int index) => Resolve(root, index, null);

    /// <summary>
    /// The instance behind forwarded registration <paramref name="index"/>, for <paramref name="caller"/>:
    /// a registration that is not a singleton is resolved in the module scope the caller owns — that
    /// caller's instance, its root dependencies the caller's (MeshWeaver#6584) — and a singleton in this
    /// generation's container, as before.
    /// </summary>
    /// <param name="root">The mesh's root provider.</param>
    /// <param name="index">The registration's index.</param>
    /// <param name="caller">The root-side scope resolving (a Blazor circuit's, a request's, a hub's), or null.</param>
    public object Resolve(IServiceProvider root, int index, Autofac.ILifetimeScope? caller)
    {
        var registration = Registrations[index];
        var descriptor = registration.Descriptor;
        var container = Scope(root);
        var resolveIn = descriptor.Lifetime == ServiceLifetime.Singleton
            ? container
            : ModuleServiceProvider.InCallerScope(container, caller);
        return resolveIn.GetRequiredKeyedService(
            descriptor.ServiceType, descriptor.IsKeyedService ? descriptor.ServiceKey : registration.Key);
    }

    /// <summary>
    /// As <see cref="Resolve(IServiceProvider, int)"/>, or null when this generation's scope is disposed — before the resolve
    /// or while it runs (a live uninstall, a boot withdrawal). Only the disposal of THIS generation
    /// answers null; any other fault, and an <see cref="ObjectDisposedException"/> raised while this
    /// generation is still live, propagates.
    /// </summary>
    internal object? TryResolve(IServiceProvider root, int index)
    {
        if (Volatile.Read(ref disposed) != 0)
            return null;
        try
        {
            return Resolve(root, index);
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref disposed) != 0)
        {
            return null;
        }
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
    /// for the generation it puts in service. ONE AFTER ANOTHER in registration order, each once the
    /// previous start has completed — the order the generic host itself uses for root
    /// <see cref="IHostedService"/>s, which the per-registration forwarders this replaced inherited
    /// (#6128 review).</summary>
    public Task StartAllHosted(IServiceProvider root, CancellationToken ct, ILogger? logger) =>
        InSequence(Registrations
            .Where(r => r.Route == ModuleServiceRoute.Hosted)
            .OrderBy(r => r.Index)
            .Select(r => (Func<Task>)(() => StartHosted(root, r.Index, ct, logger))));

    /// <summary>Runs <paramref name="steps"/> one after another, each once the previous task completed.
    /// Every step this type hands it reports its own fault and completes, so one failing step never
    /// keeps the next from running. Task-shaped because <see cref="IHostedService"/> is — no <c>await</c>.</summary>
    internal static Task InSequence(IEnumerable<Func<Task>> steps) =>
        steps.Aggregate(Task.CompletedTask, (prior, next) => prior.ContinueWith(
                _ => next(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)
            .Unwrap());

    /// <summary>The hosted registrations this generation has started.</summary>
    public ImmutableList<int> StartedHosted
    {
        get { lock (gate) return started.Select(s => s.Index).ToImmutableList(); }
    }

    /// <summary>Stops every hosted service this generation started — before it is retired — one after
    /// another in REVERSE registration order, as the generic host stops its own. A stop that faults is
    /// logged; it never keeps the others running.</summary>
    public Task StopHosted(CancellationToken ct, ILogger? logger)
    {
        ImmutableList<(int Index, IHostedService Instance)> running;
        lock (gate)
        {
            running = started;
            started = [];
        }
        return InSequence(running.OrderByDescending(r => r.Index).Select(r => (Func<Task>)(() =>
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
        })));
    }

    // Leases held on this generation's services (low bits) and whether its disposal was requested
    // (Retiring). ONE word, changed only by compare-and-swap, so "may I lease?" and "may I dispose?"
    // are decided against each other atomically — no lock, no wait (MeshWeaver#6715).
    private const int Retiring = 1 << 30;
    private int leaseState;

    /// <summary>
    /// A lease on this generation's services, or null once their disposal was requested (MeshWeaver#6715).
    /// While any lease is held, <see cref="Dispose"/> only MARKS the services retiring: no new lease is
    /// granted, and the scope — this generation's singletons with it — is disposed by whoever releases
    /// the LAST lease. So a call that leased the generation runs to its end on live services, however a
    /// retirement interleaves with it; nothing waits and nothing blocks.
    /// </summary>
    internal IDisposable? TryLease()
    {
        while (true)
        {
            var state = Volatile.Read(ref leaseState);
            if ((state & Retiring) != 0)
                return null;
            if (Interlocked.CompareExchange(ref leaseState, state + 1, state) == state)
                return new ServicesLease(this);
        }
    }

    /// <summary>Whether this generation's disposal was requested — it grants no new lease.</summary>
    internal bool IsRetiring => (Volatile.Read(ref leaseState) & Retiring) != 0;

    /// <summary>Whether this generation's scope has actually been disposed.</summary>
    internal bool IsDisposed => Volatile.Read(ref disposed) != 0;

    private void ReleaseLease()
    {
        // The last lease out after a requested disposal performs it — exactly once: no lease can be
        // granted once Retiring is set, so the count reaches zero under it only once.
        if (Interlocked.Decrement(ref leaseState) == Retiring)
            DisposeNow();
    }

    private sealed class ServicesLease(ModuleServices owner) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
                owner.ReleaseLease();
        }
    }

    /// <summary>
    /// Disposes the scope — this generation's singletons with it — now, or, while a lease
    /// (<see cref="TryLease"/>) is held, when the last one is released. Grants no lease from here on.
    /// </summary>
    public void Dispose()
    {
        int state;
        do
        {
            state = Volatile.Read(ref leaseState);
            if ((state & Retiring) != 0)
                return;
        }
        while (Interlocked.CompareExchange(ref leaseState, state | Retiring, state) != state);
        if (state == 0)
            DisposeNow();
    }

    private void DisposeNow()
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
