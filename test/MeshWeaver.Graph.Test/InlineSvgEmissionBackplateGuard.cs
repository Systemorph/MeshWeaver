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
        ("src/MeshWeaver.Graph/MeshNodeThumbnailControl.cs", "thumbnail",
            "carries the content's thumbnail/avatar/logo VALUE into MeshNodeThumbnailControl.ImageUrl; "
            + "the view packs that render it plate at their own emission seam (MeshWeaver.Plugins#1880), "
            + "and plating here would put generated markup into a property that is also compared and persisted"),
        ("src/MeshWeaver.Hosting/Persistence/Parsers/MarkdownFileParser.cs", "iconValue",
            "is the PERSISTENCE side — front matter into MeshNode.Icon on import. Plating here would "
            + "rewrite the author's icon in the store rather than in the rendering of it"),
    ];

    /// <summary>
    /// One classification found in the sources, and what the branch does with the value.
    /// <paramref name="File"/> is the REPO-RELATIVE path, in <c>/</c> form: matching an exemption on
    /// a bare file name would silently exempt an unrelated same-named file in another root.
    /// </summary>
    private sealed record Classified(string File, int Line, string Value, string Region);

    // ── The invariant ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryInlineSvgClassificationThatRenders_GoesThroughTheBackplate()
    {
        var offenders = Scan()
            .Where(c => !IsDeclaredValueCarrier(c))
            .SelectMany(c => UnplatedUses(c).Select(use => $"  {c.File}:{c.Line} renders `{c.Value}` {use}"))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToImmutableArray();

        // The offenders go into the message, not just their count: a guard whose failure says
        // "1 item(s)" sends the next reader hunting for which one.
        Assert.True(offenders.Length == 0,
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
        var files = Scan().Select(c => c.File).Distinct().OrderBy(f => f, StringComparer.Ordinal).ToImmutableArray();

        string[] expected =
        [
            "src/MeshWeaver.Graph/CreateLayoutArea.cs",
            "src/MeshWeaver.Graph/MeshNodeLayoutAreas.cs",
            "src/MeshWeaver.Graph/NodeIconPickerDialog.cs",
            "src/MeshWeaver.Graph/OverviewLayoutArea.cs",
            "src/MeshWeaver.Graph/MeshNodeImageHelper.cs",
            "src/MeshWeaver.Graph/MeshNodeThumbnailControl.cs",
            "src/MeshWeaver.Graph/IconBackplate.cs",
            "src/MeshWeaver.Domain/Icon.cs",
            "src/MeshWeaver.Hosting/Persistence/Parsers/MarkdownFileParser.cs",
            "memex/Memex.Portal.Shared/Seo/SeoResolver.cs",
        ];
        var missing = expected.Where(e => !files.Contains(e)).ToImmutableArray();

        Assert.True(missing.Length == 0,
            "the scan stopped seeing files it guards — a rename, a project move, or a broken scan. "
            + "Missing: " + string.Join(", ", missing) + ". Found: " + string.Join(", ", files));
    }

    /// <summary>
    /// 🚨 THE DETECTOR'S OWN BLIND SPOT, held still. It reads two spellings of the question "is this
    /// inline svg?" — <c>StartsWith("&lt;svg"</c> and <c>IsInlineSvg(…)</c> — because those are the
    /// two every surface in this repo uses. There is a THIRD: the typed
    /// <c>DomainIcon.InlineSvgProvider</c> / <see cref="IconRenderKind.InlineSvg"/> classification,
    /// which the detector cannot follow (its value is a pattern-bound name inside a switch arm, not
    /// a local whose later uses can be traced).
    ///
    /// <para>Rather than claim a coverage the scan does not have — which is the exact failure this
    /// whole change is about — the third spelling is CONFINED: it may appear only in the files
    /// below, all of which plate or exist to define the policy. A new file using it fails here, and
    /// the author has to say how it is plated. Extending the detector instead would be better; this
    /// keeps the claim honest until someone does.</para>
    /// </summary>
    [Fact]
    public void TheTypedClassification_StaysInTheFilesThatPlateIt()
    {
        var root = FindRepoRoot();
        string[] mayUseIt =
        [
            "src/MeshWeaver.Domain/Icon.cs",              // defines the provider constant and parses to it
            "src/MeshWeaver.Graph/MeshNodeImageHelper.cs", // ResolveRenderable/IconLinkFor — both plate
        ];

        var users = ScannedRoots
            .Select(r => Path.Combine(root, r))
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            .Where(f => !IsExcluded(root, f))
            .Where(f => File.ReadAllText(f).Contains("InlineSvgProvider", StringComparison.Ordinal)
                        || File.ReadAllText(f).Contains("IconRenderKind.InlineSvg", StringComparison.Ordinal))
            .Select(f => RelativePath(root, f))
            .Where(f => !mayUseIt.Contains(f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToImmutableArray();

        Assert.True(users.Length == 0,
            "a file outside the two that plate now classifies icons through the TYPED inline-svg "
            + "provider, which this guard's source scan cannot follow. Either plate it through "
            + "MeshNodeImageHelper.ResolveRenderable / IconBackplate.Ensure and add it to mayUseIt "
            + "with the reason, or teach the detector that spelling: "
            + string.Join(", ", users));
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
            .ToImmutableArray();

        Assert.True(stale.Length == 0,
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
    // Straight into a raw-html consumer, with no interpolation for the scan to see.
    [InlineData("""if (MeshNodeImageHelper.IsInlineSvg(icon)) return Controls.Html(icon);""", true)]
    // …and the shapes that are correct.
    [InlineData("""if (icon.TrimStart().StartsWith("<svg", StringComparison.Ordinal)) return Controls.Html($"<div>{MeshNodeImageHelper.SizeInlineSvg(icon, 48)}</div>");""", false)]
    [InlineData("""if (MeshNodeImageHelper.IsInlineSvg(icon)) return IconBackplate.Ensure(icon);""", false)]
    [InlineData("""if (MeshNodeImageHelper.IsInlineSvg(icon)) return Controls.Html(IconBackplate.Ensure(icon));""", false)]
    [InlineData("""var html = url.StartsWith("<svg", StringComparison.Ordinal) ? $"<div>{IconBackplate.Ensure(url)}</div>" : $"<img src=\"{url}\" alt=\"\" />";""", false)]
    public void TheDetectorFlagsABypass_AndOnlyABypass(string source, bool expectedBypass)
    {
        var classified = Classify("src/Sample.cs", source).ToImmutableArray();
        Assert.NotEmpty(classified); // the sample must be recognised as a classification at all
        Assert.Equal(expectedBypass, classified.Any(c => UnplatedUses(c).Any()));
    }

    // ── Detection ─────────────────────────────────────────────────────────────────────────────

    private static bool IsDeclaredValueCarrier(Classified c) =>
        ValueCarriers.Any(carrier => carrier.File == c.File && carrier.Value == c.Value);

    private static ImmutableArray<Classified> Scan()
    {
        var root = FindRepoRoot();

        // 🚨 Every scanned root must EXIST. Skipping a missing one would leave the guard green over
        // a tree it never opened — the same shape as a CI gate whose input step is allowed to fail.
        foreach (var scanned in ScannedRoots)
            Assert.True(Directory.Exists(Path.Combine(root, scanned)),
                $"The scanned root '{scanned}' is absent from {root}. This guard verifies nothing "
                + "about a tree it cannot find; if the root moved, repoint ScannedRoots.");

        var found = ScannedRoots
            .Select(r => Path.Combine(root, r))
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            .Where(f => !IsExcluded(root, f))
            .SelectMany(f => Classify(RelativePath(root, f), File.ReadAllText(f)))
            .ToImmutableArray();

        Assert.True(found.Length > 0,
            $"No inline-svg classification was found under {string.Join(", ", ScannedRoots)} of {root}. "
            + "The scan is looking at the wrong tree, or the sources moved — either way it is "
            + "verifying nothing, which is the one thing a guard must never do quietly.");
        return found;
    }

    /// <summary>The path relative to the repo root, always <c>/</c>-separated so an exemption reads
    /// the same on every platform.</summary>
    private static string RelativePath(string root, string file) =>
        Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');

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
        // Handed whole to something that puts a string on the page as MARKUP. Without this, a
        // surface could take the raw-html route with no interpolation at all —
        // `return Controls.Html(icon);` — and the scan would see nothing.
        foreach (var consumer in RawHtmlConsumers)
            if (Regex.IsMatch(c.Region, Regex.Escape(consumer) + @"\s*\(\s*" + v + @"\s*[,)]"))
                yield return $"whole into `{consumer}(…)`";
        if (Regex.IsMatch(c.Region, @"\breturn\s+" + v + @"\s*;"))
            yield return "verbatim as its result";
        if (Regex.IsMatch(c.Region, @"\?\s*" + v + @"\s*:") || Regex.IsMatch(c.Region, @":\s*" + v + @"\s*[;,)]"))
            yield return "verbatim as a ternary result";
    }

    /// <summary>The calls that put a whole string on the page as markup rather than as text. A
    /// classified icon passed to one of these unwrapped is the same bypass as an interpolation, and
    /// it was invisible to the first version of this scan.</summary>
    private static readonly ImmutableArray<string> RawHtmlConsumers =
        ["Controls.Html", "new HtmlControl", "HtmlControl", "MarkupString"];

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
