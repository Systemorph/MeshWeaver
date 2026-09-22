using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>A leaf handed to <c>IIoPool.Invoke</c> that discards the token the pool gives it can never
/// be settled by the drain — and the drain is what stands between a teardown and a use-after-unload
/// SIGSEGV.</b>
///
/// <para><b>The mechanism, exactly.</b> <c>IoPool.InvokeCore</c> runs the leaf as
/// <c>return await io(ct)</c>, where <c>ct</c> links the subscriber's token with the pool-wide one
/// that <c>Drain()</c>/<c>Dispose()</c> cancels, and the gate permit is released in that
/// <c>await</c>'s <c>finally</c>. So if the task <c>io</c> returns cannot observe <c>ct</c>,
/// cancelling the pool changes NOTHING: the permit is held until the underlying call returns on its
/// own, <c>IoPool.TryFinishDisposal</c> never fires <c>Disposed</c>, and the caller's bounded join
/// expires. On a silo that is <c>IoPoolSiloTeardown</c> releasing over live work while collectible
/// node ALCs are unloaded underneath it.</para>
///
/// <para><b>Why a guard and not a fix alone.</b> The defect is one character wide (<c>_</c> where
/// <c>ct</c> belongs), silent at the call site, and surfaces only as one anonymous line at
/// shutdown: Systemorph/MeshWeaver#2480 ran 18 occurrences over three weeks saying <i>"A leaf
/// ignored its cancellation token; fix the leaf, do not widen the budget"</i> with nothing that
/// named a leaf.</para>
///
/// <para>🚨 <b>Two correct shapes, and they are what the guard measures.</b> Where the inner call
/// accepts a token, pass it. Where it does not — Orleans'
/// <c>StreamSubscriptionHandle.UnsubscribeAsync()</c> is the live example, a grain call to the
/// pub-sub rendezvous with no token parameter at all — project the token onto the WAIT
/// (<c>theTask.WaitAsync(ct)</c>) and REPORT the resulting cancellation, so the abandoned work is
/// named rather than silent. Both reference the parameter.</para>
///
/// <para>🚨 <b>Scope: <c>Invoke</c> alone, because it is the ONLY entry point with no projection of
/// its own</b> — and that boundary is measured, not assumed:</para>
/// <list type="bullet">
/// <item><c>InvokeObservable</c> composes <c>source(ct).LastAsync().ObserveCompletion(report, ct)</c>
/// and <c>ObserveCompletion</c> cancels its task on that token, so the WAIT settles whatever the
/// lambda did with <c>ct</c>. Every Octokit.Reactive call in <c>OctokitGitHubRepoClient</c> relies
/// on exactly that; flagging them would demand a token from SDK methods that take none, for a
/// permit that is already released.</item>
/// <item><c>InvokeStream</c> enumerates as <c>source(ct).WithCancellation(ct)</c>, so the framework
/// hands the token to the enumerator itself.</item>
/// <item><c>InvokeBlocking</c> holds no gate permit and is joined on its own <c>_blockingIdle</c>
/// signal; its leaf is synchronous CPU/file work whose API frequently has no token to take.</item>
/// </list>
///
/// <para>🚨 <b>The detector is asserted in BOTH directions, and its denominator is asserted too.</b>
/// A guard whose predicate cannot match is green over an unenforced rule — that has happened on this
/// codebase — so this one fires on the pre-fix line verbatim, stays silent on the two correct
/// shapes, and the scan itself fails if it did not find a single <c>Invoke(lambda)</c> site in
/// <c>src</c>: a zero over a denominator of zero is not a reading.</para>
/// </summary>
public class PooledLeafObservesItsTokenGuard
{
    /// <summary>
    /// Production only. A TEST parks a leaf on purpose — that IS the subject of <c>IoPoolTest</c>
    /// and <c>IoPoolDisposeReleaseOrderTest</c> — so scanning <c>test</c> would flag the apparatus
    /// that proves the drain works.
    ///
    /// <para>A LOCAL rather than a <c>static readonly string[]</c>: an array is mutable whatever the
    /// field is, so a shared one is process-wide state another test could write through.</para>
    /// </summary>
    private static string[] ScannedRoots() => ["src"];

    /// <summary>
    /// A call to <c>Invoke</c> whose first argument is a lambda, capturing the lambda's parameter
    /// name. <c>async</c> is optional — both shapes reach the same <c>await io(ct)</c>. The
    /// <c>\s*\(</c> is what keeps <c>InvokeObservable</c>, <c>InvokeStream</c> and
    /// <c>InvokeBlocking</c> out: each has its own projection (see the type remarks).
    /// </summary>
    private static readonly Regex PooledAsyncLeaf = new(
        @"\.Invoke\s*\(\s*(?:async\s+)?(\w+)\s*=>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void EveryPooledAsyncLeafObservesItsCancellationToken()
    {
        var root = SourceScan.FindRepoRoot();
        var scanned = 0;
        var offenders = new List<string>();
        foreach (var file in SourceScan.SourceFiles(root, ScannedRoots()))
        {
            var code = SourceScan.MaskCommentsAndStrings(File.ReadAllText(file));
            foreach (var (parameter, observed) in LeavesIn(code))
            {
                scanned++;
                if (!observed)
                    offenders.Add($"{SourceScan.Relative(root, file)}: .Invoke({parameter} => …) "
                                  + $"— {parameter} is never referenced in the leaf");
            }
        }

        // 🚨 THE DENOMINATOR. A pattern that matches nothing reports zero offenders, which reads
        // exactly like a clean tree. src/ has always carried pooled leaves, so a scan that finds
        // none has stopped measuring — a renamed entry point, a moved root, a broken regex.
        Assert.True(scanned > 0,
            "The scan found no .Invoke(lambda) site anywhere under "
            + string.Join(", ", ScannedRoots())
            + " — the detector, not the tree, is what changed. A zero offender count over a zero "
            + "denominator is not a reading.");

        Assert.True(offenders.Count == 0,
            $"🚨 {offenders.Count} of {scanned} pooled async leaves discard the token the pool "
            + "hands them:\n  " + string.Join("\n  ", offenders.OrderBy(x => x, System.StringComparer.Ordinal))
            + "\n\nIoPool runs the leaf as `await io(ct)` and releases the gate permit in that "
            + "await's finally, so a task that cannot observe `ct` holds its permit through the "
            + "whole drain: IoPool.Disposed never fires, the silo's bounded join expires, and "
            + "teardown proceeds over live work while collectible node ALCs are unloaded "
            + "(Systemorph/MeshWeaver#2480 — 'A leaf ignored its cancellation token; fix the leaf, "
            + "do not widen the budget').\n"
            + "Fix: pass the token to the inner call. If that call takes none — an Orleans grain "
            + "call, say — project the token onto the WAIT (`theTask.WaitAsync(ct)`) and REPORT the "
            + "resulting cancellation, so the abandoned work is named rather than silent. See "
            + "OrleansRoutingService.UnsubscribeObservingToken.");
    }

    /// <summary>
    /// 🚨 The detector must fire on the exact text this guard exists to forbid — the pre-fix line
    /// from <c>OrleansRoutingService.EnqueueStreamTeardown</c>, verbatim. Without this, the zero
    /// above is indistinguishable from a pattern that matches nothing.
    /// </summary>
    [Fact]
    public void TheDetectorFindsTheShapeItIsLookingFor()
    {
        const string preFix = """
            .SelectMany(subscription => subscription is null
                ? Observable.Return(Unit.Default)
                : ioPool.Invoke(_ => subscription.UnsubscribeAsync()))
            """;
        Assert.Equal(["_"], Offenders(preFix));

        // The subtler half of the same defect: the parameter is NAMED, reads as observed, and is
        // not. A predicate keyed on the discard `_` alone would pass this.
        Assert.Equal(["ct"], Offenders("pool.Invoke(async ct => await store.WriteAsync(payload));"));
    }

    /// <summary>
    /// The negative control — the two CORRECT shapes, and the three entry points deliberately out
    /// of scope. A predicate that flagged these would be unsatisfiable, and an unsatisfiable guard
    /// is removed rather than obeyed.
    /// </summary>
    [Fact]
    public void TheDetectorDoesNotFireOnALeafThatObservesItsToken()
    {
        // 1. The token passed straight down — what a token-taking API gets.
        Assert.Empty(Offenders("pool.Invoke(ct => store.WriteAsync(payload, ct));"));

        // 2. The token projected onto the wait — what a token-LESS API gets (the #2480 fix).
        Assert.Empty(Offenders("ioPool.Invoke(ct => UnsubscribeObservingToken(subscription, address, ct))"));

        // 3. The three entry points with their own projection (see the type remarks). Each of
        //    these WOULD be an offender under a name-prefix match, and none of them is one.
        Assert.Empty(Offenders("Http.InvokeObservable(_ => client.Repository.Get(owner, repo))"));
        Assert.Empty(Offenders("pool.InvokeStream(_ => reader.ReadAllAsync());"));
        Assert.Empty(Offenders("pool.InvokeBlocking(_ => File.ReadAllText(path));"));
    }

    private static IEnumerable<string> Offenders(string code) =>
        LeavesIn(code).Where(l => !l.Observed).Select(l => l.Parameter);

    /// <summary>
    /// Every <c>Invoke(lambda)</c> in <paramref name="code"/>, with whether that lambda references
    /// its own parameter. The body is delimited by walking parentheses from the <c>=&gt;</c> to the
    /// call's own closing one, so a nested call, a collection expression or a further lambda inside
    /// the leaf is included rather than truncating the search.
    /// </summary>
    private static IEnumerable<(string Parameter, bool Observed)> LeavesIn(string code)
    {
        foreach (Match m in PooledAsyncLeaf.Matches(code))
        {
            var parameter = m.Groups[1].Value;
            var depth = 1;
            var i = m.Index + m.Length;
            while (i < code.Length && depth > 0)
            {
                if (code[i] == '(') depth++;
                else if (code[i] == ')') depth--;
                i++;
            }
            var body = code[(m.Index + m.Length)..(i > 0 ? i - 1 : code.Length)];
            yield return (parameter,
                parameter != "_" && Regex.IsMatch(body, @"\b" + Regex.Escape(parameter) + @"\b"));
        }
    }
}
