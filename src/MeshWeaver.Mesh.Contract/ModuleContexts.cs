using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reflection;
using System.Runtime.Loader;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.Mesh;

/// <summary>
/// One loaded generation of one module: the collectible context it runs in and its entry
/// assembly. The unit <see cref="ModuleContexts"/> installs, swaps and retires.
/// </summary>
/// <param name="Name">The module's entry-assembly simple name.</param>
/// <param name="Location">The entry DLL this generation was loaded from.</param>
/// <param name="Context">The collectible context it runs in.</param>
/// <param name="Assembly">The module's entry assembly, loaded in <paramref name="Context"/>.</param>
public sealed record ModuleGeneration(string Name, string Location, ModuleLoadContext Context, Assembly Assembly)
{
    /// <summary>What this generation contributes, once materialised; null while it is being loaded.</summary>
    public ModuleContributions? Contributions { get; internal set; }

    /// <summary>This generation's root services, served from a scope of its own (see
    /// <see cref="ModuleServices"/>); null when it registers none, or when they could not be
    /// converted and went straight into the root (then the module is restart-required).</summary>
    public ModuleServices? Services { get; internal set; }

    /// <summary>Why this generation's root services went straight into the root container instead of
    /// a scope of its own — empty when they did not.</summary>
    public System.Collections.Immutable.ImmutableList<string> RootServiceBlockers { get; internal set; } = [];
}

/// <summary>
/// An assembly <see cref="ModuleContexts.ResolveDependency"/> bound for a context outside the
/// modules, the module context it lives in, and a lease on that context — taken BEFORE anything was
/// loaded from it, while it was still current, so it cannot have started unloading. The caller holds
/// <paramref name="Lease"/> for as long as it can call into the assembly, and disposes it after.
/// </summary>
/// <param name="Assembly">The bound assembly.</param>
/// <param name="Context">The module context that holds it.</param>
/// <param name="Lease">The caller's lease on <paramref name="Context"/> (<see cref="ModuleContexts.Leases"/>).</param>
public sealed record ModuleBinding(Assembly Assembly, ModuleLoadContext Context, IDisposable Lease);

/// <summary>
/// The mesh's modules, each in its OWN collectible load context — the registry a live module
/// update swaps generations in (policy <c>module-live-update-default</c>,
/// <c>Doc/Architecture/LiveModuleUpdate</c>).
///
/// <para><b>What it owns.</b> For every module name, the CURRENT generation. A generation is
/// loaded (<see cref="Load"/>), its contributions are materialised by the caller, and only then is
/// it made current (<see cref="Commit"/>) — so a generation that fails to materialise never becomes
/// the one other contexts resolve, and the one that was serving keeps serving. A generation that
/// stops being current is RETIRED (<see cref="Retire"/>): unloaded on a POSITIVE quiescence signal
/// from <see cref="Leases"/> and recorded on the mesh's <see cref="CollectibleContextUnloads"/> so
/// "it has really been collected" is observable — never on a timer (the use-after-unload SIGSEGV,
/// <see cref="AlcLeaseRegistry"/>).</para>
///
/// <para><b>What stays in the default context.</b> A module whose entry assembly is part of the
/// application's own closure (<see cref="IsImageBound"/> — it is in
/// <c>TRUSTED_PLATFORM_ASSEMBLIES</c>) is BOUND to the image: the default context resolves that
/// name for every platform assembly that references it, so a second copy in a module context would
/// split the identity. Those keep loading exactly as before and are what a live update cannot
/// reach; the registry does not hold them.</para>
///
/// <para>Mesh-scoped instance state (NoStaticState): created by the <see cref="MeshBuilder"/> that
/// installs the modules, registered as that mesh's singleton, and disposed with it — which retires
/// every generation it still holds.</para>
/// </summary>
public sealed class ModuleContexts : IDisposable, MeshWeaver.Layout.Client.IViewContributionSource
{
    private readonly ConcurrentDictionary<string, ModuleGeneration> current = new(StringComparer.Ordinal);
    // Which module contributed a mesh node — by REFERENCE, weakly: the seed node list asks it so a
    // module's nodes can be served from its CURRENT generation once the generation that contributed
    // them at boot has been swapped out (see StaticMeshNodeListProvider).
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<MeshNode, string> nodeOwners = new();
    private long sequence;
    private CollectibleContextUnloads? unloads;
    private ILogger? logger;
    private IServiceProvider? root;
    private int disposed;

    private long version;
    private readonly System.Reactive.Subjects.Subject<long> versionChanged = new();

    /// <summary>
    /// Emits the new <see cref="Version"/> whenever which generation is current changes. A cache of a
    /// module's contributions DROPS what it holds here — waiting for its next read to notice would keep
    /// the swapped-out generation referenced, and so loaded, until something happened to ask
    /// (measured: the static-node query catalog pinned a retired generation exactly that way).
    /// </summary>
    public IObservable<long> VersionChanged => versionChanged;

    private void Bump()
    {
        var now = Interlocked.Increment(ref version);
        versionChanged.OnNext(now);
    }

    /// <summary>The view registrations of every module's CURRENT generation, ordered by module name —
    /// what the layout client re-reads when <see cref="Version"/> moves.</summary>
    public IReadOnlyList<Func<MeshWeaver.Layout.Client.LayoutClientConfiguration, MeshWeaver.Layout.Client.LayoutClientConfiguration>> ViewConfigurations =>
        current.Values.OrderBy(g => g.Name, StringComparer.Ordinal)
            .SelectMany(g => g.Contributions?.Views ?? [])
            .ToArray();

    /// <summary>Moves on every change of which generation is current — what a cached view of the
    /// modules' contributions (the static-node query catalog) compares against.</summary>
    public long Version => Interlocked.Read(ref version);

    /// <summary>
    /// Makes a SLOT — a boot-time stand-in for <paramref name="node"/>, one of <paramref name="moduleName"/>'s
    /// nodes, stripped of everything that can reference module code — as owned by that module, so the
    /// static-node list serves the module's CURRENT nodes in its place and never pins a generation.
    /// </summary>
    public MeshNode SlotFor(MeshNode node, string moduleName)
    {
        ArgumentNullException.ThrowIfNull(node);
        var slot = node with
        {
            Content = null,
            HubConfiguration = null,
            GlobalServiceConfigurations = [],
            ExcludeFromContext = node.ExcludeFromContext?.ToArray(),
        };
        nodeOwners.AddOrUpdate(slot, moduleName);
        return slot;
    }

    /// <summary>Who is executing inside which module generation — the quiescence an unload waits on.</summary>
    public AlcLeaseRegistry Leases { get; } = new();

    /// <summary>The current generation of every module this registry holds.</summary>
    public IReadOnlyCollection<ModuleGeneration> Generations => current.Values.ToArray();

    /// <summary>The current generation of <paramref name="moduleName"/>, or null.</summary>
    public ModuleGeneration? Current(string moduleName) =>
        current.TryGetValue(moduleName, out var generation) ? generation : null;

    /// <summary>
    /// Whether <paramref name="simpleName"/> is part of the application's own closure — listed in
    /// <c>TRUSTED_PLATFORM_ASSEMBLIES</c>, so the default context binds it for every platform
    /// assembly that references it. Such a module cannot live in its own context without splitting
    /// its identity, so it is loaded into the default context and its update needs a restart.
    /// Pure over the process's TPA list.
    /// </summary>
    public static bool IsImageBound(string simpleName)
    {
        if (string.IsNullOrEmpty(simpleName)
            || AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is not string tpa)
            return false;
        foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (string.Equals(Path.GetFileNameWithoutExtension(path), simpleName, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// Attaches the mesh's collection tracker and logger once the container exists. Retirements
    /// before this are still unloaded; they are just not observable as collected.
    /// </summary>
    public ModuleContexts Attach(CollectibleContextUnloads? collectibleUnloads, ILogger? log)
        => Attach(collectibleUnloads, log, null);

    /// <summary>As <see cref="Attach(CollectibleContextUnloads?, ILogger?)"/>, plus the ROOT provider
    /// a module's service scope is a child of.</summary>
    public ModuleContexts Attach(CollectibleContextUnloads? collectibleUnloads, ILogger? log, IServiceProvider? rootProvider)
    {
        unloads ??= collectibleUnloads;
        logger ??= log;
        root ??= rootProvider;
        return this;
    }

    /// <summary>A service of the mesh's root container, or null when it has none or the registry is not
    /// attached to it yet.</summary>
    internal T? RootService<T>() where T : class => root?.GetService<T>();

    private IServiceProvider Root => root ?? throw new InvalidOperationException(
        "The module registry is not attached to its mesh's container yet — a module service was resolved before the mesh was built.");

    /// <summary>
    /// 🚨 Every root forwarder, of every module's CURRENT generation, that no lease can resolve —
    /// empty when all of them resolve, and while the registry is not yet attached (nothing can lease
    /// before then). The reading behind the <c>module_forwarders</c> readiness check
    /// (<see cref="ModuleForwarderHealthCheck"/>, MeshWeaver#6847); see
    /// <see cref="ModuleServices.UnresolvableForwarders"/> for why one reading is already sustained.
    ///
    /// <para>🚨 Each generation is inspected under the SAME leases a forwarder's resolve takes
    /// (<see cref="TryLeaseCurrent"/>, as <see cref="TryLeaseModuleService(string, int, MeshWeaver.ServiceProvider.MeshServiceProvider?, Type?)"/>): its context and its
    /// services are leased, and its currency re-checked, BEFORE its container is read — so a live swap
    /// that retires it mid-scan defers the disposal to the scan's release instead of pulling the
    /// container from under it (an <see cref="ObjectDisposedException"/> read as a transient Unhealthy).
    /// A generation that is no longer current, or already retiring, is skipped: it is not what any
    /// forwarder resolves, and the generation that replaced it is read by the next probe.</para>
    /// </summary>
    public ImmutableList<ModuleForwarderGap> UnresolvableForwarders()
    {
        if (root is not { } attached)
            return [];
        var gaps = ImmutableList.CreateBuilder<ModuleForwarderGap>();
        foreach (var name in current.Keys.OrderBy(n => n, StringComparer.Ordinal))
        {
            if (TryLeaseCurrent(name, "the forwarder health scan") is not { } leased)
                continue;
            using (leased.Release)
                gaps.AddRange(leased.Services.UnresolvableForwarders(attached)
                    .Select(gap => new ModuleForwarderGap(leased.Generation.Name, leased.Generation.Location, gap)));
        }
        return gaps.ToImmutable();
    }

    /// <summary>
    /// <paramref name="module"/>'s CURRENT generation and its services, with a lease on both its context and
    /// its services — or null when it has none, when its services are already retiring, or when it stopped
    /// being current before the leases were taken (MeshWeaver#6715). The generation is read ONCE; its
    /// context is leased BEFORE its currency is re-checked, so a <see cref="Retire"/> that removes it after
    /// that check finds the lease and defers the services' disposal (and the unload) to its release.
    /// </summary>
    /// <param name="module">The module's entry-assembly name.</param>
    private (ModuleGeneration Generation, ModuleServices Services, IDisposable Release)? TryLeaseCurrent(string module, string holder)
    {
        if (Current(module) is not { Services: { } services } generation)
            return null;
        var context = Leases.Enter(generation.Context, holder);
        var held = services.TryLease();
        if (held is null || !ReferenceEquals(Current(module), generation))
        {
            held?.Dispose();
            context.Dispose();
            return null;
        }
        // Released in this order: the services first — their disposal, when this is the last lease of a
        // retiring generation, runs module code — and the context lease after it.
        return (generation, services, new System.Reactive.Disposables.CompositeDisposable(held, context));
    }

    // The holder a service lease is recorded under (AlcLeaseRegistry.Holders): the platform interface the
    // consumer leased — which names the consumer's kind (IMeshQueryProvider: the query fan-out;
    // INodeTypeAccessRule: the access-rule set; IConfigureOptions: the options pipeline) — and the
    // registration. A label per kind, never per call, so the registry's map stays small.
    internal static string ServiceLeaseHolder(Type? expected, int index) =>
        $"{(expected is null ? "module service" : DisplayName(expected))} (registration {index})";

    private static string DisplayName(Type type) =>
        type.IsGenericType ? DisplayName(type, type.GetGenericArguments()) : type.Name;

    // 🚨 A diagnostic label must never break the lease it labels (review of #7647). The generic arguments
    // of a constructed type cover its DECLARING types' parameters as well as its own, and only the type's
    // OWN arity is written after its backtick — a type nested in a generic declarer (Outer<int>.Options)
    // is generic with no backtick at all. So the name is split only where a backtick is present, the own
    // arity is read from it (never assumed), and the leading arguments go to the declaring type.
    private static string DisplayName(Type type, Type[] arguments)
    {
        var tick = type.Name.IndexOf('`', StringComparison.Ordinal);
        var stem = tick < 0 ? type.Name : type.Name[..tick];
        var own = tick >= 0 && int.TryParse(type.Name.AsSpan(tick + 1), out var arity)
            ? Math.Clamp(arity, 0, arguments.Length)
            : 0;
        var inherited = arguments.Length - own;
        var declaring = type.IsNested && inherited > 0 && type.DeclaringType is { } outer
            ? DisplayName(outer, arguments[..inherited]) + "."
            : "";
        var parameters = own > 0 ? $"<{string.Join(", ", arguments[inherited..].Select(DisplayName))}>" : "";
        return declaring + stem + parameters;
    }

    /// <summary>The instance behind forwarded registration <paramref name="index"/> of
    /// <paramref name="module"/>'s CURRENT generation — what every root forwarder resolves.</summary>
    public object ResolveModuleService(string module, int index) => ResolveModuleService(module, index, null);

    /// <summary>As <see cref="ResolveModuleService(string, int)"/>, for <paramref name="caller"/> — the
    /// root-side scope resolving (a Blazor circuit's, a request's, a hub's), or null (MeshWeaver#6584,
    /// <see cref="ModuleServices.Resolve(IServiceProvider, int, MeshWeaver.ServiceProvider.MeshServiceProvider?)"/>).</summary>
    /// <param name="module">The module's entry-assembly name.</param>
    /// <param name="index">The registration's index.</param>
    /// <param name="caller">The root-side scope resolving, or null.</param>
    public object ResolveModuleService(string module, int index, MeshWeaver.ServiceProvider.MeshServiceProvider? caller) =>
        (Current(module)?.Services ?? throw new InvalidOperationException(
            $"Module {module} holds no service scope in this mesh."))
        .Resolve(Root, index, caller);

    /// <summary>
    /// The instance behind registration <paramref name="index"/> of <paramref name="module"/>'s CURRENT
    /// generation, or null when there is none — read off ONE snapshot of that generation
    /// (MeshWeaver#6557). A live uninstall or a boot withdrawal can remove and dispose the generation
    /// at any moment; a check of <see cref="Current"/> followed by <see cref="ResolveModuleService(string, int)"/>
    /// reads it twice and faults when it went in between. Here the generation read once is the one
    /// resolved, and its disposal — before or during the resolve — answers null: the member is absent.
    /// </summary>
    internal object? TryResolveCurrentModuleService(string module, int index) =>
        Current(module)?.Services is { } services ? services.TryResolve(Root, index) : null;

    /// <summary>
    /// 🚨 The ATOMIC "try current service" (MeshWeaver#6715): the instance behind registration
    /// <paramref name="index"/> of <paramref name="module"/>'s CURRENT generation, together with a lease
    /// that keeps that generation's services from being disposed and its context from being unloaded
    /// until it is released — or null when the module has no current generation, or its generation is
    /// already retiring. Replaces "is it still current?" followed by a call through the forwarder, which
    /// a retirement in between turned into <c>Module … holds no service scope</c>.
    ///
    /// <para>The generation is read ONCE. Its context is leased BEFORE its currency is re-checked, as in
    /// <see cref="ResolveDependency"/>: <see cref="Retire"/> removes a generation from the current set
    /// before it waits for quiescence, so a lease taken while it was still current is one the unload
    /// waits for. Its services are leased the same way (<see cref="ModuleServices.TryLease"/>): a
    /// <see cref="ModuleServices.Dispose"/> that comes after the lease is deferred to its release.</para>
    ///
    /// <para>🚨 <b>A registration the current generation cannot PRODUCE is absent, never a fault in the
    /// caller</b> (MeshWeaver#6847). The root's forwarders were laid out at boot; when the current
    /// generation's registration at that index cannot be resolved, or resolves to something that is not
    /// what the forwarder forwards (<paramref name="expected"/>), every consumer that leased it used to
    /// fault — and the query fan-out leases EVERY module's provider for EVERY query, so one optional
    /// module's broken service failed node resolution, and with it every hub activation and the
    /// sign-in's identity lookup (memex, 2026-10-16: WhatsApp's <c>mw-module-service:10</c>). The lease
    /// answers null, which every leasing consumer already reads as "the module's feature is absent" (the
    /// shape a module withdrawn at boot has had since #6542). A direct call through the forwarder still
    /// throws the real error — that call IS the feature.</para>
    ///
    /// <para>🚨 <b>Only a GENERATION-WIDE fault is a durable verdict</b> (review of #6854). A registration this
    /// generation does not LAY OUT the way the root's forwarders were laid out at boot — its container
    /// has nothing registered under the forwarded key, or what it resolves is not what the forwarder
    /// forwards — fails for every caller alike, so it is recorded as UNSERVABLE in that generation
    /// (<see cref="ModuleServices.Unservable"/>) and reported once, on stderr and as an error log naming
    /// the module, the registration and why; the feature stays absent until a new generation is installed
    /// or the process restarts. Any OTHER construction fault — a scoped or transient registration failing
    /// in ONE caller's scope, a singleton whose constructor threw this time — is that lease's alone: it
    /// answers null for this caller and records nothing, so the next caller (or the next query) resolves
    /// afresh; it is reported once per registration and exception type. The caller's own scope
    /// ending (<see cref="ObjectDisposedException"/>, also when the container wrapped it in a resolution
    /// exception) is the caller's fault and propagates — but only when that scope HAS ended: a disposal
    /// the module's constructor ran into under a live caller is a construction fault, isolated like any
    /// other.</para>
    /// </summary>
    /// <param name="module">The module's entry-assembly name.</param>
    /// <param name="index">The registration's index.</param>
    /// <param name="caller">The root-side scope resolving, or null.</param>
    /// <param name="expected">What the root's forwarder forwards — the leased instance must be one; null
    /// when the caller does not know.</param>
    internal (object Instance, IDisposable Release)? TryLeaseModuleService(
        string module, int index, MeshWeaver.ServiceProvider.MeshServiceProvider? caller, Type? expected = null) =>
        TryLeaseModuleService(module, index, caller, expected, out ModuleLeaseFault _);

    /// <summary>As <see cref="TryLeaseModuleService(string, int, MeshWeaver.ServiceProvider.MeshServiceProvider?, Type?)"/>, saying WHY a
    /// null answer is null (<paramref name="fault"/>): the module is not held or is retiring
    /// (<see cref="ModuleLeaseFault.None"/> — the module's feature is genuinely ABSENT), or the module is
    /// held and could not produce the registration — this time (<see cref="ModuleLeaseFault.Temporary"/>)
    /// or in this generation at all (<see cref="ModuleLeaseFault.Unservable"/>). A consumer whose safe
    /// reading of a fault differs from absence asks this: an options build must not be cached over a
    /// temporary fault, and an ACCESS RULE that cannot be produced must deny, never fall back to the
    /// ordinary permission check (review of #6854).</summary>
    /// <param name="module">The module's entry-assembly name.</param>
    /// <param name="index">The registration's index.</param>
    /// <param name="caller">The root-side scope resolving, or null.</param>
    /// <param name="expected">What the root's forwarder forwards, or null.</param>
    /// <param name="fault">Why the answer is null; <see cref="ModuleLeaseFault.None"/> when it is not.</param>
    internal (object Instance, IDisposable Release)? TryLeaseModuleService(
        string module, int index, MeshWeaver.ServiceProvider.MeshServiceProvider? caller, Type? expected, out ModuleLeaseFault fault)
    {
        fault = ModuleLeaseFault.None;
        if (TryLeaseCurrent(module, ServiceLeaseHolder(expected, index)) is not { } leased)
            return null;
        var (generation, services, release) = leased;
        if (services.Unservable.ContainsKey(index))
        {
            release.Dispose();
            fault = ModuleLeaseFault.Unservable;
            return null;
        }
        object instance;
        try
        {
            instance = services.Resolve(Root, index, caller);
        }
        catch (Exception exception) when (!(IsDisposal(exception) && CallerHasEnded(caller)))
        {
            // 🚨 The lease is held THROUGH the classification and the report, released after them
            // (review of #6854): NotLaidOut reads this generation's container, and a retirement that
            // runs meanwhile defers its disposal to the last lease — released before the read, a
            // concurrent retirement could dispose the container between reading it and asking it, and
            // that ObjectDisposedException escaped into the consumer (a whole query).
            try
            {
                // Decided AFTER the resolve failed, never instead of it: a registration that resolves is
                // served, whatever a registry lookup would say about it.
                if (services.NotLaidOut(Root, index) is { } notLaidOut)
                {
                    Unservable(generation, services, index, exception.ToString(), notLaidOut);
                    fault = ModuleLeaseFault.Unservable;
                    return null;
                }
                NotServedThisTime(generation, services, index, caller, exception);
                fault = ModuleLeaseFault.Temporary;
                return null;
            }
            finally
            {
                release.Dispose();
            }
        }
        catch
        {
            release.Dispose();
            throw;
        }
        if (expected is null || expected.IsInstanceOfType(instance))
            return (instance, release);
        try
        {
            var mismatch = $"it resolved a {instance.GetType().FullName} where the root's forwarder forwards {expected.FullName} — "
                           + "this generation does not lay out its forwarded registrations the way the root's forwarders were laid out at boot";
            Unservable(generation, services, index, mismatch, mismatch);
            fault = ModuleLeaseFault.Unservable;
            return null;
        }
        finally
        {
            release.Dispose();
        }
    }

    // 🚨 Whether the CALLER's scope has ended (review of #6854) — asked only once a resolve failed with
    // a disposal somewhere in its causes. A disposal is the caller's fault only when its own scope (or,
    // for a root-side lease, the mesh's root) is the thing that ended; a constructor that ran into a
    // disposed dependency of its OWN under a live caller is a construction fault like any other, and is
    // isolated to its lease. A scope is marked disposed before it runs its disposal, so a scope that is
    // ENDING already answers true.
    private bool CallerHasEnded(MeshWeaver.ServiceProvider.MeshServiceProvider? caller)
    {
        if (caller is not null)
            return caller.IsDisposed;
        try
        {
            Root.GetService(typeof(IServiceProvider));
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    // A disposal anywhere in the causes, as the container reports it: an ObjectDisposedException, or one
    // wrapped by whatever the resolution that ran into it threw.
    private static bool IsDisposal(Exception exception)
    {
        for (var cause = exception; cause is not null; cause = cause.InnerException)
        {
            if (cause is ObjectDisposedException)
                return true;
            if (cause is AggregateException aggregate && aggregate.InnerExceptions.Any(IsDisposal))
                return true;
        }
        return false;
    }

    private static Exception InnermostOf(Exception exception)
    {
        var cause = exception;
        while (cause.InnerException is not null)
            cause = cause.InnerException;
        return cause;
    }

    private static string Innermost(Exception exception)
    {
        var cause = InnermostOf(exception);
        return $"{cause.GetType().Name}: {cause.Message}";
    }

    private static string Named(ModuleServices services, int index) =>
        services.Registrations.ElementAtOrDefault(index) is { } registration
            ? $"registration #{index} ({registration.Descriptor.ServiceType.Name}, route {registration.Route})"
            : $"registration #{index}";

    // Records registration `index` of `generation` as unservable and reports it ONCE (MeshWeaver#6847).
    // Only for a fault every caller shares — the registration is not laid out the way the root forwards it.
    private void Unservable(ModuleGeneration generation, ModuleServices services, int index, string detail, string why)
    {
        var reason = $"{Named(services, index)} of module {generation.Name} could not be served by its current generation "
                     + $"'{generation.Context.Name}' ({generation.Location}): {why}";
        if (!services.MarkUnservable(index, reason))
            return;
        var report = reason + ". It is left out by every consumer that leases it (the query fan-out, the access-rule set, "
                     + "the options it configures) — the module's feature is ABSENT, the rest of the mesh keeps serving — "
                     + "until a new generation of the module is installed or the process restarts.";
        Console.Error.WriteLine($"[MeshWeaver.Mesh.UnservableModuleService] {report}");
        logger?.LogError("[MeshWeaver.Mesh.UnservableModuleService] {Report} {Detail}", report, detail);
    }

    // A construction fault that is THIS lease's alone (review of #6854): one caller's scope, or one attempt
    // at a singleton. Nothing is recorded — the next lease resolves afresh — and it is reported once per
    // registration and exception type, so a fault that repeats on every query cannot flood the log.
    private void NotServedThisTime(ModuleGeneration generation, ModuleServices services, int index,
        MeshWeaver.ServiceProvider.MeshServiceProvider? caller, Exception exception)
    {
        if (!services.MarkReported(index, InnermostOf(exception).GetType().FullName ?? ""))
            return;
        logger?.LogError(exception,
            "[MeshWeaver.Mesh.UnservableModuleService] {Registration} of module {Module} (generation '{Generation}') could not be "
            + "constructed for {Caller}: {Cause}. Only this use goes without the module's feature; nothing is recorded, the next "
            + "use resolves it afresh. Reported once per registration and exception type.",
            Named(services, index), generation.Name, generation.Context.Name,
            caller is null ? "the root" : "a caller's scope", Innermost(exception));
    }

    /// <summary>The scope of <paramref name="module"/>'s CURRENT generation.</summary>
    public IServiceProvider? ModuleScope(string module) =>
        Current(module)?.Services?.Scope(Root);

    /// <summary>The scope of the generation <paramref name="services"/> belong to — a snapshot the caller
    /// already took, so the scope and whatever else it reads off that snapshot are the SAME generation's —
    /// or null once that generation's services are disposed (MeshWeaver#6766).</summary>
    internal IServiceProvider? TryScopeOf(ModuleServices services) => services.TryScope(Root);

    /// <summary>Registration <paramref name="index"/> of the generation <paramref name="services"/> belong
    /// to — a snapshot the caller already took, never a second read of <see cref="Current"/>.</summary>
    internal object ResolveIn(ModuleServices services, int index) => services.Resolve(Root, index);

    /// <summary>The current generations, with <paramref name="stage"/>'s generations in place of the
    /// current ones of the same name while it is open — what a STAGED generation's container sees.</summary>
    internal IReadOnlyCollection<ModuleGeneration> GenerationsSeenFrom(ModuleSwapStage? stage) =>
        stage is null
            ? Generations
            : current.Values.Select(g => stage.Get(g.Name) ?? g).ToArray();

    /// <summary>As <see cref="ModuleScope"/>, seen from a container of <paramref name="stage"/>.</summary>
    internal IServiceProvider? ModuleScopeSeenFrom(string module, ModuleSwapStage? stage) =>
        (stage?.Get(module) ?? Current(module))?.Services?.Scope(Root);

    /// <summary>As <see cref="ResolveModuleService(string, int, MeshWeaver.ServiceProvider.MeshServiceProvider?)"/>, seen from a container of <paramref name="stage"/>.</summary>
    internal object ResolveModuleServiceSeenFrom(string module, int index, ModuleSwapStage? stage, MeshWeaver.ServiceProvider.MeshServiceProvider? caller) =>
        ((stage?.Get(module) ?? Current(module))?.Services ?? throw new InvalidOperationException(
            $"Module {module} holds no service scope in this mesh."))
        .Resolve(Root, index, caller);

    /// <summary>
    /// 🚨 <b>Built before published</b> (MeshWeaver#6542): builds every shared instance of
    /// <paramref name="generation"/>'s container (<see cref="ModuleServices.BuildBeforePublish"/>) while
    /// nothing can reach it yet. Returns why it could not be built — the generation must then not be
    /// published — or null.
    /// </summary>
    internal string? BuildBeforePublish(ModuleGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        if (generation.Services is not { } services)
            return null;
        try
        {
            services.BuildBeforePublish(Root);
            return null;
        }
        catch (Exception exception)
        {
            var cause = exception;
            // A container may wrap a constructor's exception once per level of the resolve;
            // the sentence an operator acts on is the innermost one, the outer ones name the path.
            while (cause.InnerException is not null)
                cause = cause.InnerException;
            return $"{cause.GetType().Name}: {cause.Message} (while building {exception.Message})";
        }
    }

    private ImmutableList<IncompatibleModule> notBuilt = [];
    private int publicationClaimed;

    /// <summary>The modules whose shared instances could not be built at boot, so were never published
    /// — served as <see cref="IncompatibleModule"/> records by the mesh's <see cref="InstalledModules"/>
    /// registry, on every container kind (MeshWeaver#6557).</summary>
    internal ImmutableList<IncompatibleModule> NotBuilt => notBuilt;

    /// <summary>Claims the ONE boot publication (<see cref="BuildAllBeforePublish"/>) for the caller;
    /// false when somebody already claimed it. Whoever claims it runs it.</summary>
    internal bool TryClaimPublication() => Interlocked.Exchange(ref publicationClaimed, 1) == 0;

    /// <summary>
    /// Runs the boot publication unless it was already claimed — what the registry's own singleton
    /// registration calls on its first resolve (MeshWeaver#6557), so every host kind withdraws a module
    /// whose shared instances cannot be built. The root container claims it earlier, outside any lock
    /// (<see cref="MeshWeaver.ServiceProvider.IRootContainerBuilt"/>); a host built with plain MS DI
    /// <c>BuildServiceProvider()</c> reaches it here, at the first read of the registry. Claimed BEFORE it
    /// runs, so a resolve of the registry from inside a constructor it builds does not run it again.
    /// </summary>
    internal ModuleContexts PublishOnce()
    {
        if (TryClaimPublication())
            BuildAllBeforePublish();
        return this;
    }

    /// <summary>
    /// 🚨 <b>Built before published, at boot</b> (MeshWeaver#6542): called once the root container is
    /// built and before the provider is handed out (<see cref="MeshWeaver.ServiceProvider.IRootContainerBuilt"/>).
    /// Builds every held generation's shared instances, a module after the modules it depends on. A
    /// module whose instances cannot be built is withdrawn — never left half-published — together with
    /// every module that depends on it, and each is reported as an <see cref="IncompatibleModule"/>
    /// (stderr, the log, and the records <c>/health</c> and the module-status surfaces read).
    /// </summary>
    internal void BuildAllBeforePublish()
    {
        var ordered = new List<ModuleGeneration>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var generation in current.Values.OrderBy(g => g.Name, StringComparer.Ordinal))
            Visit(generation);
        var withdrawn = new HashSet<string>(StringComparer.Ordinal);
        foreach (var generation in ordered)
        {
            if (withdrawn.Contains(generation.Name) || BuildBeforePublish(generation) is not { } failed)
                continue;
            Withdraw(generation, failed);
            foreach (var dependent in DependentsOf(generation.Name).Where(d => !withdrawn.Contains(d.Name)).ToArray())
                Withdraw(dependent, $"it depends on {generation.Name}, whose shared instances could not be built: {failed}");
        }
        return;

        void Visit(ModuleGeneration generation)
        {
            if (!visited.Add(generation.Name))
                return;
            foreach (var dependency in generation.Context.DependsOn.OrderBy(d => d, StringComparer.Ordinal))
                if (Current(dependency) is { } held)
                    Visit(held);
            ordered.Add(generation);
        }

        void Withdraw(ModuleGeneration generation, string why)
        {
            withdrawn.Add(generation.Name);
            Uncommit(generation);
            // Its singletons go with it; its context stays loaded — a dependent may already hold
            // types from it, and unloading under it is the use-after-unload the leases exist for.
            generation.Services?.Dispose();
            var record = new IncompatibleModule(generation.Location, generation.Name,
                $"its shared instances could not be built before it was published, so it was withdrawn: {why}", null);
            ImmutableInterlocked.Update(ref notBuilt, list => list.Add(record));
            Console.Error.WriteLine($"[MeshWeaver.Mesh.IncompatibleModule] {record.Report()}");
            logger?.LogError("[MeshWeaver.Mesh.IncompatibleModule] {Report}", record.Report());
        }
    }

    internal Task StartAllModuleHosted(ModuleGeneration generation, CancellationToken ct, ILogger? log) =>
        generation.Services is { } services
            ? services.StartAllHosted(Root, ct, log ?? logger)
            : Task.CompletedTask;

    /// <summary>
    /// Prepares the root services of <paramref name="to"/> — the generation replacing
    /// <paramref name="from"/> — for a live swap: its delegates run against the SAME root prefix the
    /// boot generation saw, and the result must route the same way and keep the same shape, because
    /// the root's forwarders were laid out at boot and are what every consumer holds. Returns why it
    /// cannot be swapped live, or null when <paramref name="to"/> now carries its services.
    /// </summary>
    public string? PrepareServices(ModuleGeneration from, ModuleGeneration to, ModuleContributions contributions)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(contributions);
        var configurations = contributions.ServiceConfigurations;
        if (from.Contributions is { } running && !running.BuilderHooks.Shape.SequenceEqual(contributions.BuilderHooks.Shape))
            return "the new generation's builder hook changed what was applied once at boot (autocomplete exclusions) — "
                   + $"was [{string.Join(", ", running.BuilderHooks.Shape)}], is [{string.Join(", ", contributions.BuilderHooks.Shape)}]";
        if (!from.RootServiceBlockers.IsEmpty)
            return "the running generation's root services went straight into the root container at boot: "
                   + string.Join("; ", from.RootServiceBlockers);
        if (from.Services is not { } old)
            return configurations.Count == 0
                ? null
                : "the new generation registers root services and the running one did not — the root's forwarders are laid out at boot";
        ModuleServices fresh;
        try
        {
            fresh = ModuleServices.Probe(to.Name, to.Context, configurations, old.Prefix);
        }
        catch (Exception exception)
        {
            return $"the new generation's root services could not be built: {exception.GetType().Name}: {exception.Message}";
        }
        if (!fresh.Blockers.IsEmpty)
            return "the new generation's root services cannot be served from its own scope: " + string.Join("; ", fresh.Blockers);
        // Compared against the forwarders the root ACTUALLY holds — laid out at boot and inherited by
        // every generation since — never merely against the generation being replaced.
        if (!fresh.Shape.SequenceEqual(old.ForwarderLayout))
            return "the new generation's root services changed shape (the platform services it forwards to the root differ) — "
                   + $"was [{string.Join(", ", old.ForwarderLayout)}], is [{string.Join(", ", fresh.Shape)}]";
        // Its container is built before the swap commits (BuildBeforePublish), so it binds the other
        // generations of the same swap, not the ones it is replacing.
        fresh.Stage = to.Context.Stage;
        // The root's forwarders are the BOOT generation's, whatever generation serves them now
        // (UnresolvableForwarders, the module_forwarders readiness check — MeshWeaver#6847).
        fresh.InheritForwarderLayout(old);
        to.Services = fresh;
        return null;
    }

    /// <summary>
    /// Re-applies <paramref name="generation"/>'s MESH-level contributions to the RUNNING mesh: its mesh-hub
    /// configuration (attributes, address types, builder hook — each measured at load to mutate only the
    /// configuration it is handed, i.e. its type registry) to <paramref name="meshHub"/>'s live
    /// configuration, and its mesh types to the mesh's shared type registry. The retired generation's
    /// entries are demoted by the registries themselves when its context unloads.
    /// </summary>
    public void ApplyToRunningMesh(ModuleGeneration generation, MeshWeaver.Messaging.IMessageHub meshHub)
    {
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(meshHub);
        if (generation.Contributions is not { } contributions)
            return;
        foreach (var configure in contributions.AllMeshHubConfigurations)
            configure(meshHub.Configuration);
        if (contributions.BuilderHooks.MeshTypes.IsEmpty)
            return;
        var registry = Root.GetRequiredService<MeshWeaver.Domain.ITypeRegistry>();
        foreach (var (type, name) in contributions.BuilderHooks.MeshTypes)
            registry.WithType(type, name);
    }

    /// <summary>
    /// Moves the hosted services of a swapped module from <paramref name="from"/> to
    /// <paramref name="to"/>: stops what the old generation started, then starts the same registrations
    /// — every hosted registration the new generation has, which may differ from the old one's. Task-shaped
    /// because <c>IHostedService</c> is.
    /// </summary>
    public Task HandOverHosted(ModuleGeneration from, ModuleGeneration to, CancellationToken ct)
    {
        var stopping = from.Services?.StopHosted(ct, logger) ?? Task.CompletedTask;
        return stopping.ContinueWith(
                _ => to.Services is { } fresh
                    ? fresh.StartAllHosted(Root, ct, logger)
                    : Task.CompletedTask,
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)
            .Unwrap();
    }

    /// <summary>
    /// Loads the module whose entry DLL is <paramref name="entryLocation"/> into a FRESH collectible
    /// context. Not yet current — the caller materialises its contributions and then calls
    /// <see cref="Commit"/>, or <see cref="Discard"/> when they fail. Throws what the load throws.
    /// </summary>
    public ModuleGeneration Load(string entryLocation) => LoadInto(entryLocation, null);

    /// <summary>
    /// Loads like <see cref="Load(string)"/>, but INSIDE <paramref name="stage"/>. A distinct name,
    /// never an overload of <c>Load</c>: an added overload turns every existing
    /// <c>&lt;see cref="Load"/&gt;</c> into CS0419 under <c>-warnaserror</c>, here and in dependents.
    /// The new context
    /// binds every module the stage holds to the STAGED generation rather than the current one, so a
    /// swap can load and materialise a module's dependents against the module's new generation
    /// before ANY of them is made current (#6128 review: committing first published N+1, its
    /// endpoints included, while the swap could still fail).
    /// </summary>
    public ModuleGeneration LoadStaged(string entryLocation, ModuleSwapStage stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        return LoadInto(entryLocation, stage);
    }

    private ModuleGeneration LoadInto(string entryLocation, ModuleSwapStage? stage)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var fullPath = Path.GetFullPath(entryLocation);
        var name = Path.GetFileNameWithoutExtension(fullPath);
        var directory = Path.GetDirectoryName(fullPath) ?? AppContext.BaseDirectory;
        var context = new ModuleLoadContext(this, name, directory, Interlocked.Increment(ref sequence), stage);
        try
        {
            var assembly = context.LoadFromAssemblyPath(fullPath);
            return new ModuleGeneration(assembly.GetName().Name ?? name, fullPath, context, assembly);
        }
        catch
        {
            // Nothing ran in it and nothing holds it: unload at once so a refused generation leaves
            // no context behind.
            context.Unload();
            throw;
        }
    }

    /// <summary>Records <paramref name="contributions"/> as what <paramref name="generation"/>
    /// contributes, and each of its nodes as owned by its module.</summary>
    public void SetContributions(ModuleGeneration generation, ModuleContributions contributions)
    {
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(contributions);
        generation.Contributions = contributions;
        foreach (var node in contributions.AllNodes)
            nodeOwners.AddOrUpdate(node, generation.Name);
    }

    /// <summary>The module that contributed <paramref name="node"/> (by reference), or null.</summary>
    public string? OwnerOf(MeshNode node) =>
        nodeOwners.TryGetValue(node, out var owner) ? owner : null;

    /// <summary>
    /// The nodes the CURRENT generation of <paramref name="moduleName"/> contributes — empty when
    /// the module is not held or its contributions are not materialised.
    /// </summary>
    public IReadOnlyCollection<MeshNode> CurrentNodes(string moduleName) =>
        Current(moduleName)?.Contributions?.AllNodes ?? [];

    /// <summary>
    /// Makes <paramref name="generation"/> the current one for its module and returns the one it
    /// replaces (null on first install). The replaced generation keeps running for whoever still
    /// leases it; the caller retires it once the hubs bound to it are gone.
    /// </summary>
    public ModuleGeneration? Commit(ModuleGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        ModuleGeneration? replaced = null;
        current.AddOrUpdate(generation.Name, generation, (_, previous) =>
        {
            replaced = previous;
            return generation;
        });
        Bump();
        return ReferenceEquals(replaced, generation) ? null : replaced;
    }

    /// <summary>
    /// Makes every generation <paramref name="stage"/> holds current, in the order they were staged,
    /// only AFTER all of them loaded, materialised and prepared, with ONE <see cref="VersionChanged"/>
    /// emission, so no reader of the generations (the module endpoint table, the view registrations,
    /// the static-node catalog) ever observes part of a swap, or a swap that can still fail. Seals
    /// the stage, and RELEASES it only once every one of its generations is current: from then on its
    /// contexts resolve modules through the current generations. While the generations are made
    /// current one at a time, the stage still answers them — so a staged container that is already
    /// reachable through the first one never falls back to a retiring generation of a module later in
    /// the same swap (all-or-none binding, MeshWeaver#6542).
    /// </summary>
    public void CommitAll(ModuleSwapStage stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        var generations = stage.Seal();
        foreach (var generation in generations)
            current[generation.Name] = generation;
        // Evict BEFORE announcing: VersionChanged's subscribers run synchronously inside Bump, and one
        // that resolves a configured client there must already be served N+1's options, not N's cache.
        EvictRootOptions(generations);
        Bump();
        stage.Release();
    }

    /// <summary>
    /// 🚨 Evicts, from the ROOT's options cache, every options type the swapped generations configure
    /// through a root forwarder. <see cref="IOptionsMonitor{TOptions}"/> builds a named instance ONCE and
    /// caches it — so without this, after a live swap the root kept serving the options generation N's
    /// delegates built (a module's typed HttpClient kept N's headers and base address), and the cached
    /// instance kept N's delegates, and with them N's collectible load context, reachable. Cleared AFTER
    /// the generations are current, so the next read is built from the new one's — and BEFORE
    /// <see cref="VersionChanged"/> fires, so a subscriber that resolves during the notification already
    /// sees N+1.
    ///
    /// <para>Not reached by this: <see cref="IOptions{TOptions}"/>.Value, which a singleton caches for the
    /// life of the container, and a consumer that copied an options instance into its own state.</para>
    /// </summary>
    private void EvictRootOptions(IEnumerable<ModuleGeneration> generations)
    {
        var provider = root;
        if (provider is null)
            return; // nothing was ever resolved through a container that does not exist yet
        var options = generations
            .SelectMany(g => g.Services?.Registrations ?? [])
            .Where(r => r.Route == ModuleServiceRoute.Proxy && !r.Descriptor.IsKeyedService)
            .Select(r => ModuleServiceForwarding.OptionsPipelineType(r.Descriptor.ServiceType))
            .OfType<Type>()
            .Distinct();
        foreach (var type in options)
        {
            var cacheType = typeof(IOptionsMonitorCache<>).MakeGenericType(type);
            cacheType.GetMethod(nameof(IOptionsMonitorCache<object>.Clear))!
                .Invoke(provider.GetRequiredService(cacheType), null);
        }
    }

    /// <summary>
    /// Unloads every generation <paramref name="stage"/> holds. None of them was ever current, so
    /// nothing was routed to them and <see cref="Discard"/>'s "loaded but never committed" holds.
    /// </summary>
    public void DiscardStaged(ModuleSwapStage stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        var generations = stage.Seal();
        stage.Release();
        foreach (var generation in generations)
            Discard(generation);
    }

    /// <summary>
    /// Withdraws <paramref name="generation"/> as its module's current generation when it still is
    /// one — the undo of a <see cref="Commit"/> whose contributions then failed and that replaced
    /// nothing. A no-op when another generation is current.
    /// </summary>
    public void Uncommit(ModuleGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        if (current.TryRemove(new KeyValuePair<string, ModuleGeneration>(generation.Name, generation)))
            Bump();
    }

    /// <summary>Unloads a generation that was loaded but never committed (its contributions failed).</summary>
    public void Discard(ModuleGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        if (current.TryGetValue(generation.Name, out var live) && ReferenceEquals(live, generation))
            throw new InvalidOperationException(
                $"'{generation.Context.Name}' is the current generation of {generation.Name}; retire it instead.");
        generation.Services?.Dispose();
        RetireCore(generation.Context);
        generation.Context.Unload();
    }

    /// <summary>
    /// Retires <paramref name="generation"/>: removes it from <see cref="Generations"/> if it is
    /// still current, then unloads it once nothing holds a lease on it — and NOT AT ALL if that does
    /// not happen within <paramref name="budget"/> (a leak, deliberately chosen over a crash). Cold:
    /// the caller subscribes. Emits whether the unload was started.
    /// </summary>
    public IObservable<bool> Retire(ModuleGeneration generation, TimeSpan budget) =>
        Observable.Defer(() =>
        {
            ArgumentNullException.ThrowIfNull(generation);
            current.TryRemove(new KeyValuePair<string, ModuleGeneration>(generation.Name, generation));
            // Its singletons go first — they are this generation's objects and must not outlive it. A
            // caller that leased the generation (TryLeaseModuleService) finishes on live services: the
            // disposal then runs when the last lease is released (MeshWeaver#6715).
            generation.Services?.Dispose();
            var retirement = RetireCore(generation.Context);
            return Leases.UnloadWhenQuiesced(generation.Context, budget, logger, generation.Context.Name)
                .Do(unloaded =>
                {
                    if (!unloaded)
                        retirement?.Faulted(new InvalidOperationException(
                            $"Module generation '{generation.Context.Name}' ({generation.Location}) was KEPT loaded: "
                            + $"it still had {Leases.InFlight(generation.Context)} in-flight lease(s) after {budget}, held by {string.Join(", ", Leases.Holders(generation.Context))}."));
                });
        });

    /// <summary>
    /// The current entry assembly of the module named <paramref name="simpleName"/>, or null — the
    /// explicit resolution a NodeType build or a script session asks for a module it references,
    /// since no default-context binding may ever hand one out (see <see cref="ModuleLoadContext"/>).
    /// </summary>
    public Assembly? Resolve(string simpleName) =>
        !string.IsNullOrEmpty(simpleName) && current.TryGetValue(simpleName, out var generation)
            ? generation.Assembly
            : null;

    /// <summary>
    /// <paramref name="assemblyName"/> as a context that is NOT a module asks for it at run time —
    /// a kernel script session: a module's entry assembly, or one of a module's PRIVATE
    /// dependencies, bound through that module's own context and returned with a LEASE on it. Null
    /// when no candidate module holds or ships it, and null when the module context answers it from
    /// the default context (the platform), so the caller falls through to the default context for
    /// exactly the same answer.
    ///
    /// <para><b>Why the module's dependencies too.</b> A module ships its private closure beside its
    /// entry DLL (Microsoft.Graph and Kiota beside <c>MeshWeaver.Mail.MicrosoftGraph</c>), and the
    /// image does not. A script compiles against that closure — the shared metadata resolver finds
    /// the file next to the module DLL that references it — so binding only the entry assembly made
    /// the script compile and then throw <see cref="FileNotFoundException"/> the moment it touched a
    /// type from the closure: the default context cannot see the module's directory. Loading the
    /// dependency through the module's context also gives the script the SAME identity the module's
    /// own code binds, so a value crosses between them with its type intact.</para>
    ///
    /// <para><b>Which generations.</b> <paramref name="among"/> — the generations the caller compiled
    /// against, so compile and run see the same module code — or, when null, the current ones. A
    /// candidate is used only while it is still CURRENT, and it is leased before anything is loaded
    /// from it and re-checked after: a generation a swap has already displaced may be unloading, and
    /// loading from it is the use-after-unload this registry's leases exist to prevent. A retirement
    /// that starts after the lease waits for it.</para>
    ///
    /// <para><b>Resolution order.</b> (1) a candidate's entry assembly; (2) an assembly a
    /// candidate's context already holds; (3) a file of that name in a candidate's generation
    /// directory, loaded through that candidate's context — whose own order puts the platform first.
    /// Candidates are visited in name order, and one whose version is lower than the one requested
    /// is skipped.</para>
    /// </summary>
    public ModuleBinding? ResolveDependency(AssemblyName assemblyName, IReadOnlyCollection<ModuleGeneration>? among = null)
    {
        ArgumentNullException.ThrowIfNull(assemblyName);
        if (assemblyName.Name is not { Length: > 0 } name)
            return null;
        var candidates = (among ?? current.Values.ToArray())
            .OrderBy(g => g.Name, StringComparer.Ordinal)
            .ToArray();
        // The binding's lease is held by whoever asked — a kernel script session holds it until it ends.
        var holder = $"a script session's binding of {name}";

        foreach (var generation in candidates)
            if (string.Equals(generation.Name, name, StringComparison.Ordinal)
                && Satisfies(generation.Assembly.GetName(), assemblyName)
                && LeaseIfCurrent(generation, holder) is { } lease)
                return new ModuleBinding(generation.Assembly, generation.Context, lease);

        foreach (var generation in candidates)
        {
            if (LeaseIfCurrent(generation, holder) is not { } lease)
                continue;
            foreach (var assembly in generation.Context.Assemblies)
                if (assembly.GetName() is { } held
                    && string.Equals(held.Name, name, StringComparison.Ordinal)
                    && Satisfies(held, assemblyName))
                    return new ModuleBinding(assembly, generation.Context, lease);
            lease.Dispose();
        }

        foreach (var generation in candidates)
        {
            var candidate = Path.Combine(generation.Context.Directory, name + ".dll");
            if (!File.Exists(candidate) || !Satisfies(NameOf(candidate), assemblyName)
                || LeaseIfCurrent(generation, holder) is not { } lease)
                continue;
            Assembly loaded;
            try
            {
                loaded = generation.Context.LoadFromAssemblyName(assemblyName);
            }
            catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                lease.Dispose();
                continue;
            }
            if (AssemblyLoadContext.GetLoadContext(loaded) is ModuleLoadContext owner)
            {
                if (ReferenceEquals(owner, generation.Context))
                    return new ModuleBinding(loaded, owner, lease);
                // Served from a module this one depends on: lease THAT generation instead, on the
                // same terms — current, or not at all.
                var ownerLease = Current(owner.ModuleName) is { } ownerGeneration
                                 && ReferenceEquals(ownerGeneration.Context, owner)
                    ? LeaseIfCurrent(ownerGeneration, holder)
                    : null;
                lease.Dispose();
                return ownerLease is null ? null : new ModuleBinding(loaded, owner, ownerLease);
            }
            // The platform's (or the image's) answer is the default context's to give.
            lease.Dispose();
            return null;
        }
        return null;
    }

    // Leases `generation` and keeps the lease only if it is STILL current after the lease is taken:
    // Retire removes a generation from `current` BEFORE it waits for quiescence, so a lease that
    // sees it current is one the retirement will wait for.
    private IDisposable? LeaseIfCurrent(ModuleGeneration generation, string holder)
    {
        var lease = Leases.Enter(generation.Context, holder);
        if (current.TryGetValue(generation.Name, out var live) && ReferenceEquals(live, generation))
            return lease;
        lease.Dispose();
        return null;
    }

    private static AssemblyName? NameOf(string path)
    {
        try
        {
            return AssemblyName.GetAssemblyName(path);
        }
        catch (Exception e) when (e is BadImageFormatException or FileLoadException or FileNotFoundException)
        {
            return null;
        }
    }

    // A context may not answer a request with a LOWER version than the one asked for.
    private static bool Satisfies(AssemblyName? candidate, AssemblyName requested) =>
        candidate is not null
        && (requested.Version is null || (candidate.Version is { } version && version >= requested.Version));

    /// <summary>
    /// The current generations that depend — directly or transitively — on
    /// <paramref name="moduleName"/>: what has to be swapped with it, because each one is bound to
    /// that module's types.
    /// </summary>
    public IReadOnlyList<ModuleGeneration> DependentsOf(string moduleName)
    {
        var result = new List<ModuleGeneration>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { moduleName };
        var frontier = new Queue<string>([moduleName]);
        var snapshot = current.Values.ToArray();
        while (frontier.TryDequeue(out var name))
            foreach (var generation in snapshot)
                if (generation.Context.DependsOn.Contains(name) && seen.Add(generation.Name))
                {
                    result.Add(generation);
                    frontier.Enqueue(generation.Name);
                }
        return result;
    }

    /// <summary>
    /// Step 2 of <see cref="ModuleLoadContext"/>'s resolution: <paramref name="name"/> as another
    /// module's entry assembly, or as an assembly already loaded by a module
    /// <paramref name="requester"/> depends on.
    /// </summary>
    internal Assembly? ResolveForDependent(string name, ModuleLoadContext requester)
    {
        // A staged context binds the stage's generation first: the swap it belongs to makes them
        // current together, so this is the binding it will have once the swap commits.
        ModuleGeneration? Held(string module) =>
            requester.Stage?.Get(module) ?? (current.TryGetValue(module, out var generation) ? generation : null);

        if (!string.Equals(name, requester.ModuleName, StringComparison.Ordinal)
            && Held(name) is { } module)
        {
            requester.RecordDependency(name);
            return module.Assembly;
        }

        foreach (var dependency in requester.DependsOn)
        {
            if (Held(dependency) is not { } held)
                continue;
            foreach (var assembly in held.Context.Assemblies)
                if (string.Equals(assembly.GetName().Name, name, StringComparison.Ordinal))
                    return assembly;
        }
        return null;
    }

    private CollectibleContextUnloads.Retirement? RetireCore(ModuleLoadContext context)
    {
        if (unloads is null)
            return null;
        var retirement = unloads.Retire(context.Name ?? ModuleLoadContext.NamePrefix);
        // 🚨 Held in a FIELD of the context — never in an Unloading closure. Unload() swaps the
        // Unloading delegate out to null as it raises it, so a sentinel captured there is unreachable
        // the moment the unload STARTS and reports "collected" while the context is still alive (the
        // negative control in ModulesRunInTheirOwnContextTest caught exactly that). Referenced only by
        // the context, it dies in the same collection as the context — after the runtime has destroyed
        // its LoaderAllocator — and its finalizer reports the retirement collected (the shape
        // NodeAssemblyLoadContext uses, Plugins#1605).
        context.HoldRetirementSentinel(new RetirementSentinel(retirement));
        return retirement;
    }

    private sealed class RetirementSentinel(CollectibleContextUnloads.Retirement retirement)
    {
        ~RetirementSentinel() => retirement.Collected();
    }

    /// <summary>
    /// Retires every generation still held — the mesh is going down, so no hub can enter a module
    /// any more. A generation somebody still leases is left loaded rather than unloaded under them,
    /// and says so.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        foreach (var generation in current.Values.ToArray())
        {
            current.TryRemove(generation.Name, out _);
            generation.Services?.Dispose();
            var retirement = RetireCore(generation.Context);
            var inFlight = Leases.InFlight(generation.Context);
            if (inFlight > 0)
            {
                var holders = string.Join(", ", Leases.Holders(generation.Context));
                logger?.LogError(
                    "NOT unloading module generation {Context} at mesh teardown: {InFlight} lease(s) still held, by {Holders}.",
                    generation.Context.Name, inFlight, holders);
                retirement?.Faulted(new InvalidOperationException(
                    $"Module generation '{generation.Context.Name}' was kept loaded at teardown: {inFlight} lease(s) held, by {holders}."));
                continue;
            }
            generation.Context.Unload();
        }
        // The registry's half of MeshWeaver#7373: a subject holds every observer until it unsubscribes,
        // so completing it at teardown releases a subscriber that never ended its own subscription —
        // nothing a cache of the modules' contributions left behind outlives the registry's mesh.
        versionChanged.OnCompleted();
    }
}

/// <summary>
/// The generations of ONE live swap, loaded and materialised but not yet current. A context loaded
/// into the stage (<see cref="ModuleContexts.LoadStaged"/>) binds these in place
/// of the current generations, so a module's dependents re-bind to its new generation before anything
/// is published. Made current all at once by <see cref="ModuleContexts.CommitAll"/>, or unloaded by
/// <see cref="ModuleContexts.DiscardStaged"/>; either seals it. Used by one swap at a time (the swap
/// pipeline is serial).
/// </summary>
public sealed class ModuleSwapStage
{
    private ImmutableList<ModuleGeneration> staged = ImmutableList<ModuleGeneration>.Empty;
    private int sealedFlag;
    private int releasedFlag;

    /// <summary>Adds <paramref name="generation"/> to the stage; later contexts in the stage bind it.</summary>
    public void Add(ModuleGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        if (Volatile.Read(ref sealedFlag) != 0)
            throw new InvalidOperationException("The swap stage is sealed: it was committed or discarded.");
        ImmutableInterlocked.Update(ref staged, list => list.Add(generation));
    }

    /// <summary>The staged generation of <paramref name="moduleName"/>, or null; always null once the
    /// commit or discard that sealed the stage has released it. Between the seal and the release — while
    /// <see cref="ModuleContexts.CommitAll"/> makes the generations current one at a time — it still
    /// answers them, so a staged container never sees half of its swap.</summary>
    public ModuleGeneration? Get(string moduleName) =>
        Volatile.Read(ref releasedFlag) != 0
            ? null
            : Volatile.Read(ref staged).LastOrDefault(g => string.Equals(g.Name, moduleName, StringComparison.Ordinal));

    /// <summary>Refuses any further <see cref="Add"/>, commit or discard, and hands over the generations;
    /// the stage keeps answering them until <see cref="Release"/>.</summary>
    internal ImmutableList<ModuleGeneration> Seal()
    {
        if (Interlocked.Exchange(ref sealedFlag, 1) != 0)
            throw new InvalidOperationException("The swap stage was already committed or discarded.");
        return Volatile.Read(ref staged);
    }

    /// <summary>Stops answering, once the generations were made current or discarded.</summary>
    internal void Release()
    {
        Volatile.Write(ref releasedFlag, 1);
        // FORGOTTEN: a context, and a staged container's fallback, keep a reference to their stage for
        // life, so a stage that kept its list would pin every generation it ever held — one of them
        // retired by a later swap of that module alone.
        Interlocked.Exchange(ref staged, ImmutableList<ModuleGeneration>.Empty);
    }
}

/// <summary>
/// Why a lease of a module's service answered null (<see cref="ModuleContexts.TryLeaseModuleService(string, int, MeshWeaver.ServiceProvider.MeshServiceProvider?, Type?, out ModuleLeaseFault)"/>).
/// Internal and closed: never persisted or serialised, and decided and read only in this assembly.
/// </summary>
internal enum ModuleLeaseFault
{
    /// <summary>No fault: the module is not held, or is retiring — its feature is absent.</summary>
    None,

    /// <summary>The module is held and could not produce the registration THIS time; the next lease may.</summary>
    Temporary,

    /// <summary>The module is held and its current generation cannot produce the registration at all.</summary>
    Unservable,
}
