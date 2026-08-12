using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace MeshWeaver.Hosting.Blazor;

/// <summary>
/// The decision behind the nearest-existing-ancestor fallback, as a PURE predicate — no navigation
/// manager, no mesh, no circuit — so the one thing that must not drift can be asserted directly.
///
/// <para>The hazard this rule exists to avoid is not "the fallback doesn't fire". It is the fallback
/// firing on a failure that is NOT an absence: a denial rendered as "here is something else" tells a
/// correctly-blocked user that the content is simply gone, and tells a wrongly-blocked one nothing
/// they can act on. An availability failure (<see cref="ErrorType.Unavailable"/>) is worse still —
/// no verdict was reached at all, so presenting it as absence is a fabricated negative.</para>
///
/// <para>So the trigger is not "the load failed". It is the pair of TYPED absence outcomes the
/// navigation layer already collapsed into its page-not-found branch before this feature existed:
/// <see cref="ErrorType.NotFound"/> (routing found no node at the address) and
/// <see cref="ErrorType.Ignored"/> (the target hub has no handler for it — the area does not exist).
/// This deliberately does NOT widen the class of failures treated as absence; it only changes what
/// the viewer is shown for a class that already read as "page not found". Everything else —
/// <see cref="ErrorType.Unauthorized"/>, <see cref="ErrorType.Forbidden"/>,
/// <see cref="ErrorType.Unavailable"/>, <see cref="ErrorType.Unknown"/> (the value an unclassified
/// <c>d.Failed(reason)</c> refusal carries, see #1253/#1279), timeouts, and any other exception —
/// keeps failing with its own reason.</para>
/// </summary>
internal static class AncestorFallbackRule
{
    /// <summary>
    /// Whether a navigation whose load failed should fall back to <paramref name="prefix"/>, the
    /// nearest EXISTING ancestor the resolver already computed.
    /// </summary>
    /// <param name="error">The exception the load faulted with.</param>
    /// <param name="prefix">
    /// <see cref="AddressResolution.Prefix"/> — the deepest node that actually exists on the
    /// requested path. This is the same computation behind the routing diagnostic
    /// "Closest ancestor is 'X' (remainder='Y')"; the fallback consumes it rather than re-deriving it.
    /// </param>
    /// <param name="remainder">
    /// <see cref="AddressResolution.Remainder"/> — what did NOT match. Required to be non-empty: a
    /// bare existing path that fails to load is a real failure, not a wrong address, and must stay one.
    /// </param>
    /// <param name="route">The path the viewer asked for. The fallback must move them somewhere else.</param>
    internal static bool ShouldFallBack(Exception? error, string? prefix, string? remainder, string? route)
    {
        if (string.IsNullOrEmpty(remainder) || string.IsNullOrEmpty(prefix))
            return false;
        if (string.Equals(prefix.Trim('/'), (route ?? string.Empty).Trim('/'), StringComparison.Ordinal))
            return false;   // one hop, and it must be to a DIFFERENT path — this is what bounds it
        return IsTypedAbsence(error);
    }

    /// <summary>
    /// True ONLY for a typed "there is no such node / no such handler" outcome. Walks the inner
    /// exceptions because the delivery failure arrives wrapped when it crosses a stream boundary.
    /// </summary>
    internal static bool IsTypedAbsence(Exception? error)
    {
        for (var e = error; e is not null; e = e.InnerException)
        {
            if (e is DeliveryFailureException { Failure: { } failure }
                && failure.ErrorType is ErrorType.NotFound or ErrorType.Ignored)
                return true;
        }
        return false;
    }
}
