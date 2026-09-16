#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 A node LANDING PAGE must say what it does about provenance — Systemorph/MeshWeaver#4500.
///
/// <para><b>What went wrong.</b> The <c>Type · Created · Updated</c> line rode on ONE renderer,
/// the framework's <c>Overview</c>. A node type that registered its own landing page replaced that
/// renderer and the line went with it — silently, because <c>WithNamedRenderer</c> is
/// last-writer-wins and nothing anywhere recorded the loss. Measured 2026-09-16 across the fleet:
/// of the 99 landing pages that replace the framework renderer, 86 shipped without the line — 4 in
/// this repo (PluginCatalog, GlobalSettings, User, Partition) and 82 in MeshWeaver.Plugins. At an
/// 87% miss rate, adding call sites is not a fix; the DEFAULT had to change.</para>
///
/// <para><b>What this guard is for.</b> <c>MeshNodeLayoutAreas.WithNodePage</c> now carries the
/// line by default, so the remaining failure mode is narrower and entirely mechanical: registering
/// a landing page through the bare <c>WithView</c> / <c>WithNamedRenderer</c>, which records no
/// verdict at all. This guard finds exactly that, and turns it into a decision somebody has to
/// write down — either by using <c>WithNodePage</c>, or by adding a reasoned line here.</para>
///
/// <para>🚨 <b>What it CANNOT see, stated rather than implied.</b> Its subject is
/// <c>src/</c> — the compiled framework. Most of the fleet's landing pages are NOT there: they are
/// in-mesh <c>Source/*.cs</c> and NodeType <c>configuration</c> lambdas that compile at RUNTIME in
/// the portal and are invisible to every compiler and every scan in this repo (AGENTS.md: "green CI
/// does NOT mean the mesh compiles"). Those 82 pages are reached by the DEFAULT, not by this guard:
/// a page that moves to <c>WithNodePage</c> gains the line, and one that stays on <c>WithView</c>
/// stays as it is. Nothing in core can red on them, and pretending otherwise by widening the roots
/// would produce a guard that scans a tree the defect does not live in.</para>
///
/// <para>🚨 <b>Why the rule over-flags on purpose.</b> An area name is a landing page on one hub
/// and an ordinary tab on another — <c>Search</c> is <c>Partition</c>'s landing page and every
/// other node's search tab. A scanner cannot tell which hub a registration lands on, so it flags
/// every registration of a name that is a default area SOMEWHERE, and the allow file carries the
/// judgement. Over-flagging costs a reasoned line; under-flagging costs a page nobody notices is
/// missing something — which is the whole issue.</para>
/// </summary>
public class NodePageProvenanceGuard
{
    private static readonly string[] ScannedRoots = ["src"];

    private const string AllowFileName = "NodePagesWithoutProvenance.allow";

    /// <summary>
    /// A CALL to <c>WithDefaultArea</c> — the leading dot is load-bearing, because
    /// <c>LayoutDefinition.WithDefaultArea(string area)</c> is a DECLARATION and matching it would
    /// put the parameter name <c>area</c> into the landing-area set and flag half the repo.
    /// </summary>
    private static readonly Regex DefaultAreaCall = new(
        @"\.\s*WithDefaultArea\s*\(", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>A renderer registered WITHOUT a verdict. <c>WithNodePage</c> is deliberately NOT in
    /// this alternation — it is the shape that records one. Both other spellings are, and every
    /// <c>WithView(area, …)</c> overload funnels into <c>WithNamedRenderer</c>.</summary>
    private static readonly Regex PlainViewCall = new(
        @"\.\s*(?:WithView|WithNamedRenderer|WithDecoratedView)\s*(?:<[^>]*>)?\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The area expression reduced to its last identifier: <c>MeshNodeLayoutAreas.OverviewArea</c>
    /// and <c>OverviewArea</c> are the same area named from two files, and a scanner that treated
    /// them as different names would miss every cross-file registration — which is most of them
    /// (<c>UserNodeType</c> names the default area that <c>UserActivityLayoutAreas</c> registers).
    /// </summary>
    private static string? AreaToken(string expression)
    {
        var token = expression.Trim().Split('.').LastOrDefault()?.Trim();
        return token is not null && Regex.IsMatch(token, @"^[A-Za-z_][A-Za-z0-9_]*$") ? token : null;
    }

    [Fact]
    public void EveryLandingPageInSrc_DeclaresWhatItDoesAboutProvenance()
    {
        var root = SourceScan.FindRepoRoot();

        // 🚨 A guard must never pass on no evidence — SourceScan.SourceFiles throws on an empty
        // scan, but a root that quietly stopped existing would still narrow the population.
        foreach (var scanned in ScannedRoots)
            Assert.True(Directory.Exists(Path.Combine(root, scanned)),
                $"Scanned root '{scanned}' does not exist — this guard would scan a smaller tree "
                + "and pass. Update ScannedRoots to match the tree; never delete the root.");

        var files = SourceScan.SourceFiles(root, ScannedRoots).ToList();
        var masked = files.ToDictionary(f => f, f => SourceScan.MaskCommentsAndStrings(File.ReadAllText(f)));

        // Pass 1 — which area names are somebody's LANDING page.
        var landingAreas = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, code) in masked)
            foreach (Match m in DefaultAreaCall.Matches(code))
                if (AreaToken(SourceScan.FirstArgument(code, code.IndexOf('(', m.Index))) is { } token)
                    landingAreas.Add(token);

        // 🚨 The denominator, asserted. Zero landing areas means the scan found no
        // WithDefaultArea call anywhere in src/ — which cannot be true while the framework
        // registers one, so it is a broken scan reporting as a clean tree.
        Assert.True(landingAreas.Count >= 4,
            $"Only {landingAreas.Count} landing-area name(s) found across src/ "
            + $"[{string.Join(", ", landingAreas.OrderBy(a => a, StringComparer.Ordinal))}]. "
            + "src/ declared 4 (Overview, Activity, Search, GlobalSettings) when this floor was "
            + "set; fewer means the scanner stopped recognising WithDefaultArea, and everything "
            + "below it would then measure nothing.");

        // Pass 2 — landing-area renderers registered without a verdict.
        var offenders = new List<string>();
        foreach (var (file, code) in masked)
        {
            var relative = SourceScan.Relative(root, file);
            foreach (Match m in PlainViewCall.Matches(code))
            {
                var open = code.IndexOf('(', m.Index);
                if (open < 0)
                    continue;
                var token = AreaToken(SourceScan.FirstArgument(code, open));
                if (token is null || !landingAreas.Contains(token))
                    continue;
                offenders.Add($"{relative}:{LineOf(code, m.Index)} — {token}");
            }
        }

        var allowed = SourceScan.ReadAllowFile(
            Path.Combine(root, "test", AllowFileName), AllowFileName);

        var byFile = offenders
            .GroupBy(o => o[..o.IndexOf(':')], StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var failures = new List<string>();
        foreach (var (file, sites) in byFile.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var budget = allowed.GetValueOrDefault(file, 0);
            if (sites.Count > budget)
                failures.Add(
                    $"{file}: {sites.Count} landing-area registration(s) without a provenance "
                    + $"verdict, {budget} allowed" + Environment.NewLine
                    + string.Join(Environment.NewLine, sites.Select(s => "      · " + s)));
        }

        // 🚨 A ratchet that only ever grows is a list, not a ratchet: a stale entry has to red so
        // the reason it carries is re-read when it stops being true.
        foreach (var (file, budget) in allowed.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var actual = byFile.GetValueOrDefault(file)?.Count ?? 0;
            if (actual < budget)
                failures.Add(
                    $"{file}: allows {budget} but only {actual} remain — lower the entry (or delete "
                    + $"it at 0). The reason written beside it in {AllowFileName} no longer "
                    + "describes the tree.");
        }

        Assert.True(failures.Count == 0,
            "A node LANDING PAGE was registered with a bare WithView/WithNamedRenderer, so it "
            + "records no provenance verdict and — unless it happens to call BuildHeader itself — "
            + "ships without the Type · Created · Updated line, invisibly (#4500)."
            + Environment.NewLine
            + "  Fix: register it with MeshNodeLayoutAreas.WithNodePage(area, page). With no "
            + "second argument the framework composes the line for you; pass "
            + "NodePageProvenance.RenderedByThePage if the page already calls BuildHeader, or "
            + "NodePageProvenance.Declined(\"<why>\") if it should carry none."
            + Environment.NewLine
            + $"  Or, when the area is a TAB on this hub rather than its landing page, add a "
            + $"reasoned entry to test/{AllowFileName}."
            + Environment.NewLine
            + string.Join(Environment.NewLine, failures.Select(f => "  · " + f)));
    }

    /// <summary>
    /// 🚨 The negative control for the DETECTOR. Everything above runs against a tree that is
    /// currently clean, so a regex that silently stopped matching would leave this guard green
    /// forever while enforcing nothing — the exact shape AGENTS.md calls "a verification step that
    /// cannot fail". This plants both halves of the distinction and asserts the detector separates
    /// them.
    /// </summary>
    [Fact]
    public void TheDetector_SeparatesABareRegistrationFromAVerdictBearingOne()
    {
        const string Planted = """
            layout
                .WithDefaultArea(OverviewArea)
                .WithView(OverviewArea, SomePage)
                .WithNodePage(MeshNodeLayoutAreas.OverviewArea, AnotherPage)
                .WithView(EditArea, TheEditor);
            """;

        var code = SourceScan.MaskCommentsAndStrings(Planted);

        var landingAreas = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in DefaultAreaCall.Matches(code))
            if (AreaToken(SourceScan.FirstArgument(code, code.IndexOf('(', m.Index))) is { } t)
                landingAreas.Add(t);

        Assert.Single(landingAreas);
        Assert.Contains("OverviewArea", landingAreas);

        var flagged = PlainViewCall.Matches(code)
            .Select(m => AreaToken(SourceScan.FirstArgument(code, code.IndexOf('(', m.Index))))
            .Where(t => t is not null && landingAreas.Contains(t))
            .ToList();

        // Exactly ONE: the bare WithView of the landing area. Not the WithNodePage registration of
        // the SAME area (it records a verdict), and not the WithView of a non-landing area.
        Assert.Single(flagged);
        Assert.Equal("OverviewArea", flagged[0]);
    }

    private static int LineOf(string code, int index) =>
        code.Take(index).Count(c => c == '\n') + 1;
}
