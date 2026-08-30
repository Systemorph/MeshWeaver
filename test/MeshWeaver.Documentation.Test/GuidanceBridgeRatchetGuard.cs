using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Governance ratchet for GUIDANCE: a doc page, skill or review-instruction file may not gain a
/// copyable C# example that writes <c>.ToTask(</c>.
///
/// <para><b>Why guidance needs its own ratchet.</b> The maintainer ruling of 2026-08-30 —
/// <i>"totask is forbidden"</i>, <i>"no totask ever"</i> — retracted the last exemption, the one that
/// called tests "the only sanctioned place". A code scanner can hold <c>src/</c> and <c>test/</c> to
/// that. It cannot see a MARKDOWN file, and a markdown file that PRESCRIBES the shape generates new
/// violations faster than a sweep removes them: every agent and every reviewer reads it as the house
/// pattern, and the examples with the shortest path to production are the ones nothing compiles —
/// a <c>.csx</c> script body pasted into a mesh node, which compiles at RUNTIME in the portal.
/// <c>.github/copilot-instructions.md</c> is the sharpest case of all: until this change it told the
/// automatic reviewer that the bridge was "the accepted bridge" in <c>test/**</c>, so review actively
/// DEFENDED the banned shape on every PR.</para>
///
/// <para><b>The discriminator: a fenced code block whose language is C#.</b> Prose that names
/// <c>.ToTask()</c> is overwhelmingly a WARNING — "never do this", "this is the deadlock pattern",
/// the war stories that explain why the rule exists — and a ratchet that counted those would push
/// people to delete the institutional memory in order to go green, which is the opposite of the
/// point. A <c>```csharp</c> fence is different: it is text written to be copied. So only
/// <c>```csharp</c> / <c>```cs</c> / <c>```c#</c> fences are counted. Unlabelled fences and
/// <c>```text</c> / <c>```output</c> / <c>```bash</c> fences are NOT — this repo puts captured stack
/// traces and measurements in those, and one of them (the /async skill's red-flag list) names the
/// shape precisely because it is banned.</para>
///
/// <para><b>What the seeded inventory is.</b> Everything left after the 2026-08-30 guidance sweep is
/// an ❌ ANTI-PATTERN example — <c>AsynchronousCalls.md</c>'s "❌ Direct .ToTask() bridge then await",
/// <c>BlazorDataBinding.md</c>'s "Anti-Patterns to Delete on Sight", <c>InitializationGates.md</c>'s
/// "❌ DEADLOCK", the /async and /sigsegv skills' "❌ parks the grain turn". Those must NOT be
/// deleted: a rule stated without the shape it forbids is unrecognisable in review. Hence a seeded
/// ratchet rather than zero tolerance.</para>
///
/// <para><b>The ratchet may only SHRINK.</b> A new file, a raised count, or a raised TOTAL is a
/// failure. A line that has become stale is REPORTED, not failed — two PRs shrinking concurrently
/// would otherwise red <c>main</c> on whichever merged second, and a gate that punishes the direction
/// it is asking for teaches people to stop shrinking. Delete the stale line and lower
/// <see cref="TotalBudget"/> in the same change.</para>
/// </summary>
public class GuidanceBridgeRatchetGuard(ITestOutputHelper output)
{
    /// <summary>
    /// The seeded inventory's size. Per-file entries stop a new example in a file that already
    /// carries one; this stops the list as a WHOLE from growing — including by the trick of adding a
    /// new file's line. Lower it whenever you delete or lower an entry.
    /// </summary>
    private const int TotalBudget = 12;

    /// <summary>
    /// Where guidance lives: the embedded documentation tree, the agent skills, the GitHub review
    /// instructions and workflow prose, and the repo-root instruction files (AGENTS.md, CLAUDE.md).
    /// </summary>
    private static readonly string[] ScannedRoots =
        ["src/MeshWeaver.Documentation/Data", ".claude/skills", ".github"];

    /// <summary>The shape, as it appears in an example. Matched with the open paren so a sentence
    /// about "ToTask" in a comment-free code line still reads as a call.</summary>
    private const string Marker = ".ToTask(";

    /// <summary>Fence info strings that mean "this is C# you could paste".</summary>
    private static readonly string[] CSharpFenceLanguages = ["csharp", "cs", "c#"];

    private const string AllowFileName = "GuidanceBridgeSites.allow";

    [Fact]
    public void NoGuidanceFileTeachesTheForbiddenBridge()
    {
        var root = SourceScan.FindRepoRoot();
        var allowed = SourceScan.ReadAllowFile(Path.Combine(root, "test", AllowFileName), AllowFileName);
        var found = Scan(root);

        var failures = new List<string>();

        foreach (var (file, count) in found.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!allowed.TryGetValue(file, out var budget))
                failures.Add(
                    $"  NEW SITE   {file} ({count}) — a C# example in guidance now writes "
                    + $"{Marker}, which is forbidden repo-wide (2026-08-30, \"no ToTask ever\"). "
                    + "Write what the reader should copy instead: return IObservable<T> and "
                    + "Subscribe; or, where a foreign signature genuinely hands back a Task, "
                    + "`.FirstAsync().ObserveCompletion(reportLateFault, ct)`. Do NOT add a line to "
                    + AllowFileName + ".");
            else if (count > budget)
                failures.Add(
                    $"  MORE       {file} ({count} > {budget} allowed) — an example was ADDED to a "
                    + "file whose allowance covers only its existing ❌ anti-pattern blocks.");
        }

        var total = allowed.Values.Sum();
        if (total > TotalBudget)
            failures.Add(
                $"  TOTAL      {total} allowances > {TotalBudget} budgeted — the inventory GREW. "
                + "Adding a line to " + AllowFileName + " is not a fix.");

        foreach (var (file, budget) in allowed.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var count = found.GetValueOrDefault(file, 0);
            if (count < budget)
                output.WriteLine(
                    $"STALE (please tidy): {file} — {count} found, {budget} allowed. "
                    + $"{(count == 0 ? "Delete the line" : $"Lower it to {count}")} and lower "
                    + $"TotalBudget by {budget - count}.");
        }

        Assert.True(failures.Count == 0,
            "Guidance that PRESCRIBES the Rx→Task bridge generates new violations faster than a "
            + "sweep removes them, and no code scanner will ever see it — a `.csx` example is "
            + "pasted into a mesh node and compiles at RUNTIME in the portal. Prose that WARNS "
            + "against the shape is fine and is deliberately not counted; only a ```csharp fence "
            + "is.\n"
            + string.Join("\n", failures));
    }

    /// <summary>
    /// Non-vacuity, both directions, driven through the REAL counter: it must SEE the shape inside a
    /// C# fence and must NOT see it in prose or in a non-C# fence. Without this, a scanner that had
    /// silently stopped matching (a renamed marker, a fence parser that swallowed the file) would
    /// report every seeded entry as STALE — which this guard only prints — and the ratchet above
    /// would pass on no evidence.
    /// </summary>
    [Fact]
    public void TheScannerCountsCSharpFencesAndIgnoresEverythingElse()
    {
        const string prose =
            "Never write `.ToTask()` — it resumes the awaiter inline on the signalling thread.\n";
        Assert.Equal(0, CountSitesInMarkdown(prose));

        const string textFence =
            "Captured stack:\n\n```text\n  -> ToTaskObserver.OnCompleted   // a .ToTask() resolves\n```\n";
        Assert.Equal(0, CountSitesInMarkdown(textFence));

        const string unlabelledFence =
            "Red flags:\n\n```\n.ToTask() / .Result / .Wait()\n```\n";
        Assert.Equal(0, CountSitesInMarkdown(unlabelledFence));

        const string csharpFence =
            "Do not write this:\n\n```csharp\nvar n = await hub.GetMeshNode(p).ToTask(ct);\n```\n";
        Assert.Equal(1, CountSitesInMarkdown(csharpFence));

        const string shortAlias =
            "```cs\nvar a = await x.FirstAsync().ToTask();\nvar b = await y.FirstAsync().ToTask();\n```\n";
        Assert.Equal(2, CountSitesInMarkdown(shortAlias));

        // An indented fence inside a list item is still a fence, and its language still decides.
        const string indentedInsideList =
            "- For waits:\n\n  ```csharp\n  await s.FirstAsync().ToTask(Ct);\n  ```\n\n- Done.\n";
        Assert.Equal(1, CountSitesInMarkdown(indentedInsideList));

        // Everything at once, so a parser that leaks state across fences is caught too.
        Assert.Equal(4,
            CountSitesInMarkdown(prose + textFence + unlabelledFence + csharpFence + shortAlias
                                 + indentedInsideList));
    }

    /// <summary>
    /// The same non-vacuity check against the REAL tree, both directions: the seeded files must
    /// still be seen, and a file whose only mentions are prose must still be absent. Pinned on
    /// <c>.github/copilot-instructions.md</c> because that file is the whole reason this guard
    /// exists — it names the shape in order to tell the reviewer to flag it, and a scanner that
    /// counted that would ratchet against the fix itself.
    /// </summary>
    [Fact]
    public void TheScannerFindsTheShapeItIsRatchetingAndNotTheWarningsAgainstIt()
    {
        var root = SourceScan.FindRepoRoot();
        var found = Scan(root);

        Assert.True(found.Count > 0,
            "The scanner found no C# example writing " + Marker + " anywhere under "
            + string.Join(", ", ScannedRoots) + " or at the repo root. Either every ❌ anti-pattern "
            + "block was deleted — in which case empty " + AllowFileName + " and delete this "
            + "assertion — or the fence parser is broken, which would make the ratchet above pass "
            + "on no evidence.");

        var reviewInstructions = Path.Combine(root, ".github", "copilot-instructions.md");
        Assert.True(File.Exists(reviewInstructions),
            "the review instructions must exist for this check to mean anything");
        Assert.Contains(Marker, File.ReadAllText(reviewInstructions), StringComparison.Ordinal);
        Assert.False(found.ContainsKey(".github/copilot-instructions.md"),
            "the scanner counted PROSE as a teaching site — the fence discriminator is broken, and "
            + "every count in the allow file is therefore unreliable. This file names the shape "
            + "only to tell the reviewer to flag it.");
    }

    private static Dictionary<string, int> Scan(string root) =>
        GuidanceFiles(root)
            .Select(f => (Relative: SourceScan.Relative(root, f), Count: CountSites(f)))
            .Where(x => x.Count > 0)
            .ToDictionary(x => x.Relative, x => x.Count, StringComparer.Ordinal);

    private static IEnumerable<string> GuidanceFiles(string root) =>
        ScannedRoots
            .Select(r => Path.Combine(root, r))
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(root, "*.md", SearchOption.TopDirectoryOnly))
            .Where(f => !SourceScan.IsExcluded(root, f))
            .Distinct(StringComparer.Ordinal);

    private static int CountSites(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (IOException) { return 0; } // a file a concurrent build is writing is not evidence

        return text.Contains(Marker, StringComparison.Ordinal) ? CountSitesInMarkdown(text) : 0;
    }

    /// <summary>
    /// Occurrences of <see cref="Marker"/> that sit inside a fenced code block whose info string
    /// names C#. Everything else in the document — prose, tables, block quotes, and fences of any
    /// other language — is invisible to this count on purpose.
    /// </summary>
    internal static int CountSitesInMarkdown(string text)
    {
        var lines = text.Split('\n');
        var count = 0;
        var i = 0;

        while (i < lines.Length)
        {
            var opening = TryReadFenceOpening(lines[i]);
            if (opening is null)
            {
                i++;
                continue;
            }

            var (fence, length, language) = opening.Value;
            var isCSharp = CSharpFenceLanguages.Contains(language, StringComparer.Ordinal);
            i++;

            while (i < lines.Length && !IsFenceClose(lines[i], fence, length))
            {
                if (isCSharp) count += Occurrences(lines[i]);
                i++;
            }

            i++; // step past the closing fence, or off the end of an unterminated one
        }

        return count;
    }

    /// <summary>CommonMark fence opening: up to three spaces of indent, three or more backticks or
    /// tildes, then an info string whose first word is the language.</summary>
    private static (char Fence, int Length, string Language)? TryReadFenceOpening(string rawLine)
    {
        var line = rawLine.TrimEnd('\r');
        var indent = 0;
        while (indent < line.Length && line[indent] == ' ') indent++;
        if (indent > 3 || indent >= line.Length) return null;

        var fence = line[indent];
        if (fence is not ('`' or '~')) return null;

        var length = 0;
        while (indent + length < line.Length && line[indent + length] == fence) length++;
        if (length < 3) return null;

        var info = line[(indent + length)..].Trim();
        // A backtick fence's info string may not contain a backtick — that is inline code, not a fence.
        if (fence == '`' && info.Contains('`', StringComparison.Ordinal)) return null;

        var language = new string(info.TakeWhile(c => c is not (' ' or '\t' or ',' or '{')).ToArray())
            .ToLowerInvariant();
        return (fence, length, language);
    }

    private static bool IsFenceClose(string rawLine, char fence, int openingLength)
    {
        var line = rawLine.TrimEnd('\r');
        var indent = 0;
        while (indent < line.Length && line[indent] == ' ') indent++;
        if (indent > 3) return false;

        var length = 0;
        while (indent + length < line.Length && line[indent + length] == fence) length++;
        return length >= openingLength && line[(indent + length)..].Trim().Length == 0;
    }

    private static int Occurrences(string line)
    {
        var count = 0;
        var at = 0;
        while ((at = line.IndexOf(Marker, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += Marker.Length;
        }

        return count;
    }
}
