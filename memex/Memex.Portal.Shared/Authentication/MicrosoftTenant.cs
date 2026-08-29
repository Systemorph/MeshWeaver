namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// The one place a Microsoft (Entra) <b>authority</b> is composed from the configured tenant —
/// <c>https://login.microsoftonline.com/{tenant}/{path}</c> — and the guard that refuses a tenant
/// which cannot form one.
///
/// <para>🚨 <b>Why this exists (MeshWeaver#2621).</b> An environment variable or configMap entry
/// cannot be null, only EMPTY, so <c>""</c> is the deployed shape of "unset". The portal chart
/// rendered <c>Authentication__Microsoft__TenantId</c> with an empty default and the composition
/// read that empty string AS the tenant: the authority became
/// <c>login.microsoftonline.com//v2.0</c>, whose discovery document Entra will never serve
/// (<c>IDX20803</c>). Every Microsoft sign-in on memex-cloud answered <b>500</b>, per request,
/// deterministically — and the 500 named an unreachable URL rather than the configuration key an
/// operator had to set.</para>
///
/// <para><b>Two rules, and they are deliberately different.</b></para>
/// <list type="number">
/// <item><description><b>Blank is UNSET, never a tenant.</b> Null, empty and whitespace all resolve
/// to <see cref="Unset"/> — the same as no key at all. This must NOT refuse: an install that never
/// wired Microsoft sign-in leaves the key blank, and aborting a portal because an optional
/// integration is unconfigured is the #2510 failure (a guard that fails worse than the thing it
/// guards).</description></item>
/// <item><description><b>A present-but-malformed tenant is REFUSED, at startup.</b> A value that is
/// not a single URL path segment cannot compose an authority at all, so no install is serving
/// sign-in with one today — refusing it at boot cannot take a working portal down, and it converts
/// the outage's per-request 500 into a named configuration error that says which key is
/// wrong.</description></item>
/// </list>
///
/// <para>Pure decision, no I/O and no container, so the boot-time guard is unit-testable without
/// spinning a host — the same shape as <c>MemexConfiguration.ValidateContentStorageDurability</c>.
/// The sign-in handler that actually 500-ed lives in the portal package
/// (<c>MeshWeaver.Blazor.Portal.Authentication.AuthenticationBuilderExtensions</c>, plugins repo)
/// and carries the same two rules at its registration; this covers every authority composed
/// platform-side, starting with the Executive Assistant's delegated Graph flow.</para>
/// </summary>
public static class MicrosoftTenant
{
    /// <summary>The configuration key the tenant is read from, in binder form.</summary>
    public const string ConfigurationKey = "Authentication:Microsoft:TenantId";

    /// <summary>
    /// The same key as an environment variable / configMap entry — the form an operator actually
    /// sets, and the form that must appear in a refusal so the message is actionable in a pod.
    /// </summary>
    public const string EnvironmentKey = "Authentication__Microsoft__TenantId";

    /// <summary>The Entra login host every authority is composed against.</summary>
    public const string Host = "login.microsoftonline.com";

    /// <summary>
    /// The tenant an UNSET key resolves to: the multi-tenant <c>common</c> authority, which accepts
    /// both work/school and personal accounts. An environment that needs a single tenant, or the
    /// work/school-only <c>organizations</c> authority the onboarding guide prescribes, sets its
    /// value explicitly.
    /// </summary>
    public const string Unset = "common";

    /// <summary>
    /// The tenant to compose an authority from: the configured value, trimmed, or
    /// <see cref="Unset"/> when the key is absent or blank.
    /// </summary>
    /// <param name="configured">The raw configured value (<c>null</c> when the key is absent).</param>
    /// <returns>A tenant that is guaranteed to form a single, non-empty authority segment.</returns>
    /// <exception cref="InvalidOperationException">
    /// The value is present but is not a single URL path segment, so no authority can be composed
    /// from it. The message names <see cref="EnvironmentKey"/> and the accepted values.
    /// </exception>
    public static string Resolve(string? configured)
    {
        // Blank is the deployed shape of "unset" — see rule 1 on the type.
        if (string.IsNullOrWhiteSpace(configured))
            return Unset;

        var tenant = configured.Trim();
        // A tenant id is a GUID, a verified domain (contoso.onmicrosoft.com), or one of the
        // well-known multi-tenant aliases — all of which are a single path segment of letters,
        // digits, '-', '.' and '_'. Anything else ('/', whitespace, '?', '#', ':') would either
        // split the path or need escaping, and both compose a URL Entra never serves.
        foreach (var c in tenant)
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '.' && c != '_')
                throw new InvalidOperationException(Refusal(configured));

        return tenant;
    }

    /// <summary>
    /// Composes <c>https://login.microsoftonline.com/{tenant}/{path}</c> from the configured tenant.
    /// </summary>
    /// <param name="configured">The raw configured value (<c>null</c> when the key is absent).</param>
    /// <param name="path">The authority path after the tenant, e.g. <c>v2.0</c> or <c>oauth2/v2.0</c>.</param>
    /// <returns>An absolute authority with no empty segment.</returns>
    /// <exception cref="InvalidOperationException">
    /// The configured tenant cannot form an authority — see <see cref="Resolve"/>.
    /// </exception>
    public static string Authority(string? configured, string path)
    {
        var authority = $"https://{Host}/{Resolve(configured)}/{path}";
        // Belt and braces: the composed URL is re-read, so an EMPTY SEGMENT can never leave this
        // method however the tenant rule above evolves. `//` in the path is the outage's exact
        // signature, and it is cheaper to assert here than to recognise in a log at 13:50Z.
        if (!Uri.TryCreate(authority, UriKind.Absolute, out var uri)
            || uri.Host != Host
            || uri.AbsolutePath.Contains("//", StringComparison.Ordinal)
            || uri.AbsolutePath.EndsWith('/'))
            throw new InvalidOperationException(Refusal(configured));
        return authority;
    }

    /// <summary>
    /// The BOOT-TIME guard: throws when the configured tenant cannot form an authority, so the
    /// misconfiguration is named at startup instead of surfacing as a 500 on the first sign-in.
    /// A blank or absent key is legitimate (it means "unset") and passes.
    /// </summary>
    /// <param name="configured">The raw configured value (<c>null</c> when the key is absent).</param>
    /// <exception cref="InvalidOperationException">
    /// The value is present but malformed; the message names the configuration key.
    /// </exception>
    public static void Validate(string? configured) => Authority(configured, "v2.0");

    private static string Refusal(string? configured) =>
        $"Microsoft sign-in misconfiguration (issue #2621): {EnvironmentKey} ('{configured}') is not a "
        + "tenant. It has to be a single authority segment — an Entra tenant GUID, a verified domain "
        + $"such as 'contoso.onmicrosoft.com', or one of '{Unset}' (work/school and personal), "
        + "'organizations' (work/school only — what the onboarding guide prescribes for a public "
        + $"portal) and 'consumers'. Composing https://{Host}/<tenant>/... from this value yields a "
        + "URL Entra will never serve a discovery document for, so EVERY Microsoft sign-in would "
        + $"answer 500 at the first request that touched the handler. Set {EnvironmentKey} to a valid "
        + "tenant, or leave it unset (an unset key means the multi-tenant "
        + $"'{Unset}' authority). Refusing to start so the misconfiguration surfaces now rather than "
        + "on the first user who tries to sign in.";
}
