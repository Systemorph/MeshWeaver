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

        var nextDocument = NextDocumentSeparator(template, resource);
        Assert.True(nextDocument > resource,
            $"The '{PortalResourceMarker}' resource in {IngressTemplate} is not followed by a YAML "
            + "document separator, so this guard cannot tell where it ends — it would otherwise "
            + "read annotations belonging to the gRPC or MCP Ingress and pass on the wrong object.");

        Assert.Contains(".Values.ingress.annotations", template[resource..nextDocument],
            StringComparison.Ordinal);
    }

    /// <summary>
    /// 🚨 The end of the resource is the next YAML DOCUMENT SEPARATOR — a line that <i>is</i>
    /// <c>---</c>, not any occurrence of three hyphens.
    ///
    /// <para>This was <c>IndexOf("---")</c>, which matches inside a comment. The guard therefore
    /// never once found a real separator: on <c>main</c> the first hit was the
    /// <c># ---- Next.js React GUI</c> banner 610 characters in, which happens to sit AFTER
    /// <c>.Values.ingress.annotations</c>, so the assertion passed by luck. Adding a
    /// <c># ---- Which certificate …</c> banner inside the <c>annotations:</c> block — i.e. BEFORE
    /// the reference — cut the slice to 202 characters ending at <c>annotations:\n    # </c>, and
    /// the guard reported a value that was present as missing. A guard that passes by luck is not
    /// a guard, and a false positive here costs an author a hunt for a defect that is not there.</para>
    ///
    /// <para><c>[^\S\n]</c> is whitespace other than a newline, so a CRLF line ending and any
    /// trailing spaces are consumed before <c>$</c>; a trailing <c>#</c> comment on the separator
    /// line is valid YAML and allowed. Four hyphens do NOT match, because the fourth is neither
    /// whitespace nor end of line — which is exactly what makes a <c># ----</c> banner safe.</para>
    /// </summary>
    private static readonly Regex DocumentSeparator =
        new(@"^---[^\S\n]*(?:#[^\n]*)?$", RegexOptions.Multiline | RegexOptions.Compiled);

    private static int NextDocumentSeparator(string template, int from)
    {
        var match = DocumentSeparator.Match(template, from);
        return match.Success ? match.Index : -1;
    }

    /// <summary>
    /// The control for <see cref="NextDocumentSeparator"/> — the predicate the guard above slices
    /// with. Without it the slicing is only ever exercised on whatever the template happens to
    /// contain today, which is precisely how the <c>IndexOf("---")</c> version passed for as long
    /// as it did. A comment banner must not end the resource, and a real separator must.
    /// </summary>
    [Fact]
    public void TheDocumentSeparatorIsALine_NotAnyThreeHyphens()
    {
        // The shape that broke it: a banner whose dashes sit INSIDE a comment, before the value.
        const string banner = "  annotations:\n    # ---- Which certificate ----\n    x: 1\n";
        Assert.Equal(-1, NextDocumentSeparator(banner, 0));

        // Four or more hyphens opening a line are still a banner, not a separator.
        Assert.Equal(-1, NextDocumentSeparator("----\n", 0));
        Assert.Equal(-1, NextDocumentSeparator("  --- indented\n", 0));

        // A real separator is found, in each of its legal spellings. ("a: 1\n" is five characters,
        // so the separator opens at index 5.)
        Assert.Equal(5, NextDocumentSeparator("a: 1\n---\nb: 2\n", 0));
        Assert.Equal(5, NextDocumentSeparator("a: 1\n---\r\nb: 2\n", 0));
        Assert.Equal(5, NextDocumentSeparator("a: 1\n---   \nb: 2\n", 0));
        Assert.Equal(5, NextDocumentSeparator("a: 1\n--- # the portal Ingress\nb: 2\n", 0));

        // And the search honours its start offset, which is what confines the slice to ONE resource.
        const string two = "---\nname: first\n---\nname: second\n";
        Assert.Equal(0, NextDocumentSeparator(two, 0));
        Assert.Equal(16, NextDocumentSeparator(two, 1));
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
