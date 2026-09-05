#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 Resolution is EXPLICIT off the viewer, never off an ambient culture — and until now that rule
/// was asserted by four documents and measured by nothing.
///
/// <para><b>Why the ban is not pedantry.</b> On Blazor Server <c>CultureInfo.CurrentUICulture</c> is
/// the CONTAINER's culture: one value shared by every viewer being served at that moment. A date
/// formatted through it comes out in whichever language the pod happens to be set to, so the same
/// message reads <i>"vor 2 Stunden"</i> to an English reader and <i>"2 hours ago"</i> to a German
/// one depending on which replica answered. That is worse than untranslated — it is
/// nondeterministic, and it cannot be reproduced from the request.</para>
///
/// <para><b>The shape that actually ships is the IMPLICIT one.</b> Nobody writes
/// <c>CurrentUICulture</c> on purpose; they write <c>timestamp.Humanize()</c> and
/// <c>value.ToString("N2")</c>, which reach for it silently. Every instance found in the fleet
/// during Systemorph/MeshWeaver#3203 was of that form — a culture-less <c>Humanize()</c> — which is
/// why this guard bans the CALL SHAPE rather than the symbol.</para>
///
/// <para>🚨 This is a RATCHET AT ZERO, seeded with nothing: <c>src/</c> has no offender today, so
/// there is no allow file and no debt to grandfather. The correct fix is never to add an entry
/// here — it is to state the culture (<c>Humanize(culture: …)</c> derived from
/// <c>AccessContext.Locale</c> / <c>host.ViewerLocale()</c>), or the invariant one where the value
/// sits inside deliberately unlocalized text.</para>
/// </summary>
public class AmbientCultureRatchetGuard
{
    private static readonly string[] ScannedRoots = ["src"];

    /// <summary>
    /// The ambient-culture symbols, as EXPRESSIONS. Comments are masked before matching, because
    /// several of these files explain the ban in their remarks and naming a defect is the opposite
    /// of committing it.
    /// </summary>
    private static readonly Regex AmbientSymbol = new(
        @"\b(?:CultureInfo\s*\.\s*Current(?:UI)?Culture"
        + @"|CurrentThread\s*\.\s*Current(?:UI)?Culture)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The implicit form: <c>Humanize()</c> with NO arguments, which resolves through
    /// <c>CultureInfo.CurrentUICulture</c>. <c>Humanize(culture: …)</c> and any other argument are
    /// fine — this matches only the empty parameter list.
    /// </summary>
    private static readonly Regex CultureLessHumanize = new(
        @"\.\s*Humanize\s*\(\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void NoSourceFile_ReachesForAnAmbientCulture()
    {
        var root = SourceScan.FindRepoRoot();

        // 🚨 A guard must never pass on no evidence. SourceFiles() silently drops a root that does
        // not exist, so a rename here would turn this into a green check that scanned nothing —
        // the skip-trapdoor shape AGENTS.md bans for CI gates.
        foreach (var scanned in ScannedRoots)
            Assert.True(Directory.Exists(Path.Combine(root, scanned)),
                $"Scanned root '{scanned}' does not exist — this guard would scan nothing and pass. "
                + "Update ScannedRoots to match the tree; never delete the root to make it green.");

        var files = SourceScan.SourceFiles(root, ScannedRoots).ToList();
        Assert.True(files.Count > 800,
            $"Only {files.Count} files were scanned across src/ — too few to be the real tree, so a "
            + "pass here would mean nothing. src/ held 1258 .cs files when this floor was set.");

        var offenders = new List<string>();
        foreach (var file in files)
        {
            var code = SourceScan.MaskCommentsAndStrings(File.ReadAllText(file));
            foreach (Match m in AmbientSymbol.Matches(code))
                offenders.Add($"{SourceScan.Relative(root, file)}:{LineOf(code, m.Index)} — {m.Value}");
            foreach (Match m in CultureLessHumanize.Matches(code))
                offenders.Add($"{SourceScan.Relative(root, file)}:{LineOf(code, m.Index)} — culture-less Humanize()");
        }

        Assert.True(offenders.Count == 0,
            "Ambient-culture resolution in src/. On Blazor Server the ambient culture is the "
            + "CONTAINER's — one value shared by every simultaneous viewer — so this renders a date "
            + "or number in whichever language the pod is set to, not the reader's. State the "
            + "culture explicitly off the viewer (AccessContext.Locale / host.ViewerLocale()), or "
            + "CultureInfo.InvariantCulture where the value sits inside deliberately unlocalized "
            + "text. There is no allow file: src/ was at ZERO when this guard landed."
            + Environment.NewLine
            + string.Join(Environment.NewLine, offenders.Select(o => "  · " + o)));
    }

    private static int LineOf(string code, int index) =>
        code.Take(index).Count(c => c == '\n') + 1;
}
