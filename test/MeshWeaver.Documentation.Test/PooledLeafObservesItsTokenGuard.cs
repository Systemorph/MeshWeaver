using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>A leaf handed to an <c>IIoPool</c> that discards the token the pool gives it can never be
/// settled by the drain — and the drain is what stands between a teardown and a use-after-unload
/// SIGSEGV.</b>
///
/// <para><b>The mechanism, exactly.</b> <c>IoPool.InvokeCore</c> runs an async leaf as
/// <c>return await io(ct)</c>, where <c>ct</c> links the subscriber's token with the pool-wide one
/// that <c>Drain()</c>/<c>Dispose()</c> cancels, and the gate permit is released in that
/// <c>await</c>'s <c>finally</c>. So if the task <c>io</c> returns cannot observe <c>ct</c>,
/// cancelling the pool changes NOTHING: the permit is held until the underlying call returns on its
/// own, <c>IoPool.TryFinishDisposal</c> never fires <c>Disposed</c>, and the caller's bounded join
/// expires. A BLOCKING leaf holds no permit, but <c>Drain()</c> joins blocking leaves on their own
/// <c>_blockingIdle</c> signal under the same budget, so a blocking leaf that never looks at its
/// token holds the silo's join just as surely. On a silo that is <c>IoPoolSiloTeardown</c>
/// releasing over live work while collectible node ALCs are unloaded underneath it.</para>
///
/// <para><b>Why a guard and not a fix alone.</b> The defect is one character wide (<c>_</c> where
/// <c>ct</c> belongs), silent at the call site, and surfaces only as one line at shutdown:
/// Systemorph/MeshWeaver#2480 ran 18 occurrences over three weeks with nothing that named a leaf,
/// and when one could be named it was a BLOCKING leaf
/// (<c>InvokeBlocking(_ =&gt; enumerateBundles())</c>), the shape this guard's first version could
/// not see. #7049 named a second (<c>PrebuiltBundleStore.Sweep</c>) and the assembly-cache sweep
/// was a third — all three <c>InvokeBlocking(_ =&gt; …)</c>.</para>
///
/// <para>🚨 <b>Scope: every entry point with no projection of its own</b>:</para>
/// <list type="bullet">
/// <item><c>Invoke</c> is <c>await io(ct)</c> and projects nothing. In scope.</item>
/// <item><c>IoPoolExtensions.Run</c> composes straight onto <c>Invoke</c>. In scope.</item>
/// <item><c>InvokeBlocking</c> and <c>RunBlocking</c> run the work itself on a pool thread; the only
/// thing that can end it early is the work noticing the cancel. In scope.</item>
/// <item><c>InvokeObservable</c> composes <c>source(ct).LastAsync().ObserveCompletion(report, ct)</c>
/// and <c>ObserveCompletion</c> cancels its task on that token, so the WAIT settles whatever the
/// lambda did with <c>ct</c>. Out of scope.</item>
/// <item><c>InvokeStream</c> (and <c>RunStream</c>) enumerates <c>source(ct).WithCancellation(ct)</c>,
/// so the framework hands the token to the enumerator itself. Out of scope.</item>
/// </list>
///
/// <para>🚨 <b>The one sanctioned exemption, and why it exists for blocking leaves ONLY.</b> A
/// blocking leaf that is ONE short call (<c>_ =&gt; File.ReadAllText(path)</c>, an assembly load, a
/// parse of bytes already in memory) has no step at which to look at a token, and passing the token
/// to it would be ceremony: nothing would check it. A blocking leaf that WALKS (a directory tree, a
/// list of archives, a loop of writes) must check it between steps, and the regex cannot tell the
/// two apart. So an atomic blocking leaf is declared at its call site with a line comment
/// <c>// atomic-blocking-leaf: &lt;why it has no step boundary&gt;</c>, on the call's own line or in
/// the comment block directly above it. The declaration is checked, not trusted blindly:</para>
/// <list type="bullet">
/// <item>it applies to <c>InvokeBlocking</c>/<c>RunBlocking</c> only. An async <c>Invoke</c>/<c>Run</c>
/// leaf always has the remedy of passing or projecting the token, so it can never be exempted;</item>
/// <item>the reason must say something (at least <see cref="MinimumReasonLength"/> characters);</item>
/// <item>a declaration attached to no unobserved blocking leaf (the leaf now observes its token, or
/// the call moved) is STALE and fails, so the list of exemptions cannot outlive what it excuses.</item>
/// </list>
/// <para>There is no allow FILE: a per-site declaration lives next to the code a reviewer reads,
/// and a blanket entry has nowhere to go.</para>
///
/// <para>🚨 <b>WHAT THIS GUARD DOES NOT COVER.</b> It is a LEXICAL scan of the LAMBDA form. A leaf
/// passed as a METHOD GROUP or a delegate variable — <c>_httpPool.Invoke(ReadIndex)</c>,
/// <c>pool.InvokeBlocking(enumerateBundles)</c>, <c>pool.Invoke(io)</c> in
/// <c>IoPoolExtensions.Run</c> itself — is invisible to it, because deciding those needs the
/// callee's body resolved. So this guard's zero means <i>"no lambda leaf discards its token without
/// a checked declaration"</i>, never <i>"no leaf can"</i>, and
/// <see cref="TheDetectorCannotSeeAMethodGroup_AndSaysSo"/> pins that boundary. It also cannot judge
/// a declaration's TRUTH: whether a leaf really is one call is a reviewer's question, which is why
/// the reason is required and sits at the call site.</para>
///
/// <para>🚨 <b>The detector is asserted in BOTH directions, and its denominator is asserted too.</b>
/// It fires on the pre-fix lines verbatim, stays silent on the correct shapes, and the scan itself
/// fails if it found no scannable site in <c>src</c>: a zero over a denominator of zero is not a
/// reading.</para>
/// </summary>
public class PooledLeafObservesItsTokenGuard
{
    /// <summary>The marker that declares an atomic blocking leaf.</summary>
    private const string MarkerName = "atomic-blocking-leaf:";

    /// <summary>A reason shorter than this says nothing a reviewer could check.</summary>
    private const int MinimumReasonLength = 20;

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
    /// A call to <c>Invoke</c>, <c>Run</c>, <c>InvokeBlocking</c> or <c>RunBlocking</c> whose first
    /// argument is a lambda, capturing the method name and the lambda's parameter. <c>async</c> is
    /// optional. The <c>\s*\(</c> right after the alternation is what keeps <c>InvokeObservable</c>,
    /// <c>InvokeStream</c> and <c>RunStream</c> out: each has its own projection (see the type
    /// remarks).
    ///
    /// <para>🚨 <c>DeliveryObservable.Run</c> is excluded BY NAME, and it is not a pool: it is
    /// <c>MessageHub</c>'s own handler bridge, which passes <c>CancellationToken.None</c>
    /// deliberately and says so at the site.</para>
    /// </summary>
    private static readonly Regex PooledLeaf = new(
        @"(?<!DeliveryObservable)\.(?<method>InvokeBlocking|RunBlocking|Invoke|Run)\s*\(\s*(?:async\s+)?(?<param>\w+)\s*=>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Marker = new(
        @"//\s*" + Regex.Escape(MarkerName) + @"(?<reason>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    [Fact]
    public void EveryPooledLeafObservesItsCancellationToken()
    {
        var root = SourceScan.FindRepoRoot();

        // ONE immutable pass; every reading below derives from this single array, so the
        // denominator and the offender list are always about the same population.
        var scans = SourceScan.SourceFiles(root, ScannedRoots())
            .Select(file => (File: SourceScan.Relative(root, file), Scan: Scan(File.ReadAllText(file))))
            .ToArray();
        var leaves = scans.SelectMany(s => s.Scan.Leaves.Select(l => (s.File, Leaf: l))).ToArray();

        // 🚨 THE DENOMINATOR. A pattern that matches nothing reports zero offenders, which reads
        // exactly like a clean tree.
        Assert.True(leaves.Length > 0,
            "The scan found no .Invoke/.Run/.InvokeBlocking/.RunBlocking(lambda) site anywhere under "
            + string.Join(", ", ScannedRoots())
            + " — the detector, not the tree, is what changed. A zero offender count over a zero "
            + "denominator is not a reading.");
        Assert.Contains(leaves, l => l.Leaf.Blocking);
        Assert.Contains(leaves, l => !l.Leaf.Blocking);

        var problems = scans
            .SelectMany(s => s.Scan.Problems.Select(p => $"{s.File}:{p}"))
            .OrderBy(x => x, System.StringComparer.Ordinal)
            .ToArray();

        Assert.True(problems.Length == 0,
            $"🚨 {problems.Length} problem(s) over {leaves.Length} pooled lambda leaves:\n  "
            + string.Join("\n  ", problems)
            + "\n\nIoPool cancels a leaf's token on Drain/Dispose, and a leaf that never looks at it "
            + "holds the silo's teardown join (a permit for Invoke/Run, the blocking-idle signal for "
            + "InvokeBlocking/RunBlocking) until the work ends on its own — teardown then proceeds over "
            + "live work while collectible node ALCs are unloaded (#2480, #7049).\n"
            + "Fix: pass the token to the inner call. An async call that takes none: project it onto "
            + "the WAIT (`theTask.WaitAsync(ct)`) and REPORT the cancellation. A blocking leaf that "
            + "walks or loops: take the token and check it between steps. A blocking leaf that is ONE "
            + "call with no step boundary: declare it with `// " + MarkerName + " <why>` on the call's "
            + "line or directly above it. See Doc/Architecture/ControlledIoPooling.");
    }

    /// <summary>
    /// 🚨 The detector must fire on the exact text this guard exists to forbid — the pre-fix lines,
    /// verbatim. Without this, the zero above is indistinguishable from a pattern that matches nothing.
    /// </summary>
    [Fact]
    public void TheDetectorFindsTheShapeItIsLookingFor()
    {
        // OrleansRoutingService.EnqueueStreamTeardown, before #2480's fix.
        const string preFix = """
            .SelectMany(subscription => subscription is null
                ? Observable.Return(Unit.Default)
                : ioPool.Invoke(_ => subscription.UnsubscribeAsync()))
            """;
        Assert.Equal(["_"], Offenders(preFix));

        // The parameter is NAMED, reads as observed, and is not.
        Assert.Equal(["ct"], Offenders("pool.Invoke(async ct => await store.WriteAsync(payload));"));

        // IoPoolExtensions.Run composes straight onto Invoke.
        Assert.Equal(["_"], Offenders("pool.Run(_ => client.SendAsync(request));"));

        // 🚨 The BLOCKING shapes this guard's first version could not see — the three named leaves,
        // verbatim: #2480's seeding walk, #7049's bundle retention, and the assembly-cache sweep.
        Assert.Equal(["_"], Offenders(".InvokeBlocking(_ => enumerateBundles())"));
        Assert.Equal(["_"], Offenders("=> pool.InvokeBlocking(_ => SweepCore(root, liveIdentity, livePlatformVersion, stampedIdentities, pinned, retention, DateTimeOffset.UtcNow, logger));"));
        Assert.Equal(["_"], Offenders("=> pool.InvokeBlocking(_ =>\n    SweepCore(rootDirectory, liveTag, retention, DateTimeOffset.UtcNow, logger));"));
        Assert.Equal(["ct"], Offenders(".Select(_ => pool.InvokeBlocking(ct => pending.Read())"));
        Assert.Equal(["token"], Offenders("IoPool.RunBlocking(token => { ReadDisk(); return Unit.Default; })"));
    }

    /// <summary>
    /// The negative control — the correct shapes, the entry points that project the token
    /// themselves, and the one same-named method that is not a pool at all.
    /// </summary>
    [Fact]
    public void TheDetectorDoesNotFireOnALeafThatObservesItsToken()
    {
        // 1. The token passed straight down.
        Assert.Empty(Offenders("pool.Invoke(ct => store.WriteAsync(payload, ct));"));
        Assert.Empty(Offenders("pool.Run(ct => store.WriteAsync(payload, ct));"));
        Assert.Empty(Offenders("pool.InvokeBlocking(ct => SweepCore(root, tag, retention, now, ct, logger));"));
        Assert.Empty(Offenders("IoPool.RunBlocking(token => { ReadDisk(token); return Unit.Default; })"));

        // 2. The token projected onto the wait — what a token-LESS async API gets.
        Assert.Empty(Offenders("ioPool.Invoke(ct => UnsubscribeObservingToken(subscription, address, ct))"));

        // 3. The entry points with their own projection.
        Assert.Empty(Offenders("Http.InvokeObservable(_ => client.Repository.Get(owner, repo))"));
        Assert.Empty(Offenders("pool.InvokeStream(_ => reader.ReadAllAsync());"));
        Assert.Empty(Offenders("pool.RunStream(_ => reader.ReadAllAsync());"));

        // 4. Not a pool at all.
        Assert.Empty(Offenders("return DeliveryObservable.Run(async _ => { await er.Action.Invoke(cancellationToken); return delivery.Processed(); });"));

        // 5. A blocking leaf that is ONE call, declared at the site — above it, or on its own line.
        Assert.Empty(Problems("""
            // atomic-blocking-leaf: one File.ReadAllText; there is no step at which to check a token
            pool.InvokeBlocking(_ => File.ReadAllText(path));
            """));
        Assert.Empty(Problems("""
            // The comment block above the call may say more first.
            // atomic-blocking-leaf: one assembly load from a local path, a single call
            pool.InvokeBlocking(_ => Load(path));
            """));
        Assert.Empty(Problems("pool.RunBlocking(_ => File.ReadAllText(path)); // atomic-blocking-leaf: one read of one small file"));
    }

    /// <summary>
    /// 🚨 The exemption is checked, not trusted: it cannot excuse an async leaf, it must give a
    /// reason, and a declaration nothing needs any more is stale — each of those is red.
    /// </summary>
    [Fact]
    public void TheExemptionIsCheckedNotTrusted()
    {
        // An async leaf always has a remedy (pass or project the token), so it is never exempt.
        Assert.Single(Problems("""
            // atomic-blocking-leaf: an Orleans grain call that takes no token at all
            ioPool.Invoke(_ => subscription.UnsubscribeAsync());
            """));

        // A reason that says nothing.
        Assert.Single(Problems("""
            // atomic-blocking-leaf: atomic
            pool.InvokeBlocking(_ => File.ReadAllText(path));
            """));

        // STALE: the leaf observes its token now, so the declaration excuses nothing.
        Assert.Single(Problems("""
            // atomic-blocking-leaf: one File.ReadAllText; there is no step at which to check a token
            pool.InvokeBlocking(ct => Read(path, ct));
            """));

        // STALE: a declaration attached to no call at all (the call moved away).
        Assert.Single(Problems("""
            // atomic-blocking-leaf: one File.ReadAllText; there is no step at which to check a token
            var x = 1;
            pool.InvokeBlocking(ct => Read(path, ct));
            """));

        // A declaration separated from its call by code does not reach it: the call is an offender
        // AND the declaration is stale.
        Assert.Equal(2, Problems("""
            // atomic-blocking-leaf: one File.ReadAllText; there is no step at which to check a token
            var x = 1;
            pool.InvokeBlocking(_ => File.ReadAllText(path));
            """).Length);
    }

    /// <summary>
    /// 🚨 The BOUNDARY, asserted rather than assumed. A leaf passed as a method group or a delegate
    /// variable cannot be decided lexically, so this guard does not see it. Real coverage for it is a
    /// call-graph pass or a Roslyn analyzer.
    /// </summary>
    [Fact]
    public void TheDetectorCannotSeeAMethodGroup_AndSaysSo()
    {
        Assert.Empty(Offenders("public IObservable<BundleIndex> FetchIndex() => _httpPool.Invoke(ReadIndex);"));
        Assert.Empty(Offenders("_index.GetOrCreate(() => _httpPool.Run(ReadIndex))"));
        Assert.Empty(Offenders("pool.Invoke(io)"));
        Assert.Empty(Offenders("pool.InvokeBlocking(enumerateBundles)"));
    }

    private static IEnumerable<string> Offenders(string code) =>
        Scan(code).Leaves.Where(l => !l.Observed).Select(l => l.Parameter);

    private static string[] Problems(string code) => [.. Scan(code).Problems];

    private sealed record Leaf(string Method, string Parameter, bool Observed, bool Exempt)
    {
        public bool Blocking => Method is "InvokeBlocking" or "RunBlocking";
    }

    private sealed record FileScan(IReadOnlyList<Leaf> Leaves, IReadOnlyList<string> Problems);

    /// <summary>
    /// Every scannable pooled leaf in <paramref name="text"/>, with whether its lambda references its
    /// own parameter and whether a declaration exempts it — plus every problem: an unobserved leaf
    /// without a valid declaration, and every declaration that is invalid or excuses nothing.
    /// </summary>
    private static FileScan Scan(string text)
    {
        var code = SourceScan.MaskCommentsAndStrings(text);
        var lines = text.Split('\n');
        var lineStarts = new List<int> { 0 };
        for (var k = 0; k < text.Length; k++)
            if (text[k] == '\n')
                lineStarts.Add(k + 1);
        int LineOf(int index)
        {
            var at = lineStarts.BinarySearch(index);
            return at >= 0 ? at : ~at - 1;
        }

        // Every declaration in the file, by 0-based line.
        var markers = new Dictionary<int, string>();
        foreach (Match m in Marker.Matches(text))
            markers[LineOf(m.Index)] = m.Groups["reason"].Value.Trim();
        var used = new HashSet<int>();

        var leaves = new List<Leaf>();
        var problems = new List<string>();
        foreach (Match m in PooledLeaf.Matches(code))
        {
            var method = m.Groups["method"].Value;
            var parameter = m.Groups["param"].Value;
            var depth = 1;
            var i = m.Index + m.Length;
            while (i < code.Length && depth > 0)
            {
                if (code[i] == '(') depth++;
                else if (code[i] == ')') depth--;
                i++;
            }
            var body = code[(m.Index + m.Length)..(i > 0 ? i - 1 : code.Length)];
            var observed = parameter != "_" && Regex.IsMatch(body, @"\b" + Regex.Escape(parameter) + @"\b");

            // The declaration: on the call's own line, or in the contiguous comment-only block
            // directly above it.
            var line = LineOf(m.Index);
            int? declaration = markers.ContainsKey(line) ? line : null;
            for (var up = line - 1; declaration is null && up >= 0 && lines[up].TrimStart().StartsWith("//"); up--)
                if (markers.ContainsKey(up))
                    declaration = up;

            var lineNo = line + 1;
            var leaf = new Leaf(method, parameter, observed, Exempt: false);
            if (declaration is { } d)
            {
                used.Add(d);
                var reason = markers[d];
                if (observed)
                    problems.Add($"{d + 1}: `// {MarkerName}` is STALE — the leaf at line {lineNo} observes its token now; delete the declaration");
                else if (!leaf.Blocking)
                    problems.Add($"{lineNo}: .{method}({parameter} => …) is an ASYNC leaf and cannot be declared atomic — pass the token, or project it onto the wait (`WaitAsync(ct)`) and report the cancellation");
                else if (reason.Length < MinimumReasonLength)
                    problems.Add($"{lineNo}: `// {MarkerName} {reason}` gives no reason a reviewer can check (at least {MinimumReasonLength} characters: name the one call and why it has no step boundary)");
                else
                    leaf = leaf with { Exempt = true };
            }
            else if (!observed)
                problems.Add($"{lineNo}: .{method}({parameter} => …) — {parameter} is never referenced in the leaf");
            leaves.Add(leaf);
        }

        foreach (var stale in markers.Keys.Where(k => !used.Contains(k)).Order())
            problems.Add($"{stale + 1}: `// {MarkerName}` is attached to no pooled lambda leaf (it must sit on the call's line or in the comment block directly above it) — move it or delete it");

        return new FileScan(leaves, problems);
    }
}
