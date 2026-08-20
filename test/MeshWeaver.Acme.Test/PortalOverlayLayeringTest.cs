#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Acme.Test;

/// <summary>
/// Pins the stacking ORDER of the portal's top-of-viewport chrome, as it is actually shipped in
/// the stylesheets.
///
/// <para>🚨 Regression (#1883): the download-complete toast rendered BEHIND the top menu bar. Fluent
/// ships its toast container at <c>position: fixed; top: 24px; right: 24px; z-index: 999</c>, while
/// the portal header is <c>z-index: 1100</c> and occupies the top ~48px of the viewport — so the
/// toast opened inside the header's band underneath it and only a sliver showed. Every other piece
/// of portal chrome that has to clear that header (the chat widget, the /model · /agent pickers, the
/// Monaco popups) was given an explicit z-index; the toast container was the one top-level overlay
/// still running on a framework default.</para>
///
/// <para>The assertion is the RELATIONSHIP, not the literal number: re-tiering the header without
/// moving the overlays fails here, which is the failure mode a hard-coded <c>== 10000</c> would
/// miss. It reads the shipped CSS files (linked into this project as content), so it cannot pass
/// against a stale copy.</para>
///
/// <para>⚠️ What this does NOT prove: that the toast is visible on screen. CSS ordering is necessary
/// but not sufficient — a stacking context on an ancestor would still trap it (verified absent:
/// <c>.body-content</c> is deliberately <c>position: relative</c> with no z-index, and nothing on
/// the path declares transform/filter/contain/will-change). The rendered result is asserted by
/// <c>LoginDialogAboveHeaderTest</c>'s sibling technique in the Playwright project, which is
/// environment-gated and does not run in the normal suite — so a human or an E2E run confirms the
/// pixels; this test confirms the contract that makes them possible.</para>
/// </summary>
public class PortalOverlayLayeringTest
{
    private static string CssDir => Path.Combine(AppContext.BaseDirectory, "PortalCss");

    private static string ReadCss(string fileName)
    {
        var path = Path.Combine(CssDir, fileName);
        Assert.True(File.Exists(path),
            $"{fileName} was not copied to the test output ({path}). It is linked in via " +
            "<Content Include> in MeshWeaver.Acme.Test.csproj — without it this test would " +
            "silently verify nothing.");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// Every <c>z-index: N</c> declared by the rule whose selector block contains
    /// <paramref name="selector"/>. Returns every match so a rule duplicated across the desktop and
    /// mobile media queries (the header is) is checked in both places rather than only the first.
    /// </summary>
    private static IReadOnlyList<int> ZIndexesFor(string css, string selector)
    {
        var results = new List<int>();
        var searchFrom = 0;
        while (true)
        {
            var at = css.IndexOf(selector, searchFrom, StringComparison.Ordinal);
            if (at < 0) break;
            searchFrom = at + selector.Length;

            // Only a real rule head counts — skip the selector appearing inside a comment.
            var lastOpen = css.LastIndexOf("/*", at, StringComparison.Ordinal);
            var lastClose = css.LastIndexOf("*/", at, StringComparison.Ordinal);
            if (lastOpen > lastClose) continue;

            var open = css.IndexOf('{', searchFrom);
            var close = open < 0 ? -1 : css.IndexOf('}', open);
            if (open < 0 || close < 0) continue;

            var body = css[open..close];
            var m = Regex.Match(body, @"z-index\s*:\s*(\d+)");
            if (m.Success) results.Add(int.Parse(m.Groups[1].Value));
        }
        return results;
    }

    [Fact]
    public void ToastContainer_OutranksTheTopMenuBar()
    {
        var headerZ = ZIndexesFor(ReadCss("PortalLayoutBase.razor.css"), "::deep.layout>header");
        Assert.True(headerZ.Count > 0,
            "no z-index found on '::deep.layout>header' — the portal header's layer is what every " +
            "top-of-viewport overlay is tiered against; if it moved, this test must follow it.");

        var toastZ = ZIndexesFor(ReadCss("standard-page-layout.css"), ".fluent-toast-provider");
        Assert.True(toastZ.Count > 0,
            "the portal declares NO z-index for '.fluent-toast-provider', so it runs on Fluent's " +
            "default of 999 — below the header's 1100. That is exactly issue #1883: the " +
            "download-complete toast opens at top:24px, inside the header's band, and is painted " +
            "over by it.");

        var header = headerZ.Max();
        foreach (var toast in toastZ)
            Assert.True(toast > header,
                $"the toast container's z-index ({toast}) must exceed the top menu bar's ({header}); " +
                "otherwise the header paints over a toast that opens inside its band (#1883).");
    }

    /// <summary>
    /// The override has to actually WIN. Fluent's own rule is scoped —
    /// <c>.fluent-toast-provider[b-…]</c>, specificity 0-2-0 — so a bare global class selector
    /// (0-1-0) loses to it and an equal-specificity one is decided by bundle order. Dropping the
    /// <c>!important</c> would leave a rule that reads correct and does nothing.
    /// </summary>
    [Fact]
    public void ToastLayerOverride_BeatsFluentsScopedRule()
    {
        var css = ReadCss("standard-page-layout.css");
        var at = css.IndexOf(".fluent-toast-provider", StringComparison.Ordinal);
        while (at >= 0)
        {
            var lastOpen = css.LastIndexOf("/*", at, StringComparison.Ordinal);
            var lastClose = css.LastIndexOf("*/", at, StringComparison.Ordinal);
            if (lastOpen <= lastClose) break; // a rule head, not a mention inside a comment
            at = css.IndexOf(".fluent-toast-provider", at + 1, StringComparison.Ordinal);
        }
        Assert.True(at >= 0, "no '.fluent-toast-provider' rule in standard-page-layout.css");

        // Locate the rule body defensively: a malformed/reformatted stylesheet must fail with a
        // readable assertion, not an ArgumentOutOfRangeException from the slice below.
        var open = css.IndexOf('{', at);
        Assert.True(open > 0, "the '.fluent-toast-provider' rule has no opening brace");
        var close = css.IndexOf('}', open);
        Assert.True(close > open, "the '.fluent-toast-provider' rule has no closing brace");

        var body = css[open..close];

        Assert.Matches(@"z-index\s*:\s*\d+\s*!important", body);
    }

    /// <summary>
    /// The header carries the SAME layer in the desktop and mobile media queries. Fixing only one
    /// would leave the bug live on the other form factor while the test above still passed.
    /// </summary>
    [Fact]
    public void HeaderLayer_IsDeclaredConsistentlyAcrossBreakpoints()
    {
        var headerZ = ZIndexesFor(ReadCss("PortalLayoutBase.razor.css"), "::deep.layout>header");
        Assert.Equal(2, headerZ.Count);
        Assert.Single(headerZ.Distinct());
    }
}
