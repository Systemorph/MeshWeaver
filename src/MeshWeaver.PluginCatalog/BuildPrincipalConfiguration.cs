using MeshWeaver.Mesh.Security;
using Microsoft.Extensions.Configuration;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The one deployment-level knob the build-principal leg has: which <c>aud</c> a GitHub Actions
/// token must carry to be considered minted for THIS registry (#2483).
///
/// <para>🚨 <b>Unconfigured means REFUSE, not "accept anything".</b> Every GitHub Actions run on
/// earth can mint a token signed by the same keys; the audience is the only claim that says "this
/// one was minted for us". A registry with no audience configured therefore has no build-principal
/// surface — the same shape as <see cref="ModulePublish.TokenConfigKey"/>, and for the same reason
/// the anonymous-when-unset registry of 2026-08-06 is not repeated here.</para>
///
/// <para>🚨 <b>The ISSUER is deliberately NOT configurable</b> (<see cref="GitHubActionsToken.Issuer"/>).
/// A configurable issuer is a configurable trust anchor: one overlay pointing it at another key set
/// and every claim below becomes attacker-authored. Only the audience — a value that can merely
/// widen or narrow which tokens are considered ours — is an operator's to set.</para>
///
/// <para>A workflow asks for a token with <c>curl "$ACTIONS_ID_TOKEN_REQUEST_URL&amp;audience=&lt;this
/// value&gt;"</c>, so the two sides carry the same string and neither holds a secret.</para>
/// </summary>
public static class BuildPrincipalConfiguration
{
    /// <summary>Configuration key holding the single accepted audience — usually the registry's own
    /// public URL (<c>https://memex.meshweaver.cloud</c>).</summary>
    public const string AudienceConfigKey = DeclaredBuildPrincipals.AudienceConfigKey;

    /// <summary>Configuration section holding several accepted audiences
    /// (<c>…:BuildPrincipalAudiences:0</c>, <c>:1</c>, …), for a registry reachable under more than
    /// one name. Folded together with <see cref="AudienceConfigKey"/>.</summary>
    public const string AudiencesConfigSection = DeclaredBuildPrincipals.AudiencesConfigSection;

    /// <summary>
    /// The audiences this registry accepts, de-duplicated and trimmed. EMPTY disables the
    /// build-principal leg entirely — which is the safe default, not a degraded one.
    /// </summary>
    /// <param name="configuration">The deployment's configuration, or null.</param>
    /// <returns>The accepted audiences; empty when none is configured.</returns>
    public static IReadOnlyCollection<string> Audiences(IConfiguration? configuration) =>
        DeclaredBuildPrincipals.Audiences(configuration);

    /// <summary>
    /// The build principals this deployment DECLARES in configuration
    /// (<see cref="DeclaredBuildPrincipals.ConfigSection"/>), validated. Consulted by the
    /// authenticator only for a repository that has NO node under
    /// <see cref="BuildPrincipal.Namespace"/> — the node, when one exists, decides. The audience
    /// still gates everything: a declared principal authenticates nothing on a registry with no
    /// audience configured.
    /// </summary>
    /// <param name="configuration">The deployment's configuration, or null.</param>
    /// <returns>The accepted and refused declarations.</returns>
    public static BuildPrincipalDeclarations Declared(IConfiguration? configuration) =>
        DeclaredBuildPrincipals.Read(configuration);
}
