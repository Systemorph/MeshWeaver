using MeshWeaver.ServiceProvider;

namespace MeshWeaver.Mesh;

/// <summary>
/// The ROOT container's view of the types modules declare: a type a module held in its own context
/// declares is answered from that module's CURRENT generation — every registration of it, plus every
/// contribution another module made of it (<see cref="ModuleServices.RegistrationsElsewhere"/>) — one
/// stand-in per registration. Registered on the root by <see cref="MeshBuilder"/> and asked last on every lookup; every hub (the
/// mesh hub and each per-node hub) is a level under the root, so all of them see the same lists.
///
/// <para>🚨 <b>Why.</b> Since each module's services live in a container of their own (core #6127) the root
/// held none of them, and code that reads a module's service off the MESH hub — every AI provider's
/// chat-client factory reads MeshWeaver.AI's <c>ChatClientCredentialResolver</c> and catalog that way —
/// found nothing. The root caches nothing a source answers (<see cref="IServiceRegistrationSource"/>), so
/// answering a module's type pins nothing of its generation in the root.</para>
/// </summary>
/// <param name="contexts">The mesh's module registry.</param>
public sealed class ModuleOwnedRootSource(ModuleContexts contexts) : IServiceRegistrationSource
{
    /// <inheritdoc />
    public IEnumerable<ServiceRegistration> RegistrationsFor(Type serviceType, object? serviceKey, bool explicitRegistrationsExist)
    {
        // A module withdrawn at boot (MeshWeaver#6542) is NOT answered here: its IncompatibleModule
        // record is served by the mesh's InstalledModules registry through an ordinary IEnumerable<T>
        // factory (#6557).
        // Each resolved for the CALLER — the hub, circuit or request scope the stand-in is resolved in —
        // so a module's scoped registration is that caller's instance (MeshWeaver#6584). A module's KEYED
        // registration of a type it declares is answered under its own key (core #6128).
        var resolvers = serviceKey is null
            ? ModuleServices.RegistrationsElsewhereForCallers(contexts, null, serviceType)
            : ModuleServices.KeyedRegistrationsElsewhereForCallers(contexts, null, serviceType, serviceKey);
        return resolvers.Count == 0
            ? []
            : ModuleServiceProvider.StandIns(serviceType, serviceKey, resolvers, ModuleServiceProvider.CallerOfRootScope);
    }
}

/// <summary>
/// Builds every held module's shared instances once the root container is built, before the root
/// provider is handed to anyone (MeshWeaver#6542, <see cref="ModuleContexts.BuildAllBeforePublish"/>).
/// It CLAIMS the publication first, then resolves the registry THROUGH the root (which attaches it to
/// that root), then builds — so the build runs outside any shared instance's construction, never inside the
/// registry's own singleton registration, which would otherwise run it on its first resolve
/// (<see cref="ModuleContexts.PublishOnce"/>, the path a plain MS DI host takes — MeshWeaver#6557).
/// </summary>
/// <param name="contexts">The mesh's module registry.</param>
internal sealed class ModulesBuiltBeforePublished(ModuleContexts contexts) : IRootContainerBuilt
{
    /// <inheritdoc />
    public void Built(IServiceProvider root)
    {
        if (!contexts.TryClaimPublication())
            return;
        if (root.GetService(typeof(ModuleContexts)) is ModuleContexts attached)
            attached.BuildAllBeforePublish();
    }
}
