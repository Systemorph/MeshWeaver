namespace MeshWeaver.ContainerImages;

/// <summary>
/// Decides whether a caller may pull. A SEAM, not an implementation: this assembly deliberately
/// does not depend on the plugin catalogue, so the portal binds it to
/// <c>InstanceRegistryAuthenticator</c> — the same instance key satellites already present to the
/// plugin registry, which is the credential this mirror exists to reuse.
/// </summary>
public interface IContainerImageAuthenticator
{
    /// <summary>
    /// Resolves the caller from an <c>Authorization</c> header. Emits <c>null</c> for "not
    /// authenticated" — which the endpoint turns into the bearer challenge, never into a pull.
    /// </summary>
    IObservable<string?> Authenticate(string? authorizationHeader, CancellationToken ct);
}
