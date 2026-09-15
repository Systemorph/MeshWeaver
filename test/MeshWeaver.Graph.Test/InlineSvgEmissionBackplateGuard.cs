using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 EVERY PLACE THIS REPO DECIDES "this value is inline &lt;svg&gt;" AND THEN RENDERS IT must put
/// it through the backplate policy (<see cref="IconBackplate.Ensure"/>) — directly, or via the two
/// seams that call it (<see cref="MeshNodeImageHelper.ResolveRenderable"/>,
/// <see cref="MeshNodeImageHelper.SizeInlineSvg"/>).
///
/// <para><b>What went wrong (#4350).</b> <c>IconBackplate</c> documented itself as running at "the
/// ONE seam every surface classifies through, so a currentColor outline or a dark pictorial can
/// never render invisibly on one theme". It ran at ONE of them. Six other places in this repo
/// classified an icon as inline svg and then emitted the authored markup as it stood: the node
/// page's icon tile and its content self-reference (<c>MeshNodeLayoutAreas</c>), the overview title
/// icon (<c>OverviewLayoutArea</c>), the create form's icon preview (<c>CreateLayoutArea</c>), the
/// icon picker's preview tile (<c>NodeIconPickerDialog</c>), and the page-head favicon plus the
/// <c>/api/icon/*.png</c> rasterization behind it (<c>SeoResolver</c>). An ordinary house icon —
/// <c>stroke="currentColor"</c>, no plate of its own — took the surrounding text color and was
/// invisible on one of the two themes; measured 2026-09-14 on ~20 nodes of one Space. The claim was
/// enforced by nothing, which is why it could be false for months without a single failing
/// test.</para>
///
/// <para><b>Why a source scan.</b> A guard written as a list of known icon surfaces would have
/// listed the one surface that was already correct — that is precisely the mistake the issue
/// documents. So the subject is derived from the SOURCES: every inline-svg classification under
/// <see cref="ScannedRoots"/>, found the same way a reader would find them. The behaviour of each
/// seam is pinned separately (<c>InlineSvgRenderPathTest</c>, <c>IconBackplateTest</c>); this says
/// only — and exactly — that no surface goes around them.</para>
///
/// <para><b>What it does NOT prove:</b> that a plate is legible, or that <c>Ensure</c> is right.
/// And it sees this repo only: the Blazor view components live in MeshWeaver.Plugins and carry
/// their own guard (<c>InlineSvgBackplateGuard</c>, MeshWeaver.Plugins#1880).</para>
/// </summary>
public class InlineSvgEmissionBackplateGuard
{
    /// <summary>Repo-root directories holding code that renders. Immutable, written once — the
    /// collections policy's sanctioned <c>static readonly</c>.</summary>
    private static readonly ImmutableArray<string> ScannedRoots = ["src", "memex", "samples"];

    private static readonly ImmutableArray<string> ExcludedSegments =
        ["bin", "obj", "node_modules", "TestResults", ".git", "dist"];

    /// <summary>
    /// What may stand between a classified icon and the markup. The first four PLATE: <c>Ensure</c>
    /// is the policy itself, and <c>ResolveRenderable</c> / the two <c>SizeInlineSvg</c> overloads /
    /// <c>IconLinkFor</c> call it on the way through (each pinned by its own test in
    /// <c>InlineSvgRenderPathTest</c>, so naming them here is not taking their word for it). The
    /// last two ESCAPE, which is the other honest answer: an html-escaped value reaches the page as
    /// text and cannot be an svg element at all.
    /// </summary>
    private static readonly ImmutableArray<string> SafeWrappers =
    [
        "IconBackplate.Ensure(", "ResolveRenderable(", "SizeInlineSvg(", "IconLinkFor(",
        "HtmlEncode(", "HtmlAttributeEncode(",
    ];

    /// <summary>
    /// The classification itself: the two ways this repo asks "is this value inline svg?", each
    /// capturing the value being classified so its later uses can be followed.
    /// </summary>
    private static readonly Regex Classification = new(
        """(?:(?<value>[A-Za-z_]\w*)(?:\.\w+\([^()]*\))*\.StartsWith\(\s*"<svg")|(?:IsInlineSvg\(\s*(?<value2>[A-Za-z_]\w*)\s*\))""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// A classification that hands the value on UNPLATED on purpose, with the reason. Two only, and
    /// both are the same reason in different clothes: they carry a VALUE onwards rather than render
    /// it, and plating a value would write the generated plate into somewhere it must not go — a
    /// stored node, or a control property another repo's view plates at its own emission seam.
    ///
    /// <para>🚨 An entry that no longer names a live classification FAILS
    /// (<see cref="EveryDeclaredValueCarrierStillNamesALiveClassification"/>), so this list cannot
    /// outlive its subjects and quietly exempt something else that moved into the same file.</para>
    /// </summary>
    private static readonly ImmutableArray<(string File, string Value, string Reason)> ValueCarriers =
    [
        ("MeshNodeThumbnailControl.cs", "thumbnail",
            "carries the content's thumbnail/avatar/logo VALUE into MeshNodeThumbnailControl.ImageUrl; "
            + "the view packs that render it plate at their own emission seam (MeshWeaver.Plugins#1880), "
            + "and plating here would put generated markup into a property that is also compared and persisted"),
        ("MarkdownFileParser.cs", "iconValue",
            "is the PERSISTENCE side — front matter into MeshNode.Icon on import. Plating here would "
            + "rewrite the author's icon in the store rather than in the rendering of it"),
    ];

    /// <summary>One classification found in the sources, and what the branch does with the value.</summary>
    private sealed record Classified(string File, int Line, string Value, string Region);

    // ── The invariant ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryInlineSvgClassificationThatRenders_GoesThroughTheBackplate()
    {
        var offenders = Scan()
            .Where(c => !IsDeclaredValueCarrier(c))
            .SelectMany(c => UnplatedUses(c).Select(use => $"  {c.File}:{c.Line} renders `{c.Value}` {use}"))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        // The offenders go into the message, not just their count: a guard whose failure says
        // "1 item(s)" sends the next reader hunting for which one.
        Assert.True(offenders.Count == 0,
            "An inline-svg icon rendered without IconBackplate.Ensure takes the surrounding text "
            + "color and is invisible on one of the two themes wherever it sits on a card, a tile, a "
            + "chip or a browser tab (#4350). Route it through MeshNodeImageHelper.SizeInlineSvg "
            + "(raw-HTML surfaces), MeshNodeImageHelper.ResolveRenderable (a classified icon) or "
            + "IconBackplate.Ensure. If the value is genuinely being CARRIED rather than rendered, "
            + "declare it in ValueCarriers with the reason. Rendering unplated:\n"
            + string.Join("\n", offenders));
    }

    // ── The guard's own subject: neither of these can pass on an empty scan ───────────────────

    /// <summary>
    /// 🚨 The non-vacuity check. Every file that classifies inline svg today is named here, so a
    /// rename, a project move or a broken scan turns into a RED that says what it stopped seeing —
    /// not into a green over an empty list. These are all of them: six that were bypassing when
    /// #4350 was filed, two value carriers, and the policy/parse primitives themselves.
    /// </summary>
    [Fact]
    public void TheScanFindsEveryKnownClassifier_SoAPassIsNotVacuous()
    {
        var files = Scan().Select(c => c.File).Distinct().OrderBy(f => f, StringComparer.Ordinal).ToList();

        Assert.Contains("CreateLayoutArea.cs", files);
        Assert.Contains("MeshNodeLayoutAreas.cs", files);
        Assert.Contains("NodeIconPickerDialog.cs", files);
        Assert.Contains("OverviewLayoutArea.cs", files);
        Assert.Contains("SeoResolver.cs", files);
        Assert.Contains("MeshNodeImageHelper.cs", files);
        Assert.Contains("IconBackplate.cs", files);
        Assert.Contains("Icon.cs", files);
        Assert.True(files.Count >= 8,
            "the scan found only " + files.Count + " classifying file(s) — it has stopped seeing the "
            + "sources it guards. Found: " + string.Join(", ", files));
    }

    /// <summary>A declared exemption whose subject is gone permits nothing and hides nothing —
    /// it just reads, forever, as sanctioned debt that was in fact paid.</summary>
    [Fact]
    public void EveryDeclaredValueCarrierStillNamesALiveClassification()
    {
        var found = Scan();
        var stale = ValueCarriers
            .Where(carrier => !found.Any(c => c.File == carrier.File && c.Value == carrier.Value))
            .Select(carrier => $"  {carrier.File} [{carrier.Value}]")
            .ToList();

        Assert.True(stale.Count == 0,
            "A ValueCarriers entry no longer names a live inline-svg classification. Either the site "
            + "moved (repoint the entry) or it is gone (delete it) — left as it is, it exempts "
            + "whatever next classifies under the same name in that file:\n" + string.Join("\n", stale));
    }

    // ── The negative control: the detector is PROVEN able to fail ─────────────────────────────

    /// <summary>
    /// 🚨 A guard that cannot fail is not a guard. This runs the same detection over a source
    /// sample written here, so the test that matters is measured against a case where the answer is
    /// known — including the three shapes that MUST NOT be flagged (a plated emission, an icon in
    /// an <c>&lt;img src&gt;</c> attribute — which is a URL, never markup — and a plated classification
    /// whose value is handed on).
    /// </summary>
    [Theory]
    // The exact shape every bypassing surface had.
    [InlineData("""if (icon.TrimStart().StartsWith("<svg", StringComparison.Ordinal)) return Controls.Html($"<div>{icon}</div>");""", true)]
    [InlineData("""var html = raw.StartsWith("<svg", StringComparison.Ordinal) ? $"<span>{raw}</span>" : "";""", true)]
    [InlineData("""if (MeshNodeImageHelper.IsInlineSvg(value)) return value;""", true)]
    // …and the shapes that are correct.
    [InlineData("""if (icon.TrimStart().StartsWith("<svg", StringComparison.Ordinal)) return Controls.Html($"<div>{MeshNodeImageHelper.SizeInlineSvg(icon, 48)}</div>");""", false)]
    [InlineData("""if (MeshNodeImageHelper.IsInlineSvg(icon)) return IconBackplate.Ensure(icon);""", false)]
    [InlineData("""var html = url.StartsWith("<svg", StringComparison.Ordinal) ? $"<div>{IconBackplate.Ensure(url)}</div>" : $"<img src=\"{url}\" alt=\"\" />";""", false)]
    public void TheDetectorFlagsABypass_AndOnlyABypass(string source, bool expectedBypass)
    {
        var classified = Classify("Sample.cs", source).ToList();
        Assert.NotEmpty(classified); // the sample must be recognised as a classification at all
        Assert.Equal(expectedBypass, classified.Any(c => UnplatedUses(c).Any()));
    }

    // ── Detection ─────────────────────────────────────────────────────────────────────────────

    private static bool IsDeclaredValueCarrier(Classified c) =>
        ValueCarriers.Any(carrier => carrier.File == c.File && carrier.Value == c.Value);

    private static ImmutableArray<Classified> Scan()
    {
        var root = FindRepoRoot();
        var found = ScannedRoots
            .Select(r => Path.Combine(root, r))
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            .Where(f => !IsExcluded(root, f))
            .SelectMany(f => Classify(Path.GetFileName(f), File.ReadAllText(f)))
            .ToImmutableArray();

        Assert.True(found.Length > 0,
            $"No inline-svg classification was found under {string.Join(", ", ScannedRoots)} of {root}. "
            + "The scan is looking at the wrong tree, or the sources moved — either way it is "
            + "verifying nothing, which is the one thing a guard must never do quietly.");
        return found;
    }

    /// <summary>Every inline-svg classification in one source text, paired with the code that runs
    /// on the strength of it (the enclosing branch or statement).</summary>
    private static IEnumerable<Classified> Classify(string file, string text)
    {
        foreach (Match m in Classification.Matches(text))
        {
            var value = m.Groups["value"].Success ? m.Groups["value"].Value : m.Groups["value2"].Value;
            yield return new Classified(file, LineOf(text, m.Index), value, RegionFrom(text, m.Index + m.Length));
        }
    }

    /// <summary>
    /// The uses of the classified value, inside its own branch, that put it in front of a user
    /// without plating it: interpolated into element CONTENT of markup, returned, or produced as a
    /// ternary result. An interpolation inside an ATTRIBUTE value is excluded — an
    /// <c>&lt;img src="…"&gt;</c> carries a URL, and markup there is a different defect
    /// (<see cref="MeshNodeImageHelper.ResolveRenderable"/>'s broken-image case), not this one.
    /// </summary>
    private static IEnumerable<string> UnplatedUses(Classified c)
    {
        var v = Regex.Escape(c.Value);
        foreach (Match hole in Regex.Matches(c.Region, @"\{[^{}]*\b" + v + @"\b[^{}]*\}"))
            if (IsInMarkupElementContent(c.Region, hole.Index)
                && !SafeWrappers.Any(w => hole.Value.Contains(w, StringComparison.Ordinal)))
                yield return $"into markup as `{hole.Value}`";
        if (Regex.IsMatch(c.Region, @"\breturn\s+" + v + @"\s*;"))
            yield return "verbatim as its result";
        if (Regex.IsMatch(c.Region, @"\?\s*" + v + @"\s*:") || Regex.IsMatch(c.Region, @":\s*" + v + @"\s*[;,)]"))
            yield return "verbatim as a ternary result";
    }

    /// <summary>
    /// Whether the interpolation at <paramref name="index"/> sits in element content rather than
    /// inside a quoted attribute value: counting the quote marks since the last <c>&lt;</c> answers
    /// it — an odd number means the tag is still inside an attribute's quotes.
    /// </summary>
    private static bool IsInMarkupElementContent(string region, int index)
    {
        var openTag = region.LastIndexOf('<', Math.Max(0, index - 1));
        if (openTag < 0)
            return false; // no markup around it at all — not an emission
        var between = region[openTag..index];
        var quotes = between.Count(ch => ch == '"');
        return quotes % 2 == 0;
    }

    /// <summary>
    /// The code that runs on the strength of a classification: the enclosing braced block when one
    /// opens, otherwise the rest of the statement. String literals and comments are stepped over,
    /// so a <c>;</c> or a <c>{</c> inside markup cannot end the region early — which it did, on the
    /// first draft of this scan, hiding the very emission it was written to find.
    /// </summary>
    private static string RegionFrom(string text, int start)
    {
        var parens = 0;
        for (var i = start; i < text.Length; i++)
        {
            if (TrySkipInert(text, ref i))
                continue;
            switch (text[i])
            {
                case '(': parens++; break;
                case ')': parens--; break;
                case '{' when parens <= 0:
                    return text[start..EndOfBlock(text, i)];
                case ';' when parens <= 0:
                    return text[start..(i + 1)];
            }
        }
        return text[start..];
    }

    /// <summary>The index just past the <c>}</c> matching the <c>{</c> at <paramref name="open"/>.</summary>
    private static int EndOfBlock(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (TrySkipInert(text, ref i))
                continue;
            if (text[i] == '{')
                depth++;
            else if (text[i] == '}' && --depth == 0)
                return i + 1;
        }
        return text.Length;
    }

    /// <summary>
    /// Steps <paramref name="i"/> past a string literal, char literal or comment starting there and
    /// reports that it did; leaves it alone otherwise. Verbatim (<c>@"…""…"</c>) and ordinary
    /// (<c>"…\"…"</c>) escaping are both handled.
    /// </summary>
    private static bool TrySkipInert(string text, ref int i)
    {
        if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
        {
            var nl = text.IndexOf('\n', i);
            i = nl < 0 ? text.Length - 1 : nl;
            return true;
        }
        if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
        {
            var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
            i = close < 0 ? text.Length - 1 : close + 1;
            return true;
        }
        if (text[i] == '\'')
        {
            var j = i + 1;
            while (j < text.Length && text[j] != '\'')
                j += text[j] == '\\' ? 2 : 1;
            i = Math.Min(j, text.Length - 1);
            return true;
        }
        if (text[i] != '"')
            return false;

        var verbatim = i > 0 && (text[i - 1] == '@' || (i > 1 && text[i - 1] == '$' && text[i - 2] == '@'));
        var k = i + 1;
        while (k < text.Length)
        {
            if (verbatim)
            {
                if (text[k] == '"')
                {
                    if (k + 1 < text.Length && text[k + 1] == '"') { k += 2; continue; }
                    break;
                }
            }
            else
            {
                if (text[k] == '\\') { k += 2; continue; }
                if (text[k] == '"') break;
            }
            k++;
        }
        i = Math.Min(k, text.Length - 1);
        return true;
    }

    private static int LineOf(string text, int index) => text[..index].Count(ch => ch == '\n') + 1;

    private static bool IsExcluded(string root, string path) =>
        Path.GetRelativePath(root, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => ExcludedSegments.Contains(segment, StringComparer.OrdinalIgnoreCase));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        return dir?.FullName
               ?? throw new InvalidOperationException(
                   "Could not locate the repo root (MeshWeaver.slnx) from " + AppContext.BaseDirectory);
    }
}
