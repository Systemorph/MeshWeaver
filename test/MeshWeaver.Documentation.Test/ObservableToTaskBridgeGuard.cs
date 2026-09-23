using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Governance ratchet for the Rx-to-<see cref="System.Threading.Tasks.Task"/> bridge — the THING,
/// not one of its spellings.
///
/// <para><b>The ruling (maintainer, 2026-08-30): <i>"totask is forbidden" · "strictly" · "no totask
/// ever" · "the only place they may work is inside activities but usually even there avoid"</i>.</b>
/// The exemption this repo used to state — <i>tests are the ONLY place
/// <c>await …FirstAsync().ToTask()</c> is acceptable</i> — is RETRACTED, in
/// <c>AGENTS.md</c>, in <c>Doc/Architecture/AsynchronousCalls</c> and in the <c>/async</c> and
/// <c>/testing</c> skills.</para>
///
/// <para><b>🚨 The lesson that produced the current shape: this guard used to enforce a SPELLING.</b>
/// It matched the literal text <c>.ToTask(</c> and nothing else, and its own remarks claimed
/// <c>src/</c> was at zero <i>"with no escape hatch"</i>. That claim was FALSE as enforced, in two
/// independent ways found on 2026-08-30:
/// <list type="number">
/// <item><c>src/MeshWeaver.Reactive.Assertions/ReactiveWait.cs</c> hand-rolls the bridge out of a
/// <see cref="System.Threading.Tasks.TaskCompletionSource{TResult}"/> and a
/// <c>Subscribe</c>. Not one character of <c>.ToTask(</c> in it — invisible.</item>
/// <item><c>src/MeshWeaver.Mesh.Contract/Services/MeshServiceExtensions.cs</c> declares a method
/// LITERALLY NAMED <c>ToTask</c> and calls it as <c>ToTask&lt;bool&gt;(service.DeleteNode(path), ct)</c>.
/// The marker's leading <c>.</c> — documented as "the honest reading: any <c>.ToTask(</c> in this
/// repo is Rx's" — walked straight past a static call to a hand-rolled bridge of the same name.</item>
/// </list>
/// A text marker can only ever find the shapes someone thought to spell out. So the production rule
/// below is STRUCTURAL: it looks for the mechanism (a <c>TaskCompletionSource</c> settled from
/// inside a subscription) and for the shape (a <c>Task</c>-returning method that takes an
/// <see cref="IObservable{T}"/> and builds its own completion source), and it does not care what
/// anything is named. Rename <c>ToTask</c> to <c>First</c> and it still fires.</para>
///
/// <para><b>🚨🚨 WHERE THE LINE IS — read this before adding a marker.</b> This rule is about
/// BRIDGING an observable to a <see cref="System.Threading.Tasks.Task"/>. It is <b>NOT</b> a ban on
/// the <see cref="System.Threading.Tasks.Task"/> type, and it must never become one:
/// <list type="bullet">
/// <item><c>IIoPool</c> is the SANCTIONED async/IO boundary — <c>pool.Invoke</c> and friends take
/// and return tasks by design. Measured 2026-08-30: <c>IoPool</c> constructs no
/// <c>TaskCompletionSource</c> at all, so nothing here touches it.</item>
/// <item>Orleans grain signatures are <c>Task</c> BY CONTRACT. A grain method returning
/// <c>Task&lt;T&gt;</c> is not a defect; hand-rolling the wait INSIDE it is what this measures.</item>
/// <item>A <c>TaskCompletionSource</c> used as a plain lifecycle SIGNAL is not a bridge.
/// <c>MessageHub.hasStarted</c> is settled from <c>Start()</c>/<c>FailStartup()</c>, never from a
/// subscription callback, and is correctly invisible to every detector below.</item>
/// <item>A <c>Task</c>-returning method that CONSUMES the sanctioned bridge is not hand-rolling one.
/// <c>HubDisposalJoin.JoinDisposalAsync</c> takes an <see cref="IObservable{T}"/> and returns
/// <c>Task&lt;bool&gt;</c>, but waits through <c>ObserveCompletion</c> and builds no completion
/// source — so the detector deliberately requires BOTH the signature and an own-built completion
/// source, and that site passes.</item>
/// </list>
/// Getting this line wrong makes the guard unshippable: a rule that reds on legitimate code gets
/// suppressed, and a suppressed rule is worse than no rule.</para>
///
/// <para><b>Why the bridge is a defect at all.</b> Rx's bridge completes its
/// <see cref="System.Threading.Tasks.TaskCompletionSource{TResult}"/> from INSIDE the pipeline,
/// without <see cref="System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously"/>.
/// So <c>TrySetResult</c> resumes the awaiter <b>inline, on the signalling thread, still inside Rx's
/// trampoline</b> (<c>Producer.SubscribeRaw</c>) — and everything the continuation then does inherits
/// that flag. The captured 558-frame stack in <c>InlineObservableExtensions</c>' remarks shows it
/// escaping the pipeline entirely (#2377), and #2301 is the same mechanism parking a grain's turn
/// scheduler on the wait its own deactivation needed. It is sticky, too: <c>await</c> captures
/// <see cref="System.Threading.Tasks.TaskScheduler.Current"/> when there is no
/// <see cref="System.Threading.SynchronizationContext"/>, so once one continuation lands on that
/// scheduler every later <c>await</c> in the same method schedules onto it.</para>
///
/// <para><b>The fix at a site.</b> Compose reactively and <c>.Subscribe(onNext, onError)</c>. Where
/// an external signature genuinely forces a <see cref="System.Threading.Tasks.Task"/> — an ASP.NET
/// endpoint, an <c>ILifecycleObserver.OnStop</c>, an SDK interface you implement — wait through
/// <c>MeshWeaver.Messaging.ReactiveCompletion.ObserveCompletion</c>, which subscribes, completes
/// with <c>RunContinuationsAsynchronously</c> (the line that stops the inline resumption) and keeps
/// its error arm attached so a late fault is reported rather than orphaned. Where the result is not
/// needed, subscribe and return <c>Task.CompletedTask</c> (<c>/async</c> skill, Rule 1a).</para>
///
/// <para><b>The rules, deliberately different in strength.</b>
/// <list type="bullet">
/// <item><b>Production, unsafe form</b> (<see cref="NoProductionBridgeUsesTheUnsafeForm"/>) — ZERO,
/// no register, no allow file, no way past it but fixing the site. A hand-rolled bridge whose
/// completion source omits <c>RunContinuationsAsynchronously</c> IS the #2377 defect, whatever it is
/// called and wherever it lives.</item>
/// <item><b>Production, safe form</b> (<see cref="EveryProductionBridgeIsASanctionedImplementation"/>)
/// — allowed only at the handful of entries in <see cref="SanctionedBridges"/>, each of which is
/// VERIFIED rather than trusted by <see cref="TheSanctionedBridgeRegisterIsStillAccurate"/>. This is
/// not an allow file wearing a different hat: an allow file lists sites you tolerate and grows by
/// appending a line, whereas every entry here must still exist, must still contain a bridge, and
/// must still construct with <c>RunContinuationsAsynchronously</c> — an entry whose subject was
/// fixed or moved FAILS and tells the next author to delete it.</item>
/// <item><b><c>.ToTask(</c> in production</b> (<see cref="NoProductionCodeBridgesAnObservableToATask"/>)
/// — ZERO, unchanged. Kept as its own assertion because it names Rx's own bridge, which is never the
/// safe form and therefore never registrable.</item>
/// <item><see cref="RatchetedRoots"/> — <c>test/</c> and <c>memex/</c> — carry a seeded inventory
/// that may only SHRINK, because their sweeps land in later waves. When a wave empties one, move
/// its root into <see cref="ProductionRoots"/> in the same change.</item>
/// <item><b>Blocking bridges in production</b> (<see cref="NoNewBlockingBridgeInProductionCode"/>) —
/// a seeded inventory that may only shrink. See that method's remarks for why this one is a ratchet
/// and not a zero.</item>
/// </list></para>
///
/// <para><b>A ratchet may only SHRINK.</b> A new file, a raised count, or a raised TOTAL is a
/// failure. A line that has become stale (its site was fixed) is REPORTED, not failed: two PRs
/// closing sites concurrently would otherwise red <c>main</c> on whichever merged second, and a gate
/// that punishes the direction it is asking for teaches people to stop shrinking. Delete the stale
/// line and lower the matching budget in the same change.</para>
/// </summary>
public class ObservableToTaskBridgeGuard(ITestOutputHelper output)
{
    /// <summary>
    /// Held at ZERO for every bridge shape. The only tolerated bridges are the sanctioned
    /// IMPLEMENTATIONS in <see cref="SanctionedBridges"/>, and those are verified, not listed.
    /// </summary>
    private static readonly string[] ProductionRoots = ["src", "tools", "samples", "clients", "memex"];

    /// <summary>
    /// The last tree still carrying a seeded inventory. <c>memex/</c> left this list when its sweep
    /// reached zero (#2764) and now sits in <see cref="ProductionRoots"/> at zero tolerance —
    /// verified on the merged tree by ALL THREE detectors here, not just the text marker that
    /// emptied it: 0 structural bridges, 0 blocking sites, 0 <c>.ToTask(</c>. <c>test/</c> moves the
    /// same way when its own inventory empties.
    /// </summary>
    private static readonly string[] RatchetedRoots = ["test"];

    /// <summary>
    /// Rx's bridge as it appears in source.
    ///
    /// <para>🚨 The trailing <c>(</c> matters: it matches the CALL and not the prose, so
    /// <c>ToTask</c> named in a sentence, or the <c>System.Reactive.Threading.Tasks</c> namespace
    /// that also hosts the SAFE direction (<c>Task&lt;T&gt;.ToObservable()</c>), are untouched.</para>
    ///
    /// <para>🚨 The leading <c>.</c> is a KNOWN, DELIBERATE hole, and the reason this class no
    /// longer relies on this marker alone: a hand-rolled <c>static Task&lt;T&gt; ToTask&lt;T&gt;(IObservable&lt;T&gt;)</c>
    /// invoked without a receiver is not matched. That is exactly what
    /// <c>MeshServiceExtensions</c> did, undetected, for the whole life of the spelling-only rule.
    /// The structural detectors below are what actually close it; this marker is retained because
    /// Rx's own bridge is never the safe form and so can never be registered.</para>
    /// </summary>
    private const string Marker = ".ToTask(";

    private const string AllowFileName = "ObservableToTaskBridgeSites.allow";

    private const string ProductionBlockingAllowFileName = "ProductionBlockingBridgeSites.allow";

    // ── The COMPILER-SYNTHESIZED bridge: `await <an observable>` ──────────────────────────────────

    /// <summary>
    /// Roots held at ZERO for the direct-await shape.
    ///
    /// <para>🚨 <b>Why this rule had to be added at all, and why it is the same lesson twice.</b>
    /// Everything above finds a bridge that EXISTS IN SOURCE — Rx's <c>.ToTask(</c>, or a
    /// <c>TaskCompletionSource</c> someone typed. <c>await source</c> is the same bridge with
    /// nothing to find: the compiler calls Rx's <c>GetAwaiter()</c> extension, which builds an
    /// <see cref="System.Reactive.Subjects.AsyncSubject{T}"/> and completes the continuation from
    /// inside <c>OnCompleted</c> — INLINE on the signalling thread, exactly as the banned bridge
    /// does. <c>InlineResumptionMechanismTest</c> has measured both since 2026-08-30 and its own
    /// remarks call the swap "a no-op dressed as a fix"; this class exempted that file so it could
    /// keep demonstrating the shape, and then never looked for the shape anywhere else.</para>
    ///
    /// <para>🚨 <b>And the instrument that DID look was keyed on the wrong thing.</b>
    /// <see cref="HubReachableAsyncGuard.NoNewAwaitOfAMeshRead"/> counts an <c>await</c> only when
    /// its expression NAMES a mesh entry point, so a generic helper that takes the observable as a
    /// PARAMETER is invisible to it by construction — the mesh call sits at the caller, which does
    /// not await. That is how <c>MeshTestContext.First</c> sat at
    /// <c>await source.Take(1).Timeout(Deadline)</c> under a doc comment asserting the opposite
    /// ("Rx's own awaiter — no task bridge") with every guard in this repo green. A sweep keyed on
    /// a REDUCER (<c>await …FirstAsync()</c>) missed it for a second, independent reason: the
    /// statement has no reducer in that position at all. So this rule reads the expression's TAIL,
    /// whatever it is, and walks balanced brackets so a chain spread over four lines is ONE
    /// expression.</para>
    ///
    /// <para>🚨 <c>test/</c> JOINED this set when its seeded inventory — 223 sites in 66 files, which
    /// <c>AGENTS.md</c>, the <c>/async</c> and <c>/testing</c> skills and
    /// <c>Doc/Architecture/AsynchronousCalls</c> had all PRESCRIBED as the way off
    /// <c>.ToTask()</c> — reached zero (MeshWeaver#4756). <c>memex/</c> made the same move before it
    /// with its five DevLogin sign-in sites. The ROOT moved rather than an allow file emptying, and
    /// the ratchet (<c>DirectObservableAwaitSites.allow</c>, its budget and its shrink-only test) is
    /// deleted rather than left at zero: a ratchet promises "this may only shrink", and what is wanted
    /// is "there are none" — so the next site the matcher can see is refused anywhere in the repo,
    /// with no line to add. A suite is not exempt for being a suite: the thread a test resumes on is
    /// the one that then runs the rest of the test, its mesh teardown and — under xUnit — the runner
    /// starting the next class (#2301, #2377).</para>
    ///
    /// <para>🚨 "The matcher can see" is the honest scope, and it is the SAME scope for every root in
    /// this list: <see cref="ObservableTails"/> is a whitelist, so an await whose expression has no
    /// Rx-named tail — <c>await source;</c> on a bare variable, <c>await Observable.Return(1);</c>
    /// on a creation operator — is not counted. That is the deliberate trade documented on
    /// <see cref="ObservableTails"/> (a missed site is safe; a false positive on a Task await gets
    /// the rule suppressed). Closing it needs a SEMANTIC scan that knows the awaited expression's
    /// static type, which is the same instrument #4756's name-keyed half is waiting for.</para>
    /// </summary>
    private static readonly ImmutableArray<string> DirectAwaitZeroRoots =
        ["src", "tools", "samples", "clients", "memex", "test"];

    /// <summary>
    /// The member names that END an expression whose static type is an
    /// <see cref="IObservable{T}"/>. A whitelist, deliberately: <c>await X</c> compiles for a Task
    /// too, and nothing textual can tell the two apart in general — so the rule fires only on a
    /// tail it can NAME as Rx. That trades false negatives (safe: a site is missed) for zero false
    /// positives (fatal: a rule that reds legitimate code gets suppressed, and a suppressed rule is
    /// worse than none).
    ///
    /// <para>🚨 The LINQ-shaped names (<c>Select</c>, <c>Count</c>, <c>ToList</c>, …) are safe to
    /// list because an <see cref="System.Collections.Generic.IEnumerable{T}"/> is not awaitable —
    /// if it compiled under an <c>await</c>, it was Rx. The names deliberately NOT here are the ones
    /// a TASK-returning member also carries: <c>Delay</c> (<c>Task.Delay</c>), and Rx's creation
    /// operators (<c>Timer</c>, <c>Interval</c>, <c>Create</c>, <c>Defer</c>, …), which are never a
    /// chain's tail in practice. Measured against this repo 2026-09-18: no Task-returning
    /// <c>Timeout</c>, <c>Do</c> or <c>Catch</c> exists anywhere in it.</para>
    /// </summary>
    private static readonly ImmutableHashSet<string> ObservableTails =
        ImmutableHashSet.Create(StringComparer.Ordinal,
        [
            // Reducers — the shape a reducer-keyed scan already finds.
            "FirstAsync", "FirstOrDefaultAsync", "LastAsync", "LastOrDefaultAsync",
            "SingleAsync", "SingleOrDefaultAsync", "ElementAt", "ElementAtOrDefault",
            // 🚨 …and the operators that end a chain when the reducer is EARLIER or ABSENT. This half is
            // the whole point: `await source.Take(1).Timeout(Deadline)` has no reducer in tail position.
            "Take", "TakeLast", "TakeWhile", "TakeUntil", "Skip", "SkipLast", "SkipWhile", "SkipUntil",
            "Where", "Select", "SelectMany", "Timeout", "ToList", "ToArray", "ToDictionary",
            "Buffer", "Window", "Sample", "Throttle", "DelaySubscription", "Do", "Finally",
            "Catch", "Retry", "OnErrorResumeNext", "StartWith", "Concat", "Merge", "Zip",
            "CombineLatest", "WithLatestFrom", "Amb", "Publish", "RefCount", "Replay",
            "Materialize", "Dematerialize", "Scan", "Aggregate", "Count", "Sum", "Average",
            "Min", "Max", "Distinct", "DistinctUntilChanged", "DefaultIfEmpty", "IgnoreElements",
            "ObserveOn", "SubscribeOn", "Switch", "TimeInterval", "Timestamp", "Cast", "OfType",
            "GroupBy", "ToObservable", "AsObservable",
        ]);

    /// <summary>The <c>await</c> keyword, word-bounded so <c>Await</c> and <c>awaited</c> are not it.</summary>
    private static readonly Regex AwaitKeyword =
        new(@"\bawait\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary><c>await foreach</c> / <c>await using</c> — statements, not an awaited expression.</summary>
    private static readonly Regex AwaitStatementForm =
        new(@"^\s+(?:foreach|using)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The ONE exemption from the <c>.ToTask(</c> scan, and it is not an escape hatch: the test
    /// whose entire PURPOSE is to demonstrate the banned shape's behaviour, by measuring that it
    /// resumes its awaiter on the signalling thread. A rule about a defect needs one place that may
    /// still exhibit the defect, or it cannot be evidenced — and this one is what stops the
    /// plausible "simplification" of swapping the bridge for a direct <c>await</c> of the
    /// observable, which resumes inline in exactly the same way.
    ///
    /// <para>🚨 It is verified rather than trusted: <see cref="TheExemptedPinningTestStillPinsTheShape"/>
    /// fails if this file stops existing or stops containing the shape, so the exemption cannot
    /// quietly decay into a hole someone parks a real bridge in.</para>
    /// </summary>
    private static readonly string[] ExemptPinningFiles =
        ["test/MeshWeaver.Messaging.Hub.Test/InlineResumptionMechanismTest.cs"];

    /// <summary>
    /// The seeded inventory's size for <see cref="RatchetedRoots"/>. Per-file entries stop a new
    /// site in a file that already carries the shape; this stops the list as a WHOLE from growing —
    /// including by the trick of adding a new file's line. Lower it whenever you delete or lower an
    /// entry.
    /// </summary>
    private const int TotalBudget = 2;

    /// <summary>The seeded inventory's size for <see cref="NoNewBlockingBridgeInProductionCode"/>.</summary>
    private const int ProductionBlockingTotalBudget = 9;

    /// <summary>
    /// A bridge found in source: where it is, and which detector saw it.
    /// </summary>
    private readonly record struct BridgeSite(string File, int Line, string Shape);

    /// <summary>
    /// The bridge IMPLEMENTATIONS production code is allowed to contain, each with the reason it
    /// cannot simply be deleted. Every entry is checked for accuracy by
    /// <see cref="TheSanctionedBridgeRegisterIsStillAccurate"/>: it must still exist, must still
    /// contain a bridge, and must still be the SAFE form. An entry whose subject was fixed, moved
    /// or renamed FAILS — an exemption that outlives its subject is how the next hole gets hidden.
    /// </summary>
    private static readonly (string File, string Why)[] SanctionedBridges =
    [
        ("src/MeshWeaver.Messaging.Hub/ReactiveCompletion.cs",
            "THE sanctioned bridge. Every failure message in this class tells authors to wait "
            + "through ObserveCompletion, so the one implementation of it must be allowed to exist. "
            + "It is the definition of the safe form: RunContinuationsAsynchronously, plus an error "
            + "arm that stays attached so a late fault is reported rather than orphaned."),

        ("src/MeshWeaver.Reactive.Assertions/ReactiveWait.cs",
            "The same guarantee for MeshWeaver.Reactive.Assertions, which ships standalone against "
            + "System.Reactive alone and therefore CANNOT reference the mesh to call "
            + "ObserveCompletion. This package exists to hand tests a Task to await, so it cannot "
            + "be bridge-free — which makes it test infrastructure sitting in a production root. "
            + "🚨 FOLLOW-UP: the project moves to test/ so it falls under the ratchet instead of "
            + "this zero rule; deferred out of this change because MeshWeaver.Reactive.Assertions."
            + "Test is being rewritten under PR #2750 and a rename underneath it turns a clean "
            + "merge into a path-rename conflict. When the move lands, this entry goes stale and "
            + "the register test above will say so."),

        ("src/MeshWeaver.Messaging.Hub/ObservableAwait.cs",
            "The ONE wait, introduced by #2750 for the test tree when 1,538 `.ToTask(` call sites "
            + "were swept, and moved by #2771 from MeshWeaver.Fixture into the messaging assembly so "
            + "PRODUCT code has a deadlock-safe bridge too: the sibling repos carried 65 raw "
            + "`.ToTask(` sites in product source that could not be swept while the only safe "
            + "wrapper lived in a test assembly. Same category as ReactiveWait: infrastructure whose "
            + "entire PURPOSE is to hand a caller a Task to await, so it cannot be bridge-free. "
            + "(The type-forwards gate was consulted: the type is one day younger than the last "
            + "release, so no shipped package binds the old name — see scripts/type-forwards.allow.) "
            + "swept. Same category as ReactiveWait: infrastructure whose entire PURPOSE is to hand "
            + "It is a FAITHFUL "
            + "ToTask (last value, faults on an empty sequence) — deliberately, because settling on "
            + "the first notification would have silently changed 462 call sites that do not reduce "
            + "to a single element; only the continuation scheduling differs. Safe form. "
            + "🚨 This entry is the guard's first live catch: ObservableAwait landed on main hours "
            + "after these detectors were written, contains not one character of `.ToTask(`, and "
            + "the spelling-only rule would have waved it through."),

        ("src/MeshWeaver.Mesh.Contract/Services/MeshServiceExtensions.cs",
            "The *Async CRUD shim, already governed by MeshServiceHasNoTaskShimGuard (one assembly, "
            + "three verbs, may shrink and never grow). It cannot simply be deleted: measured "
            + "2026-08-27, every caller left in THIS repo is a test, but MeshWeaver.Reinsurance has "
            + "58 call sites across 22 in-mesh Source/*.cs files, which compile at RUNTIME in the "
            + "portal — deleting the shim turns 22 NodeTypes CompileError and a CompileError "
            + "NodeType refuses portal readiness, with green CI proving nothing. "
            + "🚨 THIS IS THE SITE THE OLD SPELLING-ONLY RULE COULD NOT SEE: it declares a method "
            + "literally named ToTask and invokes it as `ToTask<bool>(service.DeleteNode(path), ct)` "
            + "— no receiver, so the `.ToTask(` marker's leading dot walked straight past it. It "
            + "was ALSO the unsafe form (a bare `new TaskCompletionSource<T>()`) until 2026-08-30, "
            + "when adding RunContinuationsAsynchronously fixed a live inline-resumption hazard on "
            + "the hub-reachable in-mesh callers. Exit: port those 58 sites, then move the shim to "
            + "MeshWeaver.Fixture (MeshWeaver.Reinsurance #102)."),

        ("src/MeshWeaver.Hosting.Orleans/MessageHubGrain.cs",
            "An Orleans grain method whose return type is Task BY CONTRACT, waiting on HubReady to "
            + "deliver. It cannot route through ObserveCompletion as that stands: ObserveCompletion "
            + "FAULTS the task on an error, whereas this site must map an activation fault to a "
            + "SUCCESSFUL result carrying a classified DeliveryFailure (ErrorType.Unavailable, "
            + "#1693) — faulting instead would re-report an availability fact as a defect. It is "
            + "the safe form, so the inline-resumption defect is not present. A ReactiveCompletion "
            + "overload that maps faults to values would retire this entry."),
    ];

    /// <summary>
    /// A <c>TaskCompletionSource</c> being CONSTRUCTED, in every spelling C# allows.
    ///
    /// <para>🚨 The optional qualifier chain is not decoration. A bare
    /// <c>new\s+TaskCompletionSource</c> would miss
    /// <c>new System.Threading.Tasks.TaskCompletionSource&lt;T&gt;(…)</c> and
    /// <c>new global::System.Threading.Tasks.TaskCompletionSource&lt;T&gt;(…)</c> — and missing a
    /// construction is worse than missing a call site, because
    /// <see cref="IsSafeForm"/> would then classify a file carrying an UNSAFE qualified
    /// construction as safe, having checked nothing. That is the same
    /// spelling-instead-of-the-thing hole this whole class exists to close, one level down; found
    /// in review of the change that introduced it, and pinned by the fully-qualified fixture in
    /// <see cref="TheScannerCatchesAHandRolledBridgeInAProductionRoot"/>.</para>
    /// </summary>
    private static readonly Regex TcsConstruction =
        new(@"new\s+(?:global::)?(?:[A-Za-z_]\w*\s*\.\s*)*TaskCompletionSource\s*(?:<[^;{}()]*>)?\s*\(",
            RegexOptions.Compiled);

    /// <summary>
    /// The target-typed spelling: <c>TaskCompletionSource&lt;T&gt; x = new(…)</c>, where the type is
    /// on the LEFT and <c>new</c> carries no name at all. <see cref="TcsConstruction"/> cannot see
    /// it by construction, so a file could otherwise hide an unsafe completion source behind
    /// <c>= new()</c>.
    /// </summary>
    private static readonly Regex TcsTargetTypedConstruction =
        new(@"\bTaskCompletionSource\s*(?:<[^;{}()]*>)?\s+[A-Za-z_]\w*\s*=\s*new\s*\(",
            RegexOptions.Compiled);

    /// <summary>Every completion-source construction in <paramref name="code"/>, both spellings.</summary>
    private static IEnumerable<Match> TcsConstructions(string code) =>
        TcsConstruction.Matches(code).Concat(TcsTargetTypedConstruction.Matches(code));

    private static readonly Regex SettleCall =
        new(@"\b(?:TrySetResult|TrySetException|TrySetCanceled|SetResult|SetException)\s*\(",
            RegexOptions.Compiled);

    private static readonly Regex SubscribeCall =
        new(@"\.Subscribe\s*\(", RegexOptions.Compiled);

    private static readonly Regex TaskShapedMethod =
        new(@"\b(?:Task|ValueTask)\s*(?:<[^()=;{}]*?>)?\s+([A-Za-z_]\w*)\s*(?:<[^()<>]*>)?\s*\(",
            RegexOptions.Compiled);

    private static readonly Regex ObserverImplementation =
        new(@"\bclass\s+\w+(?:<[^>{}]*>)?\s*(?:\([^()]*\))?\s*:\s*[^{;]*\bIObserver\s*<",
            RegexOptions.Compiled);

    /// <summary>The blocking bridges, as they appear in source.</summary>
    ///
    /// <remarks>
    /// 🚨 <c>.Result</c> is deliberately NOT here, and the reasoning is the same one
    /// <see cref="BlockingBridgeInTestRatchetGuard"/> records: it is overwhelmingly a domain
    /// property in this repo — <c>ToolCall.Result</c>, <c>PatchResult.Result</c>,
    /// <c>CompileResult.Result</c> — so a marker matching it would flag a hundred innocent reads to
    /// catch one bridge. A rule that cries wolf gets suppressed, and a suppressed rule is worse than
    /// no rule. Narrowing it to <c>.Result</c> preceded by a known task-returning expression was
    /// considered and rejected: the narrowing itself would need a call-graph to be sound, and a
    /// marker that is right most of the time is exactly the "spelling, not the thing" failure this
    /// class exists to end. The <c>.GetAwaiter().GetResult()</c> spelling below IS unambiguous.
    ///
    /// <para>🚨 <c>.Wait()</c> is matched with EMPTY parentheses on purpose. <c>.Wait(timeout)</c>
    /// is a different animal and is legitimate in the two places it appears: <c>IoPool</c>'s
    /// <c>_gate.Wait(DrainTimeout)</c> is the <c>SemaphoreSlim</c> sealed inside <c>IoPool</c> that
    /// AGENTS.md explicitly sanctions, and <c>HubDisposalJoin</c>'s <c>joined.Wait(budget)</c> is a
    /// documented, deliberate <c>Task.Wait(TimeSpan)</c> chosen so expiry comes back as a bool
    /// rather than an exception. Both are bounded and neither can wedge unboundedly. The empty-paren
    /// spelling is the UNBOUNDED park, which is the defect.</para>
    /// </remarks>
    private static readonly string[] BlockingMarkers = [".Wait()", ".GetAwaiter().GetResult()"];

    /// <summary>
    /// The hard half of the original rule: Rx's own <c>.ToTask(</c> is at zero in production and
    /// stays there. No allow file is read, so there is no line to add and no budget to raise.
    /// </summary>
    [Fact]
    public void NoProductionCodeBridgesAnObservableToATask()
    {
        var root = SourceScan.FindRepoRoot();
        var found = Scan(root, ProductionRoots);

        Assert.True(found.Count == 0,
            "🚨 `.ToTask(` is FORBIDDEN in production code — zero, with no allow-list "
            + "(maintainer, 2026-08-30: \"no ToTask ever\"; \"in src especially we should have "
            + "zero\"). Rx completes the Task from inside its own pipeline without "
            + "RunContinuationsAsynchronously, so the awaiter resumes INLINE on the signalling "
            + "thread, still inside Rx's trampoline, and everything the continuation does inherits "
            + "that (#2377, #2301).\n"
            + "Fix the site: compose reactively and .Subscribe(onNext, onError); where an external "
            + "signature forces a Task, wait through ReactiveCompletion.ObserveCompletion; where "
            + "the result is not needed, subscribe and return Task.CompletedTask.\n"
            + string.Join("\n", found
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"  {kv.Key} ({kv.Value})")));
    }

    /// <summary>
    /// 🚨 The rule that closes the spelling hole, at its hardest setting: a hand-rolled bridge whose
    /// completion source omits <c>RunContinuationsAsynchronously</c> IS the #2377 inline-resumption
    /// defect, and there is NO register, NO allow file and NO exemption for it. The only way past
    /// this assertion is to stop hand-rolling the bridge, or — where the shape is genuinely
    /// unavoidable — to make it the safe form AND get it into
    /// <see cref="SanctionedBridges"/> with a reason.
    /// </summary>
    [Fact]
    public void NoProductionBridgeUsesTheUnsafeForm()
    {
        var root = SourceScan.FindRepoRoot();
        var offenders = ScanBridges(root, ProductionRoots)
            .Where(g => !IsSafeForm(Path.Combine(root, g.Key.Replace('/', Path.DirectorySeparatorChar))))
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToArray();

        Assert.True(offenders.Length == 0,
            "🚨 A hand-rolled observable→Task bridge in production code is missing "
            + "TaskCreationOptions.RunContinuationsAsynchronously — which IS the defect, not a "
            + "detail. Without it the completion source resumes its awaiter INLINE on whichever "
            + "thread signalled: a hub's action block, a grain's turn scheduler, an Rx trampoline. "
            + "The caller then finishes its work there, holding a scheduler that the work it is "
            + "about to wait on needs (#2377, #2301).\n"
            + "There is no allow file for this and no line to add. Either wait through "
            + "ReactiveCompletion.ObserveCompletion, or stay reactive and Subscribe.\n"
            + string.Join("\n", offenders.Select(g =>
                $"  {g.Key}\n" + string.Join("\n", g.Select(s => $"      line {s.Line}: {s.Shape}")))));
    }

    /// <summary>
    /// The safe-form half: a bridge that IS built correctly is still a bridge, so production may
    /// only contain the ones in <see cref="SanctionedBridges"/>. A new one — however carefully
    /// written — is a second implementation of something this repo has exactly one of.
    /// </summary>
    [Fact]
    public void EveryProductionBridgeIsASanctionedImplementation()
    {
        var root = SourceScan.FindRepoRoot();
        var sanctioned = SanctionedBridges.Select(s => s.File).ToHashSet(StringComparer.Ordinal);

        var unsanctioned = ScanBridges(root, ProductionRoots)
            .Where(g => !sanctioned.Contains(g.Key))
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToArray();

        Assert.True(unsanctioned.Length == 0,
            "🚨 A NEW hand-rolled observable→Task bridge appeared in production code. It may even "
            + "be built correctly — that is not the point: this repo has exactly ONE bridge "
            + "implementation (ReactiveCompletion.ObserveCompletion), plus the standalone duplicate "
            + "in MeshWeaver.Reactive.Assertions that cannot reference the mesh. A second one is a "
            + "second thing to keep correct, and the last time this happened it went unnoticed "
            + "because the guard was matching a spelling.\n"
            + "Wait through ReactiveCompletion.ObserveCompletion, or stay reactive and Subscribe. "
            + "If the shape is genuinely unavoidable, it must be the safe form AND carry an entry "
            + "in SanctionedBridges explaining why ObserveCompletion cannot serve it.\n"
            + string.Join("\n", unsanctioned.Select(g =>
                $"  {g.Key}\n" + string.Join("\n", g.Select(s => $"      line {s.Line}: {s.Shape}")))));
    }

    /// <summary>
    /// Keeps <see cref="SanctionedBridges"/> honest, in the shape the maintainer specified: every
    /// entry must still EXIST, must still CONTAIN a bridge, and must still be the SAFE form. The
    /// last of those is the property that makes an entry tolerable at all, so it is enforced rather
    /// than assumed — and the first two make the register self-deleting, so the entry cannot outlive
    /// its subject and become a hole someone parks a real bridge in.
    /// </summary>
    [Fact]
    public void TheSanctionedBridgeRegisterIsStillAccurate()
    {
        var root = SourceScan.FindRepoRoot();

        foreach (var (relative, why) in SanctionedBridges)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(File.Exists(path),
                $"{relative} is registered as a sanctioned bridge but no longer exists — it was "
                + "moved, renamed or deleted. DELETE THE ENTRY: a register line that outlives its "
                + "subject checks nothing while looking like it does.\n"
                + $"  The reason it carried: {why}");

            var sites = BridgeSitesIn(File.ReadAllText(path)).ToArray();
            Assert.True(sites.Length > 0,
                $"{relative} is registered as a sanctioned bridge but no longer contains one. "
                + "Either it was fixed (DELETE THE ENTRY — and if this was the last one, consider "
                + "whether the register should be empty) or the detectors broke, which would make "
                + "every zero above pass on no evidence. Check the detectors first.\n"
                + $"  The reason it carried: {why}");

            Assert.True(IsSafeForm(path),
                $"{relative} is registered as a sanctioned bridge but is NOT the safe form: at "
                + "least one TaskCompletionSource in it is constructed without "
                + "TaskCreationOptions.RunContinuationsAsynchronously. That flag is the entire "
                + "reason a bridge here is tolerable — without it this file resumes its awaiter "
                + "inline on the signalling thread (#2377). Restore the flag; the register does not "
                + "cover an unsafe bridge.\n"
                + $"  The reason it carried: {why}");

            output.WriteLine($"sanctioned bridge OK: {relative} ({sites.Length} site(s))");
        }
    }

    /// <summary>
    /// 🚨 THE NEGATIVE CONTROL, and the reason to believe anything above. The production
    /// assertions' evidence is an EMPTY result, and "the scanner found nothing" is indistinguishable
    /// from "the scanner is broken" — the skip-trapdoor shape AGENTS.md forbids in a gate. This test
    /// therefore builds a REAL directory tree with a REAL production root, drops a hand-rolled
    /// bridge into it that contains not one character of <c>.ToTask(</c>, and drives the SAME
    /// scanner the assertions use over it.
    ///
    /// <para>It asserts the whole pipeline, not just a regex: root enumeration, file selection,
    /// comment/string masking, both structural detectors, and the safe-form classification. And it
    /// asserts the discrimination in both directions — the unsafe fixture is caught AND classified
    /// unsafe, the safe fixture is caught AND classified safe, and a consumer of the sanctioned
    /// bridge is NOT caught at all.</para>
    ///
    /// <para>Mutation-checked on 2026-08-30: with the structural detectors reverted to the
    /// <c>.ToTask(</c> marker alone, this test goes GREEN on the same fixture tree — which is
    /// precisely the false pass the old guard was giving on the real one.</para>
    /// </summary>
    [Fact]
    public void TheScannerCatchesAHandRolledBridgeInAProductionRoot()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "mw-bridge-guard-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(sandbox, "src", "Fixture");
        Directory.CreateDirectory(src);
        try
        {
            // Not one character of `.ToTask(` anywhere below — that is the point.
            File.WriteAllText(Path.Combine(src, "UnsafeBridge.cs"),
                """
                using System;
                using System.Threading.Tasks;

                public static class UnsafeBridge
                {
                    public static Task<T> Await<T>(IObservable<T> source)
                    {
                        var completion = new TaskCompletionSource<T>();
                        source.Subscribe(
                            v => completion.TrySetResult(v),
                            e => completion.TrySetException(e));
                        return completion.Task;
                    }
                }
                """);

            File.WriteAllText(Path.Combine(src, "SafeBridge.cs"),
                """
                using System;
                using System.Threading.Tasks;

                public static class SafeBridge
                {
                    public static Task<T> Await<T>(IObservable<T> source)
                    {
                        var completion = new TaskCompletionSource<T>(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                        source.Subscribe(v => completion.TrySetResult(v));
                        return completion.Task;
                    }
                }
                """);

            // 🚨 The same bridge in spellings a bare `new TaskCompletionSource` regex cannot see:
            // fully qualified, and target-typed with the type on the LEFT. Raised in review of the
            // change that introduced these detectors — missing a CONSTRUCTION is worse than missing
            // a call, because the safe-form classifier would then pass the file having checked
            // nothing.
            File.WriteAllText(Path.Combine(src, "QualifiedUnsafeBridge.cs"),
                """
                using System;

                public static class QualifiedUnsafeBridge
                {
                    public static System.Threading.Tasks.Task<T> Await<T>(IObservable<T> source)
                    {
                        var completion = new System.Threading.Tasks.TaskCompletionSource<T>();
                        source.Subscribe(v => completion.TrySetResult(v));
                        return completion.Task;
                    }
                }
                """);

            File.WriteAllText(Path.Combine(src, "TargetTypedUnsafeBridge.cs"),
                """
                using System;
                using System.Threading.Tasks;

                public static class TargetTypedUnsafeBridge
                {
                    public static Task<T> Await<T>(IObservable<T> source)
                    {
                        TaskCompletionSource<T> completion = new();
                        source.Subscribe(v => completion.TrySetResult(v));
                        return completion.Task;
                    }
                }
                """);

            // A Task-returning method over an IObservable that does NOT hand-roll the wait. This is
            // HubDisposalJoin.JoinDisposalAsync's shape, and it must not be flagged — if it were,
            // the rule would be banning the Task type rather than the bridge.
            File.WriteAllText(Path.Combine(src, "Consumer.cs"),
                """
                using System;
                using System.Threading.Tasks;

                public static class Consumer
                {
                    public static async Task<bool> Join(IObservable<int> source)
                    {
                        await source.ObserveCompletion(_ => { }).ConfigureAwait(false);
                        return true;
                    }
                }
                """);

            var found = ScanBridges(sandbox, ["src"]).ToDictionary(g => g.Key, g => g.ToArray(),
                StringComparer.Ordinal);

            Assert.True(found.ContainsKey("src/Fixture/UnsafeBridge.cs"),
                "The scanner did not find a hand-rolled TaskCompletionSource bridge sitting in a "
                + "production root. Every zero this class asserts is therefore worthless — it "
                + "would be passing on a scan that cannot see the defect. Found: ["
                + string.Join(", ", found.Keys.OrderBy(k => k, StringComparer.Ordinal)) + "]");

            Assert.True(found.ContainsKey("src/Fixture/SafeBridge.cs"),
                "The scanner missed the SAFE hand-rolled bridge. Both tiers depend on finding it: "
                + "the safe form is still a bridge, and is allowed only by the register.");

            foreach (var qualified in new[] { "QualifiedUnsafeBridge", "TargetTypedUnsafeBridge" })
            {
                Assert.True(found.ContainsKey($"src/Fixture/{qualified}.cs"),
                    $"🚨 The scanner missed {qualified}.cs — a hand-rolled bridge written in a "
                    + "spelling the construction regex does not cover (fully qualified, or "
                    + "target-typed with the type on the left). That is a spelling escape hatch in "
                    + "the very rule that exists to end spelling escape hatches.");

                Assert.False(IsSafeForm(Path.Combine(src, $"{qualified}.cs")),
                    $"🚨 The safe-form classifier called {qualified}.cs SAFE. It is not — its "
                    + "completion source omits RunContinuationsAsynchronously. A construction the "
                    + "regex cannot see is vacuously 'all safe', so the unsafe-form rule would pass "
                    + "having checked nothing.");
            }

            Assert.False(found.ContainsKey("src/Fixture/Consumer.cs"),
                "🚨 The scanner flagged a Task-returning method that merely CONSUMES the sanctioned "
                + "bridge. That is the false positive that would make this rule a ban on the Task "
                + "type — which would break IIoPool and every Orleans grain signature, and get the "
                + "whole guard suppressed. Require an own-built completion source, not just the "
                + "signature.");

            Assert.False(IsSafeForm(Path.Combine(src, "UnsafeBridge.cs")),
                "The safe-form classifier called a bare `new TaskCompletionSource<T>()` safe. "
                + "NoProductionBridgeUsesTheUnsafeForm would then pass on the real defect.");

            Assert.True(IsSafeForm(Path.Combine(src, "SafeBridge.cs")),
                "The safe-form classifier called a correctly-constructed completion source unsafe. "
                + "The register would red on its own sanctioned entries.");
        }
        finally
        {
            try { Directory.Delete(sandbox, recursive: true); }
            catch (IOException) { /* a sandbox left behind is not a test failure */ }
            catch (UnauthorizedAccessException) { /* ditto */ }
        }
    }

    /// <summary>
    /// The shrinking half: the trees whose sweep is still in flight may not GROW.
    /// </summary>
    [Fact]
    public void NoNewBridgeInTheTreesStillBeingSwept()
    {
        var root = SourceScan.FindRepoRoot();
        var allowed = SourceScan.ReadAllowFile(Path.Combine(root, "test", AllowFileName), AllowFileName);
        var found = Scan(root, RatchetedRoots);

        var failures = new List<string>();

        foreach (var (file, count) in found.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!allowed.TryGetValue(file, out var budget))
                failures.Add(
                    $"  NEW SITE   {file} ({count}) — await the observable through "
                    + "ReactiveCompletion.ObserveCompletion, or stay reactive and Subscribe. Do NOT "
                    + "add a line to " + AllowFileName + ".");
            else if (count > budget)
                failures.Add(
                    $"  MORE       {file} ({count} > {budget} allowed) — a bridge was ADDED to a "
                    + "file that already carries the shape.");
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
            "`.ToTask(` bridges an observable to a Task whose awaiter Rx resumes INLINE on the "
            + "signalling thread, inside its own trampoline — so a bridge written \"only in a "
            + "test\" changes how the code under test runs (maintainer, 2026-08-30: \"no ToTask "
            + "ever\"). These trees are mid-sweep: the inventory may shrink, never grow.\n"
            + string.Join("\n", failures));
    }

    // ── The direct-await rules ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 <c>await &lt;an observable&gt;</c> is at ZERO in <see cref="DirectAwaitZeroRoots"/>, with no
    /// allow file and no line to add.
    ///
    /// <para>The site this rule was written for is <c>MeshWeaver.Testing.InMesh.MeshTestContext</c>
    /// — <c>await source.Take(1).Timeout(Deadline)</c>, under a doc comment offering <i>"Rx's own
    /// awaiter — no task bridge"</i> as the SAFETY property. That assembly is the IN-MESH harness:
    /// its cases execute inside the running portal, so the thread that signals is a hub's action
    /// block or a grain's turn scheduler, and the remainder of every case that called it continued
    /// there. Nothing in this repo saw it: no <c>.ToTask(</c>, no <c>TaskCompletionSource</c>, and
    /// no mesh entry point named in the awaited expression for
    /// <see cref="HubReachableAsyncGuard"/> to key on.</para>
    ///
    /// <para><b>The fix at a site</b> is <c>.Await(ct)</c>
    /// (<c>MeshWeaver.Messaging.ObservableAwait</c>) or <c>.ObserveCompletion(reportLateFault, ct)</c>
    /// — both complete through a <c>TaskCompletionSource</c> built with
    /// <c>RunContinuationsAsynchronously</c>, which is the one line that stops the inline resume —
    /// or, better, staying reactive and subscribing.</para>
    /// </summary>
    [Fact]
    public void NoProductionCodeAwaitsAnObservableDirectly()
    {
        var root = SourceScan.FindRepoRoot();
        var found = ScanDirectAwaits(root, DirectAwaitZeroRoots);

        Assert.True(found.Count == 0,
            "🚨 `await <an observable>` is Rx's own awaiter, and it is NOT a safe alternative to "
            + "`.ToTask()` — it is the SAME defect with nothing in source to grep for. Rx's awaiter "
            + "is an AsyncSubject<T> that completes its continuation from inside OnCompleted, so "
            + "the caller resumes INLINE on whichever thread signalled (a hub action block, a "
            + "grain's turn scheduler, an Rx trampoline), and because `await` captures "
            + "TaskScheduler.Current every later await in the same method inherits it (#2301, "
            + "#2377). InlineResumptionMechanismTest measures both sides.\n"
            + "Fix the site: `.Await(ct)` (MeshWeaver.Messaging.ObservableAwait) or "
            + "`.ObserveCompletion(reportLateFault, ct)` — or stay reactive and Subscribe. There is "
            + "no allow file for this root set.\n"
            + string.Join("\n", found
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"  {kv.Key} ({kv.Value})")));
    }

    /// <summary>
    /// 🚨 EVERY zero-tolerance root EXISTS, and each one's contribution is PRINTED (review finding on
    /// #5153).
    ///
    /// <para><c>SourceScan.SourceFiles</c> drops a root that does not exist and raises only when the
    /// COMBINED result is empty. With one root that is a perfect instrument; with five it is not —
    /// <c>src/</c> alone keeps the combined set enormous, so a renamed, moved or mistyped
    /// <c>memex/</c> would leave <see cref="NoProductionCodeAwaitsAnObservableDirectly"/> and the
    /// three <c>.ToTask(</c> rules reporting zero **having scanned nothing there**, with nothing red.
    /// That is the failure this repository names most often: a guard whose subject moved and whose
    /// roots did not, answering green.</para>
    ///
    /// <para>The exposure is older than #5153 — <see cref="ProductionRoots"/> has carried five roots
    /// since <c>memex/</c>'s <c>.ToTask(</c> sweep reached zero (#2764) — but widening
    /// <see cref="DirectAwaitZeroRoots"/> to the same list is what made it worth closing: before it,
    /// a vanished <c>memex/</c> at least turned that root's allow-file entry STALE and printed it. A
    /// zero root has no allow file, so it has no such signal.</para>
    ///
    /// <para>🚨 <b>EXISTENCE is the assertion; a count of zero is REPORTED, not failed</b> — and that
    /// distinction is what this test measured on its first run. <c>clients/</c> is a declared root
    /// that holds Python, Docker and a README and <b>no</b> <c>.cs</c>, <c>.razor</c> or <c>.csx</c>
    /// at all, so it has contributed nothing to any of these rules since it was listed. That is not a
    /// broken scan — the root is there, it is polyglot, and the day someone adds a C# file to it the
    /// rules cover it automatically. Failing on it would push the next reader toward DELETING the
    /// root, which is the one change that WOULD leave a future file there unguarded. So the rule is
    /// the one the <c>/health</c> census follows: a root that does not exist FAILS, and every root's
    /// count is printed, so that "I measured nothing there" and "I measured, and it was clean" are two
    /// different printed sentences rather than one silence.</para>
    /// </summary>
    [Fact]
    public void EveryZeroToleranceRootExistsAndItsContributionIsPrinted()
    {
        var root = SourceScan.FindRepoRoot();
        var declared = ProductionRoots
            .Concat(DirectAwaitZeroRoots)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToArray();

        var missing = new List<string>();
        foreach (var scanned in declared)
        {
            var dir = Path.Combine(root, scanned);
            if (!Directory.Exists(dir))
            {
                missing.Add($"  {scanned} — no such directory under {root}");
                continue;
            }

            // Counted through the SAME extension and exclusion predicate SourceScan.SourceFiles uses,
            // so the number printed is the number the rules scanned — not a re-implementation that
            // could agree today and drift tomorrow. A zero is legitimate for a polyglot root and is
            // printed rather than failed; see the remarks.
            var count = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Count(f => Path.GetExtension(f) is ".cs" or ".razor" or ".csx"
                            && !SourceScan.IsExcluded(root, f));
            output.WriteLine($"{scanned}: {count} file(s) reach these rules"
                             + (count == 0 ? "  ← carries no C# at all; covered the moment it does" : ""));
        }

        Assert.True(missing.Count == 0,
            "🚨 A declared zero-tolerance root DOES NOT EXIST, so every rule over it is reporting zero "
            + "having checked nothing there. `SourceScan.SourceFiles` only raises when the COMBINED "
            + "result is empty, and `src/` alone keeps it full — so this is invisible to the rules "
            + "themselves. Fix the root name or the repository layout; never drop the root to make this "
            + "pass, because dropping it is what leaves the next file there unguarded.\n"
            + string.Join("\n", missing));
    }

    /// <summary>
    /// 🚨 THE FALSIFICATION for the direct-await matcher, re-run every CI run against planted text.
    /// Both directions, because this matcher's two plausible failures are opposite and both are
    /// silent: reading the tail too NARROWLY reports a clean tree (that is how the site this rule
    /// exists for was missed), and truncating the expression too EAGERLY reports an already-fixed
    /// site as an offender.
    ///
    /// <para>The <c>,</c>-inside-a-generic case is not hypothetical. A scanner that ends the
    /// awaited expression at the first comma turns
    /// <c>await source.Select&lt;TIn, TOut&gt;(f).Await(ct)</c> — already converted — into a tail of
    /// <c>Select</c>, i.e. an offender. The parallel sweep of MeshWeaver.Plugins hit exactly
    /// that.</para>
    /// </summary>
    [Fact]
    public void TheDirectAwaitMatcherSeesWhatItClaimsTo()
    {
        // 🚨 THE SITE ITSELF, verbatim from the revision that shipped it. This is the assertion the
        // rule was watched RED on: with MeshTestContext.First reverted, the file is under
        // DirectAwaitZeroRoots and NoProductionCodeAwaitsAnObservableDirectly fails naming it.
        Assert.Equal(1, CountDirectObservableAwaitsIn(
            "public async Task<T> First<T>(IObservable<T> source) =>\n"
            + "    await source.Take(1).Timeout(Deadline);"));

        // The reducer spellings a reducer-keyed scan already finds…
        Assert.Equal(1, CountDirectObservableAwaitsIn("var n = await stream.FirstAsync();"));
        Assert.Equal(1, CountDirectObservableAwaitsIn("var n = await stream.FirstOrDefaultAsync();"));
        Assert.Equal(1, CountDirectObservableAwaitsIn("var n = await stream.LastOrDefaultAsync();"));
        // …and the tails it does not.
        Assert.Equal(1, CountDirectObservableAwaitsIn("await stream.FirstAsync().Timeout(budget);"));
        Assert.Equal(1, CountDirectObservableAwaitsIn("await stream.Where(x => x is not null).Take(1);"));
        Assert.Equal(1, CountDirectObservableAwaitsIn("var all = await stream.ToList();"));

        // Spelled across lines — one expression, which a line-keyed grep cannot see.
        Assert.Equal(1, CountDirectObservableAwaitsIn(
            "var node = await workspace\n    .GetMeshNodeStream(path)\n    .Take(1)\n"
            + "    .Timeout(TestTimeouts.Convergence);"));

        // Nested in an argument list: the comma ENDS the awaited expression, so the following
        // argument's own chain is never read as this await's tail.
        Assert.Equal(0, CountDirectObservableAwaitsIn(
            "Assert.Equal(await ThingAsync(), other.Take(1));"));

        // 🚨 …but a comma inside a GENERIC ARGUMENT LIST does not, or an already-converted site
        // reads as an offender. Both orderings, because the skip has to work mid-chain too.
        Assert.Equal(0, CountDirectObservableAwaitsIn(
            "var x = await source.Select<TIn, TOut>(f).Await(ct);"));
        Assert.Equal(1, CountDirectObservableAwaitsIn(
            "var x = await hub.Observe<Request, Response>(r).Take(1).Timeout(budget);"));
        Assert.Equal(0, CountDirectObservableAwaitsIn(
            "var x = await hub.Observe<Request, Response>(r).Take(1).Await(ct);"));

        // 🚨 GROUPED, which the first version of TailMemberOf could not see at all: the leading `(`
        // put every member access at depth ≥ 1 and the tail came back null, so the shape could be
        // added under parentheses and pass the zero rule. Raised in review of the change that
        // introduced this rule.
        Assert.Equal(1, CountDirectObservableAwaitsIn("await (source.Take(1).Timeout(budget));"));
        Assert.Equal(1, CountDirectObservableAwaitsIn("var x = await ((source.Take(1).Timeout(budget)));"));
        // …and the strip must NOT swallow a group that is only the RECEIVER of the chain, or the
        // real tail would be lost and a genuine offender would read as clean.
        Assert.Equal(1, CountDirectObservableAwaitsIn("await (a ? b : c).Take(1).Timeout(budget);"));
        Assert.Equal(0, CountDirectObservableAwaitsIn("await (source.Take(1).Timeout(d)).Await(ct);"));

        // The SANCTIONED bridges, which must never be flagged — the false positive that would get
        // the whole rule suppressed, since every fix this guard asks for produces one of these.
        Assert.Equal(0, CountDirectObservableAwaitsIn("await source.Take(1).Timeout(d).Await(ct);"));
        Assert.Equal(0, CountDirectObservableAwaitsIn(
            "await source.FirstAsync().ObserveCompletion(ex => Log(ex), ct);"));
        Assert.Equal(0, CountDirectObservableAwaitsIn(
            "await joined.ObserveCompletion(LateFault(description, report), cts.Token);"));
        Assert.Equal(0, CountDirectObservableAwaitsIn(
            "await x.Should().Within(TestTimeouts.Convergence).Emit(\"the node arrives\");"));

        // Genuine Task awaits — a rule that flagged these would be a ban on `await`.
        Assert.Equal(0, CountDirectObservableAwaitsIn("await httpClient.GetStringAsync(url);"));
        Assert.Equal(0, CountDirectObservableAwaitsIn("await Task.Delay(300, ct);"));
        Assert.Equal(0, CountDirectObservableAwaitsIn("await pool.Invoke(ct => Work(ct));"));
        Assert.Equal(0, CountDirectObservableAwaitsIn("await foreach (var x in source) { }"));
        Assert.Equal(0, CountDirectObservableAwaitsIn("await using var scope = Open();"));

        // Prose and string literals quoting the shape — every remark in this repo does that, and a
        // matcher that counted them would ratchet against its own documentation.
        Assert.Equal(0, CountDirectObservableAwaitsIn(
            "// await source.Take(1).Timeout(Deadline) is the defect this file is about."));
        Assert.Equal(0, CountDirectObservableAwaitsIn(
            "var doc = \"await source.Take(1).Timeout(Deadline)\";"));
    }

    /// <summary>
    /// Blocking bridges in production code: <c>.Wait()</c> and <c>.GetAwaiter().GetResult()</c>.
    ///
    /// <para><b>Why this one is a ratchet and not a zero, unlike everything else in this class.</b>
    /// The zero rules above hold because production genuinely contains no unsanctioned
    /// observable→Task bridge once <c>MeshServiceExtensions</c> is fixed. The blocking shapes do
    /// NOT start from zero: measured 2026-08-30 there are 9 sites across 5 files, in the compiler,
    /// the Orleans test-base disposal path, the plugin tester and a sample. Holding them at zero
    /// today would mean this guard reds on merge, and a rule that reds on arrival gets deleted or
    /// suppressed rather than obeyed. So the inventory is seeded and may only shrink.</para>
    ///
    /// <para><b>Why they belong here at all.</b> <see cref="BlockingBridgeInTestRatchetGuard"/> owns
    /// this defect class but scans <c>test/</c> ONLY, on the stated grounds that <c>src/</c> is
    /// "governed by the harder rule AGENTS.md already states for product code … and by the reviews
    /// that enforce it". Reviews are not a gate. That left the production trees with NO mechanical
    /// check at all for a shape whose consequence — an unbounded park on a turn-based scheduler —
    /// is strictly worse in product code than in a test. This closes it. The two guards are split
    /// by ROOT rather than by marker so each keeps one budget over one tree.</para>
    /// </summary>
    [Fact]
    public void NoNewBlockingBridgeInProductionCode()
    {
        var root = SourceScan.FindRepoRoot();
        var allowed = SourceScan.ReadAllowFile(
            Path.Combine(root, "test", ProductionBlockingAllowFileName), ProductionBlockingAllowFileName);
        var found = ScanBlocking(root, ProductionRoots);

        var failures = new List<string>();

        foreach (var (file, count) in found.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!allowed.TryGetValue(file, out var budget))
                failures.Add(
                    $"  NEW SITE   {file} ({count}) — a blocking wait parks the calling thread "
                    + "until the source produces. When the source schedules onto that same thread "
                    + "(a hub action block, a grain turn) it self-deadlocks, and no timeout can "
                    + "abort a thread parked in a native wait. Stay reactive and Subscribe, or go "
                    + "through IIoPool at a genuine IO edge. Do NOT add a line to "
                    + ProductionBlockingAllowFileName + ".");
            else if (count > budget)
                failures.Add(
                    $"  MORE       {file} ({count} > {budget} allowed) — a blocking wait was ADDED "
                    + "to a file that already carries the shape.");
        }

        var total = allowed.Values.Sum();
        if (total > ProductionBlockingTotalBudget)
            failures.Add(
                $"  TOTAL      {total} allowances > {ProductionBlockingTotalBudget} budgeted — the "
                + "inventory GREW. Adding a line to " + ProductionBlockingAllowFileName
                + " is not a fix.");

        foreach (var (file, budget) in allowed.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var count = found.GetValueOrDefault(file, 0);
            if (count < budget)
                output.WriteLine(
                    $"STALE (please tidy): {file} — {count} found, {budget} allowed. "
                    + $"{(count == 0 ? "Delete the line" : $"Lower it to {count}")} and lower "
                    + $"ProductionBlockingTotalBudget by {budget - count}.");
        }

        Assert.True(failures.Count == 0,
            "A blocking bridge in PRODUCTION code parks a thread on a turn-based scheduler. This "
            + "inventory is seeded because the trees did not start clean; it may shrink, never "
            + "grow.\n" + string.Join("\n", failures));
    }

    /// <summary>
    /// Non-vacuity for the text scanner, pinned in the same run. This drives the real scanner over a
    /// synthetic file and asserts BOTH directions: it counts a call, and it does not count the same
    /// text in prose.
    ///
    /// <para>The prose half is not hypothetical. This repo's remarks quote the banned shape
    /// verbatim, at length, to explain WHY it is banned — <c>ReactiveCompletion</c>,
    /// <c>InlineObservableExtensions</c>, <c>IIoPool</c>, this very file. A scanner that counted
    /// those would ratchet against its own documentation and force the deletion of the institutional
    /// record that makes the rule teachable.</para>
    /// </summary>
    [Fact]
    public void TheScannerCountsCallsAndIgnoresProse()
    {
        const string sample = """
            // A comment mentioning .ToTask() must NOT count.
            /// <summary>Nor an XML doc quoting <c>source.FirstAsync().ToTask(ct)</c>.</summary>
            /* Nor a block comment: .ToTask(ct) */
            public class Sample
            {
                private const string Prose = "not a call either: .ToTask(";
                public Task<int> Real() => source.FirstAsync().ToTask(ct);
            }
            """;

        Assert.Equal(1, CountIn(sample));

        // And the masker really is what makes the difference — without it the same text reads as
        // five sites, so a regression in MaskCommentsAndStrings would be caught here rather than
        // silently inflating every count in the allow file.
        var unmasked = 0;
        for (var at = 0; (at = sample.IndexOf(Marker, at, StringComparison.Ordinal)) >= 0; at += Marker.Length)
            unmasked++;
        Assert.True(unmasked > 1,
            "the sample must contain prose occurrences, or this test proves nothing about masking");

        // The live counterpart: the trees still being swept must actually yield sites, so the
        // shrinking half above cannot be passing on an empty scan.
        var root = SourceScan.FindRepoRoot();
        Assert.True(Scan(root, RatchetedRoots).Count > 0,
            "The scanner found no bridge anywhere under " + string.Join(", ", RatchetedRoots)
            + ". Either every site was swept — in which case move those roots into ProductionRoots, "
            + "empty " + AllowFileName + " and delete this assertion — or the scanner is broken, "
            + "which would make the ratchet above pass on no evidence.");
    }

    private static Dictionary<string, int> Scan(string root, IEnumerable<string> roots) =>
        SourceScan.SourceFiles(root, roots)
            .Select(f => (Relative: SourceScan.Relative(root, f), Count: CountSites(f)))
            .Where(x => x.Count > 0)
            .Where(x => !ExemptPinningFiles.Contains(x.Relative, StringComparer.Ordinal))
            .ToDictionary(x => x.Relative, x => x.Count, StringComparer.Ordinal);

    private static Dictionary<string, int> ScanDirectAwaits(string root, IEnumerable<string> roots) =>
        SourceScan.SourceFiles(root, roots)
            .Select(f => (Relative: SourceScan.Relative(root, f),
                          Count: ReadOrEmpty(f) is { } t ? CountDirectObservableAwaitsIn(t) : 0))
            .Where(x => x.Count > 0)
            .Where(x => !ExemptPinningFiles.Contains(x.Relative, StringComparer.Ordinal))
            .ToDictionary(x => x.Relative, x => x.Count, StringComparer.Ordinal);

    /// <summary>
    /// Counts <c>await</c>s whose expression's TAIL names an Rx operator — i.e. the compiler
    /// synthesized Rx's <c>AsyncSubject</c> awaiter for it.
    ///
    /// <para>Statement-aware rather than line-aware, which is the whole difference between this and
    /// a grep. The awaited expression runs from the keyword until a <c>;</c>, a <c>,</c> or a
    /// closing bracket AT ITS OWN DEPTH — so a chain spread over four lines is one expression, and
    /// the NEXT argument in a call is not part of this one. A <c>&lt;…&gt;</c> that follows an
    /// identifier and contains only what a generic argument list may contain is absorbed whole, so
    /// its commas do not end the expression; without that,
    /// <c>await source.Select&lt;TIn, TOut&gt;(f).Await(ct)</c> truncates to a tail of
    /// <c>Select</c> and an already-converted site reads as an offender.</para>
    /// </summary>
    internal static int CountDirectObservableAwaitsIn(string source)
    {
        var code = SourceScan.MaskCommentsAndStrings(source);
        var count = 0;

        foreach (Match keyword in AwaitKeyword.Matches(code))
        {
            var after = keyword.Index + keyword.Length;
            if (AwaitStatementForm.IsMatch(code[after..Math.Min(code.Length, after + 12)]))
                continue;

            if (TailMemberOf(AwaitedExpression(code, after)) is { } tail
                && ObservableTails.Contains(tail))
                count++;
        }

        return count;
    }

    /// <summary>The awaited expression, brackets balanced and generic argument lists absorbed.</summary>
    private static string AwaitedExpression(string code, int start)
    {
        var depth = 0;
        var i = start;

        while (i < code.Length)
        {
            var c = code[i];

            if (c is '(' or '[' or '{') { depth++; i++; continue; }

            if (c is ')' or ']' or '}')
            {
                if (depth == 0) break;
                depth--;
                i++;
                continue;
            }

            if (c == ';')
            {
                if (depth == 0) break;
                i++;
                continue;
            }

            if (c == ',' && depth == 0) break;

            if (c == '<' && depth == 0 && i > start && IsIdentifierChar(code[i - 1])
                && SkipGenericArguments(code, i) is { } past)
            {
                i = past;
                continue;
            }

            i++;
        }

        return code[start..i];
    }

    /// <summary>
    /// The LAST member accessed at the expression's own depth — its tail. Null when the expression
    /// ends in something that is not a member access (a bare identifier, a constructor call).
    ///
    /// <para>🚨 A FULLY-enclosing parenthesis pair is stripped first, repeatedly. Without that,
    /// <c>await (source.Take(1).Timeout(budget));</c> puts every member access at depth ≥ 1 and the
    /// tail comes back null — a direct observable await could be added under parentheses and the
    /// zero rule would never see it. The strip is deliberately narrow: it applies only when the
    /// OPENING paren's match is the expression's LAST character, so <c>await (a ? b : c).Take(1)</c>
    /// keeps its real tail instead of being stripped down to the condition.</para>
    /// </summary>
    private static string? TailMemberOf(string expression)
    {
        expression = StripEnclosingParentheses(expression);

        var depth = 0;
        string? tail = null;

        for (var i = 0; i < expression.Length; i++)
        {
            var c = expression[i];

            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == '<' && depth == 0 && i > 0 && IsIdentifierChar(expression[i - 1])
                     && SkipGenericArguments(expression, i) is { } past)
                i = past - 1;
            else if (c == '.' && depth == 0)
            {
                var j = i + 1;
                while (j < expression.Length && char.IsWhiteSpace(expression[j])) j++;
                var from = j;
                while (j < expression.Length && IsIdentifierChar(expression[j])) j++;
                if (j > from) tail = expression[from..j];
            }
        }

        return tail;
    }

    /// <summary>
    /// Removes parenthesis pairs that enclose the WHOLE expression, so a grouped chain's members are
    /// read at depth 0. Only a pair whose opening paren matches the final character is removed, and
    /// the check repeats so <c>((x.Take(1)))</c> unwraps fully.
    /// </summary>
    private static string StripEnclosingParentheses(string expression)
    {
        var trimmed = expression.Trim();

        while (trimmed.Length > 1 && trimmed[0] == '('
               && MatchingClose(trimmed, 0) == trimmed.Length - 1)
            trimmed = trimmed[1..^1].Trim();

        return trimmed;
    }

    /// <summary>
    /// The index just past the <c>&gt;</c> closing a generic argument list opened at
    /// <paramref name="open"/>, or null when this <c>&lt;</c> is a comparison. Discriminated by
    /// CONTENT: only what may appear between the angle brackets of a type argument list is
    /// accepted, so <c>if (a &lt; b &amp;&amp; c &gt; d)</c> is rejected on the <c>&amp;</c>.
    /// </summary>
    private static int? SkipGenericArguments(string code, int open)
    {
        var depth = 1;

        for (var i = open + 1; i < code.Length; i++)
        {
            var c = code[i];
            if (c == '<') depth++;
            else if (c == '>')
            {
                if (--depth == 0) return i + 1;
            }
            else if (!(IsIdentifierChar(c) || c is '.' or ',' or ' ' or '?' or '[' or ']'))
                return null;
        }

        return null;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static Dictionary<string, int> ScanBlocking(string root, IEnumerable<string> roots) =>
        SourceScan.SourceFiles(root, roots)
            .Select(f => (Relative: SourceScan.Relative(root, f), Count: CountBlocking(f)))
            .Where(x => x.Count > 0)
            .ToDictionary(x => x.Relative, x => x.Count, StringComparer.Ordinal);

    /// <summary>Every hand-rolled bridge under <paramref name="roots"/>, grouped by file.</summary>
    private static IEnumerable<IGrouping<string, BridgeSite>> ScanBridges(
        string root, IEnumerable<string> roots) =>
        SourceScan.SourceFiles(root, roots)
            .SelectMany(f => ReadOrEmpty(f) is { } text
                ? BridgeSitesIn(text).Select(s => s with { File = SourceScan.Relative(root, f) })
                : [])
            .GroupBy(s => s.File, StringComparer.Ordinal);

    private static string? ReadOrEmpty(string path)
    {
        try { return File.ReadAllText(path); }
        catch (IOException) { return null; } // a file a concurrent build is writing is not evidence
    }

    /// <summary>
    /// The structural detectors — the part that finds the THING rather than a spelling. Three
    /// shapes, all name-independent:
    ///
    /// <list type="number">
    /// <item><b>A completion source settled from inside a subscription.</b> The mechanism itself:
    /// <c>.Subscribe(</c> whose argument region contains a <c>TrySetResult</c>/<c>TrySetException</c>.
    /// This is what <c>ReactiveWait</c>, <c>ReactiveCompletion</c> and <c>MessageHubGrain</c> do.</item>
    /// <item><b>A <c>Task</c>-returning method over an <see cref="IObservable{T}"/> that builds its
    /// own completion source.</b> Both halves are REQUIRED: the signature alone would flag
    /// <c>HubDisposalJoin.JoinDisposalAsync</c>, which consumes the sanctioned bridge rather than
    /// hand-rolling one, and flagging it would turn this rule into a ban on the <c>Task</c> type.
    /// This is what <c>MeshServiceExtensions.ToTask</c> does — the site a leading <c>.</c> in a text
    /// marker could never see.</item>
    /// <item><b>An <see cref="IObserver{T}"/> implementation that settles a completion source.</b>
    /// The same bridge with the callbacks moved into a named type, which is how
    /// <c>MeshServiceExtensions</c>' <c>SingleObserver&lt;T&gt;</c> is written. Catching it twice is
    /// deliberate: a detector that can be defeated by extracting a class is a spelling again.</item>
    /// </list>
    /// </summary>
    private static IEnumerable<BridgeSite> BridgeSitesIn(string text)
    {
        var code = SourceScan.MaskCommentsAndStrings(text);

        foreach (Match m in SubscribeCall.Matches(code))
            if (SettleCall.IsMatch(BalancedRegion(code, m.Index + m.Length - 1)))
                yield return new BridgeSite(string.Empty, LineOf(code, m.Index),
                    "a TaskCompletionSource settled from inside a Subscribe callback");

        foreach (Match m in TaskShapedMethod.Matches(code))
        {
            var open = m.Index + m.Length - 1;
            if (!BalancedRegion(code, open).Contains("IObservable<", StringComparison.Ordinal))
                continue;

            var close = MatchingClose(code, open);
            if (close < 0 || !TcsConstructions(BodyAfter(code, close)).Any())
                continue;

            yield return new BridgeSite(string.Empty, LineOf(code, m.Index),
                $"'{m.Groups[1].Value}' returns a Task over an IObservable<> and builds its own "
                + "TaskCompletionSource");
        }

        foreach (Match m in ObserverImplementation.Matches(code))
        {
            var brace = code.IndexOf('{', m.Index);
            if (brace < 0 || !SettleCall.IsMatch(BalancedRegion(code, brace)))
                continue;

            yield return new BridgeSite(string.Empty, LineOf(code, m.Index),
                "an IObserver<> implementation that settles a TaskCompletionSource");
        }
    }

    /// <summary>
    /// Whether every <c>TaskCompletionSource</c> constructed in the file specifies
    /// <c>RunContinuationsAsynchronously</c> — the flag that queues the awaiter's continuation
    /// instead of running it on the producer's thread, and therefore the whole difference between a
    /// tolerable bridge and the #2377 defect.
    ///
    /// <para>The check is FILE-scoped rather than method-scoped on purpose. Method scoping would be
    /// defeated by hoisting the construction into a field or a helper, and the cost of the wider
    /// scope is only that a file mixing a bridge with an unrelated plain-signal completion source
    /// must also pass the flag there — which is harmless on a signal. No production file does that
    /// today (<c>MessageHub</c>'s <c>hasStarted</c> lives in a file that contains no bridge at all,
    /// so this never runs against it).</para>
    /// </summary>
    private static bool IsSafeForm(string path)
    {
        var text = ReadOrEmpty(path);
        if (text is null) return true; // unreadable is not evidence of a defect

        var code = SourceScan.MaskCommentsAndStrings(text);
        return TcsConstructions(code).All(m =>
            BalancedRegion(code, m.Index + m.Length - 1)
                .Contains("RunContinuationsAsynchronously", StringComparison.Ordinal));
    }

    /// <summary>The text inside the bracket opened at <paramref name="open"/>, brackets balanced.</summary>
    private static string BalancedRegion(string code, int open)
    {
        var close = MatchingClose(code, open);
        return close < 0 ? code[(open + 1)..] : code[(open + 1)..close];
    }

    private static int MatchingClose(string code, int open)
    {
        var depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            switch (code[i])
            {
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    if (--depth == 0) return i;
                    break;
            }
        }

        return -1;
    }

    /// <summary>
    /// A method's body, given the close paren of its parameter list: the braced block, or the
    /// expression up to its <c>;</c> for an expression-bodied member.
    /// </summary>
    private static string BodyAfter(string code, int closeParen)
    {
        var i = closeParen + 1;
        while (i < code.Length && char.IsWhiteSpace(code[i])) i++;

        // `where T : ...` constraints sit between the parameter list and the body.
        while (i < code.Length && code[i] is not ('{' or '=' or ';'))
        {
            i++;
        }

        if (i >= code.Length) return string.Empty;
        if (code[i] == '{') return BalancedRegion(code, i);
        if (code[i] == ';') return string.Empty;

        var end = code.IndexOf(';', i);
        return end < 0 ? code[i..] : code[i..end];
    }

    private static int LineOf(string code, int index) =>
        code.AsSpan(0, index).Count('\n') + 1;

    /// <summary>
    /// Keeps <see cref="ExemptPinningFiles"/> honest. An exemption that outlives its subject is a
    /// hole with a comment on it — the failure mode AGENTS.md names as "a guard whose subject moved
    /// and whose roots did not passes having checked nothing".
    /// </summary>
    [Fact]
    public void TheExemptedPinningTestStillPinsTheShape()
    {
        var root = SourceScan.FindRepoRoot();
        foreach (var relative in ExemptPinningFiles)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path),
                $"{relative} is exempted from this guard but no longer exists. Delete the exemption "
                + "— an exemption whose subject is gone is a hole a real bridge can be parked in.");
            Assert.True(CountSites(path) > 0,
                $"{relative} is exempted from this guard but no longer contains the shape it exists "
                + "to demonstrate. Either it was rewritten (delete the exemption) or the scanner "
                + "broke (fix that first — every count in the allow file depends on it).");

            // 🚨 The exemption covers the DIRECT-await scan too, so it must still pin that shape as
            // well. This is the half that matters most: the whole point of the pinning test is that
            // `await source.FirstAsync()` is NOT a lighter alternative to the bridge, and an
            // exemption that outlived that demonstration would be a hole in the newer rule.
            Assert.True(CountDirectObservableAwaitsIn(File.ReadAllText(path)) > 0,
                $"{relative} is exempted from the direct-await scan but no longer awaits an "
                + "observable directly — the very shape it exists to measure. Either it was "
                + "rewritten (delete the exemption) or the direct-await matcher broke, which would "
                + "make NoProductionCodeAwaitsAnObservableDirectly pass on no evidence.");
        }
    }

    /// <summary>Occurrences of <see cref="Marker"/>, with comments and string literals masked first.</summary>
    private static int CountSites(string path)
    {
        var text = ReadOrEmpty(path);
        if (text is null) return 0;

        return text.Contains(Marker, StringComparison.Ordinal) ? CountIn(text) : 0;
    }

    private static int CountIn(string text)
    {
        var code = SourceScan.MaskCommentsAndStrings(text);
        var count = 0;
        for (var at = 0; (at = code.IndexOf(Marker, at, StringComparison.Ordinal)) >= 0; at += Marker.Length)
            count++;
        return count;
    }

    private static int CountBlocking(string path)
    {
        var text = ReadOrEmpty(path);
        if (text is null) return 0;
        if (!BlockingMarkers.Any(m => text.Contains(m, StringComparison.Ordinal))) return 0;

        var code = SourceScan.MaskCommentsAndStrings(text);
        var count = 0;
        foreach (var marker in BlockingMarkers)
            for (var at = 0; (at = code.IndexOf(marker, at, StringComparison.Ordinal)) >= 0; at += marker.Length)
                count++;
        return count;
    }
}
