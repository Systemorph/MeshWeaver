using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;

namespace MeshWeaver.Blazor.Portal;

/// <summary>
/// The ONE place a portal-chrome component resolves "who is the signed-in user" at interaction time.
///
/// <para>🚨 Never read <see cref="AccessService.Context"/> alone in a Blazor event handler. That
/// AsyncLocal is populated per HUB message delivery — a UI click is a circuit inbound activity, where
/// <c>CircuitAccessHandler</c> stamps <see cref="AccessService.CircuitContext"/> instead, so
/// <c>Context</c> reads null and a "resolve the user, then act" handler silently does nothing (the
/// AI menu's dead "New thread" entry). The durable per-circuit identity is <c>CircuitContext</c>;
/// <c>Context</c> is only a fallback for code reached from within a delivery, and is filtered for a
/// leaked <c>system-security</c> / hub-shaped principal, which is never a real user.</para>
/// </summary>
internal static class CircuitUser
{
    /// <summary>
    /// The signed-in user's id (their partition key), or null when no real user identity is
    /// available (anonymous circuit, SSR/prerender, or a leaked system/hub principal).
    /// </summary>
    internal static string? ResolveUserId(AccessService? accessService)
    {
        if (accessService is null)
            return null;
        foreach (var candidate in new[] { accessService.CircuitContext?.ObjectId, accessService.Context?.ObjectId })
        {
            if (!string.IsNullOrEmpty(candidate)
                && candidate != WellKnownUsers.System
                && !AccessService.LooksLikeHubPrincipal(candidate))
                return candidate;
        }
        return null;
    }
}
