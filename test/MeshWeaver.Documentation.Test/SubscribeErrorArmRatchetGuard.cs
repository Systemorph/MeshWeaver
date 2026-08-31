using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <c>.Subscribe(x =&gt; …)</c> with no error arm is how a fault escapes the mesh and kills the
/// process. Every subscription in production MUST carry <c>.Subscribe(onNext, onError)</c>.
///
/// <para>🚨 <b>Stated as the requirement, not as the state of the tree — the tree does not meet it
/// yet.</b> This is a RATCHET seeded at <see cref="Baseline"/>, so bare subscriptions currently
/// exist and pass. Reading the rule as a description would be the "prose asserts a guard that does
/// not exist" trap: a later reader concludes the property is already held, and stops checking. What
/// is enforced is only that the count never rises.</para>
///
/// <para><b>Why the single-argument overload specifically.</b> Rx routes a throw inside
/// <c>Select</c>/<c>SelectMany</c>/<c>Defer</c> to <c>onError</c> — but it <b>rethrows out of an
/// <c>onNext</c> delegate</b>, onto whatever thread the scheduler happened to use. With no
/// <c>onError</c> to receive it there is nowhere for that fault to go, so it becomes an unhandled
/// exception on a pool thread. A resolve inside an operator chain does not do this; the bare
/// <c>Subscribe(onNext)</c> does.</para>
///
/// <para><b>What it costs, measured (#2666).</b> One Release run of <c>MeshWeaver.AI.Test</c>
/// recorded <b>476 first-chance disposed-scope stragglers across 19 seams</b>. xunit.v3 never hooks
/// <c>TaskScheduler.UnobservedTaskException</c>, so an unobserved task exception is silent — but
/// <c>AppDomain.UnhandledException</c> <i>is</i> hooked, and it takes the runner to
/// <c>Environment.Exit(2)</c>. The signature that produces is the one this repo has chased
/// repeatedly: <c>Passed! - Failed: 0</c> <b>and a non-zero exit</b> — a shard that reports
/// everything green and reds anyway, carrying a message with no stack.</para>
///
/// <para><b>Why a ratchet, and what that means the guard does NOT prove.</b> 95 sites across
/// <c>src/</c> and <c>memex/</c> at seeding — small enough that conversion is realistic, too large
/// to fix in one change. So a green run means "no NEW bare subscription landed", never "the tree is
/// clean". 🚨 Raising <see cref="Baseline"/> is not a fix; the number is in the diff so that
/// re-seeding is visible to a reviewer.</para>
///
/// <para><b>🚨 The conversion depends on the subscription's LIFETIME, and getting this backwards
/// builds a wedge.</b> <c>onError</c> is TERMINAL in Rx: the subscription is finished the moment it
/// fires.</para>
/// <list type="bullet">
/// <item><b>One-shot</b> (a write, a request/response, anything that completes after its work):
/// add the arm — <c>.Subscribe(_ =&gt; { }, ex =&gt; logger.LogWarning(ex, "… {Path}", path))</c>,
/// or a propagating arm where the caller must learn. Termination is correct here; the work is
/// over either way.</item>
/// <item><b>Long-lived</b> (a timer tick, a broadcast, an idle sweep, anything expected to keep
/// firing): handle the fault <b>inside</b> <c>onNext</c>, or <c>.Catch</c> upstream so the
/// sequence continues. An <c>onError</c> arm here converts "an unhandled exception once" into
/// "this subscription silently stopped working forever" — which is worse, and is the frame-loss
/// class this repo has been bitten by. <c>MeshNodeStreamCache.cs:1987</c> is the shape to copy:
/// the <c>try</c> lives in the <c>onNext</c>, so a fault costs one tick and not the subscription.
/// Such a site satisfies this guard by ALSO carrying an arm for the sequence's own fault, but the
/// per-item handling is the part that keeps it alive.</item>
/// </list>
/// <para>An empty <c>ex =&gt; { }</c> is not the answer to either: it trades this guard for the
/// swallow-and-continue AGENTS.md forbids.</para>
/// </summary>
public class SubscribeErrorArmRatchetGuard
{
    /// <summary>Production only. A test may subscribe bare — its failure IS the report.</summary>
    private static readonly string[] ScannedRoots = ["src", "memex"];

    /// <summary>
    /// 🚨 <b>This count UNDERCOUNTS, deliberately, and the number must not be read as complete.</b>
    ///
    /// <para>A single-argument <c>Subscribe(Foo)</c> where <c>Foo</c> is a method group has exactly
    /// the same defect as <c>Subscribe(x =&gt; Foo(x))</c> — it binds to
    /// <c>Subscribe(Action&lt;T&gt;)</c> and rethrows the same way. But
    /// <c>Subscribe(someObserver)</c>, which is FINE (an <c>IObserver</c> carries its own
    /// <c>OnError</c>), is syntactically identical: one identifier. Nothing in the text
    /// distinguishes them, and only the compiler knows which overload binds.</para>
    ///
    /// <para>So this guard flags only the case it can be SURE of — a single argument that is
    /// visibly a lambda — and accepts false negatives rather than crying wolf on every
    /// <c>Subscribe(stream)</c> in the tree. A real example it misses sits at
    /// <c>MeshNodeStreamCache.cs:613</c>: <c>?.Subscribe(OnMeshChange)</c>, a method group on a
    /// long-lived change feed. Closing that gap needs a Roslyn symbol lookup, not a regex; until
    /// then the honest description of the baseline is "at least this many".</para>
    /// </summary>

    /// <summary>
    /// 🚨 Seeded from the tree on 2026-08-30 (95), lowered to 94 by this change's conversion of the idle sweep. MAY ONLY DECREASE.
    ///
    /// <para>Unlike the timeout-literal ratchet, this one carries <b>no transitional margin</b>.
    /// There the rule post-dated the branches it would fail, so a margin protected authors from a
    /// rule they could not have known about. Here the rule is not new — AGENTS.md has always
    /// required the error arm — so a bare subscription arriving from a branch cut yesterday is a
    /// TRUE positive, and the right outcome is that it is seen.</para>
    /// </summary>
    private const int Baseline = 94;

    private static readonly Regex SubscribeCall = new(@"\.Subscribe\s*\(", RegexOptions.Compiled);

    [Fact]
    public void EverySubscriptionCarriesAnErrorArm_AndTheCountOnlyFalls()
    {
        var root = SourceScan.FindRepoRoot();
        var perFile = SourceScan.SourceFiles(root, ScannedRoots)
            .Select(f => (Path: SourceScan.Relative(root, f), Count: CountIn(f)))
            .Where(x => x.Count > 0)
            .OrderByDescending(x => x.Count)
            .ToList();
        var total = perFile.Sum(x => x.Count);

        Assert.True(total <= Baseline,
            $"🚨 Bare .Subscribe(onNext) rose to {total} (baseline {Baseline}). Rx RETHROWS a fault "
            + "out of an onNext delegate onto the scheduler's thread — with no onError there is "
            + "nowhere for it to go, so it becomes an unhandled exception that takes the xunit "
            + "runner to Environment.Exit(2). That is the 'Passed! - Failed: 0' shard that reds "
            + "anyway, with a message and no stack (#2666).\n\n"
            + "🚨 The fix depends on LIFETIME, because onError is TERMINAL. A ONE-SHOT (a write, "
            + "a request/response) takes the arm: .Subscribe(_ => { }, ex => logger.LogWarning(ex, "
            + "\"… {Path}\", path)). A LONG-LIVED subscription (a timer, a broadcast, an idle "
            + "sweep) must handle the fault INSIDE onNext, or .Catch upstream — an onError arm "
            + "there turns 'threw once' into 'stopped working forever'. Copy "
            + "MeshNodeStreamCache.cs:1987, whose try lives in the onNext. Never an empty "
            + "ex => { }: that trades this guard for the swallow-and-continue AGENTS.md forbids.\n\n"
            + "🚨 Do NOT raise the Baseline.\n"
            + "Heaviest files:\n"
            + string.Join("\n", perFile.Take(10).Select(x => $"  {x.Path} ({x.Count})")));
    }

    /// <summary>
    /// 🚨 A ratchet seeded well above the tree tolerates a large regression before it ever fires,
    /// so the slack is itself checked. The one always-correct edit to <see cref="Baseline"/> is
    /// downward, in whichever change does the converting.
    /// </summary>
    [Fact]
    public void TheBaselineStaysCloseToTheTree()
    {
        var root = SourceScan.FindRepoRoot();
        var total = SourceScan.SourceFiles(root, ScannedRoots).Sum(CountIn);

        Assert.True(Baseline - total <= 15,
            $"The tree holds {total} bare subscriptions but Baseline is {Baseline}: slack of "
            + $"{Baseline - total} is how many NEW ones could land before this fires. Lower the "
            + "baseline in the change that converted them.");
    }

    /// <summary>
    /// 🚨 Proven by MUTATION over a planted tree, running the REAL scan. The arity parse is the
    /// part that can silently stop working — a lambda contains commas, parentheses and braces, so a
    /// naive "does it contain a comma" test misreads almost every real call site.
    /// </summary>
    [Fact]
    public void TheScannerSeesWhatItClaimsTo()
    {
        var dir = Directory.CreateTempSubdirectory("subscribe-arm-selftest");
        try
        {
            var s = Directory.CreateDirectory(Path.Combine(dir.FullName, "src")).FullName;
            void W(string name, string body) => File.WriteAllText(Path.Combine(s, name), body);

            W("Bare.cs", "class A { void M() { o.Subscribe(x => Handle(x)); } }");
            W("BareBlock.cs", "class B { void M() { o.Subscribe(x => { Handle(x); }); } }");
            W("BareMultiline.cs", "class C { void M() { o\n  .Subscribe(change =>\n  {\n    Handle(change);\n  }); } }");
            W("Armed.cs", "class D { void M() { o.Subscribe(x => Handle(x), ex => Log(ex)); } }");
            W("ArmedMultiline.cs", "class E { void M() { o\n  .Subscribe(\n    x => Handle(x),\n    ex => Log(ex)); } }");
            W("Observer.cs", "class F { void M() { o.Subscribe(stream); } }");
            W("NoArgs.cs", "class G { void M() { o.Subscribe(); } }");
            W("CommaInsideLambda.cs",
                "class H { void M() { o.Subscribe(r => hub.Post(r, x => x.ResponseFor(q))); } }");
            W("NestedLambdaArgument.cs",
                "class I { void M() { o.Subscribe(MakeObserver(x => x)); } }");
            W("Prose.cs", "// never write .Subscribe(x => f(x)) without an error arm\nclass J { }");
            Directory.CreateDirectory(Path.Combine(s, "obj"));
            W(Path.Combine("obj", "Ignored.cs"), "class K { void M() { o.Subscribe(x => Handle(x)); } }");

            var found = SourceScan.SourceFiles(dir.FullName, ["src"])
                .Select(f => (Name: Path.GetFileName(f), Count: CountIn(f)))
                .Where(x => x.Count > 0)
                .ToDictionary(x => x.Name, x => x.Count);

            Assert.True(found.ContainsKey("Bare.cs"), "a bare expression-lambda subscription must be found");
            Assert.True(found.ContainsKey("BareBlock.cs"), "a bare block-lambda subscription must be found");
            Assert.True(found.ContainsKey("BareMultiline.cs"),
                "🚨 real call sites wrap — a scanner that only sees one line sees almost nothing");
            Assert.True(found.ContainsKey("CommaInsideLambda.cs"),
                "🚨 THE case a naive comma count gets wrong: the comma belongs to a nested call, so "
                + "this is still a ONE-argument Subscribe and must be flagged");
            Assert.False(found.ContainsKey("Armed.cs"), "an error arm is the fix, not a finding");
            Assert.False(found.ContainsKey("ArmedMultiline.cs"), "…including when the arms wrap");
            Assert.False(found.ContainsKey("Observer.cs"),
                "Subscribe(IObserver) carries its own OnError — not a bare subscription");
            Assert.False(found.ContainsKey("NoArgs.cs"), "Subscribe() has no onNext to rethrow from");
            Assert.False(found.ContainsKey("NestedLambdaArgument.cs"),
                "🚨 the lambda is an argument to the OBSERVER FACTORY, not the onNext — a top-level "
                + "'=>' test is what separates these two, and getting it wrong cries wolf");
            Assert.False(found.ContainsKey("Prose.cs"), "a comment describing the rule is not a violation");
            Assert.False(found.ContainsKey("Ignored.cs"), "obj/ must not be scanned");
        }
        finally { dir.Delete(recursive: true); }
    }

    /// <summary>
    /// Counts <c>.Subscribe(</c> calls whose argument list is exactly ONE argument that is itself a
    /// lambda. Balances <c>()</c>, <c>[]</c> and <c>{}</c> so a comma or an arrow nested inside the
    /// lambda body is not mistaken for a second argument or for the argument's own arrow.
    /// </summary>
    private static int CountIn(string file)
    {
        var text = SourceScan.MaskCommentsAndStrings(File.ReadAllText(file));
        var count = 0;

        foreach (Match m in SubscribeCall.Matches(text))
        {
            var depth = 0;
            var topLevelCommas = 0;
            var topLevelArrow = false;
            var any = false;

            for (var i = m.Index + m.Length; i < text.Length; i++)
            {
                var c = text[i];
                if (c is '(' or '[' or '{') depth++;
                else if (c is ')' or ']' or '}')
                {
                    if (depth == 0) break;   // the closing paren of Subscribe(
                    depth--;
                }
                else if (depth == 0)
                {
                    if (c == ',') topLevelCommas++;
                    else if (c == '=' && i + 1 < text.Length && text[i + 1] == '>') topLevelArrow = true;
                }

                if (!char.IsWhiteSpace(c)) any = true;
            }

            if (any && topLevelCommas == 0 && topLevelArrow) count++;
        }

        return count;
    }
}
