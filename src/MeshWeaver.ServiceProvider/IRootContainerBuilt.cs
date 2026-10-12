#nullable enable
namespace MeshWeaver.ServiceProvider;

/// <summary>
/// Work a ROOT container needs done once it is built and before anything else can resolve from it.
/// Registered as an INSTANCE (<c>services.AddSingleton&lt;IRootContainerBuilt&gt;(instance)</c>);
/// <see cref="ServiceProviderExtensions.SetupModules"/> calls every such instance on the building
/// thread, right after the root container is built and before the provider is returned — so nothing
/// the callback builds can race a resolve made by somebody else.
///
/// <para>The one user is the mesh's module registry: every shared instance of every module container
/// is built here, before the root's forwarders can reach it (MeshWeaver#6542,
/// <c>Doc/Architecture/LiveModuleUpdate</c> → "Built before published").</para>
/// </summary>
public interface IRootContainerBuilt
{
    /// <summary>Called once, with the root provider that was just built.</summary>
    /// <param name="root">The root provider.</param>
    void Built(IServiceProvider root);
}
