using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;

namespace MeshWeaver.Blazor.Infrastructure;

/// <summary>
/// The ONE projection of an authoritative mesh <c>User</c> node onto a seeded
/// <see cref="AccessContext"/> — shared by every entry path that resolves a caller, so the SSR
/// request path and the Blazor circuit path can never disagree about who a viewer is or what
/// language and time zone they see the portal in.
///
/// <para>🚨 <b>Why shared rather than duplicated.</b> The two paths used to project differently:
/// <c>CircuitAccessHandler</c> read <c>TimeZoneId</c> and <c>Locale</c> off the profile, while
/// <c>UserContextMiddleware</c> took only <c>Id</c> and <c>Name</c> and left both null. That was
/// invisible while nothing seeded a locale — every server-rendered string was English anyway. The
/// moment a request-derived locale exists (<see cref="Locales.Negotiate"/>), the divergence becomes
/// user-visible and WRONG: a signed-in user whose profile says English, browsing from a German
/// browser, would get German chrome on the server-rendered shell and English inside the circuit.
/// One projection, applied by both, makes "the stored preference wins" true by construction rather
/// than by two call sites happening to agree.</para>
///
/// <para>Pure: a function of (seed, node, options) with no hub, circuit or HTTP state, which is why
/// it can be pinned directly by tests.</para>
/// </summary>
public static class MeshUserProjection
{
    /// <summary>
    /// Projects <paramref name="meshUser"/> onto <paramref name="seed"/>.
    ///
    /// <para>Identity (<c>ObjectId</c>, <c>Name</c>) always comes from the node — it is the
    /// authoritative partition key, and a claim-derived guess must never outrank it. Preferences
    /// (<c>TimeZoneId</c>, <c>Locale</c>) come from the node's profile WHEN SET, and otherwise leave
    /// the seed untouched: an unset profile field means "this user never expressed a preference",
    /// which must fall back to whatever the caller already knew (the request's
    /// <c>Accept-Language</c>, say) rather than erase it to null.</para>
    /// </summary>
    /// <param name="seed">The context built from the authentication claims (and the request).</param>
    /// <param name="meshUser">The authoritative mesh <c>User</c> node for that identity.</param>
    /// <param name="options">Serializer options used to read the node's <c>User</c> content.</param>
    public static AccessContext Apply(
        AccessContext seed,
        MeshNode meshUser,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(meshUser);
        var profile = meshUser.ContentAs<User>(options);
        var timeZoneId = profile?.TimeZoneId;
        var locale = profile?.Locale;
        return seed with
        {
            ObjectId = meshUser.Id,
            Name = meshUser.Name ?? meshUser.Id,
            TimeZoneId = string.IsNullOrWhiteSpace(timeZoneId) ? seed.TimeZoneId : timeZoneId,
            Locale = string.IsNullOrWhiteSpace(locale) ? seed.Locale : locale
        };
    }
}
