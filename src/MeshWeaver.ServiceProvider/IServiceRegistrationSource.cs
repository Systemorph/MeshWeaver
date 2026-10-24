#nullable enable
namespace MeshWeaver.ServiceProvider;

/// <summary>
/// The last step of a lookup on one <see cref="MeshServiceProvider"/> level: registrations made on
/// demand for a service the level's own descriptors do not answer by themselves — how a mesh's module
/// registry answers the types its modules declare (<c>ModuleOwnedRootSource</c>) and how a module's
/// container reaches the mesh's root (<c>ModuleFallbackSource</c>).
///
/// <para>Registered on a ROOT container as an instance
/// (<c>services.AddSingleton&lt;IServiceRegistrationSource&gt;(source)</c>); every hub level below it
/// asks its way up to it. Asked on every lookup that reaches it — what it returns is never cached by
/// the container, so a source that answers from live state (a module's CURRENT generation) is followed
/// at once, and nothing the source answered can pin a collectible type in the root.</para>
/// </summary>
public interface IServiceRegistrationSource
{
    /// <summary>
    /// The registrations to add, in enumeration order, for <paramref name="serviceType"/> under
    /// <paramref name="serviceKey"/>. They follow the level's own registrations, so a single resolve
    /// answers the level's own last registration when there is one and the source's last otherwise.
    /// </summary>
    /// <param name="serviceType">The (closed) service type asked for.</param>
    /// <param name="serviceKey">The service key, or null.</param>
    /// <param name="explicitRegistrationsExist">Whether the level registers the service itself.</param>
    IEnumerable<ServiceRegistration> RegistrationsFor(Type serviceType, object? serviceKey, bool explicitRegistrationsExist);
}

/// <summary>
/// What a level tells the observers of its activations — the diagnostics that watch which scope owns
/// what a container built (<see cref="ModuleScopeOwnership"/>).
/// </summary>
/// <param name="Registration">The registration being activated.</param>
/// <param name="ServiceType">The service type asked for.</param>
/// <param name="ActivationScope">The scope the instance is built in and owned by.</param>
internal readonly record struct Activation(ServiceRegistration Registration, Type ServiceType, MeshServiceProvider ActivationScope);

/// <summary>Watches the activations of one level's own registrations.</summary>
internal interface IActivationObserver
{
    /// <summary>An activation is about to run.</summary>
    void Activating(in Activation activation);

    /// <summary>An activation built <paramref name="instance"/>.</summary>
    void Activated(in Activation activation, object instance);
}
