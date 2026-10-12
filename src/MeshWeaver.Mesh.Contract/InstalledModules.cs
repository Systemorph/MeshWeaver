using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Mesh;

/// <summary>
/// 🚨 <b>The ONE answer to "which modules does this mesh run"</b> (MeshWeaver#6557) — what
/// <c>GetServices&lt;InstalledModuleAssembly&gt;()</c>, an injected
/// <c>IEnumerable&lt;InstalledModuleAssembly&gt;</c> and <see cref="Current"/> all read, on every
/// container kind.
///
/// <para><b>Two kinds of member.</b> (1) Every <see cref="InstalledModuleAssembly"/> DECLARED in the
/// mesh's service collection — the image-bound modules <see cref="MeshBuilder.InstallAssemblies"/>
/// records, and whatever else registers one (a module shipping extra reference assemblies, a test).
/// (2) Every module held in its own collectible context, answered by NAME from the module registry
/// (<see cref="ModuleContexts"/>) at each read, so it follows a live swap (policy
/// <c>module-live-update-default</c>). A held module the registry no longer holds — withdrawn at boot
/// because its shared instances could not be built (<see cref="ModuleContexts"/>' "built before
/// published"), or retired — is simply ABSENT; its withdrawal is reported where every module that
/// failed to install is (<see cref="IncompatibleModule"/>, <c>/health</c>, the module-status
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
/// <para><b>A third kind, and the incompatible set</b> (MeshWeaver#6557). (3) Every
/// <see cref="InstalledModuleAssembly"/> a HELD module registers itself — the further reference
/// assemblies it ships (MeshWeaver.Testing's xunit closure) — recorded at the module's install by
/// (module, registration) and answered from its current generation while the registry holds the module;
/// absent once it is withdrawn or retired, so no forwarder of a withdrawn module is ever enumerated.
/// And the <see cref="IncompatibleModule"/> records — the declared ones plus every module withdrawn at
/// boot — are answered here too (<see cref="Incompatible"/>), so a host built with plain MS DI lists a
/// withdrawal exactly as the Autofac host does. Every read first resolves the module registry, whose
/// first resolve runs the boot publication on a host that has not run it yet
/// (<see cref="ModuleContexts.PublishOnce"/>): the set is never read from a registry that has not
/// decided which modules it publishes.</para>
///
/// <para>Mesh-scoped instance state (NoStaticState): created by the <see cref="MeshBuilder"/> and
/// registered as that mesh's singleton.</para>
/// </summary>
public sealed class InstalledModules
{
    private readonly ModuleContexts contexts;
    private ImmutableList<string> held = [];
    private ImmutableList<(string Module, int Index)> moduleRegistered = [];
    private IServiceCollection? declarations;
    private ServiceDescriptor[]? declared;

    internal InstalledModules(ModuleContexts contexts) =>
        this.contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));

    /// <summary>Records <paramref name="moduleName"/> as a module installed at boot in its own load
    /// context, answered from the registry's CURRENT generation at each read.</summary>
    internal void AddHeld(string moduleName)
    {
        if (!string.IsNullOrEmpty(moduleName))
            ImmutableInterlocked.Update(ref held, list => list.Contains(moduleName) ? list : list.Add(moduleName));
    }

    /// <summary>Records registration <paramref name="index"/> of <paramref name="moduleName"/> — an
    /// <see cref="InstalledModuleAssembly"/> a held module registers itself — answered from the module's
    /// CURRENT generation while the registry holds it, and absent once it does not.</summary>
    internal void AddModuleRegistered(string moduleName, int index) =>
        ImmutableInterlocked.Update(ref moduleRegistered,
            list => list.Contains((moduleName, index)) ? list : list.Add((moduleName, index)));

    /// <summary>The service collection the mesh's container is built from — read once, at the first
    /// read after the container exists, for the declared members.</summary>
    internal void Declare(IServiceCollection services) =>
        Interlocked.CompareExchange(ref declarations, services, null);

    /// <summary>The names of the modules installed at boot in their own load contexts, whether or not
    /// the registry still holds them.</summary>
    public IReadOnlyList<string> HeldModuleNames => held;

    /// <summary>
    /// The installed modules NOW: every declared one, then every held module the registry still holds,
    /// as its current generation. Never throws for a module that is gone — it is absent.
    /// </summary>
    /// <param name="services">The provider a declared factory or type is activated against.</param>
    public IReadOnlyList<InstalledModuleAssembly> Current(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        Published(services);
        var result = new List<InstalledModuleAssembly>();
        foreach (var descriptor in Declared<InstalledModuleAssembly>())
            result.Add(Activate<InstalledModuleAssembly>(descriptor, services));
        foreach (var name in held)
            if (contexts.Resolve(name) is { } assembly)
                result.Add(new InstalledModuleAssembly(assembly));
        foreach (var (module, index) in moduleRegistered)
            if (contexts.Current(module) is { Services: not null })
                result.Add((InstalledModuleAssembly)contexts.ResolveModuleService(module, index));
        return result;
    }

    /// <summary>
    /// The modules this replica carries and does not run NOW: every <see cref="IncompatibleModule"/>
    /// declared in the service collection (a module that failed to load or install), then every module
    /// withdrawn at boot because its shared instances could not be built (MeshWeaver#6542).
    /// </summary>
    /// <param name="services">The provider a declared factory or type is activated against.</param>
    public IReadOnlyList<IncompatibleModule> Incompatible(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        Published(services);
        var result = new List<IncompatibleModule>();
        foreach (var descriptor in Declared<IncompatibleModule>())
            result.Add(Activate<IncompatibleModule>(descriptor, services));
        result.AddRange(contexts.NotBuilt);
        return result;
    }

    // The registry decides which held modules it publishes on its first resolve, on a host that has
    // not run the publication yet (ModuleContexts.PublishOnce) — so resolve it before reading.
    private static void Published(IServiceProvider services) =>
        services.GetService(typeof(ModuleContexts));

    private static T Activate<T>(ServiceDescriptor descriptor, IServiceProvider services) where T : class =>
        descriptor.ImplementationInstance as T
        ?? (descriptor.ImplementationFactory is { } factory
            ? (T)factory(services)
            : (T)ActivatorUtilities.CreateInstance(services, descriptor.ImplementationType!));

    private ServiceDescriptor[] Declared<T>() =>
        Declared().Where(d => d.ServiceType == typeof(T)).ToArray();

    // Read once: the first read happens through the built container, after which the collection no
    // longer changes.
    private ServiceDescriptor[] Declared()
    {
        if (Volatile.Read(ref declared) is { } cached)
            return cached;
        var found = Volatile.Read(ref declarations)?
                        .Where(d => !d.IsKeyedService
                                    && (d.ServiceType == typeof(InstalledModuleAssembly)
                                        || d.ServiceType == typeof(IncompatibleModule)))
                        .ToArray()
                    ?? [];
        return Interlocked.CompareExchange(ref declared, found, null) ?? found;
    }
}
