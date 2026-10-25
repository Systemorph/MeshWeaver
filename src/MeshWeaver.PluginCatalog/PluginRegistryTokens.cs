namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The wire shape of the credential a consumer presents to the plugin registry: an instance key sent
/// as <c>Authorization: Bearer &lt;key&gt;</c> (<see cref="PluginRegistryReference.Token"/>). The
/// registry resolves it to a registered instance and its plugin grant; see
/// <c>Doc/Architecture/PluginRegistry</c>. The former flat <c>PluginCatalog:RegistryTokens</c>
/// allowlist and its validator are gone.
/// </summary>
public static class PluginRegistryTokens
{
    /// <summary>The HTTP auth scheme the token travels under.</summary>
    public const string Scheme = "Bearer";

    /// <summary>Formats the <c>Authorization</c> header value the consumer sends.</summary>
    public static string AuthorizationHeader(string token) => $"{Scheme} {token}";
}
