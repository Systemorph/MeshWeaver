namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// The ONE rule for a <c>returnUrl</c> that arrives from the outside world: it is honoured only if
/// it is LOCAL — a path on this host. Anything else falls back to the root.
///
/// <para>🚨 Before this existed, <c>ExternalAuthController</c> redirected to
/// <c>returnUrl ?? "/"</c> unvalidated on both the sign-in callback and logout — a classic open
/// redirect (#2302): a crafted link to our own <c>/auth/external/callback?returnUrl=https://evil</c>
/// signs the victim in HERE and then sends them THERE, on a page that looks like the tail of our
/// own login flow. Pure and static so it is unit-tested without constructing a controller.</para>
/// </summary>
public static class ReturnUrlPolicy
{
    /// <summary>A safe target: the given URL if it is local, otherwise <c>"/"</c>.</summary>
    public static string Sanitize(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl)) return "/";
        var u = returnUrl.Trim();
        // Local = a single leading slash, and NOT protocol-relative ("//evil.com") or a
        // backslash variant browsers normalise to one ("/\evil.com").
        if (u.Length == 0 || u[0] != '/') return "/";
        if (u.Length > 1 && (u[1] == '/' || u[1] == '\\')) return "/";
        return u;
    }
}
