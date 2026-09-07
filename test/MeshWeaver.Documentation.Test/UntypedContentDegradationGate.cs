#pragma warning disable CS1591

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>The control arm for <c>check-untyped-content.sh</c>.</b> That script greps a shard's logs
/// for one phrase. A phrase-matching gate has exactly one silent failure mode — the phrase changes
/// and the gate keeps passing, having matched nothing forever — and nothing else can catch it.
///
/// <para>This test pins the coupling from both ends: the source must still EMIT the phrase, and the
/// script must still GREP the identical phrase. Reword either and the build fails, naming the
/// other.</para>
///
/// <para><b>What is being protected.</b> A content type that is not registered on the hub reading
/// it does not throw. The polymorphic converter cannot resolve the <c>$type</c>, degrades the value
/// to a raw <c>JsonElement</c>, and everything downstream reads it as absent — an
/// <c>is MyType</c> check misses, a view renders empty, a reactive wait never completes. The
/// message-PAYLOAD equivalent is loud (<c>type 'X' is not registered in this hub's
/// TypeRegistry</c>, with a NACK); the CONTENT equivalent is this warning. That asymmetry is why
/// the warning needs a gate at all.</para>
///
/// <para><b>Why now (MeshWeaver#3056).</b> <c>MessageService.Post</c> used to log through
/// <c>JsonSerializer.Serialize(ret, …)</c>, and that serialize registered every posted payload type
/// as a side effect (<c>ObjectPolymorphicConverter.Write</c> → <c>GetOrAddType</c>). #3056 removed
/// it — correctly, logging must not register types — and with that net gone, every place relying on
/// "registered because it was once posted" now surfaces as this warning. On the day it merged it
/// surfaced as a validator that silently stopped validating.</para>
///
/// <para>🚨 <b>Neither of the two assertions below asks the question that actually mattered</b>, and
/// for five days the answer to that question was "no". They pin that the phrase EXISTS in the
/// source and that the script GREPS it — both were true the whole time — but not whether a
/// degradation record can REACH the directory the script scans. It could not: both warnings were
/// logged with no exception object, and the sink that feeds <c>collected-logs/</c> takes a record
/// if and only if <c>exception is not null &amp;&amp; logLevel &gt;= Warning</c>. The gate was
/// permanently green having matched nothing (MeshWeaver#3625) — the subject moved and the roots did
/// not, which is the exact shape AGENTS.md names.
///
/// <para>The missing half now lives in
/// <c>MeshWeaver.Hosting.Test.UntypedContentDegradationReachesTheTraceSinkTest</c>, which drives the
/// PRODUCTION emitter and evaluates the sink's own predicate against the captured record. It runs
/// against a private recording logger, so it proves reachability without writing into the shared
/// trace file — which is why the earlier "plant it and watch the gate red every run" objection no
/// longer applies.</para>
///
/// <para>🚨 <b>The gate's PRIMARY key is now the exception type, not the phrase.</b> A message is a
/// DESCRIPTION of the event; <c>MeshNodeContentDegradedException</c> is the event's IDENTITY, bound
/// by the compiler at every construction site. That gives the coupling two independent bindings
/// (a rename is a compile-wide change, AND this test pins the script's key to
/// <c>nameof(...)</c>) where the phrase has only this file. The phrase is kept as a second net
/// because a per-test file log carries the formatted message with no exception attached at all.</para>
/// </summary>
public class UntypedContentDegradationGate
{
    /// <summary>The exact phrase. Both ends are asserted against THIS constant, so the two can
    /// never agree with each other while disagreeing with reality.</summary>
    private const string Phrase = "stayed an untyped JsonElement";

    /// <summary>The gate's PRIMARY key, taken from the type itself so a rename cannot leave the
    /// script grepping for a name nothing constructs.</summary>
    private static readonly string Marker = nameof(MeshWeaver.Mesh.MeshNodeContentDegradedException);

    private const string Script = ".github/scripts/check-untyped-content.sh";
    private const string EmittingSource = "src/MeshWeaver.Hosting/MeshNodeStreamCache.cs";

    private static string Read(string relative)
    {
        var path = Path.Combine(SourceScan.FindRepoRoot(), relative);
        Assert.True(File.Exists(path), $"{relative} is missing — this gate's subject moved; follow it.");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// 🚨 The identity key. Every degradation seam must ATTACH the marker exception — that is both
    /// what makes the record reach the trace sink at all and what the gate keys on. A seam that
    /// logs the prose without the exception is a seam whose degradations are invisible to CI, which
    /// is the state this whole gate was in before #3625.
    /// </summary>
    [Fact]
    public void EverySeamAttachesTheMarkerExceptionTheGateKeysOn()
    {
        // 🚨 Count CONSTRUCTIONS in code, not lines that happen to contain a substring (Copilot
        // review). The first version split on newlines and looked for `"new " + Marker`, which is
        // wrong in both directions at once: it MISSES a construction the formatter wrapped across
        // two lines or wrote alias-qualified (`Mesh.MeshNodeContentDegradedException`), and it
        // COUNTS a prose mention in a comment — and this file's subject is a source file that
        // explains itself at length, so comments naming the type are the norm here rather than the
        // exception. Masking first (the same helper every other source ratchet uses) removes the
        // second failure mode outright; the regex removes the first.
        var source = SourceScan.MaskCommentsAndStrings(Read(EmittingSource));
        var constructions = new Regex(
                @"\bnew\s+(?:[A-Za-z_][\w]*\s*\.\s*)*" + Regex.Escape(Marker) + @"\s*\(",
                RegexOptions.Compiled)
            .Matches(source).Count;

        Assert.True(
            constructions >= 3,
            $"Only {constructions} construction(s) of {Marker} in {EmittingSource}; expected at least 3 "
            + "(GetStream, GetQuery, and GetQuery's deserialization catch).\n"
            + "A degradation warning WITHOUT this exception cannot reach "
            + "collected-logs/_meshweaver-test-trace.log — the sink takes a record if and only if "
            + "`exception is not null && logLevel >= Warning` — so the shard gate that scans that "
            + "directory has nothing to match, and passes having checked nothing. That is exactly "
            + "how this gate spent its first five days (#3625).");
    }

    /// <summary>
    /// 🚨 <b>The control arm for the gate's ONLY sanctioned exemption.</b>
    ///
    /// <para>The gate has no allow-list, on purpose. But a test whose SUBJECT is a degradation —
    /// <c>LateContentTypeRegistrationTest</c> asserts that content nothing can resolve stays
    /// untyped, and that a live reader is re-typed when the type finally registers (#2952) — cannot
    /// take the gate's prescribed remedy either: seeding a DIFFERENT, REGISTERED type deletes the
    /// thing it tests. Such a test therefore keeps its own records out of the shared trace file by
    /// substituting <c>ILogger&lt;MeshNodeStreamCache&gt;</c> for its own mesh.</para>
    ///
    /// <para>🚨 <b>That substitution is the exemption, so it must never be silent.</b> Capturing
    /// degradation records and not asserting them is EXACTLY the permanently-green check this file
    /// exists to prevent — the gate would pass because nothing reached it, which is
    /// indistinguishable from passing because nothing degraded. So: any test that substitutes the
    /// emitter's logger must also assert what it caught.</para>
    ///
    /// <para><b>Scope, stated honestly.</b> This sees ONE diversion mechanism — replacing the
    /// closed <c>ILogger&lt;MeshNodeStreamCache&gt;</c>, which is the only way a full-mesh test can
    /// keep a degradation record out of <c>TestTraceLog</c>. A future test that invents a different
    /// route (its own logger provider, a filter rule) is not covered here, and that is a limit to
    /// re-measure rather than to assume away. The denominator arm below is what stops this guard
    /// from quietly checking zero files.</para>
    /// </summary>
    [Fact]
    public void ADivertedDegradationIsAssertedWhereItIsDiverted()
    {
        const string Diversion = "ILogger<MeshWeaver.Hosting.MeshNodeStreamCache>";
        const string ShortDiversion = "ILogger<MeshNodeStreamCache>";
        const string Assertion = "AssertReportedFor(";

        var root = SourceScan.FindRepoRoot();
        var diverting = SourceScan.SourceFiles(root, ["test"])
            .Select(path => (Path: path, Text: File.ReadAllText(path)))
            .Where(f => f.Text.Contains(Diversion, StringComparison.Ordinal)
                        || f.Text.Contains(ShortDiversion, StringComparison.Ordinal))
            // This gate's own prose names the type; only a file that USES it counts.
            .Where(f => !SourceScan.Relative(root, f.Path)
                .EndsWith("MeshWeaver.Documentation.Test/UntypedContentDegradationGate.cs", StringComparison.Ordinal))
            .ToArray();

        // 🚨 The DENOMINATOR. "No offender found" and "no file was examined" read identically, and
        // the second is how a guard spends years enforcing nothing. The sanctioned diversion exists
        // today; if it is ever removed, delete this guard deliberately rather than leaving it green
        // over an empty set.
        Assert.True(
            diverting.Length > 0,
            $"No test substitutes {ShortDiversion} any more, so this guard examined ZERO files and "
            + "passed having checked nothing. Either the sanctioned diversion "
            + "(LateContentTypeRegistrationTest) was removed — in which case remove this guard in "
            + "the same change and say so — or the scan is looking at the wrong tree.");

        var silent = diverting
            .Where(f => !f.Text.Contains(Assertion, StringComparison.Ordinal))
            .Select(f => SourceScan.Relative(root, f.Path))
            .ToArray();

        Assert.True(
            silent.Length == 0,
            $"These test files divert {ShortDiversion} away from the shared trace file without "
            + $"asserting what they caught ({Assertion}):\n  {string.Join("\n  ", silent)}\n"
            + "A degradation record kept out of collected-logs/ and then never asserted is an "
            + "exemption from check-untyped-content.sh that can never fail — the gate passes "
            + "because nothing reached it, which is indistinguishable from passing because nothing "
            + "degraded, and is the exact state #3625 was about. Assert that the record WAS "
            + "produced, that it names the node you expect, and that it would have satisfied the "
            + "trace sink's own predicate (exception is not null && level >= Warning).");
    }

    [Fact]
    public void TheGateScriptStillKeysOnTheMarkerExceptionType()
    {
        var script = Read(Script);

        // The ASSIGNMENT, not the file — same reason as the phrase assertion below: the script
        // explains itself at length and quotes the type name in its own comments.
        var assignment = new Regex(
            @"MARKER\s*=\s*[""']" + Regex.Escape(Marker) + @"[""']", RegexOptions.Compiled);
        Assert.True(
            assignment.IsMatch(script),
            $"{Script} no longer binds MARKER to \"{Marker}\". The exception type is the gate's "
            + "primary key precisely because it is compiler-bound at every construction site — "
            + "renaming it is a repo-wide change, and this assertion is what stops such a rename "
            + "leaving the script grepping for a name nothing writes any more.");
    }

    [Fact]
    public void TheSourceStillEmitsThePhraseTheGateGrepsFor()
    {
        var emitted = Read(EmittingSource).Split('\n').Count(l => l.Contains(Phrase, StringComparison.Ordinal));

        Assert.True(
            emitted > 0,
            $"'{EmittingSource}' no longer contains \"{Phrase}\".\n"
            + $"{Script} greps for exactly that phrase, so it now matches NOTHING and passes every "
            + "run having checked nothing — the silent retirement this test exists to prevent.\n"
            + "If the message was reworded, update the phrase in BOTH this test and the script. If "
            + "the degradation warning was removed entirely, remove the gate deliberately and say "
            + "why in the commit — do not leave a gate grepping for a string nobody writes.");

        // Both read seams — GetStream and GetQuery — must keep reporting. A degradation reachable
        // through only one of them is still a view that renders empty.
        Assert.True(
            emitted >= 2,
            $"Only {emitted} occurrence(s) of \"{Phrase}\" in {EmittingSource}; expected at least 2 "
            + "(GetStream and GetQuery). One read seam falling silent means content can degrade "
            + "down that path with nothing said, which is exactly the state before this gate.");
    }

    [Fact]
    public void TheGateScriptStillGrepsTheIdenticalPhrase()
    {
        var script = Read(Script);

        // 🚨 The ASSIGNMENT, not the file. My first version asserted the phrase appeared anywhere
        // in the script — and the script EXPLAINS itself at length, so its own comments contain the
        // phrase. Mutating `PHRASE=` to something else left the comments intact and the test passed
        // while the gate matched nothing: a grep hit is not a binder, in a guard whose entire job is
        // to stop a silent no-op. Caught by mutating this test rather than by reading it.
        // Match the ASSIGNMENT, tolerant of formatting. The invariant is "PHRASE is bound to this
        // string", not "this file contains these exact characters" — so whitespace around `=` and
        // either quote style pass, while the phrase itself must be exact.
        //
        // 🚨 Two failure modes had to be excluded together, and it took two attempts:
        //   * too loose — an earlier version searched the WHOLE FILE, and the script's own comments
        //     quote the phrase, so changing `PHRASE=` to something else still passed;
        //   * too strict — the fix for that pinned the literal `PHRASE='…'`, which would red on a
        //     harmless reformat (double quotes, a space around `=`) while the gate stayed correct.
        // A guard that cries wolf on formatting gets deleted, which costs the same as one that
        // never fires.
        var assignment = new Regex(
            @"PHRASE\s*=\s*[""']" + Regex.Escape(Phrase) + @"[""']", RegexOptions.Compiled);
        Assert.True(
            assignment.IsMatch(script),
            $"{Script} no longer binds PHRASE to \"{Phrase}\". The source still emits that phrase, "
            + "so the gate is now looking for something that is never written and will pass "
            + "forever. Keep the two in step, or retire both together.\n"
            + "(Matched on the ASSIGNMENT, not the file — the script's comments quote the phrase "
            + "too, so a whole-file match would not notice a rebinding.)");

        // A gate handed a directory that does not exist must FAIL, never pass. "Nothing to scan" is
        // a failed sweep, not a clean one — the same reading that makes an absent required check
        // read as green.
        Assert.True(
            script.Contains("does not exist", StringComparison.Ordinal)
            && script.Contains("FAILED sweep", StringComparison.Ordinal),
            $"{Script} lost its missing-directory guard. Without it, a rename of the log-collection "
            + "directory turns this gate into a no-op that reports success on every shard.");

        // 🚨 grep exits 0 on a match, 1 on NO match, and 2+ on a real error. Collapsing those — the
        // `$(grep … 2>/dev/null || true)` idiom — makes an ERRORED scan indistinguishable from a
        // clean one, so an unreadable log file reports "no degradation" and the gate passes. The
        // first version of the script did exactly that; the repo's own `CI's own shell` gate caught
        // it. Pinned here so it cannot come back under a reformat.
        Assert.True(
            script.Contains("scan_rc", StringComparison.Ordinal)
            && script.Contains("-gt 1", StringComparison.Ordinal),
            $"{Script} no longer distinguishes a FAILED scan from an EMPTY one. grep's exit code is "
            + "the only thing that separates 'nothing degraded' from 'I could not look', and this "
            + "gate's whole purpose is to make the second one impossible to mistake for the first.");
    }
}
