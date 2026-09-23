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
/// <para>🚨 <b>Scope: the two entry points with NO projection of their own</b> — and that boundary
/// is measured, not assumed:</para>
/// <list type="bullet">
/// <item><c>Invoke</c> is <c>await io(ct)</c> and projects nothing. In scope.</item>
/// <item><c>IoPoolExtensions.Run</c> composes straight onto <c>Invoke</c>, so
/// <c>pool.Run(_ =&gt; neverObserves())</c> reaches that same <c>await</c>. In scope, and it was
/// missed by this guard's first draft (Copilot review on #5281).</item>
/// <item><c>InvokeObservable</c> composes <c>source(ct).LastAsync().ObserveCompletion(report, ct)</c>
/// and <c>ObserveCompletion</c> cancels its task on that token, so the WAIT settles whatever the
/// lambda did with <c>ct</c>. Every Octokit.Reactive call in <c>OctokitGitHubRepoClient</c> relies
/// on exactly that; flagging them would demand a token from SDK methods that take none, for a
/// permit that is already released.</item>
/// <item><c>InvokeStream</c> (and <c>RunStream</c>, which forwards to it) enumerates
/// <c>source(ct).WithCancellation(ct)</c>, so the framework hands the token to the enumerator
/// itself.</item>
/// <item><c>InvokeBlocking</c> (and <c>RunBlocking</c>) is OUT OF THIS GUARD'S SCOPE — but 🚨 NOT
/// because it cannot hold the drain. This bullet used to say it "holds no gate permit and is joined
/// on its own <c>_blockingIdle</c> signal", and the second half is exactly why it CAN: <c>Drain</c>
/// waits on that signal under the same budget, so a blocking leaf that never looks at its token
/// holds the silo's join just as surely. #2480's production report, once it could name a leaf,
/// named one of these: <c>prebuilt:files=1 [ShippedPrebuiltBundles+&lt;&gt;c__DisplayClass24_0.&lt;SeedBundles&gt;b__8]</c>
/// — <c>InvokeBlocking(_ =&gt; enumerateBundles())</c>, a multi-step walk of a network share.
/// It is excluded here only because the lexical question cannot be decided: a blocking leaf that
/// is ONE short syscall (<c>_ =&gt; File.ReadAllText(path)</c>) has no step at which to look at a
/// token and is correct, while a leaf that WALKS (a directory tree, a list of archives) must check
/// it between steps, and the regex cannot tell the two apart. The rule for a walking blocking leaf
/// is stated in <c>Doc/Architecture/ControlledIoPooling</c>.</item>
/// </list>
///
/// <para>🚨 <b>WHAT THIS GUARD DOES NOT COVER, stated because a claim wider than its coverage is
/// the very defect it exists to prevent.</b> It is a LEXICAL scan of the LAMBDA form. A leaf passed
/// as a METHOD GROUP or a delegate variable — <c>_httpPool.Invoke(ReadIndex)</c> in
/// <c>PluginBundleClient</c>, <c>pool.Invoke(io)</c> in <c>IoPoolExtensions.Run</c> itself — is
/// invisible to it, because deciding those needs the callee's body resolved, which is a call-graph
/// or analyzer job and not a regex one. Both sites above are clean today (<c>ReadIndex</c> does
/// observe its <c>ct</c>; the extension is a forwarder whose caller's lambda IS scanned at its own
/// call site), and a non-lambda argument is deliberately NOT failed here: turning a correct form red
/// to protect a lexical scan is the wrong trade. So this guard's zero means <i>"no lambda leaf
/// discards its token"</i>, never <i>"no leaf can"</i>, and
/// <see cref="TheDetectorCannotSeeAMethodGroup_AndSaysSo"/> pins that boundary so it stays a
/// recorded limit rather than a forgotten one.</para>
///
/// <para>🚨 <b>The detector is asserted in BOTH directions, and its denominator is asserted too.</b>
/// A guard whose predicate cannot match is green over an unenforced rule — that has happened on this
/// codebase — so this one fires on the pre-fix line verbatim, stays silent on the correct shapes,
/// and the scan itself fails if it did not find a single scannable site in <c>src</c>: a zero over a
/// denominator of zero is not a reading.</para>
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
    /// A call to <c>Invoke</c> or <c>Run</c> whose first argument is a lambda, capturing the
    /// lambda's parameter name. <c>async</c> is optional — both shapes reach the same
    /// <c>await io(ct)</c>. The <c>\s*\(</c> after the method name is what keeps
    /// <c>InvokeObservable</c>, <c>InvokeStream</c>, <c>InvokeBlocking</c>, <c>RunStream</c> and
    /// <c>RunBlocking</c> out: each has its own projection or holds no permit (see the type
    /// remarks).
    ///
    /// <para>🚨 <c>DeliveryObservable.Run</c> is excluded BY NAME, and it is not a pool: it is
    /// <c>MessageHub</c>'s own handler bridge, which passes <c>CancellationToken.None</c>
    /// deliberately and says so at the site. Matching it would make this guard red on a correct
    /// line — the excuse every suppressed guard starts with. Pinned in both directions by
    /// <see cref="TheDetectorDoesNotFireOnALeafThatObservesItsToken"/> and
    /// <see cref="TheDetectorFindsTheShapeItIsLookingFor"/>.</para>
    /// </summary>
    private static readonly Regex PooledAsyncLeaf = new(
        @"(?<!DeliveryObservable)\.(?:Invoke|Run)\s*\(\s*(?:async\s+)?(\w+)\s*=>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void EveryPooledAsyncLeafObservesItsCancellationToken()
    {
        var root = SourceScan.FindRepoRoot();

        // ONE immutable pass. Both readings below are derived from this single array rather than
        // accumulated side by side: two counters advanced by hand can drift, and the drift that
        // matters is the denominator quietly ceasing to be about the same population as the
        // offender list (Copilot review on #5281).
        var leaves = SourceScan.SourceFiles(root, ScannedRoots())
            .SelectMany(file => LeavesIn(SourceScan.MaskCommentsAndStrings(File.ReadAllText(file)))
                .Select(leaf => (File: SourceScan.Relative(root, file), leaf.Parameter, leaf.Observed)))
            .ToArray();

        // 🚨 THE DENOMINATOR. A pattern that matches nothing reports zero offenders, which reads
        // exactly like a clean tree. src/ has always carried pooled leaves, so a scan that finds
        // none has stopped measuring — a renamed entry point, a moved root, a broken regex.
        Assert.True(leaves.Length > 0,
            "The scan found no .Invoke(lambda) or .Run(lambda) site anywhere under "
            + string.Join(", ", ScannedRoots())
            + " — the detector, not the tree, is what changed. A zero offender count over a zero "
            + "denominator is not a reading.");

        var offenders = leaves
            .Where(leaf => !leaf.Observed)
            .Select(leaf => $"{leaf.File}: .Invoke({leaf.Parameter} => …) — {leaf.Parameter} is "
                            + "never referenced in the leaf")
            .OrderBy(x => x, System.StringComparer.Ordinal)
            .ToArray();

        Assert.True(offenders.Length == 0,
            $"🚨 {offenders.Length} of {leaves.Length} pooled async leaves discard the token the "
            + "pool hands them:\n  " + string.Join("\n  ", offenders)
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

        // IoPoolExtensions.Run composes straight onto Invoke, so a leaf handed to it reaches the
        // same `await io(ct)`. Missed by the first draft of this guard.
        Assert.Equal(["_"], Offenders("pool.Run(_ => client.SendAsync(request));"));
    }

    /// <summary>
    /// The negative control — the correct shapes, the entry points that project the token
    /// themselves, and the one same-named method that is not a pool at all. A predicate that
    /// flagged these would be unsatisfiable, and an unsatisfiable guard is removed rather than
    /// obeyed.
    /// </summary>
    [Fact]
    public void TheDetectorDoesNotFireOnALeafThatObservesItsToken()
    {
        // 1. The token passed straight down — what a token-taking API gets.
        Assert.Empty(Offenders("pool.Invoke(ct => store.WriteAsync(payload, ct));"));
        Assert.Empty(Offenders("pool.Run(ct => store.WriteAsync(payload, ct));"));

        // 2. The token projected onto the wait — what a token-LESS API gets (the #2480 fix).
        Assert.Empty(Offenders("ioPool.Invoke(ct => UnsubscribeObservingToken(subscription, address, ct))"));

        // 3. The entry points with their own projection, or with no permit to hold. Each of these
        //    WOULD be an offender under a name-prefix match, and none of them is one.
        Assert.Empty(Offenders("Http.InvokeObservable(_ => client.Repository.Get(owner, repo))"));
        Assert.Empty(Offenders("pool.InvokeStream(_ => reader.ReadAllAsync());"));
        Assert.Empty(Offenders("pool.RunStream(_ => reader.ReadAllAsync());"));
        Assert.Empty(Offenders("pool.InvokeBlocking(_ => File.ReadAllText(path));"));
        Assert.Empty(Offenders("pool.RunBlocking(_ => File.ReadAllText(path));"));

        // 4. Not a pool at all. MessageHub's handler bridge passes CancellationToken.None on
        //    purpose and its leaf observes the DELIVERY's token instead; flagging it would make
        //    this guard red on a correct line.
        Assert.Empty(Offenders("return DeliveryObservable.Run(async _ => { await er.Action.Invoke(cancellationToken); return delivery.Processed(); });"));
    }

    /// <summary>
    /// 🚨 The BOUNDARY, asserted rather than assumed. A leaf passed as a method group or a delegate
    /// variable cannot be decided lexically — the callee's body has to be resolved — so this guard
    /// does not see it, and a future one of those CAN stop observing its token without reddening
    /// anything here.
    ///
    /// <para>That is a limitation, not a bug, and the reason it is pinned as a test rather than
    /// written in a comment is that a comment cannot fail: if someone later widens the regex to
    /// match a bare identifier, this case tells them they have changed the guard's contract — and
    /// what it would cost, since <c>PluginBundleClient.FetchIndex</c> and
    /// <c>IoPoolExtensions.Run</c> both use the form correctly today. Real coverage for it is a
    /// call-graph pass or a Roslyn analyzer.</para>
    /// </summary>
    [Fact]
    public void TheDetectorCannotSeeAMethodGroup_AndSaysSo()
    {
        Assert.Empty(Offenders("public IObservable<BundleIndex> FetchIndex() => _httpPool.Invoke(ReadIndex);"));
        Assert.Empty(Offenders("_index.GetOrCreate(() => _httpPool.Run(ReadIndex))"));
        Assert.Empty(Offenders("pool.Invoke(io)"));
    }

    private static IEnumerable<string> Offenders(string code) =>
        LeavesIn(code).Where(l => !l.Observed).Select(l => l.Parameter);

    /// <summary>
    /// Every scannable pooled leaf in <paramref name="code"/>, with whether its lambda references
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
