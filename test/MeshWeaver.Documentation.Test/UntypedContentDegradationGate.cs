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
/// <para><b>What this test deliberately does NOT do.</b> It does not plant an unregistered type and
/// assert the warning is emitted at runtime. That would be a stronger control arm — it would prove
/// the emission path is live, not merely that the string exists — but the planted case would write
/// the very phrase the shard gate greps for, so the gate validating itself would red every run.
/// Isolating the planted log from <c>collected-logs/</c> is possible and is the right follow-up;
/// it is called out here rather than silently omitted.</para>
/// </summary>
public class UntypedContentDegradationGate
{
    /// <summary>The exact phrase. Both ends are asserted against THIS constant, so the two can
    /// never agree with each other while disagreeing with reality.</summary>
    private const string Phrase = "stayed an untyped JsonElement";

    private const string Script = ".github/scripts/check-untyped-content.sh";
    private const string EmittingSource = "src/MeshWeaver.Hosting/MeshNodeStreamCache.cs";

    private static string Read(string relative)
    {
        var path = Path.Combine(SourceScan.FindRepoRoot(), relative);
        Assert.True(File.Exists(path), $"{relative} is missing — this gate's subject moved; follow it.");
        return File.ReadAllText(path);
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
