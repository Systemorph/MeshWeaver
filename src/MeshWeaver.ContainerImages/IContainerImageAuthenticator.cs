namespace MeshWeaver.ContainerImages;

/// <summary>
/// Decides whether a caller may pull. A SEAM, not an implementation: this assembly deliberately
/// does not depend on the plugin catalogue, so the portal binds it to
/// <c>InstanceRegistryAuthenticator</c> — the same instance key satellites already present to the
/// plugin registry, which is the credential this mirror exists to reuse.
///
/// <para>🚨 <b>The contract is ONE shape: <c>Bearer &lt;instance key&gt;</c>.</b> A registry client
/// speaks two — <c>Basic base64(user:key)</c> to the token endpoint, <c>Bearer &lt;token&gt;</c>
/// everywhere after — and the mirror reduces both to a bearer (<see cref="RegistryCredential"/>)
/// BEFORE calling this. An implementation therefore never has to know about Basic, and must not
/// grow a second code path for it: two places deciding what a credential means is how one of them
/// ends up disagreeing. A header the mirror could not read is passed through verbatim, so this
/// seam stays the authority on what counts as authenticated.</para>
/// </summary>
public interface IContainerImageAuthenticator
{
    /// <summary>
    /// Resolves the caller from an <c>Authorization</c> header. Emits <c>null</c> for "not
    /// authenticated" — which the endpoint turns into the bearer challenge, never into a pull.
    /// </summary>
    /// <param name="authorizationHeader">The header, normalised to <c>Bearer &lt;key&gt;</c>.</param>
    /// <param name="ct">Cancels the resolution.</param>
    /// <returns>The caller's identity, or null when the credential does not authenticate.</returns>
    IObservable<string?> Authenticate(string? authorizationHeader, CancellationToken ct);
}
