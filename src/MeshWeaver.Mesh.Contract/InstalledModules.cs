using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Mesh;

/// <summary>
/// 🚨 <b>The ONE answer to "which modules does this mesh run"</b> (MeshWeaver#6557) — what
/// <c>GetServices&lt;InstalledModuleAssembly&gt;()</c>, an injected
/// <c>IEnumerable&lt;InstalledModuleAssembly&gt;</c> and <see cref="Current"/> all read, on every
/// container kind.
///
/// <para><b>One ordered sequence of slots</b>, in the order they were declared in the mesh's service
/// collection — order is load-bearing (first-mount-wins static assets, the list-ordered compile
/// reference surface). A slot is (1) a FIXED member — an image-bound module
/// <see cref="MeshBuilder.InstallAssemblies"/> records, or whatever else registers one (a module's
/// builder hook shipping extra reference assemblies, a test); or (2) a member that can GO
/// (<see cref="InstalledModuleSlot"/>) — a module held in its own collectible context, answered by
/// NAME from the module registry (<see cref="ModuleContexts"/>) at each read so it follows a live swap
/// (policy <c>module-live-update-default</c>), or an <see cref="InstalledModuleAssembly"/> such a module
/// registers through its own services. A member whose module the registry no longer holds — withdrawn
/// at boot because its shared instances could not be built (<see cref="ModuleContexts"/>' "built
/// before published"), or retired — is simply ABSENT; its withdrawal is reported where every module
/// that failed to install is (<see cref="IncompatibleModule"/>, <c>/health</c>, the module-status
/// surfaces).</para>
///
/// <para><b>Why not one transient registration per held module</b> (what this replaced). A DI
/// registration is fixed once the container is built; the module set is not. A held module withdrawn
/// AFTER the container was built left a registration that could only answer by throwing, and every
/// enumeration — the boot pre-warm sweep's included — faulted on it, so one broken module refused
/// readiness for the whole pod. An Autofac registration source would have been container-specific: a
/// host that builds with <c>BuildServiceProvider()</c> ignores sources. This registry is ordinary
/// state owned by the mesh, and the enumerable registration that serves it is an ordinary factory, so
/// both container kinds answer the same set.</para>
///
/// <para>Mesh-scoped instance state (NoStaticState): created by the <see cref="MeshBuilder"/> and
/// registered as that mesh's singleton.</para>
/// </summary>
public sealed class InstalledModules
{
    private readonly ModuleContexts contexts;
    private ImmutableList<string> held = [];
    private IServiceCollection? declarations;
    private ServiceDescriptor[]? declared;
    private IServiceProvider? root;
    // Instance state of the mesh-scoped registry (NoStaticState): a declared singleton's one instance,
    // and each scope's scoped instances — weakly keyed by the scope's provider, so a scope that ends
    // takes its instances with it.
    private readonly ConditionalWeakTable<ServiceDescriptor, Lazy<object>> singletons = new();
    private readonly ConditionalWeakTable<IServiceProvider, ConcurrentDictionary<ServiceDescriptor, Lazy<object>>> scoped = new();

    internal InstalledModules(ModuleContexts contexts) =>
        this.contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));

    /// <summary>Records <paramref name="moduleName"/> as a module installed at boot in its own load
    /// context, answered from the registry's CURRENT generation at each read, and returns the
    /// declaration that holds its SLOT — added to the service collection where the module was
    /// installed, so the set keeps install order across held and image-bound modules.</summary>
    internal ServiceDescriptor AddHeld(string moduleName)
    {
        if (!string.IsNullOrEmpty(moduleName))
            ImmutableInterlocked.Update(ref held, list => list.Contains(moduleName) ? list : list.Add(moduleName));
        var registry = contexts;
        return InstalledModuleSlot.Declare(_ => registry.Resolve(moduleName) is { } assembly
            ? new InstalledModuleAssembly(assembly)
            : null, $"Module {moduleName} is no longer held by this mesh's module registry.");
    }

    /// <summary>The service collection the mesh's container is built from — read once, at the first
    /// read after the container exists, for the declared members.</summary>
    internal void Declare(IServiceCollection services) =>
        Interlocked.CompareExchange(ref declarations, services, null);

    /// <summary>The names of the modules installed at boot in their own load contexts, whether or not
    /// the registry still holds them.</summary>
    public IReadOnlyList<string> HeldModuleNames => held;

    /// <summary>
    /// The installed modules NOW, in the order they were declared — an image-bound module, a held
    /// module (as its CURRENT generation) and one a module contributes, each in its own slot. Never
    /// throws for a module that is gone — it is absent.
    /// </summary>
    /// <param name="services">The provider a declared factory or type is activated against.</param>
    public IReadOnlyList<InstalledModuleAssembly> Current(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        AttachRoot(services);
        return Declared()
            .Select(descriptor => descriptor.ImplementationFactory?.Target is InstalledModuleSlot slot
                ? slot.Current(services)
                : (InstalledModuleAssembly)Activate(descriptor, services))
            .OfType<InstalledModuleAssembly>()
            .ToImmutableList();
    }

    // The registry's singleton registration is built in the mesh's ROOT provider, so resolving it hands
    // over that root (Attach) — what a declared SINGLETON is activated against, as DI would.
    private static void AttachRoot(IServiceProvider services) =>
        services.GetService(typeof(ModuleContexts));

    /// <summary>The mesh's root provider — set by the registry's own singleton registration.</summary>
    internal void Attach(IServiceProvider rootProvider) =>
        Interlocked.CompareExchange(ref root, rootProvider, null);

    /// <summary>
    /// A fixed declaration's member, with the LIFETIME its declaration asked for — the
    /// <c>IEnumerable&lt;T&gt;</c> registration that serves this set replaces the per-descriptor
    /// collection DI would have built, so this keeps what DI kept: a provided instance is itself, a
    /// singleton is built once for the mesh (against the root provider), a scoped one once per scope, a
    /// transient one at every read. Both member types are sealed, non-disposable records, so there is no
    /// disposal for DI to have tracked.
    /// </summary>
    private object Activate(ServiceDescriptor descriptor, IServiceProvider services)
    {
        if (descriptor.ImplementationInstance is { } instance)
            return instance;
        return descriptor.Lifetime switch
        {
            ServiceLifetime.Singleton => singletons
                .GetValue(descriptor, d => new Lazy<object>(() => Create(d, Volatile.Read(ref root) ?? services)))
                .Value,
            ServiceLifetime.Scoped => scoped
                .GetValue(services, _ => new ConcurrentDictionary<ServiceDescriptor, Lazy<object>>(ReferenceEqualityComparer.Instance))
                .GetOrAdd(descriptor, d => new Lazy<object>(() => Create(d, services)))
                .Value,
            _ => Create(descriptor, services),
        };
    }

    private static object Create(ServiceDescriptor descriptor, IServiceProvider services) =>
        descriptor.ImplementationFactory is { } factory
            ? factory(services)
            : ActivatorUtilities.CreateInstance(services, descriptor.ImplementationType!);

    // Read once: the first read happens through the built container, after which the collection no
    // longer changes.
    private ServiceDescriptor[] Declared()
    {
        if (Volatile.Read(ref declared) is { } cached)
            return cached;
        var found = Volatile.Read(ref declarations)?
                        .Where(d => !d.IsKeyedService && d.ServiceType == typeof(InstalledModuleAssembly))
                        .ToArray()
                    ?? [];
        return Interlocked.CompareExchange(ref declared, found, null) ?? found;
    }
}

/// <summary>
/// One slot of the installed-module set whose member can GO — a held module (withdrawn at boot, or
/// retired) or an <see cref="InstalledModuleAssembly"/> a held module contributes through its own
/// services (MeshWeaver#6557). <see cref="InstalledModules.Current"/> recognises the slot by its
/// factory's target and asks <see cref="Current"/>, so a gone member is ABSENT from the set; the
/// factory itself — what a single <c>GetService&lt;InstalledModuleAssembly&gt;()</c> invokes — still
/// names the module it can no longer answer for.
/// </summary>
internal sealed class InstalledModuleSlot
{
    private readonly Func<IServiceProvider, InstalledModuleAssembly?> current;
    private readonly string gone;

    private InstalledModuleSlot(Func<IServiceProvider, InstalledModuleAssembly?> current, string gone)
    {
        this.current = current;
        this.gone = gone;
    }

    /// <summary>A transient declaration of <see cref="InstalledModuleAssembly"/> served by a slot.</summary>
    /// <param name="current">The member now, or null once it is gone.</param>
    /// <param name="gone">What a single resolve throws once it is gone.</param>
    internal static ServiceDescriptor Declare(Func<IServiceProvider, InstalledModuleAssembly?> current, string gone) =>
        ServiceDescriptor.Transient(typeof(InstalledModuleAssembly), new InstalledModuleSlot(current, gone).Create);

    /// <summary>The member now, or null once it is gone.</summary>
    internal InstalledModuleAssembly? Current(IServiceProvider services) => current(services);

    private object Create(IServiceProvider services) =>
        current(services) ?? throw new InvalidOperationException(gone);
}
