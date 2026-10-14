using Autofac;
using Autofac.Builder;
using Autofac.Core;
using MeshWeaver.ServiceProvider;

namespace MeshWeaver.Mesh;

/// <summary>
/// The ROOT container's view of the types modules declare: a type a module held in its own context
/// declares is answered from that module's CURRENT generation — every registration of it, plus every
/// contribution another module made of it (<see cref="ModuleServices.RegistrationsElsewhere"/>) — one
/// stand-in per registration. Registered on the root by <see cref="MeshBuilder"/>; every hub (the mesh hub
/// and each per-node hub) is a scope under the root, so all of them see the same lists.
///
/// <para>🚨 <b>Why.</b> Since each module's services live in a container of their own (core #6127) the root
/// held none of them, and code that reads a module's service off the MESH hub — every AI provider's
/// chat-client factory reads MeshWeaver.AI's <c>ChatClientCredentialResolver</c> and catalog that way —
/// found nothing. Nothing about a type is cached in the root until something ASKS for it, and asking
/// already enters the type in the root's registered-services tracker whether or not anything answers, so
/// answering pins nothing that asking did not.</para>
/// </summary>
/// <param name="contexts">The mesh's module registry.</param>
public sealed class ModuleOwnedRootSource(ModuleContexts contexts) : IRegistrationSource
{
    /// <inheritdoc />
    public bool IsAdapterForIndividualComponents => false;

    /// <inheritdoc />
    public IEnumerable<IComponentRegistration> RegistrationsFor(
        Service service, Func<Service, IEnumerable<ServiceRegistration>> registrationAccessor)
    {
        if (service is KeyedService { ServiceKey: { } key } keyed)
            return KeyedStandIns(keyed.ServiceType, key, registrationAccessor(service).Any());
        if (service is not TypedService typed)
            return [];
        // A module withdrawn at boot (MeshWeaver#6542) is NOT answered here: its IncompatibleModule
        // record is served by the mesh's InstalledModules registry through an ordinary IEnumerable<T>
        // factory, which a plain MS DI host answers too — a registration source it ignores (#6557).
        // Each resolved for the CALLER — the hub, circuit or request scope the stand-in is resolved in —
        // so a module's scoped registration is that caller's instance (MeshWeaver#6584).
        var resolvers = ModuleServices.RegistrationsElsewhereForCallers(contexts, null, typed.ServiceType);
        return resolvers.Count == 0
            ? []
            : ModuleServiceProvider.StandIns(typed.ServiceType, resolvers, registrationAccessor(service).Any(),
                ModuleServiceProvider.CallerOfRootScope);
    }

    // A module's KEYED registration of a type it declares, asked for by its key — a module keeps the key
    // in its own container, so a consumer resolving by key on the mesh hub or a per-node hub gets the
    // module's (core #6128: keyed module services are forwarded under their own key, not refused).
    private IEnumerable<IComponentRegistration> KeyedStandIns(Type type, object key, bool explicitRegistrationsExist)
    {
        var resolvers = ModuleServices.KeyedRegistrationsElsewhereForCallers(contexts, null, type, key);
        var standIns = resolvers
            .Select(resolve => RegistrationBuilder
                .ForDelegate(type, (context, _) => resolve(ModuleServiceProvider.CallerOfRootScope(context.Resolve<ILifetimeScope>())))
                .Keyed(key, type)
                .ExternallyOwned()
                .InstancePerDependency()
                .CreateRegistration())
            .ToArray();
        // As ModuleServiceProvider.StandIns: the FIRST a source yields becomes the default, which must be
        // the LAST of the list unless the container registers the key itself.
        return explicitRegistrationsExist ? standIns : standIns.Reverse();
    }
}

/// <summary>
/// Builds every held module's shared instances once the root container is built, before the root
/// provider is handed to anyone (MeshWeaver#6542, <see cref="ModuleContexts.BuildAllBeforePublish"/>).
/// It CLAIMS the publication first, then resolves the registry THROUGH the root (which attaches it to
/// that root), then builds — so the build runs outside the root's shared-instance lock, never inside the
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
