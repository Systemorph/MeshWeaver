using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>An external sign-in through this chart's ingress returned 502 Bad Gateway — from nginx,
/// not from the portal.</b>
///
/// <para>Observed on a chart-deployed portal on 2026-08-31. The Entra round-trip is correct end to
/// end: <c>GET /auth/login?provider=Microsoft</c> answers 302, the user authenticates, and Entra
/// form-POSTs back to <c>/signin-microsoft</c>. The portal handles that callback and answers with
/// the sign-in <c>Set-Cookie</c> block. nginx then refuses its OWN upstream's answer:</para>
///
/// <code>
/// "POST /signin-microsoft HTTP/2.0" 502
/// [error] upstream sent too big header while reading response header from upstream
/// </code>
///
/// <para><b>Why the header is large by construction.</b> ASP.NET Core's cookie authentication
/// serialises the whole authentication ticket — the external identity's claims — into the auth
/// cookie, and splits anything past ~4 KB into numbered chunks (<c>MemexAuthC1</c>,
/// <c>MemexAuthC2</c>, …), each its own <c>Set-Cookie</c> header. The same response also deletes
/// the OIDC correlation and nonce cookies, whose NAMES alone are ~380 characters. An Entra ticket
/// with group claims therefore lands well past nginx's DEFAULT <c>proxy_buffer_size</c> of 4 KB —
/// so the header block is too big on the very first real sign-in, not in some edge case.</para>
///
/// <para><b>Why nothing caught it.</b> Developer login writes a small cookie and passes, so a
/// deployment can look completely healthy while every external provider 502s. And the failure is
/// invisible from the portal side: its logs show the callback handled successfully — the response
/// is discarded one hop later, by the proxy.</para>
///
/// <para>This is the same shape as the module-bundle 413 (#2489): the chart carried
/// <c>proxy-body-size</c> for how big a REQUEST may be, and said nothing about how big a RESPONSE
/// HEADER may be. So the budget stops being implicit.</para>
/// </summary>
public class IngressAuthCookieHeaderBudgetGuard
{
    private const string Values = "deploy/helm/values.yaml";

    private const string BufferSizeAnnotation = "nginx.ingress.kubernetes.io/proxy-buffer-size";

    private const string IngressTemplate = "deploy/helm/templates/memex-portal/ingress.yaml";

    /// <summary>The resource that terminates the sign-in flow, as the template names it.</summary>
    private const string PortalResourceMarker = "name: \"memex-portal\"";

    /// <summary>
    /// The floor, in kilobytes. Four chunks of the ~4 KB ASP.NET Core cookie limit is the ticket
    /// size an Entra identity with group claims reaches, and the response carries the correlation
    /// and nonce deletions on top. Below this the sign-in 502s; nginx's default 4 KB is not close.
    /// </summary>
    private const int MinimumKilobytes = 32;

    [Fact]
    public void ThePortalIngress_BudgetsForTheChunkedAuthCookie()
    {
        var text = File.ReadAllText(Path.Combine(FindRepoRoot(), Values));

        var match = Regex.Match(
            text,
            @"^\s*" + Regex.Escape(BufferSizeAnnotation) + @":\s*""(?<size>\d+)k""\s*$",
            RegexOptions.Multiline);

        Assert.True(match.Success,
            $"The portal ingress ships no '{BufferSizeAnnotation}', so nginx applies its 4 KB "
            + "default and answers 502 to the sign-in callback of every external provider — with "
            + "the portal's own logs showing the callback handled successfully. Set it in "
            + $"{Values} under ingress.annotations.");

        var kilobytes = int.Parse(match.Groups["size"].Value, CultureInfo.InvariantCulture);

        Assert.True(kilobytes >= MinimumKilobytes,
            $"'{BufferSizeAnnotation}' is {kilobytes}k, below the {MinimumKilobytes}k a chunked "
            + "authentication ticket needs. A value that merely beats the 4 KB default still 502s "
            + "the moment an identity carries group claims.");
    }

    /// <summary>
    /// The budget only helps if the ingress that terminates the sign-in flow actually receives it.
    /// The portal Ingress renders <c>.Values.ingress.annotations</c> wholesale, so a future edit
    /// that moves the annotations behind a condition, or hard-codes a competing set on the
    /// resource, would leave this guard passing while the deployed object lost the value.
    /// </summary>
    [Fact]
    public void ThePortalIngress_TakesItsAnnotationsFromTheValuesTheGuardChecks()
    {
        var template = File.ReadAllText(Path.Combine(FindRepoRoot(), IngressTemplate));

        // The `memex-portal` resource — the one serving /signin-* — up to the next document.
        // Both markers are asserted before they are used to slice: a template refactor that renamed
        // the resource or moved the document separator would otherwise surface as an opaque
        // ArgumentOutOfRangeException, which says nothing about what an author has to fix.
        var resource = template.IndexOf(PortalResourceMarker, StringComparison.Ordinal);
        Assert.True(resource >= 0,
            $"{IngressTemplate} declares no '{PortalResourceMarker}' — the resource this guard "
            + "exists to pin was renamed or removed, and the guard now covers nothing.");

        var nextDocument = template.IndexOf("---", resource, StringComparison.Ordinal);
        Assert.True(nextDocument > resource,
            $"The '{PortalResourceMarker}' resource in {IngressTemplate} is not followed by a YAML "
            + "document separator, so this guard cannot tell where it ends — it would otherwise "
            + "read annotations belonging to the gRPC or MCP Ingress and pass on the wrong object.");

        Assert.Contains(".Values.ingress.annotations", template[resource..nextDocument],
            StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
