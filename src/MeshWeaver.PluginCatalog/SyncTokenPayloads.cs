using System.Text.Json;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The wire contract of <c>POST /api/instances/token</c> — a registered instance exchanges its
/// durable <c>mwi_</c> key for a short-lived, scoped <c>mwa_</c> access token. One place for both
/// sides, mirroring <see cref="InstanceRegistrationPayloads"/> and <see cref="PluginRegistryPayloads"/>
/// so producer and consumer cannot drift.
///
/// <para>Deliberately OAuth-shaped (<c>access_token</c> / <c>token_type</c> / <c>expires_in</c> /
/// <c>scope</c>) because that is what every consumer already knows how to hold: mint at the start of
/// a run, present it on each call, discard it. It is not an OAuth server — there is no user, no
/// authorization code and no refresh token; the durable instance key IS the long-lived credential
/// and a new exchange is the refresh.</para>
/// </summary>
public static class SyncTokenPayloads
{
    /// <summary>The route the endpoint is mapped under.</summary>
    public const string Route = "/api/instances/token";

    /// <summary>Serializer options both sides use (Web camelCase).</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The request body — both fields optional, so an empty body is a valid "give me a default
    /// token for everything I am licensed for".
    /// </summary>
    /// <param name="Scope"><c>Source/Package</c> entries to narrow the token to. Anything not
    /// already licensed is dropped rather than refused: a token can only ever narrow, so asking for
    /// more than you hold is not an attack, it is a stale caller.</param>
    /// <param name="LifetimeSeconds">Requested lifetime; clamped to the registry's maximum.</param>
    public record Request(IReadOnlyCollection<string>? Scope = null, int? LifetimeSeconds = null);

    /// <summary>
    /// The success response.
    /// </summary>
    /// <param name="AccessToken">The minted token, presented as <c>Authorization: Bearer</c>.</param>
    /// <param name="TokenType">Always <c>Bearer</c>.</param>
    /// <param name="ExpiresIn">Seconds until it stops verifying.</param>
    /// <param name="Scope">The EFFECTIVE scope — what the caller asked for intersected with what it
    /// is licensed for. Returned explicitly so a consumer can see it got less than it requested
    /// instead of discovering it one 404 at a time.</param>
    public record Response(
        string AccessToken, string TokenType, int ExpiresIn, IReadOnlyCollection<string> Scope);
}
