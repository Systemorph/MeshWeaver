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
        if (service is not TypedService typed)
            return [];
        var resolvers = ModuleServices.RegistrationsElsewhere(contexts, null, typed.ServiceType);
        return resolvers.Count == 0
            ? []
            : ModuleServiceProvider.StandIns(typed.ServiceType, resolvers, registrationAccessor(service).Any());
    }
}
