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
/// <para>Mesh-scoped instance state (NoStaticState): created by the <see cref="MeshBuilder"/> and
/// registered as that mesh's singleton.</para>
/// </summary>
public sealed class InstalledModules
{
    private readonly ModuleContexts contexts;
    private ImmutableList<string> held = [];
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
        var result = new List<InstalledModuleAssembly>();
        foreach (var descriptor in Declared())
            result.Add(descriptor.ImplementationInstance as InstalledModuleAssembly
                       ?? (descriptor.ImplementationFactory is { } factory
                           ? (InstalledModuleAssembly)factory(services)
                           : (InstalledModuleAssembly)ActivatorUtilities.CreateInstance(services, descriptor.ImplementationType!)));
        foreach (var name in held)
            if (contexts.Resolve(name) is { } assembly)
                result.Add(new InstalledModuleAssembly(assembly));
        return result;
    }

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
