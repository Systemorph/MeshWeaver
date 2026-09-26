using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using MeshWeaver.Compiler;
namespace MeshWeaver.Hosting;

/// <summary>Terminal outcome of pre-warming one dynamic NodeType's hub.</summary>
public enum PreWarmStatus
{
    /// <summary>The NodeType reached a usable compiled build.</summary>
    Compiled,
    /// <summary>
    /// NOT ATTEMPTED because it did not need to be: the shared assembly store already holds this
    /// NodeType's bytes for the LIVE framework (<see cref="BakeState.Baked"/>), so there is nothing
    /// to compile. This is what makes the sweep restartable — an interrupted or partial bake resumes
    /// from the share instead of starting over, and a second pod finds the first pod's work.
    /// </summary>
    AlreadyBaked,
    /// <summary>The NodeType's compile settled at Error (its diagnostics are already logged by the compile watcher).</summary>
    CompileError,
    /// <summary>The per-type warm budget elapsed before the compile settled — Part 2 handles the late arrival.</summary>
    TimedOut,
    /// <summary>
    /// NOT ATTEMPTED: a NodeType this one draws sources from did not reach a usable build, so this
    /// type cannot build either — its assembly would be missing exactly the sources the upstream
    /// owns. <see cref="PreWarmOutcome.Detail"/> names the blocking type. Skipping is the graceful
    /// outcome: warming it anyway burns the whole per-type budget on a guaranteed failure, and
    /// doing that for a fan-out of dependents is how one broken type used to stall a whole sweep.
    /// </summary>
    UpstreamFailed,
    /// <summary>
    /// NOT ATTEMPTED, and NOT a verdict: a NodeType this one draws sources from was itself never
    /// evaluated (it timed out, or ITS upstream did). The sweep therefore knows nothing about this
    /// type either — "I don't know" propagates as "I don't know".
    ///
    /// <para>🚨 This member exists because the alternative silently freezes the fleet. A cross-silo
    /// <c>SubscribeRequest</c> timeout on a shared upstream (core #694) is not evidence that anything
    /// is broken, and <see cref="NodeTypeBakeGateState"/> correctly refuses to gate on a direct
    /// <see cref="TimedOut"/>. But before this status existed, that same unevaluated upstream turned
    /// every previously-healthy DEPENDENT into <see cref="UpstreamFailed"/> — which DOES gate. So the
    /// leniency stopped at depth 1 and the false regression simply reappeared one hop downstream,
    /// re-creating the 2026-08-02 memex-cloud stall through the back door.</para>
    ///
    /// <para>Deliberately distinct from <see cref="UpstreamFailed"/>: a dependent of a genuinely
    /// broken (<see cref="CompileError"/>) upstream still gates, because that upstream IS a verdict.
    /// Only "no answer" propagates as "no answer".</para>
    /// </summary>
    UpstreamUnevaluated,
    /// <summary>
    /// The compile settled at Error AND the type's source queries currently match ZERO Code nodes
    /// (<see cref="NodeTypeDefinition.CurrentSourceVersions"/> is EXPLICITLY empty) — the sources
    /// were deleted or moved out from under the type. This is a CONTENT verdict, not an image
    /// verdict: which nodes a mesh query matches is a property of the mesh, not of the framework
    /// being rolled out, so no image caused it and no rollout can fix it.
    ///
    /// <para>🚨 "Source queries" here means the type's EFFECTIVE queries — the ones it DECLARES in
    /// <see cref="NodeTypeDefinition.Sources"/> or, far more commonly, the DEFAULT
    /// <c>namespace:{path}/Source scope:subtree</c> pair it gets when it declares none. The
    /// classifier used to additionally require declared queries, which made this member
    /// unreachable for nearly every NodeType in a real mesh and is what let a DELETED type gate
    /// readiness on every boot (#1391). Do not reintroduce that condition: an empty
    /// <see cref="NodeTypeDefinition.Sources"/> means "uses the defaults", not
    /// "configuration-only".</para>
    ///
    /// <para>🚨 This member exists because on 2026-08-10 four such types (KmuBasics/* — their
    /// Source subtrees removed when the course was re-installed under a new id, the type nodes
    /// left behind) were counted as image regressions and stalled memex-cloud's self-update for a
    /// day, across two successive images. A failure the image cannot influence must never hold
    /// the image out of rotation — the same deploy-freeze rule as
    /// <see cref="PreWarmOutcome.WasHealthyBeforeBake"/>, arriving through content deletion
    /// instead of an abandoned Error record.</para>
    ///
    /// <para>Deliberately narrow: only an EXPLICITLY empty snapshot reclassifies. A null snapshot
    /// means the sources watcher never seeded, so the sweep does not actually know the sources are
    /// gone — that stays <see cref="CompileError"/>, because a real regression must not hide
    /// behind an unseeded snapshot.</para>
    /// </summary>
    NoSources,
    /// <summary>
    /// 🚨 <see cref="NoSources"/> one granularity finer, and the shape <see cref="NoSources"/> can
    /// never see (issue #3903): the source snapshot is NOT empty, but one of the type's DECLARED
    /// source queries answered and matched NOTHING — its own <c>Source/</c> subtree is gone while
    /// the <c>shared=@Lib/Source</c> entries beside it still resolve, so the union stays populated
    /// and the emptiness that matters is invisible to a count.
    ///
    /// <para>The consequence is the phantom-diagnostic failure <c>SourceSnapshot</c> exists to
    /// prevent, arriving through the one door it does not watch: Roslyn is handed a set that is
    /// SHORT of what the type declares and emits completely genuine-looking <c>CS0246</c>/
    /// <c>CS1061</c> about symbols the author never lost. Measured on memex.meshweaver.cloud —
    /// <c>rbuergi/OperationRequest</c> failed from 2026-09-06 on three symbols that are exactly its
    /// three absent <c>Source/*</c> nodes, against 41 sources pulled in by five <c>shared=</c>
    /// entries, and the investigation went looking for them in module surfaces.</para>
    ///
    /// <para>A CONTENT verdict, like <see cref="NoSources"/> and for the identical reason: which
    /// nodes a mesh query matches is a property of the mesh, not of the framework being rolled out,
    /// so no image caused it and no rollout can fix it. Dependents inherit
    /// <see cref="UpstreamContentBroken"/>, the gate files it under content-broken, and the batch
    /// driver stops re-attempting the compile — see
    /// <c>NodeTypeCompilationHelpers.IsUnconvergableSourceFailure</c> for why that is a
    /// classification and not a retry cap, and for the second witness
    /// (<see cref="NodeTypeDefinition.LastCompileSucceededAt"/>) that keeps a type which NEVER
    /// built — whose failure may be its own configuration — gating exactly as before.</para>
    /// </summary>
    DeclaredSourcesMissing,
    /// <summary>
    /// NOT ATTEMPTED, and content-broken one hop up: a NodeType this one draws sources from is
    /// <see cref="NoSources"/>-broken, so this type cannot build either — for the same
    /// content-not-image reason, which must propagate AS ITSELF rather than as a gating
    /// <see cref="UpstreamFailed"/>. This is the same depth-1 hole
    /// <see cref="UpstreamUnevaluated"/> closes for timeouts: leniency on the direct outcome is
    /// worth nothing if the identical condition gates through the dependents.
    /// </summary>
    UpstreamContentBroken,
    /// <summary>
    /// The compile settled at Error on a type its REPOSITORY HAS RETIRED — the definition carries
    /// <see cref="NodeTypeDefinition.PendingRetirement"/>: the source that owns it no longer ships
    /// it, and the import kept it only because the mesh still holds instances that have not been
    /// retyped (see <c>NodeTypeInstanceProbe</c>). Its sources were withdrawn on purpose, so
    /// whether it still compiles is no longer evidence about an image. A CONTENT verdict, like
    /// <see cref="NoSources"/>: dependents inherit <see cref="UpstreamContentBroken"/>, and the
    /// gate files it under <c>NodeTypeBakeGateState.Retired</c> without stalling anything.
    /// </summary>
    Retired,
    /// <summary>
    /// 🚨 The type's definition node NO LONGER EXISTS — it was pruned by its repository (a
    /// completed retirement), and this sweep, which enumerated it before the prune landed, then
    /// measured a "failure" against a node that was gone. Measured on memex.systemorph.com
    /// 2026-09-08: <c>Crm/Mail</c> pruned at 20:29:14Z, its compile failed at 20:29:52Z with
    /// <c>No node found at 'Crm/Mail'</c>, recorded as a CompileError regression on a healthy
    /// baseline, and at 20:31:12Z the pod refused readiness on it — with a recovery watch
    /// subscribed to a node that did not exist, so nothing could ever retract it. A deliberate
    /// retirement must not hold every rollout on every instance for ever.
    ///
    /// <para>Established by a LISTING that came back and did not name the node — never by a
    /// point read, which on an absent node terminates with a routing NotFound and opens the
    /// storm-breaker on that path — and never by the shape of the failure message. A faulted
    /// listing leaves the original verdict standing: absence is asserted, not assumed.</para>
    /// </summary>
    Removed,
    /// <summary>The warm subscription faulted (best-effort — the lazy path still works).</summary>
    Faulted
}

/// <summary>One dynamic NodeType's pre-warm result.</summary>
public record PreWarmOutcome(string TypePath, PreWarmStatus Status, string? Detail = null)
{
    /// <summary>
    /// The type is usable on this framework now — whether this sweep compiled it
    /// (<see cref="PreWarmStatus.Compiled"/>) or found it already on the shared store
    /// (<see cref="PreWarmStatus.AlreadyBaked"/>).
    ///
    /// <para>Callers deciding "is this mesh ready to serve?" want THIS, not an equality check
    /// against <see cref="PreWarmStatus.Compiled"/> — a warm share is a success, not a miss, and a
    /// gate that insisted on a fresh compile would fail every pod that inherited a good cache.</para>
    /// </summary>
    public bool ReachedUsableBuild =>
        Status is PreWarmStatus.Compiled or PreWarmStatus.AlreadyBaked;

    /// <summary>
    /// Whether this NodeType was WORKING before the sweep started — a FACT about the type, taken
    /// faithfully from <see cref="NodeTypeBakeEntry.WasHealthy"/>, which counts
    /// <see cref="BakeState.NeverBuilt"/> as healthy: a type nobody has built yet is not damaged
    /// goods.
    ///
    /// <para>Only a failure on a type that was previously healthy is a REGRESSION, and only a
    /// regression may hold a pod out of rotation. A type that was already sitting at
    /// <c>CompilationStatus.Error</c> before this image failing again is not new damage — gating on it
    /// would let one abandoned NodeType block every future deploy.</para>
    ///
    /// <para>🚨 This is HALF of the gating question; the other half is
    /// <see cref="HasRegressionBaseline"/>, and the two are deliberately separate fields. They were
    /// briefly collapsed into this one (#4472) by stamping it from a baseline that had already been
    /// emptied for a first bake, which made a never-built type report
    /// <c>WasHealthyBeforeBake == false</c> — "this type was broken on the way in", said about a type
    /// nothing had ever built. It reached the right verdict through a false statement, and the two
    /// readers then disagreed: <c>BuildProtocolDriver.OutcomesOf</c> stamps this field straight off
    /// the entry, so the GO still saw <c>true</c> while the gate saw <c>false</c> for the same type
    /// (#4496).</para>
    /// </summary>
    public bool WasHealthyBeforeBake { get; init; } = true;

    /// <summary>
    /// Whether a failure of this type on this process would be a REGRESSION OF THIS IMAGE — i.e.
    /// there is a working build to regress FROM, and a previous image this refusal would protect.
    /// <c>false</c> in three cases, each of which means refusing readiness protects nobody:
    /// <list type="bullet">
    /// <item>the first bake of a brand-new instance, where every type in the report is
    /// <see cref="BakeState.NeverBuilt"/> (see <see cref="DynamicTypePreWarmer.IsFirstBake"/>) —
    /// there is no previous image and no previous pod;</item>
    /// <item>🚨 #5544: this process's platform build has ALREADY SERVED this mesh
    /// (<see cref="NodeTypeBakeReport.ThisBuildHasServed"/>) — it is a restart of the image the
    /// rollout falls back on, and refusing it is how the gate took memex.systemorph.com fully
    /// down;</item>
    /// <item>🚨 #5544: THIS TYPE has no working build produced by another, not-newer image
    /// (<see cref="NodeTypeBakeEntry.IsRegressionBaselineFor"/>) — it never built (a type whose
    /// source never compiled refused every pod), or only this build or a newer one built it.</item>
    /// </list>
    ///
    /// <para>The first two are report-level facts carried per outcome so a gate need not re-read
    /// the report; the third is per type. All three are read off the SAME report by
    /// <see cref="DynamicTypePreWarmer.BaselineStamp"/>.</para>
    ///
    /// <para>Kept apart from <see cref="WasHealthyBeforeBake"/> because the two answer different
    /// questions — "was THIS TYPE working?" and "is there anything here to protect?" — and only
    /// their conjunction may gate. Defaults to <c>true</c> so every hand-built outcome keeps the
    /// strict reading.</para>
    /// </summary>
    public bool HasRegressionBaseline { get; init; } = true;

    /// <summary>
    /// 🚨 WALL-CLOCK COST of this one type's bake — the number that was missing.
    ///
    /// <para>Before this existed the only signal a compile unit produced was a binary pass/fail
    /// against a bootstrap deadline, so "which types are expensive?" and "did that change help?"
    /// were both unanswerable, and the only available response to a type brushing the budget was to
    /// widen it (Systemorph/MeshWeaver#1439). A per-type duration turns a budget into a measurement.</para>
    ///
    /// <para><see cref="TimeSpan.Zero"/> for an outcome that did no work — a type skipped because
    /// its upstream failed, or one already on the shared store — so a zero here means "not compiled",
    /// never "compiled instantly".</para>
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// How many source documents this type's compile unit contained, and how many bytes of C# they
    /// amounted to — the correlate a duration is only interesting NEXT to. A type whose duration
    /// grows while its unit does not is a different problem from one where both grow together, and
    /// the whole point of #1439 is that nobody could tell those apart. Zero when the driver did not
    /// resolve a source set (the activation path, which lets the compiler do its own discovery).
    /// </summary>
    public int SourceCount { get; init; }

    /// <inheritdoc cref="SourceCount"/>
    public long SourceBytes { get; init; }

    /// <summary>
    /// The cost, rendered for the per-type log line that already exists — e.g.
    /// <c>"3.4 s over 72 file(s), 1399 KB"</c>, or an empty string for an outcome that compiled
    /// nothing (so a skip does not gain a misleading "0 s").
    /// </summary>
    public string DescribeCost() =>
        Duration <= TimeSpan.Zero
            ? string.Empty
            : SourceCount > 0
                ? string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0:F1} s over {1} file(s), {2} KB",
                    Duration.TotalSeconds, SourceCount, SourceBytes / 1024)
                : string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0:F1} s", Duration.TotalSeconds);

    /// <summary>
    /// Evidence that this compile sampled a TORN source snapshot: at least one of the type's
    /// sources was last modified AT OR AFTER the compile started
    /// (<see cref="DynamicTypePreWarmer.SourcesMovedDuringCompile(NodeTypeDefinition)"/>), so the
    /// diagnostics describe a source set the mesh was in the middle of replacing.
    ///
    /// <para>Diagnostic only — it deliberately does NOT reclassify the outcome. A source moving
    /// mid-compile makes a failure SUSPECT, not innocent, and a torn compile of genuinely broken
    /// code must still gate. What clears the gate is the type actually rebuilding on this image
    /// (<c>NodeTypeBakeGateState.RetractRegression</c>); this flag is what lets the log say WHY the
    /// pod is refusing readiness on content that may repair itself, instead of telling an operator
    /// at 3am that the image is broken (issue #1214, proposal 3).</para>
    /// </summary>
    public bool SourcesMovedDuringCompile { get; init; }

    /// <summary>
    /// For a DERIVED outcome — <see cref="PreWarmStatus.UpstreamFailed"/>,
    /// <see cref="PreWarmStatus.UpstreamContentBroken"/>,
    /// <see cref="PreWarmStatus.UpstreamUnevaluated"/> — the upstream type that blocked this one.
    /// <c>null</c> for an outcome the sweep measured directly.
    ///
    /// <para>It is what makes a derived regression retractable WITHOUT touching this type: such a
    /// verdict's entire evidentiary basis is the blocker's verdict (the sweep never attempted this
    /// type — deliberately, since attempting it would burn a whole per-type budget on a build that
    /// cannot succeed). So when the blocker's regression is withdrawn, this one has nothing left
    /// holding it up and is withdrawn too. The alternative — watching each dependent for its own
    /// recovery — would ACTIVATE every skipped dependent's hub and hold it for the pod's lifetime,
    /// undoing exactly the saving the skip exists for.</para>
    /// </summary>
    public string? BlockedBy { get; init; }
}

/// <summary>
/// Best-effort, background PRE-WARM of dynamic NodeType hubs at startup (Part 1 of the
/// fresh-pod compile-race hardening).
///
/// <para><b>The window it shrinks.</b> On a fresh pod — every image roll / self-update
/// spins one up — the platform's <see cref="NodeTypeCompilationHelpers.FrameworkVersion"/>
/// (Graph's MVID) changes, so every dynamic NodeType's cached assembly is ABI-stale and
/// must recompile. Nothing drives that until the FIRST user request activates a per-node
/// hub — so the unlucky first visitor of each type waits out the cold Roslyn compile.
/// This warmer front-loads those compiles: it activates each dynamic NodeType's own hub
/// (which fires the framework-stale / first-build kickoff → Roslyn), so the compiles run
/// proactively rather than on a user's critical path.</para>
///
/// <para><b>Best-effort — never blocks, never wedges.</b> It runs on a background
/// subscription after the silo is up (<c>ApplicationStarted</c>), bounds concurrency with
/// a reactive <c>Merge(maxConcurrency)</c> — which is the ONLY bound on how many Roslyn emits
/// run at once, so do not widen it on the belief that something below serializes them (see the
/// #890 correction on the sweep default) — and gives each type a generous per-type budget. A type that fails to compile, times out, or faults is
/// LOGGED and skipped — it does not block the others and it is NOT gated on. If any type
/// is still compiling when a user arrives, Part 2
/// (<see cref="NodeTypeEnrichmentHelpers.WaitForCompileSettled"/>) makes that activation
/// WAIT for the compile instead of faulting — so the warmer is a latency optimisation, not
/// a correctness dependency. It deliberately does NOT gate the readiness probe: a slow or
/// broken compile must never keep a pod out of rotation (Part 2 already covers late
/// arrivals), and a readiness gate would risk the exact "slow pod startup" it is meant to
/// avoid.</para>
/// </summary>
public static class DynamicTypePreWarmer
{
    /// <summary>
    /// Pause between types, so a long warm sweep stays a background trickle rather than a queue of
    /// back-to-back cold activations. Cheap insurance: the sweep is a latency optimisation with no
    /// deadline, so there is no reason for it to ever look like load.
    ///
    /// <para>🚨 Why there is no concurrency knob beside it (there was one, defaulting to 4, and 4 is
    /// measurably harmful): on 2026-07-28 04:05 four compiles were triggered on memex in quick
    /// succession — nothing to do with this warmer, but the identical load shape — and within
    /// minutes SIX plugin roots (Claims, Edu, Underwriting, Chess, Publish, Training) fell to the
    /// "did not settle" overlay and needed a scale-to-zero to recover. Concurrency bought
    /// nothing except simultaneous cold ACTIVATIONS — the expensive part (109ms/0ms discovery for the same NodeType shape on a fresh
    /// mesh, versus 45.20s on memex — issue #686). A dependency ORDER cannot be honoured while its
    /// members run in parallel either, so the sweep is now strictly sequential and the knob is
    /// gone rather than left lying about what it does.</para>
    ///
    /// <para>🚨 <b>Correction (#890).</b> Two comments here used to assert that "Roslyn itself is
    /// already serialized on the Compile IoPool". It is not, and never was. The Compile pool gates
    /// only the NuGet-restore leaf; <c>MeshNodeCompilationService.OnThreadPool</c> deliberately
    /// runs the compile on a bare <c>Task.Run</c> — its own comment says "NOT the IoPool … no gate,
    /// no re-entrancy" — precisely because that pool's semaphore deadlocked the compile against
    /// itself. So concurrent Roslyn emits over the process-wide
    /// <c>CompileReferences.Default</c> reference instances ARE reachable. That is a live axis in
    /// #890, and a false claim of serialization sitting in the one place a reader would check is
    /// how an axis gets ruled out without ever being tested.</para>
    /// </summary>
    /// <remarks>
    /// 🚨 This is the SERVING-pod default, and on a big mesh it — not Roslyn — is the bake's
    /// wall-clock. Measured on memex-cloud 2026-08-11 (healthy single silo): 162 types compiled in
    /// a 7-minute sweep of which the compiles themselves summed to 51 SECONDS (p50 0.2s, p90 0.5s,
    /// max 3.4s) — the 2s trickle was ~5.4 of the 7 minutes. The trickle exists to protect a pod
    /// that is SERVING while it warms; an INITIAL BAKE on a readiness-gated pod serves nobody, so
    /// the hosted service passes <see cref="TimeSpan.Zero"/> there (see
    /// <c>DynamicTypePreWarmerHostedService</c> — the gated fast path, overridable via
    /// <c>PreWarm:BetweenTypes</c>). That distinction is what turns "the bake takes 20 minutes"
    /// into "the bake takes ~2".
    /// </remarks>
    public static readonly TimeSpan BetweenTypes = TimeSpan.FromSeconds(2);

    /// <summary>Per-type warm budget — generous, because a cold Roslyn compile queued behind
    /// others on the Compile IoPool can legitimately take a while. On elapse we log
    /// <see cref="PreWarmStatus.TimedOut"/> and move on (Part 2 handles the eventual arrival).</summary>
    public static readonly TimeSpan DefaultPerTypeBudget = TimeSpan.FromMinutes(5);

    /// <summary>Budget for the one-shot enumeration query of dynamic NodeTypes.</summary>
    private static readonly TimeSpan EnumerationBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Enumerate the dynamic NodeTypes on the mesh and activate each one's hub, waiting
    /// (best-effort, bounded) for its compile to settle. Emits one
    /// <see cref="PreWarmOutcome"/> per type and completes when all have settled or timed out.
    ///
    /// <para>🚨 <b>A FAILED ENUMERATION FAULTS THE STREAM — it is never laundered into an empty
    /// completion.</b> Per-TYPE trouble still folds into an outcome
    /// (<see cref="PreWarmStatus.CompileError"/>, <see cref="PreWarmStatus.TimedOut"/>,
    /// <see cref="PreWarmStatus.Faulted"/>), because that is a measurement. But if the ENUMERATION
    /// itself errors or times out, the sweep measured NOTHING, and the two states a subscriber has
    /// to tell apart are:</para>
    /// <list type="bullet">
    ///   <item><b>Zero types because none exist</b> — a fresh or genuinely empty mesh. Legitimate:
    ///     the stream completes normally with no emissions, and a readiness gate may serve.</item>
    ///   <item><b>Zero types because enumeration threw</b> — the pod has learned nothing. The
    ///     stream FAULTS, so a readiness gate can refuse rather than certify an unmeasured
    ///     bake.</item>
    /// </list>
    /// <para>These used to be indistinguishable here: a <c>Catch</c> swallowed the enumeration
    /// fault and returned <c>Observable.Empty</c>, so both arrived at the subscriber as "completed,
    /// zero outcomes" and <c>DynamicTypePreWarmerHostedService</c> marked the gate Complete →
    /// Healthy. The pre-run bake Job used to catch that from the outside — <i>"FINDING NOTHING IS
    /// NOT PASSING … a gate that certifies 'I verified nothing' is worse than no gate"</i>, exit 3,
    /// with a <c>Bake:AllowEmpty</c> escape — and it named THIS <c>Catch</c> as the reason it had
    /// to. Retiring that Job (#1357) removed the counterpart guard and left the hole live on the
    /// only remaining path. The distinction now lives at the source instead, which is the one place
    /// it can be made honestly.</para>
    ///
    /// <para>Note what did NOT change: an empty result is NOT an error. Emptiness is a legitimate
    /// answer, and refusing readiness for it would black-hole a genuinely empty mesh. Only the
    /// inability to obtain an answer gates.</para>
    ///
    /// <para>Types are warmed STRICTLY SEQUENTIALLY in
    /// <see cref="NodeTypeDependencyGraph.TopologicalOrder(IReadOnlyDictionary{string, ImmutableHashSet{string}}, out ImmutableList{string})"/>
    /// order — dependencies before dependents. There is deliberately no concurrency knob: a
    /// dependency order cannot be honoured while its members run in parallel, and concurrent cold
    /// activations are what produced the 60s <c>SubscribeRequest</c> timeouts this warmer exists to
    /// prevent.</para>
    /// </summary>
    public static IObservable<PreWarmOutcome> WarmDynamicTypes(
        IMessageHub mesh,
        ILogger? logger = null,
        TimeSpan? perTypeBudget = null,
        TimeSpan? betweenTypes = null,
        bool batchBake = false,
        bool buildProtocol = false,
        Action<string>? progress = null)
    {
        var budget = perTypeBudget ?? DefaultPerTypeBudget;
        var pacing = betweenTypes ?? BetweenTypes;
        var meshService = mesh.ServiceProvider.GetService<IMeshService>();
        if (meshService is null)
        {
            logger?.LogDebug("DynamicTypePreWarmer: no IMeshService registered — nothing to warm");
            return Observable.Empty<PreWarmOutcome>();
        }
        // A CLOSED type set (ClosedTypeSet) has no dynamic types by definition: its types are the
        // image's code registrations, which ship their assembly with the process. So the sweep
        // enumerates NOTHING — not "every NodeType row, then filters": the rows are exactly the
        // input a closed mesh must not read, and a broken one in a user partition must not be able
        // to occupy, slow or fail this pass.
        if (mesh.ServiceProvider.IsClosedTypeSet())
        {
            logger?.LogInformation(
                "DynamicTypePreWarmer: {Key}=true — the type set is closed, no database NodeType is warmed",
                ClosedTypeSet.ConfigKey);
            return Observable.Empty<PreWarmOutcome>();
        }
        var accessService = mesh.ServiceProvider.GetService<AccessService>();
        var workspace = mesh.GetWorkspace();

        // 🚨 System-scoped: enumerating + activating dynamic NodeType defs across EVERY
        // partition is infrastructure, not a user-attributable read (mirrors the enrichment
        // probe + activation reads).
        //
        // 🚨 RunAsSystem, never `Observable.Using(AccessContextScope.AsSystem, …)` (#1444/#1790) —
        // and `AccessContextScope.AsSystem(x)` IS `x.ImpersonateAsSystem()`, so routing through the
        // helper does not make it a different animal. `Using` opens the AsyncLocal on the
        // SUBSCRIBING thread and disposes it when the inner observable TERMINATES (for these
        // cross-hub reads, the owning hub's response thread), so the subscriber — the bootstrap
        // hosted service — is left holding `system-security` for everything it does afterwards.
        // What used to stand here read "Observable.Using holds the scope across the live
        // subscription, not just the synchronous build", i.e. the defect described as the feature.
        // RunAsSystem seals both ends inside one Subscribe: the whole cold pipeline below is
        // composed and subscribed impersonated (so every Query/stream read is issued as System and
        // every continuation it schedules captures that ExecutionContext), and the scope is left on
        // the way out of that same Subscribe.
        return accessService.RunAsSystem(
            () => meshService
                // Every NodeType definition — a catalog, mesh-wide by nature (#3202 — fan-out is opt-in).
                .Query<MeshNode>(MeshQueryRequest.FromQuery(MeshWideQuery.OfType(MeshNode.NodeTypePath)))
                .Take(1)
                .Timeout(EnumerationBudget)
                .SelectMany(change =>
                {
                    // The full nodes (not just their definitions): the batch driver feeds the
                    // compiler the enumerated MeshNode directly — no re-fetch, no activation.
                    var dynamicTypes = DynamicTypesOf(
                        change.Items, mesh.JsonSerializerOptions, logger);
                    var (nodes, definitions) = (dynamicTypes.Nodes, dynamicTypes.Definitions);
                    progress?.Invoke(
                        $"enumerated {definitions.Count} dynamic NodeType(s) — probing the assembly "
                        + "store for the builds their records name");

                    // 🚨 ASK THE SHARE WHAT IS ACTUALLY THERE, before deciding what to build.
                    //
                    // The obvious shortcut — trust each NodeType's own record (HasUsableBuild) — is
                    // wrong in the one case that matters operationally: that check is deliberately a
                    // pure record read ("no store probe, no File.Exists"), so when the assembly-cache
                    // volume is cleared, remounted, or restored from a stale snapshot, every type
                    // still claims a usable build while its bytes are gone. Nothing re-drives a
                    // compile and the miss surfaces later, one instance at a time, at activation.
                    //
                    // Probing the STORE makes this level-triggered on reality rather than on history,
                    // which buys three properties at once: a cleared cache re-bakes by itself, an
                    // interrupted bake RESUMES (what already landed comes back Baked), and a second
                    // pod inherits the first pod's work instead of repeating it.
                    //
                    // 🚨 #3703 — the enumeration is a PROJECTION, and this process's own prebuilt
                    // adoptions may already have superseded it. Classify from the newer of the two.
                    //
                    // 🚨 The overlay moves DEFINITIONS and deliberately NOT `nodes`. A node carries
                    // the VERSION the compiler's store upload keys on, and an overlaid definition on
                    // a snapshot node would pair a fresh record with a stale version — strictly
                    // worse than either. It costs nothing: an adopted type classifies Baked, so the
                    // batch driver (which is the only consumer of `nodes`) never reaches it.
                    var overlay = OverlayThisProcessAdoptions(mesh, definitions, nodes, logger);
                    var classified = overlay.Definitions;

                    var store = ResolveAssemblyStore(mesh);
                    return NodeTypeBakeStatus
                        .Probe(classified, store, logger: logger,
                            liveDependencyIdOf: NodeTypeCompilationHelpers.DependencyIdResolverOf(mesh),
                            liveToolchainId: NodeTypeCompilationHelpers.ProcessToolchainId)
                        .Select(report => report with
                        {
                            ClassifiedFromLocalAdoption = overlay.Applied.Count,
                        })
                        // #5544: whether a replica of this build has served here decides whether
                        // anything this sweep finds may refuse readiness — read BEFORE the stamp.
                        .SelectMany(report => ServedBuildWitness.Annotate(mesh, report, logger))
                        .Do(report => PublishReport(
                            mesh, report, NodeTypeBakeReportRegistry.CompilingSweep))
                        .SelectMany(report => BakeOrFollow(
                            mesh, workspace, accessService, classified, nodes, store, report,
                            budget, pacing, batchBake, buildProtocol, logger, progress));
                })
                // 🚨 NO Catch HERE, AND NO LOG-AND-SWALLOW — DELIBERATELY.
                //
                // The fault must reach the subscriber, because it is the ONLY thing that
                // distinguishes "this mesh has no dynamic types" from "this pod could not find
                // out". What used to stand here logged a warning and returned Observable.Empty,
                // which handed both cases to the caller as the identical terminal — and the
                // readiness gate then certified a bake that never happened.
                //
                // Nor is a log added here to compensate: the subscriber
                // (DynamicTypePreWarmerHostedService) already logs THIS exception, with the
                // severity that depends on whether the gate is armed — something only it knows.
                // A second Error line at the source would say the same thing worse and pay twice
                // for it in Loki.
                );
    }

    /// <summary>
    /// The DYNAMIC NodeTypes of an enumeration snapshot: the active nodes that carry compilable
    /// source, keyed by path, plus their definitions. Shared by the compiling sweep
    /// (<see cref="WarmDynamicTypes"/>) and the probe-only pass
    /// (<see cref="ProbeDynamicTypes"/>) so the two can never disagree about WHAT the mesh's
    /// dynamic types are — a drift there would make the adopt-only report describe a different
    /// population than the sweep it replaces.
    ///
    /// <para>🚨 <b>The typing is <c>ContentAs</c>, never <c>Content is NodeTypeDefinition</c>, and
    /// a failure to type is COUNTED</b> (#3703). A query row's <c>Content</c> is deserialised by
    /// whichever hub served it, so an unresolvable <c>$type</c> degrades to a raw
    /// <c>JsonElement</c> and a pattern-match on the CLR type silently drops that NodeType from
    /// this population — out of <c>total</c>, out of <c>baked</c>, out of <c>pending</c>, with
    /// nothing to grep. That is the same defect this whole issue is about wearing a different hat:
    /// an instrument that cannot say "I did not check". It can now, via
    /// <see cref="DynamicTypes.Untyped"/>.</para>
    /// </summary>
    /// <param name="items">The enumeration snapshot.</param>
    /// <param name="options">The mesh hub's serializer options — the registry that resolves the
    /// content's <c>$type</c>.</param>
    /// <param name="logger">Diagnostics; an unconvertible value is named by node path.</param>
    internal static DynamicTypes DynamicTypesOf(
        IEnumerable<MeshNode> items, JsonSerializerOptions options, ILogger? logger)
    {
        var untyped = ImmutableList.CreateBuilder<string>();
        var typed = items
            .Where(n => !string.IsNullOrEmpty(n.Path) && n.State == MeshNodeState.Active)
            .Select(n => (Node: n, Definition: n.ContentAs<NodeTypeDefinition>(options, logger)))
            .Where(pair =>
            {
                if (pair.Definition is { } d)
                    // Only DYNAMIC types have source to compile. Static/framework
                    // NodeTypes ship their assembly with the process — nothing to warm.
                    return HasCompilableSource(d);
                if (pair.Node.Content is not null)
                    untyped.Add(pair.Node.Path!);
                return false;
            })
            .GroupBy(pair => pair.Node.Path!, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var nodes = typed.ToDictionary(
            g => g.Key, g => g.First().Node, StringComparer.OrdinalIgnoreCase);
        var definitions = typed.ToDictionary(
            g => g.Key, g => g.First().Definition, StringComparer.OrdinalIgnoreCase);

        if (untyped.Count > 0)
            // Warning, not silence: a NodeType this pass could not type is a type NOTHING in the
            // bake decides anything about, and the whole point of the report is that a number it
            // prints is a number over a population it can name.
            logger?.LogWarning(
                "DynamicTypePreWarmer: {Count} node(s) matched the NodeType enumeration but their "
                + "content did not resolve to a NodeTypeDefinition on this hub — they are NOT in "
                + "this report's population and NOTHING decided anything about them (#3703). "
                + "Unresolved: {Paths}",
                untyped.Count, string.Join(", ", untyped.Order(StringComparer.OrdinalIgnoreCase)));

        return new DynamicTypes(nodes, definitions, untyped.ToImmutable());
    }

    /// <summary>
    /// The dynamic NodeTypes an enumeration snapshot yielded, WITH what it could not read.
    /// </summary>
    /// <param name="Nodes">The nodes, keyed by path.</param>
    /// <param name="Definitions">Their definitions, keyed by path.</param>
    /// <param name="Untyped">Paths whose content this hub could not resolve to a
    /// <see cref="NodeTypeDefinition"/> — checked by nothing, so named by this.</param>
    internal sealed record DynamicTypes(
        Dictionary<string, MeshNode> Nodes,
        Dictionary<string, NodeTypeDefinition?> Definitions,
        ImmutableList<string> Untyped);

    /// <summary>
    /// 🚨 ASKS, NEVER BUILDS — the adopt-only pass that every boot runs.
    ///
    /// <para>Enumerates the mesh's dynamic NodeTypes and probes the assembly store for each,
    /// returning the same <see cref="NodeTypeBakeReport"/> the compiling sweep uses to decide what
    /// to build — but stopping there. No hub is activated, no compiler is driven, nothing is
    /// written. It is the measurement half of <see cref="WarmDynamicTypes"/> with the building half
    /// removed, which is exactly what a deployment that adopts CI-baked assemblies needs: after the
    /// bundle seeding has run, this says which types the adoption actually COVERED and which are
    /// left to lazy compilation.</para>
    ///
    /// <para>Faults propagate for the same reason they do in <see cref="WarmDynamicTypes"/>: an
    /// enumeration that could not be taken must not be reportable as "nothing pending".</para>
    /// </summary>
    public static IObservable<NodeTypeBakeReport> ProbeDynamicTypes(
        IMessageHub mesh, ILogger? logger = null)
    {
        var meshService = mesh.ServiceProvider.GetService<IMeshService>();
        if (meshService is null)
        {
            logger?.LogDebug("DynamicTypePreWarmer: no IMeshService registered — nothing to probe");
            return Observable.Return(
                NodeTypeBakeReport.Empty(NodeTypeCompilationHelpers.FrameworkVersion));
        }
        // Closed type set: nothing in the database is a type this process could adopt or compile,
        // so there is nothing to probe — the empty report is the true one, not a failure to read.
        if (mesh.ServiceProvider.IsClosedTypeSet())
            return Observable.Return(
                NodeTypeBakeReport.Empty(NodeTypeCompilationHelpers.FrameworkVersion));
        var accessService = mesh.ServiceProvider.GetService<AccessService>();

        // System-scoped for the same reason the sweep is: enumerating NodeType definitions across
        // every partition is infrastructure, not a user-attributable read. RunAsSystem, never
        // `Observable.Using(AccessContextScope.AsSystem, …)` — see WarmDynamicTypes (#1444/#1790).
        return accessService.RunAsSystem(
            () => meshService
                // Every NodeType definition — a catalog, mesh-wide by nature (#3202 — fan-out is opt-in).
                .Query<MeshNode>(MeshQueryRequest.FromQuery(MeshWideQuery.OfType(MeshNode.NodeTypePath)))
                .Take(1)
                .Timeout(EnumerationBudget)
                .SelectMany(change =>
                {
                    var dynamicTypes = DynamicTypesOf(
                        change.Items, mesh.JsonSerializerOptions, logger);
                    var (nodes, definitions) = (dynamicTypes.Nodes, dynamicTypes.Definitions);
                    var overlay = OverlayThisProcessAdoptions(mesh, definitions, nodes, logger);
                    return NodeTypeBakeStatus.Probe(
                            overlay.Definitions,
                            ResolveAssemblyStore(mesh),
                            logger: logger,
                            liveDependencyIdOf: NodeTypeCompilationHelpers.DependencyIdResolverOf(mesh),
                            liveToolchainId: NodeTypeCompilationHelpers.ProcessToolchainId)
                        .Select(report => report with
                        {
                            ClassifiedFromLocalAdoption = overlay.Applied.Count,
                        })
                        .Do(report => PublishReport(
                            mesh, report, NodeTypeBakeReportRegistry.AdoptOnlyProbe));
                }));
    }

    /// <summary>
    /// The stable id of the standing catalog subscription <see cref="ObserveLiveRecordCensus"/>
    /// opens — one per process, keyed with its query set, never composed per call (#1311).
    /// </summary>
    internal const string LiveRecordCensusQueryId = "nodetype-live-record-census";

    /// <summary>
    /// 🚨 <b>WATCHES, NEVER BUILDS — the live half of <c>bake-report</c></b> (#4632).
    ///
    /// <para>A standing subscription to the mesh-wide NodeType catalog, folded on every emission
    /// into a <see cref="NodeTypeLiveRecordCensus"/>: which records, AS THEY STAND NOW, name a build
    /// keyed to a framework this process does not run, and how many of those were stamped after
    /// <paramref name="bootedAt"/>. The two boot-time passes above (<see cref="ProbeDynamicTypes"/>,
    /// <see cref="WarmDynamicTypes"/>) take ONE enumeration and publish; a record another replica
    /// re-stamps a minute later is invisible to both, which is how a mid-roll cross-stamp reached a
    /// customer-facing page with every instrument green. This one keeps reading.</para>
    ///
    /// <para>Same population as the bake — dynamic types only, the ones with source to compile — and
    /// the same System scope, for the same reason: enumerating NodeType definitions across every
    /// partition is infrastructure, not a user-attributable read. The synced query is the canonical
    /// surface for a live collection (<c>hub.GetQuery</c>): one shared upstream, whole-set
    /// emissions, change events applied for the process's life. The fold is pure and touches no
    /// store and no hub, so an emission costs a pass over a few hundred typed records.</para>
    ///
    /// <para>Cold, like every observable here: the caller subscribes and owns the subscription
    /// (the hosted service disposes it with the service). Faults propagate — a catalog that cannot
    /// be read must not be reportable as "nothing foreign".</para>
    /// </summary>
    /// <param name="mesh">The mesh hub.</param>
    /// <param name="bootedAt">The boundary <see cref="NodeTypeLiveRecordCensus.ForeignSinceBoot"/> splits on.</param>
    /// <param name="logger">Optional logger.</param>
    /// <returns>One census per catalog emission.</returns>
    public static IObservable<NodeTypeLiveRecordCensus> ObserveLiveRecordCensus(
        IMessageHub mesh, DateTimeOffset bootedAt, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var accessService = mesh.ServiceProvider.GetService<AccessService>();
        var liveFramework = NodeTypeCompilationHelpers.FrameworkVersion;
        var options = mesh.JsonSerializerOptions;
        // RunAsSystem, never `Observable.Using(AccessContextScope.AsSystem, …)` — see
        // WarmDynamicTypes (#1444/#1790) for why the scope must be sealed inside the one Subscribe.
        return accessService.RunAsSystem(() => mesh
                // Every NodeType definition — a catalog, mesh-wide by nature (#3202 — fan-out is
                // opt-in, and this is one of its three legitimate shapes: a process-wide watch).
                .GetQuery(LiveRecordCensusQueryId, MeshWideQuery.OfType(MeshNode.NodeTypePath)))
            // 🚨 No logger into the fold: this runs on EVERY catalog emission for the process's
            // life, so a permanently untyped record would re-log the same conversion failure on
            // every unrelated NodeType write — a log wave during a bake. The boot-time DynamicTypesOf
            // already names each untyped path once; here it is COUNTED (Untyped) and printed.
            .Select(nodes => NodeTypeLiveRecordCensus.Of(
                LiveRecordsOf(nodes, options, logger: null), liveFramework, bootedAt, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// The census's input from one catalog emission: every ACTIVE node whose definition has
    /// compilable source (the bake's own population rule, <see cref="HasCompilableSource"/>), or
    /// whose content could not be typed at all (carried as <c>null</c> so the census can count what
    /// it decided nothing about). Static types — no source, assembly shipped with the process — are
    /// not in it, exactly as they are not in <see cref="DynamicTypesOf"/>.
    /// </summary>
    internal static ImmutableList<(string Path, NodeTypeDefinition? Definition)> LiveRecordsOf(
        IEnumerable<MeshNode> nodes, JsonSerializerOptions options, ILogger? logger)
        => nodes
            .Where(n => !string.IsNullOrEmpty(n.Path) && n.State == MeshNodeState.Active)
            .Select(n => (
                Path: n.Path!,
                Definition: n.ContentAs<NodeTypeDefinition>(options, logger),
                HasContent: n.Content is not null))
            // A typed definition is in the population iff it has source to compile; an untyped
            // node is in it iff it HAD content the hub could not type (no content is nothing at
            // all, not a failure to type) — the same two rules DynamicTypesOf applies.
            .Where(pair => pair.Definition is { } d ? HasCompilableSource(d) : pair.HasContent)
            .GroupBy(pair => pair.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.First().Path, g.First().Definition))
            .ToImmutableList();

    /// <summary>
    /// 🚨 <b>Publishes the report so <c>/health</c> can carry it</b> (#3703).
    ///
    /// <para>The report's numbers — and above all
    /// <see cref="NodeTypeBakeReport.ClassifiedFromLocalAdoption"/>, which says out loud that the
    /// sweep's input was behind this process's own writes — existed only as a boot LOG line. Log
    /// access on this fleet is break-glass, so the confirming reading for #3703 could not be taken
    /// by anyone authorised to take it, and the issue could be neither settled nor closed. Recorded
    /// here, at both report sites, it is one unauthenticated <c>curl</c> away.</para>
    ///
    /// <para>The adoption-stamp count is captured at the SAME instant, because the pair is the
    /// point: "N assemblies adopted" and "M types whose record and the share agree" are different
    /// populations in different units, and reading them as a contradiction is what #3703 was filed
    /// as. Published side by side, they cannot be read that way again.</para>
    ///
    /// <para>Best-effort by construction: a host that registered no registry publishes nothing and
    /// the health check says so in those words. It must never be able to fault the sweep.</para>
    /// </summary>
    private static void PublishReport(IMessageHub mesh, NodeTypeBakeReport report, string pass)
    {
        var registry = mesh.ServiceProvider.GetService<NodeTypeBakeReportRegistry>();
        if (registry is null)
            return;
        var stamps = mesh.ServiceProvider
            .GetService<NodeTypeAdoptionRegistry>()?.AdoptedStamps.Count ?? 0;
        registry.Record(ReadingOf(report, pass, stamps, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// 🚨 <b>THE REDUCTION STEP — the one call that used to drop the report's identities</b>
    /// (#4258), extracted as a pure function so a test can hold it directly.
    ///
    /// <para>Every <c>NodeTypeBakeEntry</c> carries its <c>TypePath</c>, and this projection kept
    /// only counts. So <c>previouslybroken=1</c> reached <c>/health</c> saying that exactly one
    /// NodeType on this replica is broken for good — the only record of that anywhere, because the
    /// rollout gate skips such a type on purpose — and declined to say which, in the one census
    /// that can see past RLS. <c>Ownership</c> is the partition each non-baked type lives in; the
    /// node's own title stays unpublished, because the body this lands on is public.</para>
    ///
    /// <para>Pure and <c>internal</c> rather than inlined above, for the reason the census itself
    /// exists: a test that stages a <see cref="BakeReportReading"/> with <c>Ownership</c> already
    /// filled in passes whatever this bridge does, so the defect's own site would have had no
    /// guard. <c>at</c> is injected for the same reason.</para>
    /// </summary>
    /// <param name="report">The bake report to publish.</param>
    /// <param name="pass">Which pass produced it.</param>
    /// <param name="adoptionStamps">Prebuilt adoptions this process holds, captured at the same instant.</param>
    /// <param name="at">When the reading was taken.</param>
    /// <returns>The reading a <c>/health</c> line can carry.</returns>
    internal static BakeReportReading ReadingOf(
        NodeTypeBakeReport report, string pass, int adoptionStamps, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(report);
        return new BakeReportReading(
            pass,
            report.FrameworkVersion,
            report.Entries.Count,
            report.Entries.Count(e => !e.NeedsBake),
            report.Pending.Count,
            report.ClassifiedFromLocalAdoption,
            adoptionStamps,
            report.Summary,
            at)
        {
            Ownership = report.Ownership,
        };
    }

    /// <summary>
    /// The shared assembly store this mesh compiles into, or <see cref="NullAssemblyStore"/> when the
    /// host registered none. A null store reports every lookup as a miss, so every type is treated as
    /// needing a bake — which matches what a storeless host does anyway (it recompiles on every
    /// activation), so the sweep degrades to its previous behaviour rather than misreporting.
    /// </summary>
    private static IAssemblyStore ResolveAssemblyStore(IMessageHub mesh) =>
        mesh.ServiceProvider.GetService<IAssemblyStore>() ?? NullAssemblyStore.Instance;

    /// <summary>
    /// 🚨 <b>THE ENUMERATION IS A PROJECTION, AND THIS PROCESS MAY ALREADY HAVE SUPERSEDED IT</b>
    /// (#3703).
    ///
    /// <para>The sweep decides what to compile from ONE mesh-wide
    /// <c>Query&lt;MeshNode&gt;(…).Take(1)</c>. That is a CQRS read — eventually consistent, and
    /// explicitly not the authoritative source for a node's content — while the prebuilt seeding
    /// pass that runs immediately before it writes each adopted type's record through
    /// <c>GetMeshNodeStream(path).Update(…)</c>, which IS authoritative. So the sweep can be handed
    /// records that predate writes made seconds earlier by the same process, and nothing in the
    /// classification can tell that apart from a genuinely stale record: both are the same
    /// bytes.</para>
    ///
    /// <para><b>What that cost, measured.</b> memex, 2026-09-08 00:31 UTC, one cold boot: the
    /// seeding pass reported <c>78 adopted now, 0 already current</c>, and ten seconds later the
    /// sweep reported <c>baked=5 pending=204 frameworkstale=201</c> on the SAME framework identity —
    /// 197 compiles instead of ~20. The two numbers were never comparable (see
    /// <see cref="NodeTypeBakeReport.ClassifiedFromLocalAdoption"/>), and the compiles the
    /// disagreement caused are what put four already-adopted <c>Doc/**</c> types on the compile path
    /// where a short source-discovery pass could turn them into false regressions (#3663).</para>
    ///
    /// <para>The fix is neither a retry nor a wait: it is to classify each type from the NEWER of
    /// the two facts. <see cref="NodeTypeAdoptionRegistry.OverlayOnto"/> applies the process's own
    /// stamp only where the snapshot's <see cref="MeshNode.Version"/> proves the snapshot predates
    /// it, so a record that has since moved on — an owner refusal, a recompile — always wins.</para>
    /// </summary>
    private static AdoptionOverlay OverlayThisProcessAdoptions(
        IMessageHub mesh,
        IReadOnlyDictionary<string, NodeTypeDefinition?> definitions,
        IReadOnlyDictionary<string, MeshNode> nodes,
        ILogger? logger)
    {
        var registry = mesh.ServiceProvider.GetService<NodeTypeAdoptionRegistry>();
        if (registry is null)
            return new AdoptionOverlay(
                definitions.ToImmutableDictionary(
                    kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase),
                []);

        var overlay = registry.OverlayOnto(definitions, nodes);
        // 🚨 SAY IT. An instrument that silently corrected its own input would be the next version
        // of the defect: the operator reading "197 compiles" needs to know the enumeration was
        // behind, because a snapshot that is behind for 73 types on a 35-second seeding pass is a
        // fact about this deployment's read path, not about its content.
        if (!overlay.Applied.IsEmpty)
            logger?.LogInformation(
                "DynamicTypePreWarmer: the NodeType enumeration snapshot PREDATES this process's own "
                + "prebuilt adoptions for {Count} type(s) — classifying those from the record this "
                + "process wrote, not from the snapshot (#3703). A snapshot at or below the node "
                + "version an adoption wrote over cannot contain that write; one above it wins and is "
                + "used unchanged. Superseded: {Types}",
                overlay.Applied.Count, string.Join(", ", overlay.Applied));
        return overlay;
    }

    /// <summary>
    /// 🚨 ONE PROCESS BAKES; the rest SUBSCRIBE TO THE GO.
    ///
    /// <para>The cache is shared but the decision to rebuild is per-process, so without
    /// coordination every replica on a new image independently finds the same framework-stale cache
    /// and starts the same sweep into the same volume — concurrent cold compiles of the SAME
    /// NodeType, precisely the storm the sequential ordered sweep exists to prevent (four
    /// concurrent compiles on memex, 2026-07-28 04:05, dropped six plugin roots to the "did not
    /// settle" overlay). A rollout with <c>maxSurge</c>, or any <c>replicas &gt; 1</c>, hits this
    /// by default.</para>
    ///
    /// <para>Coordination is the build protocol (<c>Doc/Architecture/BuildCoordination</c>): the
    /// <c>Admin/Build</c> claim decides who bakes, and every other process completes on the
    /// per-fingerprint GO subscription. This replaced a file lease beside the assembly cache plus
    /// a 60 s share-poll follower; the lease's one-builder and steal-on-stale properties live on
    /// in the claim arbiter, asserted by the protocol's own tests. The protocol path runs even
    /// when the report is complete — a complete share still (re)publishes its fingerprint's GO,
    /// so a baker that crashed between finishing the share and announcing it heals on the next
    /// boot instead of stranding future GO waiters.</para>
    ///
    /// <para><paramref name="buildProtocol"/> <c>false</c> is the escape hatch: bake solo, no
    /// coordination — the right shape for a monolith, a test, a dev box, and the wrong one for any
    /// fleet.</para>
    /// </summary>
    private static IObservable<PreWarmOutcome> BakeOrFollow(
        IMessageHub mesh,
        IWorkspace workspace,
        AccessService? accessService,
        IReadOnlyDictionary<string, NodeTypeDefinition?> definitions,
        IReadOnlyDictionary<string, MeshNode> nodes,
        IAssemblyStore store,
        NodeTypeBakeReport report,
        TimeSpan budget,
        TimeSpan pacing,
        bool batchBake,
        bool buildProtocol,
        ILogger? logger,
        Action<string>? progress = null)
    {
        if (buildProtocol)
            return BuildProtocolDriver.Run(
                mesh, report, definitions, store,
                () => WarmPending(
                    mesh, workspace, accessService, definitions, nodes, report,
                    budget, pacing, batchBake, logger, progress),
                logger, progress);

        return WarmPending(mesh, workspace, accessService, definitions, nodes, report, budget, pacing, batchBake, logger, progress);
    }

    /// <summary>
    /// Warm every NodeType the <paramref name="report"/> says still needs building, dependencies
    /// first, one at a time. Types the share already holds are reported
    /// <see cref="PreWarmStatus.AlreadyBaked"/> and never activated.
    /// </summary>
    /// <summary>
    /// 🚨 <b>The FIRST bake of a brand-new instance has NO baseline at all</b> — every type in the
    /// report has never been built here.
    ///
    /// <para>Measured on <c>pearl.meshweaver.cloud</c> (2026-09-15/16): a freshly provisioned portal
    /// served <c>503</c> at the edge for nine hours with a RUNNING pod, because two types the
    /// registry pre-installs (<c>GoogleMaps/Gallery</c>, <c>MyAi/Panel</c>) could not compile —
    /// their store-delivered modules had not arrived. Every entry was
    /// <see cref="BakeState.NeverBuilt"/>, <see cref="NodeTypeBakeEntry.WasHealthy"/> counts
    /// NeverBuilt as healthy, so a first-ever compile failure was filed as a REGRESSION and the pod
    /// gated itself forever.</para>
    ///
    /// <para>The gate's own sentence is the argument: it refuses readiness <i>"so the rollout stalls
    /// with the previous image still serving"</i>. On a first rollout there is no previous image and
    /// no previous pod, so refusing protects nobody.</para>
    /// </summary>
    /// <param name="report">The bake report read before anything is rebuilt.</param>
    /// <returns><c>true</c> only when the report has entries and EVERY one is NeverBuilt.</returns>
    public static bool IsFirstBake(NodeTypeBakeReport report) =>
        report.Entries.Count > 0
        && report.Entries.All(e => e.State is BakeState.NeverBuilt);

    /// <summary>
    /// The regression baseline for <paramref name="report"/>: the types that were WORKING on the way
    /// in, so a downstream gate can tell a NEW failure from one that was already broken (see
    /// <see cref="PreWarmOutcome.WasHealthyBeforeBake"/>).
    ///
    /// <para>🚨 Faithful to <see cref="NodeTypeBakeEntry.WasHealthy"/> and nothing else — a
    /// <see cref="BakeState.NeverBuilt"/> type IS in this set, because it is not damaged goods.
    /// Whether the baseline may be USED to gate is the separate question
    /// <see cref="IsFirstBake"/> answers, carried to the gate as
    /// <see cref="PreWarmOutcome.HasRegressionBaseline"/>.</para>
    ///
    /// <para>This function briefly returned EMPTY on a first bake (#4472). That reached the right
    /// gating verdict, but by asserting something false about every type in the report — and since
    /// <c>BuildProtocolDriver.OutcomesOf</c> stamps the same field straight off the entry, the GO and
    /// the gate ended up disagreeing about one type (#4496). Two facts, two fields; a helper that
    /// pre-combines them is how they drift.</para>
    /// </summary>
    /// <param name="report">The bake report read before anything is rebuilt.</param>
    public static ImmutableHashSet<string> RegressionBaseline(NodeTypeBakeReport report) =>
        report.Entries
            .Where(e => e.WasHealthy)
            .Select(e => e.TypePath)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 🚨 <b>THE stamp — the ONLY place either gating fact is written onto an outcome.</b> Reads a
    /// report once and returns the projection that sets both
    /// <see cref="PreWarmOutcome.WasHealthyBeforeBake"/> and
    /// <see cref="PreWarmOutcome.HasRegressionBaseline"/>.
    ///
    /// <para>It exists because the alternative has already failed once. The sweep and
    /// <c>BuildProtocolDriver.OutcomesOf</c> both mint outcomes for the same gate, and each used to
    /// derive the fields its own way — one from a set, one from the entry. #4472 changed one of
    /// those derivations and the two silently disagreed about a never-built type for the whole of
    /// #4496: the GO held where the gate did not, with nothing red, because a duplicated rule does
    /// not announce that its copies have diverged.</para>
    ///
    /// <para>So there is one derivation and two call sites, not two derivations. Both facts come
    /// off the SAME report, computed once per sweep rather than per outcome.</para>
    /// </summary>
    /// <param name="report">The bake report read before anything is rebuilt.</param>
    public static Func<PreWarmOutcome, PreWarmOutcome> BaselineStamp(NodeTypeBakeReport report)
    {
        var healthyBefore = RegressionBaseline(report);
        // 🚨 #5544 — "is there anything to protect?" is asked per TYPE, not only per report. A
        // failure regresses this image only when a working build of the type is on record and was
        // produced by another, not-newer platform build, and only while this build has not already
        // served here. A never-built type counted as "healthy" (true — it is not damaged goods) and
        // therefore as regressable (false — it has nothing to regress FROM), and that one conflation
        // refused every portal pod on memex.systemorph.com, the serving image's included.
        var regressable = report.ThisBuildHasServed || IsFirstBake(report)
            ? ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase)
            : report.Entries
                .Where(e => e.IsRegressionBaselineFor(report.LivePlatformVersion))
                .Select(e => e.TypePath)
                .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        return outcome => outcome with
        {
            WasHealthyBeforeBake = healthyBefore.Contains(outcome.TypePath),
            HasRegressionBaseline = regressable.Contains(outcome.TypePath),
        };
    }

    private static IObservable<PreWarmOutcome> WarmPending(
        IMessageHub mesh,
        IWorkspace workspace,
        AccessService? accessService,
        IReadOnlyDictionary<string, NodeTypeDefinition?> definitions,
        IReadOnlyDictionary<string, MeshNode> nodes,
        NodeTypeBakeReport report,
        TimeSpan budget,
        TimeSpan pacing,
        bool batchBake,
        ILogger? logger,
        Action<string>? progress = null)
    {
        var baked = report.Entries
            .Where(e => !e.NeedsBake)
            .Select(e => e.TypePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var bytesMissing = report.BytesMissing
            .Select(e => e.TypePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The regression baseline, captured BEFORE anything is rebuilt: which types were working on
        // the way in. A type missing from this set was already broken, so its failure is pre-existing
        // damage rather than something this image caused — see PreWarmOutcome.WasHealthyBeforeBake.
        // ONE stamp, shared with BuildProtocolDriver.OutcomesOf so the two cannot derive the
        // gating facts differently — see BaselineStamp for why that is not a hypothetical.
        var stampBaseline = BaselineStamp(report);
        if (IsFirstBake(report))
            logger?.LogWarning(
                "DynamicTypePreWarmer: FIRST BAKE — all {Count} NodeType(s) are NeverBuilt on this "
                + "instance, so there is no previous image to protect. A compile failure is reported "
                + "but does NOT gate readiness; the gate resumes its full strictness once anything "
                + "has been built here.",
                report.Entries.Count);

        // Say it out loud when the SHARE changed rather than the code: a record that claims a
        // live-framework build with no bytes behind it means the cache was cleared, remounted or
        // restored — a very different diagnosis from "these types are stale", and one an operator
        // reading the log at 3am should not have to infer from a recompile count.
        if (!report.BytesMissing.IsEmpty)
            logger?.LogWarning(
                "DynamicTypePreWarmer: {Count} NodeType(s) claim a usable build for the live "
                + "framework but the assembly store has NO bytes for them — the shared cache was "
                + "cleared or replaced. Rebuilding: {Types}",
                report.BytesMissing.Count,
                string.Join(", ", report.BytesMissing.Select(e => e.TypePath)));

        // 🚨 DEPENDENCIES FIRST, ONE AT A TIME. A NodeType can compile ANOTHER type's
        // Code into its own assembly (Store/Plugin declares shared=@Store/Coupon/Source,
        // @Store/Order/Source, @Store/BillingProfile/Source), and every plugin ROOT is
        // itself an instance of such a type — so warming them in arbitrary order makes
        // dependents wait on dependencies that have not been built yet, and they blow
        // the 60s activation budget:
        //     [STALE-CALLBACK] … SubscribeRequest@Store/Plugin(45028ms)
        //     TimeoutException: No response received … within 00:01:00 → Store/Plugin
        // The order is computed from the DECLARED sources, so it stays correct as
        // plugins add cross-type sources without anyone maintaining a list.
        //
        // The order is computed over EVERY type, not just the pending ones: an
        // already-baked dependency still has to be positioned before its dependents so
        // the blocked-by walk below sees it as satisfied rather than as absent.
        var dependencies = NodeTypeDependencyGraph.Build(definitions);
        var order = NodeTypeDependencyGraph.TopologicalOrder(dependencies, out var cyclic);
        var pending = order.Where(p => !baked.Contains(p)).ToList();

        // Initial-bake fast path (issue #1207): drive the compiler DIRECTLY for every pending
        // type — batched source discovery, no per-type hub activation, no compile-watcher
        // settle, no cross-silo hop (the 2026-08-10/11 20-min/5-h bakes were per-type
        // activation round-trips eating 5-minute timeouts on a wedged peer, not compilation).
        // Only taken when requested (the pod's bake gates readiness, or PreWarm:BatchBake) AND
        // the compiler + mesh service are actually on this host.
        var batchCompiler = batchBake
            ? mesh.ServiceProvider.GetService<IMeshNodeCompilationService>()
            : null;
        var batchMeshService = batchBake ? mesh.ServiceProvider.GetService<IMeshService>() : null;
        var useBatch = batchBake && pending.Count > 0
            && batchCompiler is not null && batchMeshService is not null;
        if (batchBake && !useBatch && pending.Count > 0)
            logger?.LogWarning(
                "DynamicTypePreWarmer: batch bake requested but "
                + "{Missing} is not registered — falling back to the activation-driven sweep",
                batchCompiler is null ? "IMeshNodeCompilationService" : "IMeshService");

        // 🚨 THE UNITS ARE NODETYPES AND THE SOURCE IS THE RECORD (#3703). "already on the share"
        // used to stand where "need no build" now does, and it invited exactly one misreading: that
        // this number is a census of the assembly store, comparable with the adoption pass's "N
        // prebuilt assembly(ies) … are backed by the assembly store". It never was. The store is
        // asked ONE question per type — "bytes at the version this type's RECORD names?" — so this
        // counts types whose record and the share agree, over the enumeration snapshot, in
        // NodeTypes; the adoption line counts BUNDLE ENTRIES whose bytes were written, in
        // assemblies. Two populations, two units, two sources.
        logger?.LogInformation(
            "DynamicTypePreWarmer: {Pending} of {Total} dynamic NodeType(s) need building "
            + "(sequential, dependency order, {Mode}, perTypeBudget={Budget}) — {Baked} need no build "
            + "(record and share agree; this is a count of NodeTypes judged from their records, NOT a "
            + "census of the assembly store). "
            + "{Report}. Building: {Order}",
            pending.Count, order.Count, useBatch ? "batch direct-compile" : "activation-driven",
            budget, baked.Count, report.Summary,
            pending.Count == 0 ? "(nothing)" : string.Join(" → ", pending));
        if (!cyclic.IsEmpty)
            logger?.LogWarning(
                "DynamicTypePreWarmer: {Count} NodeType(s) form a source dependency CYCLE and "
                + "cannot be ordered RELATIVE TO EACH OTHER — warmed together, in path order, as "
                + "soon as everything outside the cycle that they wait on is built (#1347: they "
                + "used to be demoted to last, which put the whole store/paywall chain at the end "
                + "of the sweep): {Cyclic}",
                cyclic.Count, string.Join(", ", cyclic));

        // FAIL GRACEFULLY DOWNSTREAM. A type whose upstream did not reach a usable build
        // cannot build either, so it is not attempted: it is reported naming the blocker,
        // and joins a skip set so ITS dependents are skipped too — the propagation is
        // transitive purely because we walk in topological order. Without this, one broken
        // type costs every dependent a full per-type budget of waiting for something that
        // cannot succeed.
        //
        // 🚨 TWO skip sets, not one, because "it broke" and "I never found out" must NOT
        // propagate as the same thing. A readiness gate may stall a rollout on the first
        // and must never stall on the second:
        //
        //   verdictFailed — the sweep got an answer and the answer was bad (CompileError,
        //     Faulted, or a dependent of one of those). Dependents get UpstreamFailed,
        //     which GATES.
        //   unevaluated   — the sweep got no answer at all (TimedOut, or a dependent of
        //     something unevaluated). Dependents get UpstreamUnevaluated, which does NOT
        //     gate. A cross-silo SubscribeRequest timeout on a shared upstream (core #694)
        //     is not evidence that anything is broken, so it must not become one downstream.
        //
        // Collapsing these back into one set is exactly how the 2026-08-02 memex-cloud
        // stall would return: the direct-timeout leniency in NodeTypeBakeGateState would
        // still hold, and the false regression would simply reappear one hop downstream.
        //
        // The sets are mutated inside a Concat, which subscribes strictly one at a time,
        // so there is no concurrent access — and each step is wrapped in Defer so it
        // reads them as they stand WHEN ITS TURN COMES, not when the chain was built.
        var verdictFailed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unevaluated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Third cascade, same reasoning one axis over: a type whose sources were DELETED
        // (PreWarmStatus.NoSources) is a verdict — but a CONTENT verdict, and its dependents must
        // inherit "content-broken", not the gating UpstreamFailed. See PreWarmStatus.NoSources.
        var contentBroken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Concat, never Merge: the next type is SUBSCRIBED only after the previous one
        // has completed, so "dependencies first" is structural rather than a convention.
        // The gap between types keeps the sweep a background trickle rather than a queue
        // of back-to-back cold activations; it costs nothing, since the warm-up has no
        // deadline and Part 2 compiles lazily regardless. (The batch path skips the gap:
        // it activates nothing, so there is no burst to spread out — and a gated pod
        // serves nobody, so there is nothing to be gentle to.)
        IObservable<PreWarmOutcome> Sweep(
            ImmutableDictionary<string, IReadOnlyList<MeshNode>>? batchSources,
            ImmutableHashSet<string> absentPartitions)
            => order
                .Select((p, i) => Observable.Defer(() =>
                {
                    // Already on the share: report it and move on WITHOUT activating the hub.
                    // Skipping the activation is the entire saving — it is what turns a
                    // re-run, a second replica, or a resumed bake from hours of Roslyn into
                    // a directory listing.
                    if (baked.Contains(p))
                        return Observable.Return(new PreWarmOutcome(
                            p, PreWarmStatus.AlreadyBaked, "assembly store already holds this build"));

                    // 🚨 ITS PARTITION IS GONE, so there is nothing to warm and nothing to wait
                    // for (#5073). Asked BEFORE the work, never after it: warming such a type can
                    // only end one of two ways, and both were measured in production —
                    //
                    //   activation-driven: the SubscribeRequest routes to a partition hub whose
                    //     store was dropped, is never answered, and the entry costs a FULL per-type
                    //     budget (5 minutes at the default). BakePhase stays Running for the whole
                    //     sweep, so a readiness-gated pod stays out of rotation for that × the
                    //     number of dead entries — 31 of them held one pod 2/3 for 48+ minutes with
                    //     maxUnavailable: 0, i.e. two builds serving one host;
                    //   batch-driven: the compile's state write to a node in that partition fails
                    //     (OwnerUnreachable), which is a FAULTED image verdict, and the one absence
                    //     test that would rescue it (TypeNodeExists) asks the same stale index the
                    //     entry came from and answers "still there" — so the verdict stands and the
                    //     gate refuses readiness FOREVER.
                    //
                    // Reported as Removed and filed under contentBroken exactly as a repository-
                    // retired type is: which partitions exist is a property of the mesh, not of the
                    // framework being rolled out, so no image caused it and no rollout can fix it.
                    // NOT silently skipped — an operator who cannot see it cannot clean it up.
                    if (SkipForAbsentPartition(p, absentPartitions) is { } gone)
                    {
                        contentBroken.Add(p);
                        logger?.LogWarning(
                            "DynamicTypePreWarmer: {TypePath} → {Status} — its partition "
                            + "'{Partition}' is CONFIRMED ABSENT by the storage providers, so the "
                            + "definition this index row names no longer has a store to live in. "
                            + "Not warmed: activating it could only time out, and a stale index row "
                            + "cannot be disproved by asking the index. The row itself is left "
                            + "alone — a boot sweep reports data it cannot account for, it does not "
                            + "delete it",
                            p, gone.Status, PartitionExistenceProbe.PartitionOf(p));
                        return Observable.Return(gone);
                    }

                    // A real verdict upstream wins over a merely-unevaluated one: if ANY dependency
                    // actually failed to compile, this type is genuinely blocked and must gate,
                    // regardless of some other dependency having also timed out. A content-broken
                    // upstream sits between the two: it IS a verdict (so it beats "I don't know"),
                    // but a content verdict — its dependents inherit content-broken, never gating.
                    var blocker = NodeTypeDependencyGraph.FirstBlockedBy(p, dependencies, verdictFailed);
                    var contentBlocker = blocker is null
                        ? NodeTypeDependencyGraph.FirstBlockedBy(p, dependencies, contentBroken)
                        : null;
                    var unevaluatedBlocker = blocker is null && contentBlocker is null
                        ? NodeTypeDependencyGraph.FirstBlockedBy(p, dependencies, unevaluated)
                        : null;
                    var missingBytes = bytesMissing.Contains(p);
                    if (blocker is not null || contentBlocker is not null || unevaluatedBlocker is not null)
                    {
                        var named = blocker ?? contentBlocker ?? unevaluatedBlocker!;
                        var outcome = blocker is not null ? PreWarmStatus.UpstreamFailed
                            : contentBlocker is not null ? PreWarmStatus.UpstreamContentBroken
                            : PreWarmStatus.UpstreamUnevaluated;
                        (blocker is not null ? verdictFailed
                            : contentBlocker is not null ? contentBroken
                            : unevaluated).Add(p);
                        // {Outcome} carries WHICH cascade this is as a queryable token — the two
                        // differ in whether they may stall a rollout, so a log that blurred them
                        // would hide the thing an operator most needs to know.
                        logger?.LogWarning(
                            "DynamicTypePreWarmer: skipping {TypePath} — its dependency {Blocker} "
                            + "did not yield a usable build ({Outcome}), so it cannot compile either "
                            + "(lazy compile still applies if the upstream recovers)",
                            p, named, outcome);
                        // No pacing for a skip: it activates nothing, so there is no
                        // burst to spread out and no reason to slow the sweep down.
                        //
                        // BlockedBy carries the blocker as DATA, not just inside the detail text:
                        // a derived regression is retracted when its blocker's is, and parsing that
                        // relationship back out of a message would be the kind of coupling that
                        // breaks the next time the wording changes.
                        return Observable.Return(
                            new PreWarmOutcome(p, outcome, $"blocked by {named}") { BlockedBy = named });
                    }

                    // Batch mode drives the ONE compiler directly with the pre-resolved source
                    // set — no activation, no watcher settle. It also subsumes the missing-bytes
                    // case: a direct compile re-emits, re-uploads and re-stamps regardless of
                    // what the record claims, so no record surgery is needed to force it.
                    //
                    // 🚨 A type whose BYTES are gone cannot be warmed by activation alone.
                    // WarmOne waits for HasUsableBuild, which is a RECORD check that deliberately
                    // ignores status — and in this state the record is pristine. Activating the hub
                    // would therefore return "Compiled" instantly without rebuilding anything, and
                    // the sweep would report a green bake over a share that is still empty. The
                    // rebuild has to be DRIVEN.
                    // #5544 — the readiness message names THIS type and the bound it may take,
                    // so a sweep that is slow reads as slow, on the right type, rather than as
                    // "enumerating" for hours.
                    //
                    // 🚨 A type ABSENT from batchSources had no source set the discovery could
                    // establish (its own query failed, or its record contradicts an empty answer).
                    // It is warmed by activation — ONE type leaves the batch, never all of them, and
                    // it is never compiled against an empty set (that would be #1216's fabricated
                    // verdict).
                    IReadOnlyList<MeshNode>? batchSet = null;
                    MeshNode? typeNode = null;
                    var inBatch = batchSources is not null
                                  && batchSources.TryGetValue(p, out batchSet)
                                  && nodes.TryGetValue(p, out typeNode);
                    progress?.Invoke(
                        $"building {pending.IndexOf(p) + 1} of {pending.Count} pending NodeType(s) "
                        + (inBatch
                            ? "by direct batch compile"
                            : bytesMissing.Contains(p)
                                ? "by a store-miss rebuild"
                                : "by activation (the batch did not establish its sources)")
                        + $": waiting on {p}, for up to {budget}");
                    var warm = inBatch
                        ? NodeTypeBatchBake.BakeOne(mesh, typeNode!, batchSet!, budget, logger)
                        : missingBytes
                            ? RebuildMissingBytes(workspace, accessService, p, budget, logger)
                            : WarmOne(workspace, accessService, p, budget, logger);

                    return warm
                        .DelaySubscription(
                            i == 0 || batchSources is not null ? TimeSpan.Zero : pacing)
                        // (A type the batch withheld is activated WITHOUT pacing too: batchSources is
                        // non-null only on a gated pod, which serves nobody while it bakes.)
                        // 🚨 A verdict against a node that NO LONGER EXISTS is not a verdict about
                        // the image. The definitions were enumerated once, at the start of the
                        // sweep; a repository sync can prune a retired type at any moment after
                        // that, and the compile of the pruned type then fails with the routing's
                        // "No node found" — which is exactly what memex.systemorph.com recorded
                        // as a gating CompileError on 2026-09-08. Asked only for an image verdict.
                        .SelectMany(o => ReclassifyIfRemoved(mesh, o, logger))
                        .Do(o =>
                        {
                            // A timeout is not a verdict — route it to `unevaluated` so its
                            // dependents inherit "no answer", not "it broke". Deleted sources, a
                            // retired type and a removed type are CONTENT verdicts — dependents
                            // inherit content-broken, never gating. Everything else that missed a
                            // usable build (CompileError, Faulted) is an image verdict.
                            if (o.Status is PreWarmStatus.TimedOut)
                                unevaluated.Add(p);
                            else if (o.Status is PreWarmStatus.NoSources
                                     or PreWarmStatus.DeclaredSourcesMissing
                                     or PreWarmStatus.Retired
                                     or PreWarmStatus.Removed)
                                contentBroken.Add(p);
                            else if (!o.ReachedUsableBuild)
                                verdictFailed.Add(p);
                        });
                }))
                .Concat()
                // Stamp the regression baseline on every outcome so a downstream readiness gate can
                // tell a NEW failure from one that was already broken on the way in, without having
                // to re-read the report — and, beside it, whether this instance has any previous
                // build to regress FROM.
                .Select(stampBaseline);

        if (order.Count == 0)
            return Observable.Empty<PreWarmOutcome>();

        // Batch discovery first — ONE pass resolving every pending type's source queries.
        // Only a DISCOVERY failure falls back to the activation-driven sweep (before any
        // outcome was produced); a failure inside the sweep itself propagates as usual.
        //
        // 🚨 "Discovery failure" is now ASSERTED, not assumed (issue #1216). ResolveSources errors
        // when its query hit its own ceiling (possible truncation) or when a type that declares
        // source queries resolved to an empty set with nothing corroborating it. Both mean the batch
        // does not know what the sources ARE, and a compile driven from a set you did not establish
        // produces verdicts about code from evidence you do not have — on memex-cloud 2026-08-11
        // that read as "169 of 237 types are content-broken" and, on an ungated pod, would have
        // baked a fleet of empty assemblies with nothing refusing readiness. The unit of that "I don't
        // know" is the TYPE, not the batch: ResolveSources re-resolves every type from its own
        // anchored queries when the global pass cannot be trusted, and leaves out of the map only the
        // types it still could not establish — those, and only those, take the activation-driven
        // sweep (which resolves their sources itself). Measured on memex-cloud 2026-09-24: one
        // unanswerable global fetch sent ALL 38–80 pending types of every boot down that road. What
        // still reaches the Catch below is a fault in discovery's own composition, not a query's.
        IObservable<ImmutableDictionary<string, IReadOnlyList<MeshNode>>?> BatchSources() =>
            Observable.Defer(() =>
            {
                progress?.Invoke(
                    $"resolving the source sets of {pending.Count} pending NodeType(s) in one "
                    + "batched discovery pass");
                return Observable.Return(System.Reactive.Unit.Default);
            })
            .SelectMany(_ => NodeTypeBatchBake
                // 🚨 The registry is the PUBLICATION of #3704's discriminator — the per-pass chunk
                // count and the largest inter-chunk gap — so /health can carry what only a Loki query
                // could read before. Resolved, never required: a host without one publishes nothing and
                // the check says so in those words rather than reading as clean.
                .ResolveSources(
                    batchMeshService!, accessService, definitions, pending, logger,
                    mesh.ServiceProvider.GetService<SourceDiscoveryRegistry>())
                .Select(index =>
                    (ImmutableDictionary<string, IReadOnlyList<MeshNode>>?)index)
                .Catch<ImmutableDictionary<string, IReadOnlyList<MeshNode>>?, Exception>(ex =>
                {
                    logger?.LogWarning(ex,
                        "DynamicTypePreWarmer: batched source discovery did not establish the source "
                        + "sets — abandoning the batch and falling back to the activation-driven sweep "
                        + "for ALL {Pending} pending type(s). No type is reported from this pass, so "
                        + "nothing here can be mistaken for a content or compile verdict.",
                        pending.Count);
                    return Observable.Return(
                        (ImmutableDictionary<string, IReadOnlyList<MeshNode>>?)null);
                }));

        // 🚨 ONE provider vote, in front of everything, over the distinct partitions of the PENDING
        // types (#5073) — see the skip inside Sweep for what it prevents and what it cost. It runs
        // first because the fact it establishes is only useful before the work: afterwards the
        // budget has already been spent, or the verdict has already been formed and cannot be
        // disproved. The probes run together, so the whole answer is bounded by ONE probe budget
        // rather than by the number of partitions, and it fails OPEN — an indeterminate answer, or
        // any provider saying the partition IS there, leaves this sweep byte-for-byte as it was.
        return AbsentPartitionsAmongPending(mesh, pending, logger)
            .SelectMany(absent => useBatch
                ? BatchSources().SelectMany(index => Sweep(index, absent))
                : Sweep(null, absent));
    }

    /// <summary>
    /// The partitions among <paramref name="pending"/> that the writable storage providers CONFIRM
    /// are gone — the one absence witness that is not the node index the pending set came from.
    /// Emits one set and completes; a host with no partition providers, or one that cannot answer,
    /// yields the empty set (<see cref="PartitionExistenceProbe"/> fails open).
    /// </summary>
    /// <param name="mesh">The mesh hub whose container holds the partition providers.</param>
    /// <param name="pending">The type paths about to be warmed.</param>
    /// <param name="logger">Optional.</param>
    internal static IObservable<ImmutableHashSet<string>> AbsentPartitionsAmongPending(
        IMessageHub mesh, IReadOnlyCollection<string> pending, ILogger? logger)
    {
        if (pending.Count == 0)
            return Observable.Return(
                ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase));

        // Read-only seeds (EmbeddedResource, StaticNode) own no per-partition store and so can
        // never witness one missing — the same exclusion PathResolutionService makes.
        var writable = mesh.ServiceProvider.GetServices<IPartitionStorageProvider>()
            .Where(p => !p.IsReadOnly)
            .ToList();

        return PartitionExistenceProbe
            .ConfirmedAbsentAmong(
                writable, pending.Select(PartitionExistenceProbe.PartitionOf), logger)
            .Do(absent =>
            {
                if (!absent.IsEmpty)
                    logger?.LogWarning(
                        "DynamicTypePreWarmer: {Count} partition(s) named by pending NodeType rows "
                        + "are CONFIRMED ABSENT by the storage providers — their types are reported "
                        + "and NOT warmed, because warming a type whose store is gone can only spend "
                        + "its whole budget or produce a verdict no image can fix: {Partitions}",
                        absent.Count, string.Join(", ", absent.OrderBy(x => x, StringComparer.Ordinal)));
            })
            .Catch<ImmutableHashSet<string>, Exception>(ex =>
            {
                // A probe that could not be taken must never stop the bake: this is an optimisation
                // over a sweep that already works, and the fail-open direction is the safe one.
                logger?.LogWarning(ex,
                    "DynamicTypePreWarmer: the partition-existence probe could not be taken — every "
                    + "pending type is warmed exactly as before");
                return Observable.Return(
                    ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase));
            })
            // 🚨 THE OUTERMOST BACKSTOP, and the one that matters most: the whole sweep is composed
            // behind this with SelectMany, so a source that completed WITHOUT EMITTING would produce
            // a sweep that emits no outcomes and completes normally — which the hosted service reads
            // as a clean bake and the gate then certifies. That is the one failure mode
            // WarmDynamicTypes faults an enumeration error to avoid, and a silent completion here
            // would reintroduce it through the back door. The probe cannot do that today (see
            // PartitionExistenceProbe's own DefaultIfEmpty); this is what keeps that a fact rather
            // than a hope.
            .DefaultIfEmpty(ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>A NodeType has something for Roslyn to compile (so it is a dynamic type worth warming).</summary>
    internal static bool HasCompilableSource(NodeTypeDefinition d) =>
        !string.IsNullOrWhiteSpace(d.Configuration)
        || !string.IsNullOrWhiteSpace(d.HubConfiguration)
        || (d.Sources is { Count: > 0 });

    /// <summary>
    /// Rebuild a NodeType whose record claims a usable build for the LIVE framework but whose bytes
    /// are no longer in the shared assembly store — a cleared, remounted or partially-restored
    /// assembly-cache volume.
    ///
    /// <para><b>Why activation is not enough.</b> Every existing kickoff is keyed on the RECORD:
    /// first-build needs null assembly fields, recovery needs <c>Compiling</c>, framework-stale needs
    /// a version mismatch, and the release watcher needs a fresh release request. In this state the
    /// record satisfies none of them — it is a clean <c>Ok</c> pointing at bytes that are gone — so
    /// activating the hub fires nothing, and <see cref="WarmOne"/>'s <c>HasUsableBuild</c> wait
    /// (which deliberately ignores status) would return <see cref="PreWarmStatus.Compiled"/> at once.
    /// The sweep would then report a green bake over an empty share, which is the one outcome a bake
    /// gate must never produce.</para>
    ///
    /// <para><b>How.</b> Flip <see cref="CompilationStatus.Pending"/> — the same lever the
    /// framework-stale kickoff and the enrichment self-heal pull — so the per-NodeType compile
    /// watcher rebuilds, then wait for a compile that is demonstrably FRESH: a settled
    /// <see cref="CompilationStatus.Ok"/> whose <see cref="NodeTypeDefinition.LastCompileSucceededAt"/>
    /// is newer than the one we observed before flipping. Matching on status alone would accept the
    /// replayed pre-existing Ok and green-light a share that never got its bytes back.</para>
    ///
    /// <para>The settle subscription is established BEFORE the flip is issued (left-to-right
    /// <c>Merge</c>), so a compile that completes quickly cannot land in the gap between triggering
    /// and listening.</para>
    /// </summary>
    private static IObservable<PreWarmOutcome> RebuildMissingBytes(
        IWorkspace workspace,
        AccessService? accessService,
        string typePath,
        TimeSpan budget,
        ILogger? logger)
        // RunAsSystem, never `Observable.Using(AccessContextScope.AsSystem, …)` — see
        // WarmDynamicTypes (#1444/#1790). The whole flip-and-wait below stays inside the work
        // factory, so its emission-time behaviour is unchanged.
        => accessService.RunAsSystem(
                () =>
                {
                    var stream = workspace.GetMeshNodeStream(typePath);
                    return stream
                        .Take(1)
                        .Timeout(budget)
                        .SelectMany(current =>
                        {
                            var baseline = (current?.Content as NodeTypeDefinition)?.LastCompileSucceededAt;
                            logger?.LogInformation(
                                "DynamicTypePreWarmer: {TypePath} has no bytes in the assembly store despite a "
                                + "clean record — flipping CompilationStatus=Pending to force a rebuild "
                                + "(previous success {Baseline})",
                                typePath, baseline);

                            var settled = stream
                                .Where(n => n?.Content is NodeTypeDefinition d
                                    && (d.CompilationStatus is CompilationStatus.Error
                                                            or CompilationStatus.Unavailable
                                        || (d.CompilationStatus == CompilationStatus.Ok
                                            && IsFreshSuccess(d.LastCompileSucceededAt, baseline))))
                                .Take(1)
                                .Timeout(budget)
                                .Select(n =>
                                {
                                    var d = (NodeTypeDefinition)n!.Content!;
                                    return d.CompilationStatus switch
                                    {
                                        CompilationStatus.Error => FromFailedCompile(typePath, d),
                                        // The rebuild never reported an answer — not a
                                        // compile failure, so never labelled one.
                                        CompilationStatus.Unavailable => new PreWarmOutcome(
                                            typePath, PreWarmStatus.TimedOut, d.CompilationError),
                                        _ => new PreWarmOutcome(
                                            typePath, PreWarmStatus.Compiled, "rebuilt after store miss")
                                    };
                                });

                            // Never emits — it exists only for its write side effect, and it is
                            // merged SECOND so `settled` is already subscribed when it fires.
                            var trigger = stream
                                .Update(node =>
                                {
                                    if (node?.Content is not NodeTypeDefinition def)
                                        return node!;
                                    // Don't clobber an in-flight compile someone else already started…
                                    // …but ONLY while it can still be in flight (#1462).
                                    //
                                    // 🚨 `Compiling` is a non-terminal state and nothing else reconciles
                                    // it. The flip to `Compiling` is DURABLE; the terminal write is not
                                    // guaranteed — a process death mid-compile leaves the row there for
                                    // good. This guard then declines to touch it on every subsequent
                                    // sweep, so the type never reaches a terminal state: it is neither
                                    // `Ok` (usable) nor `Error` (classifiable), it holds portal
                                    // readiness, and it parks every instance hub for the full activation
                                    // budget. One row on `public.mesh_nodes` sat at `Compiling` for TEN
                                    // WEEKS this way; it was harmless only because it was an orphan the
                                    // prewarmer never enumerates.
                                    //
                                    // A claim older than the per-type budget cannot still be in flight —
                                    // whoever made it would have written a terminal state or been given
                                    // up on long ago — so it is re-driven rather than deferred to.
                                    // Note what this is NOT: no timer, no poller, no background sweep for
                                    // stale rows. The recovery rides the enumeration that already runs,
                                    // and only ever reinterprets a claim that has provably expired.
                                    if (IsLiveCompileClaim(def, budget))
                                        return node;
                                    // THE Pending door (#3390): the rebuild is dispatched against
                                    // the live inputs, so it says so instead of leaving the field
                                    // untouched — an untouched field meant a release request
                                    // arriving during the rebuild parked and compiled a second
                                    // time on the first one's terminal write-back.
                                    return node with
                                    {
                                        Content = NodeTypeCompilationHelpers.DispatchPending(
                                            def, workspace.Hub)
                                    };
                                })
                                .IgnoreElements()
                                .Select(_ => default(PreWarmOutcome)!)
                                .Catch<PreWarmOutcome, Exception>(ex =>
                                {
                                    logger?.LogWarning(ex,
                                        "DynamicTypePreWarmer: could not flip {TypePath} to Pending — the settle "
                                        + "wait below will time out and report it", typePath);
                                    return Observable.Empty<PreWarmOutcome>();
                                });

                            return Observable.Merge(settled, trigger).Take(1);
                        });
                })
            .Catch<PreWarmOutcome, Exception>(ex => Observable.Return(
                ex is TimeoutException
                    ? new PreWarmOutcome(typePath, PreWarmStatus.TimedOut, "rebuild after store miss did not settle")
                    : new PreWarmOutcome(typePath, PreWarmStatus.Faulted, ex.Message)));

    /// <summary>
    /// A compile success that is demonstrably NEWER than the one observed before the rebuild was
    /// triggered. With no baseline, any recorded success counts.
    /// </summary>
    private static bool IsFreshSuccess(DateTimeOffset? succeeded, DateTimeOffset? baseline) =>
        succeeded is { } s && (baseline is not { } b || s > b);

    /// <summary>
    /// Classify a compile that settled at Error. The discriminator is one sentence:
    /// <b>"no sources matched" is not the same as "sources matched and did not compile."</b> The
    /// first is a CONTENT fact and must not gate readiness; the second is a verdict about this
    /// image and must.
    ///
    /// <para>The evidence is the live snapshot
    /// (<see cref="NodeTypeDefinition.CurrentSourceVersions"/>, maintained by the per-NodeType
    /// sources watcher over the type's resolved source set). EXPLICITLY empty ⇒
    /// <see cref="PreWarmStatus.NoSources"/>; see that member for why it must not gate. Anything
    /// else is <see cref="PreWarmStatus.CompileError"/> — in particular a NULL snapshot (watcher
    /// never seeded) stays gating, so a real regression cannot hide behind "not seeded".</para>
    ///
    /// <para>🚨 It deliberately does NOT also require <c>d.Sources is { Count: &gt; 0 }</c>, and
    /// that removal is the fix for issue #1391. An empty <see cref="NodeTypeDefinition.Sources"/>
    /// does not mean "configuration-only" — it means <b>"uses the DEFAULT queries"</b>
    /// (<c>namespace:{path}/Source scope:subtree</c>, see
    /// <see cref="MeshWeaver.Compiler.CodeQueryResolver.DefaultSources"/>), which is how very
    /// nearly every NodeType in a real mesh is authored. Requiring declared queries therefore made
    /// <see cref="PreWarmStatus.NoSources"/> unreachable for almost the entire population: a type
    /// whose <c>Source/</c> subtree had been DELETED was compiled against nothing, its
    /// configuration lambda's resulting <c>CS0246</c>/<c>CS1061</c> were recorded as an image
    /// verdict, and a node that no longer exists held portal readiness hostage on every pod boot.
    /// That is exactly what <c>Edu/Course</c> was doing to <c>memex</c>.</para>
    ///
    /// <para>This restores the three-outcome contract <see cref="MeshWeaver.Compiler.SourceSnapshot"/>
    /// already documents — an established-but-EMPTY snapshot is a content fact "(the sources were
    /// deleted, or the type is configuration-only)" and classifies as <c>NoSources</c> — from which
    /// the extra conjunct had silently drifted. A genuinely configuration-only type is folded in on
    /// purpose: with no Code nodes at all there is nothing for an image to regress, so a failure in
    /// its configuration lambda is content drift for its owner to fix, not a reason to stall
    /// everyone else's rollout.</para>
    ///
    /// <para>🚨 An empty snapshot is NOT sufficient on its own, and the second witness —
    /// <see cref="NodeTypeDefinition.LastCompileSucceededAt"/> — is what keeps the gate intact.
    /// Two very different types both present an empty snapshot:</para>
    /// <list type="bullet">
    ///   <item><b>Sources were DELETED</b> (<c>Edu/Course</c>): the type built successfully at some
    ///   point, so its sources demonstrably existed then, and their absence now is a content
    ///   change. Content verdict — must not gate.</item>
    ///   <item><b>Never had sources, and its own <c>Configuration</c> is broken</b>: nothing was
    ///   deleted; the type is defective as authored. That is a real defect and MUST gate — it is
    ///   what <c>DynamicTypePreWarmerTest</c>'s broken fixtures
    ///   (<c>Configuration = "config =&gt; this is not valid C# at all (("</c>, no sources) pin, and
    ///   reclassifying it also downgraded its dependents' cascade from the gating
    ///   <see cref="PreWarmStatus.UpstreamFailed"/> to the non-gating
    ///   <see cref="PreWarmStatus.UpstreamContentBroken"/>.</item>
    /// </list>
    /// <para>"It once produced a working build" is the durable evidence that separates them, and it
    /// survives a failure — <c>ApplyCompileFailure</c> clears <c>CompiledSources</c> but never
    /// <c>LastCompileSucceededAt</c>. A type that has NEVER built cannot have lost anything.</para>
    ///
    /// <para>🚨 THREE shapes of <see cref="NodeTypeDefinition.CurrentSourceVersions"/> exist in
    /// production, not two — populated, explicitly <c>{}</c>, and ABSENT (SQL NULL; observed on
    /// <c>public.mesh_nodes</c> rows). Only the middle one may reclassify. The pattern
    /// <c>is { Count: 0 }</c> gets this right BY CONSTRUCTION — a C# property pattern never matches
    /// null — so absent falls through to <see cref="PreWarmStatus.CompileError"/> and gates. Do not
    /// "simplify" it to <c>d.CurrentSourceVersions?.Count == 0</c> or a bare <c>.Count == 0</c>:
    /// the first is equivalent but easy to misread, the second throws. Pinned by
    /// <c>ClassifyCompileFailure_DefaultQueriesWithNullSnapshot_StaysCompileError</c>.</para>
    ///
    /// <para><b>Why not <see cref="NodeTypeDefinition.CompiledSources"/></b>, which would be the
    /// more precise evidence ("the last successful build CONSUMED sources"): it is written only on
    /// SUCCESS, and <c>ApplyCompileFailure</c> nulls it. Every currently-failing type in production
    /// therefore lacks it — including the three that demonstrably compiled in June — so it carries
    /// no history at all. A new field stamped on success would be no better: it could only populate
    /// after a future successful compile, which is precisely what a source-less type can no longer
    /// do.</para>
    ///
    /// <para>The one case this deliberately concedes: a genuinely configuration-only type that once
    /// built and is later broken by an IMAGE change reads as content-broken and does not gate.
    /// Accepted knowingly — it is far rarer than the population the old rule broke (every
    /// default-query type with deleted sources, gating forever), and no such type appears in the
    /// observed failures on either portal.</para>
    ///
    /// <para>🚨 What this must NEVER become is "compile errors stop gating". The gate is right; only
    /// the classification was wrong. A type whose sources are still there and do not compile keeps
    /// a non-empty snapshot and keeps gating — pinned by
    /// <c>ClassifyCompileFailure_MatchedSources_StaysCompileError</c>.</para>
    /// </summary>
    /// <param name="d">The failed type's definition.</param>
    /// <param name="nodeTypePath">
    /// The type's path, so the <see cref="PreWarmStatus.DeclaredSourcesMissing"/> question can be
    /// asked (#3903) — it needs the <c>$self</c> expansion root. 🚨 A <c>null</c> here leaves that
    /// branch unreachable and the classification falls through to
    /// <see cref="PreWarmStatus.CompileError"/>, i.e. it keeps GATING. Not being able to ask must
    /// never buy a type the leniency the answer would have bought it.
    /// </param>
    public static PreWarmStatus ClassifyCompileFailure(
        NodeTypeDefinition d, string? nodeTypePath = null) =>
        // A type its repository has retired (held for its remaining instances) is a content
        // verdict before anything else is asked: its sources were withdrawn on purpose.
        d.PendingRetirement is { Length: > 0 }
            ? PreWarmStatus.Retired
        : d.CurrentSourceVersions is { Count: 0 } && d.LastCompileSucceededAt is not null
            ? PreWarmStatus.NoSources
        // 🚨 #3903 — the same content fact one granularity finer, and the branch that catches
        // every COMPOSED type the one above cannot. The snapshot is non-empty (a `shared=` group
        // still resolves) but a DECLARED source query matched nothing, so Roslyn was handed a set
        // short of what the type declares and its CS0246/CS1061 are about symbols nobody lost.
        // Guarded by the identical second witness: the sources must have been LOST, not never
        // present, or a type broken in its own Configuration would stop gating.
        : d.LastCompileSucceededAt is not null
          && MeshWeaver.Compiler.SourceCoverage.UnmatchedSourceQueries(
                 d.Sources, nodeTypePath, d.CurrentSourceVersions?.Keys.ToList())
             is { Count: > 0 }
            ? PreWarmStatus.DeclaredSourcesMissing
            : PreWarmStatus.CompileError;

    /// <summary>
    /// The outcome once the type node's EXISTENCE is known. An image verdict
    /// (<see cref="PreWarmStatus.CompileError"/> / <see cref="PreWarmStatus.Faulted"/>) measured
    /// against a node that no longer exists is reclassified <see cref="PreWarmStatus.Removed"/> —
    /// the repository retired the type while (or before) this sweep ran, and no image caused
    /// that. Everything else is returned unchanged: an existing node keeps its verdict, and a
    /// non-verdict (a timeout, a usable build) is not touched. Pure — pinned by
    /// <c>ARetiredNodeTypeIsNotARegressionTest</c>.
    /// </summary>
    /// <param name="outcome">The outcome as measured.</param>
    /// <param name="nodeExists">Whether a listing still names the type's definition node.</param>
    public static PreWarmOutcome ReclassifyAbsent(PreWarmOutcome outcome, bool nodeExists) =>
        nodeExists || !IsImageVerdict(outcome)
            ? outcome
            : outcome with
            {
                Status = PreWarmStatus.Removed,
                Detail = "the NodeType definition no longer exists in the mesh — retired by its "
                    + $"repository, not broken by this image (was {outcome.Status}: "
                    + $"{outcome.Detail ?? "(no detail)"})",
            };

    private static bool IsImageVerdict(PreWarmOutcome outcome) =>
        outcome.Status is PreWarmStatus.CompileError or PreWarmStatus.Faulted;

    /// <summary>
    /// The outcome for a type whose PARTITION the storage providers confirm is gone, or <c>null</c>
    /// when it should be warmed as usual. Pure over its two inputs, so the decision is pinned
    /// without a mesh — <see cref="ReclassifyAbsent"/>'s neighbour, and for the same reason.
    ///
    /// <para>🚨 <b>This is asked BEFORE the work, and that ordering is the fix</b> (issue #5073).
    /// A NodeType row whose partition's store was dropped can only be warmed to one of two
    /// useless ends, and the platform measured both: the activation-driven path routes a
    /// <c>SubscribeRequest</c> to a partition hub with no store behind it, is never answered, and
    /// spends a FULL per-type budget — <see cref="BakePhase.Running"/> holds readiness for the whole
    /// sweep, so a readiness-gated pod stays out of rotation for that budget times the number of
    /// dead rows; the batch-driven path faults its state write (<c>OwnerUnreachable</c>) into a
    /// gating image verdict that <see cref="ReclassifyAbsent"/> cannot rescue, because
    /// <see cref="TypeNodeExists"/> asks the SAME index the row came from and is answered "still
    /// there". Asked afterwards, the fact is worthless: the budget is already spent, or the verdict
    /// is already formed and self-confirming.</para>
    ///
    /// <para><see cref="PreWarmStatus.Removed"/>, and deliberately not a new status: which
    /// partitions exist is a property of the mesh and not of the framework being rolled out, which
    /// is exactly what <see cref="PreWarmStatus.Removed"/> already means — so this inherits its
    /// non-gating treatment in <see cref="NodeTypeBakeGateState"/>, its
    /// <see cref="PreWarmStatus.UpstreamContentBroken"/> cascade to dependents, and its place in
    /// the report, rather than needing each of those taught a new member.</para>
    ///
    /// <para>The row is REPORTED, never deleted: a boot sweep that pruned mesh data on the strength
    /// of a probe would be a far worse failure than the one this closes.</para>
    /// </summary>
    /// <param name="typePath">The NodeType path about to be warmed.</param>
    /// <param name="absentPartitions">Partitions CONFIRMED absent — from <see cref="PartitionExistenceProbe"/>, which never guesses.</param>
    internal static PreWarmOutcome? SkipForAbsentPartition(
        string typePath, ImmutableHashSet<string> absentPartitions)
    {
        ArgumentNullException.ThrowIfNull(absentPartitions);
        var partition = PartitionExistenceProbe.PartitionOf(typePath);
        if (string.IsNullOrEmpty(partition) || !absentPartitions.Contains(partition))
            return null;
        return new PreWarmOutcome(
            typePath, PreWarmStatus.Removed,
            $"the partition '{partition}' is confirmed absent by the writable storage providers — "
            + "this NodeType row outlived the store it named, so nothing about this image caused it "
            + "and no rollout can fix it");
    }

    /// <summary>
    /// <see cref="ReclassifyAbsent"/> with the existence question actually asked — only for an
    /// image verdict, so the overwhelming majority of outcomes cost nothing.
    /// </summary>
    private static IObservable<PreWarmOutcome> ReclassifyIfRemoved(
        IMessageHub mesh, PreWarmOutcome outcome, ILogger? logger)
    {
        if (!IsImageVerdict(outcome))
            return Observable.Return(outcome);
        return TypeNodeExists(mesh, outcome.TypePath, logger)
            .Select(exists => ReclassifyAbsent(outcome, exists))
            .Do(o =>
            {
                if (o.Status is PreWarmStatus.Removed)
                    logger?.LogWarning(
                        "DynamicTypePreWarmer: {TypePath} → {Status} — the definition node is gone "
                        + "(pruned by its repository during or before this sweep); the failure "
                        + "measured against it is not evidence against this image. {Detail}",
                        o.TypePath, o.Status, o.Detail);
            });
    }

    /// <summary>
    /// Whether the type's definition node still exists — by LISTING (<c>path:</c>, the same
    /// existence idiom the importer uses for its marker), never by a point read: a point read of
    /// an absent node terminates with a routing NotFound and opens the storm-breaker on that path.
    /// System-scoped — a NodeType record may live in a partition this process's viewer cannot
    /// read, and "cannot see" must not read as "gone".
    ///
    /// <para>🚨 A listing that FAULTS answers <c>true</c>: absence is asserted only by a listing that
    /// came back and did not name the node. The direction matters — a false "gone" would launder
    /// a real regression, a false "present" merely keeps the verdict that already stood.</para>
    /// </summary>
    internal static IObservable<bool> TypeNodeExists(IMessageHub mesh, string typePath, ILogger? logger)
    {
        var meshService = mesh.ServiceProvider.GetService<IMeshService>();
        if (meshService is null)
            return Observable.Return(true);
        return meshService
            .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{typePath}").AsSystem())
            .Take(1)
            .Select(change => change.Items.Any(n =>
                string.Equals(n.Path, typePath, StringComparison.OrdinalIgnoreCase)))
            .Catch<bool, Exception>(ex =>
            {
                logger?.LogWarning(ex,
                    "DynamicTypePreWarmer: could not establish whether NodeType {TypePath} still "
                    + "exists — treated as present, so its verdict stands unchanged.",
                    typePath);
                return Observable.Return(true);
            });
    }

    /// <summary>
    /// Whether the type's SOURCES MOVED while this compile was running — at least one entry of
    /// <see cref="NodeTypeDefinition.CurrentSourceVersions"/> (each value is that Code node's
    /// <c>LastModified.UtcTicks</c>, written by the per-NodeType sources watcher) is at or after
    /// <see cref="NodeTypeDefinition.LastCompileStartedAt"/> (stamped on the Pending → Compiling
    /// transition). When that holds, Roslyn's verdict was produced against a source set the mesh
    /// was concurrently replacing — a half-applied plugin auto-update, a git sync, a bulk edit.
    ///
    /// <para>Both fields survive a failed compile: <c>ApplyCompileFailure</c> nulls
    /// <c>CompiledSources</c> but leaves the start stamp and the live snapshot alone, so the
    /// evidence is readable exactly where the verdict is formed.</para>
    ///
    /// <para>🚨 SUSPICION, NOT ABSOLUTION. This never downgrades an outcome: a torn compile of
    /// code that is genuinely broken must still gate, and the check is one-sided anyway — a source
    /// written a second AFTER the compile failed is just as much a torn snapshot but is invisible
    /// here until the write lands (in the 2026-08-11 incident the callers landed at 11:06:57, after
    /// the failures). That asymmetry is precisely why the CURE is
    /// <c>NodeTypeBakeGateState.RetractRegression</c> — level-triggered on the type rebuilding —
    /// and this predicate is only what names the suspicion in the log.</para>
    /// </summary>
    public static bool SourcesMovedDuringCompile(NodeTypeDefinition d) =>
        d.LastCompileStartedAt is { } started
        && d.CurrentSourceVersions is { Count: > 0 } current
        && current.Values.Any(ticks => ticks >= started.UtcTicks);

    /// <summary>
    /// The outcome for a compile that settled at <see cref="CompilationStatus.Error"/>: Roslyn's
    /// verdict classified by <see cref="ClassifyCompileFailure"/>, carrying the torn-snapshot
    /// evidence (<see cref="SourcesMovedDuringCompile(NodeTypeDefinition)"/>) so the reporting
    /// layer can say whether the source set was stable when the verdict was formed.
    /// </summary>
    internal static PreWarmOutcome FromFailedCompile(string typePath, NodeTypeDefinition d) =>
        new(typePath, ClassifyCompileFailure(d, typePath), d.CompilationError)
        {
            SourcesMovedDuringCompile = SourcesMovedDuringCompile(d)
        };

    /// <summary>
    /// Watch a REGRESSED NodeType until it reaches a usable build on this image, then retract its
    /// regression from <paramref name="gate"/> — the cure for issue #1214, where a bake compiled a
    /// half-applied plugin update, recorded four false regressions, and stalled the rollout even
    /// though the content converged (and the types recompiled green) seconds later.
    ///
    /// <para><b>Observation, not a watchdog.</b> Nothing here triggers, retries or polls: the
    /// platform's own park registry already un-parks and recompiles a failed type the moment its
    /// source snapshot changes (<c>NodeTypeCompileParkRegistry.ShouldRetryForSourceChange</c>), and
    /// a lazy activation compiles it too. This subscription only READS the type's own node stream
    /// — the same level-triggered surface the sweep and the GUI use — and stops at the first
    /// emission that shows a usable build. There is deliberately no timeout: a regression that is
    /// never repaired must gate forever, which is what "no emission" already means.</para>
    ///
    /// <para>Holding the subscription also keeps the condemned type's per-node hub ACTIVATED, which
    /// is what keeps its sources watcher — the thing that notices the repair — installed and
    /// running. So the watch is not merely a listener for a recovery; on an idle pod it is part of
    /// why the recovery can happen at all.</para>
    ///
    /// <para>🚨 <b>The recovery must be a compile that is demonstrably FRESH</b> — a settled
    /// <see cref="CompilationStatus.Ok"/> whose
    /// <see cref="NodeTypeDefinition.LastCompileSucceededAt"/> is strictly newer than the one this
    /// watch observed when it started. <c>NodeTypeCompilationHelpers.HasUsableBuild</c>
    /// alone is NOT sufficient and matching on it would silently disable the whole gate: a failed
    /// compile keeps the PREVIOUS build's assembly coordinates and framework stamp
    /// (<c>ApplyCompileFailure</c> clears only the status, the error and
    /// <c>CompiledSources</c>), so any type that had ever compiled successfully on this image would
    /// satisfy that check the instant its regression was recorded, and every regression would
    /// retract itself immediately. The <see cref="RebuildMissingBytes"/> path guards the identical
    /// trap the identical way; this is the same <see cref="IsFreshSuccess"/> rule.</para>
    ///
    /// <para>The subscription is returned so its owner disposes it at shutdown; a discarded
    /// subscription would root the hub.</para>
    /// </summary>
    public static IDisposable WatchForRecovery(
        IMessageHub mesh, NodeTypeBakeGateState gate, string typePath, ILogger? logger)
    {
        const string RemovedWitness = "the NodeType definition no longer exists";
        var workspace = mesh.GetWorkspace();
        var accessService = mesh.ServiceProvider.GetService<AccessService>();
        // 🚨 #3478 — THE SECOND WITNESS, and the reason gating publication does not break #1214.
        // The question this watch asks is process-LOCAL — "has this condemned type since built on
        // THIS image?" — and the record below was only ever a PROXY for it. On a pod whose bake
        // refused it, the record can no longer move (its stamps are withheld, correctly, because
        // they name an identity the serving replicas cannot load), so a record-only watch would
        // wait forever and #1214's self-healing stall would become a permanent one. This observes
        // the compile itself. The record watch stays alongside it: a type baked by a PEER on the
        // same image is a real recovery this signal cannot see, and losing that would narrow the
        // retraction rather than widen it.
        var localBuilds = mesh.ServiceProvider.GetService<LocalNodeTypeBuilds>();
        // System-scoped for the same reason the sweep's reads are: watching a NodeType record
        // across partitions is infrastructure, not a user-attributable read. RunAsSystem, never
        // `Observable.Using(AccessContextScope.AsSystem, …)` — see WarmDynamicTypes (#1444/#1790).
        // This one is the worst shape of the family: the watch is LONG-LIVED, so `Using` would hold
        // the scope open until recovery lands (or shutdown) while the subscriber — a hosted service
        // — ran on latched as System the whole time.
        return accessService
            .RunAsSystem(
                () =>
                {
                    // One shared handle (IMeshNodeStreamCache): the baseline read and the wait are
                    // the SAME stream, and it replays its latest node to the second subscriber — so
                    // a success landing between the two cannot fall through the gap.
                    var stream = workspace.GetMeshNodeStream(typePath);
                    return stream
                        .Take(1)
                        .Select(current =>
                            (current?.Content as NodeTypeDefinition)?.LastCompileSucceededAt)
                        .SelectMany(baseline => stream
                            .Where(n => n?.Content is NodeTypeDefinition d
                                && d.CompilationStatus == CompilationStatus.Ok
                                && NodeTypeCompilationHelpers.HasUsableBuild(
                                    n, d, NodeTypeCompilationHelpers.GuardsOf(mesh))
                                && IsFreshSuccess(d.LastCompileSucceededAt, baseline))
                            .Take(1)
                            .Select(_ => "the record shows a fresh usable build")
                            // Whichever witness answers first. The local one needs no freshness
                            // heuristic — the compile happened here, after this subscription, so
                            // it cannot be a replayed older success the way a record read can.
                            .Merge(localBuilds is null
                                ? Observable.Never<string>()
                                : localBuilds.Built
                                    .Where(p => string.Equals(
                                        p, typePath, StringComparison.OrdinalIgnoreCase))
                                    .Select(_ => "this process compiled it successfully"))
                            .Take(1))
                        // 🚨 THE WATCH'S SUBJECT CAN CEASE TO EXIST. A regression recorded on a type
                        // its repository then prunes (a completed retirement) has lost its subject:
                        // the node stream terminates with the routing's NotFound, and "a watch that
                        // cannot observe a recovery must never be read as one" became "a rollout
                        // that can never proceed" (memex.systemorph.com, 2026-09-08 20:31:12Z, on a
                        // node pruned two minutes earlier). So a faulted watch asks ONE more
                        // question — does the node still exist, by listing — and only an absent
                        // node yields the removal witness; an existing node re-throws, and the
                        // regression stands exactly as before.
                        .Catch<string, Exception>(ex => TypeNodeExists(mesh, typePath, logger)
                            .SelectMany(exists => exists
                                ? Observable.Throw<string>(ex)
                                : Observable.Return(RemovedWitness)));
                })
            .Subscribe(
                witness =>
                {
                    // 🚨 #4645 — THE CENSUS LEARNS THE LATER VERDICT TOO. Recording only what
                    // the sweep emitted made "the last verdict wins" false the moment a recovery
                    // watch fired: /health would keep naming a type as having no usable assembly
                    // long after this process had watched it build. A stale census is worse than
                    // none — it sends an operator after a type that is fine.
                    var census = mesh.ServiceProvider.GetService<NodeTypeBakeReportRegistry>();
                    if (string.Equals(witness, RemovedWitness, StringComparison.Ordinal))
                    {
                        census?.RecordOutcome(new PreWarmOutcome(typePath, PreWarmStatus.Removed));
                        if (gate.RetireRegression(
                                typePath,
                                "the NodeType definition no longer exists — pruned by its repository "
                                + "(a completed retirement), so there is nothing left for this image "
                                + "to have broken"))
                        {
                            mesh.ServiceProvider.GetService<MeshPublicationGate>()?.Reconsider();
                            logger?.LogWarning(
                                "DynamicTypePreWarmer: WITHDRAWING the regression recorded for "
                                + "{TypePath} — its definition node no longer exists (retired by its "
                                + "repository), so the earlier failure was measured against nothing "
                                + "and is not evidence against the image. Gate now: {Detail}",
                                typePath, gate.Detail);
                        }
                        return;
                    }
                    // Recorded on the WITNESS, not on the gate's answer: the gate returns true
                    // only where a regression was actually held, while the census's question is
                    // simply "can this replica serve the type now" — and the witness says it can.
                    census?.RecordOutcome(new PreWarmOutcome(typePath, PreWarmStatus.Compiled));
                    if (gate.RetractRegression(
                            typePath,
                            $"rebuilt to a usable build on this image after the bake ({witness})"))
                    {
                        // 🚨 #3478 — the retraction moves READINESS and MEMBERSHIP together. A
                        // regression withdrawn because the type has since built on this image
                        // leaves no evidence against the image, so the process may publish again;
                        // re-reading the verdict here is what releases anything still held. The
                        // gate is level-triggered, so this direction costs nothing to support and
                        // is the same mechanism that answers "what if it becomes unhealthy AFTER
                        // joining?" the other way round.
                        mesh.ServiceProvider.GetService<MeshPublicationGate>()?.Reconsider();
                        logger?.LogWarning(
                            "DynamicTypePreWarmer: RETRACTING the regression recorded for "
                            + "{TypePath} — it has since reached a usable build on THIS image, so "
                            + "the earlier failure was not evidence against the image (typically a "
                            + "compile that sampled a half-applied content update — issue #1214). "
                            + "Gate now: {Detail}",
                            typePath, gate.Detail);
                    }
                },
                ex => logger?.LogWarning(ex,
                    "DynamicTypePreWarmer: recovery watch for the regressed type {TypePath} "
                    + "faulted — the regression STANDS (a watch that cannot observe a recovery "
                    + "must never be read as one)",
                    typePath));
    }

    /// <summary>
    /// Whether <paramref name="def"/>'s compile claim can still be in flight, i.e. whether deferring
    /// to it is honouring a live compile rather than a stranded one (#1462).
    ///
    /// <para><c>Pending</c> is always honoured: it is the state this prewarmer itself writes to ASK
    /// for a compile, and a driver picks it up promptly.</para>
    ///
    /// <para><c>Compiling</c> is honoured only while <see cref="NodeTypeDefinition.LastCompileStartedAt"/>
    /// is within <paramref name="budget"/> — the same bound the sweep gives a type to settle. Past it,
    /// no driver is still working on it: either it finished (and would have written a terminal state)
    /// or it died. A row with NO start timestamp at all is treated as stranded too, since a live
    /// compile always stamps one (<c>NodeTypeCompilationHelpers</c>); an unstamped <c>Compiling</c> is
    /// exactly the shape a row left over from an older write carries, and honouring it forever is how
    /// this became permanent.</para>
    /// </summary>
    internal static bool IsLiveCompileClaim(NodeTypeDefinition def, TimeSpan budget) =>
        def.CompilationStatus switch
        {
            CompilationStatus.Pending => true,
            CompilationStatus.Compiling =>
                def.LastCompileStartedAt is { } startedAt
                && DateTimeOffset.UtcNow - startedAt <= budget,
            _ => false,
        };

    /// <summary>
    /// Activate one dynamic NodeType's hub by subscribing to its own MeshNode stream —
    /// which routes a SubscribeRequest to the owning hub, activating it and firing the
    /// compile watcher's framework-stale / first-build kickoff. Holds the subscription
    /// (keeping the grain alive) until the compile reaches a usable build or Error, bounded
    /// by <paramref name="budget"/>. Best-effort: every non-success folds into an outcome,
    /// never an exception.
    /// </summary>
    internal static IObservable<PreWarmOutcome> WarmOne(
        IWorkspace workspace,
        AccessService? accessService,
        string typePath,
        TimeSpan budget,
        ILogger? logger)
    {
        // 🚨 What this type's activation-driven bake COST, on the line that already exists —
        // the activation-path twin of the batch driver's measurement (issue #1439). Note what it
        // measures HERE: not Roslyn, but the whole round trip — activating the type's own hub,
        // its compile watcher settling, the state write coming back. That is the number worth
        // having, because it is the one the bootstrap's liveness deadline is actually spent on.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        // RunAsSystem, never `Observable.Using(AccessContextScope.AsSystem, …)` — see
        // WarmDynamicTypes (#1444/#1790).
        var options = workspace.Hub.JsonSerializerOptions;
        // 🚨 #5544 — THE PROCESS-LOCAL WITNESS, the same one WatchForRecovery gained for #3478.
        // On a pod whose bake GATES readiness the compile's own stamp is HELD by
        // MeshPublicationGate until the bake passes — and the bake cannot pass until this very
        // wait answers. A record-only wait is therefore a deadlock by construction on exactly the
        // pods that gate: every type waited out its full per-type budget (300 s × 107 types on
        // memex's 9260 pod, 2026-09-24 — longer than the startup probe's three hours, so the pod
        // was killed and restarted into the same sweep five times, never Ready). The local signal
        // is published BEFORE the (possibly held) stamp, so it answers the moment the compile
        // settles here. Hot and non-replaying: subscribed together with the stream below, before
        // the activation that triggers the compile.
        var localBuilds = workspace.Hub.ServiceProvider.GetService<LocalNodeTypeBuilds>();
        var builtHere = localBuilds is null
            ? Observable.Never<PreWarmOutcome>()
            : localBuilds.Built
                .Where(p => string.Equals(p, typePath, StringComparison.OrdinalIgnoreCase))
                .Select(_ => new PreWarmOutcome(typePath, PreWarmStatus.Compiled,
                    "compiled on this process; its shared stamp waits for this bake's verdict"));
        // The local witness is subscribed FIRST (Merge subscribes in argument order), so a compile
        // that settles during the activation's own subscribe cannot fire before anything listens.
        return builtHere.Merge(accessService.RunAsSystem(
                () => workspace.GetMeshNodeStream(typePath)
                    // Unavailable is terminal too — a driver already gave up determining
                    // the state, so waiting out the rest of the budget for a write that
                    // is not coming only slows the sweep down.
                    // 🚨 ContentAs, never `Content is NodeTypeDefinition`: an emission whose
                    // content arrived as an untyped JsonElement would otherwise never match, and
                    // this wait would silently run out its whole budget on a settled type.
                    .Select(n => (Node: n, Def: n?.ContentAs<NodeTypeDefinition>(options)))
                    .Where(x => x.Def is { } d
                        && (NodeTypeCompilationHelpers.HasUsableBuild(
                                x.Node!, d, NodeTypeCompilationHelpers.GuardsOf(workspace.Hub))
                            || d.CompilationStatus is CompilationStatus.Error
                                                   or CompilationStatus.Unavailable))
                    .Select(x =>
                    {
                        var d = x.Def!;
                        return d.CompilationStatus switch
                        {
                            CompilationStatus.Error => FromFailedCompile(typePath, d),
                            // TimedOut, never CompileError: the type is not broken, its
                            // state simply never came back.
                            //
                            // 🚨 This is also where a compile that REFUSED TO RUN lands. A
                            // compile whose source set could not be established never reaches
                            // Roslyn and stamps CompilationStatus.Unavailable rather than Error
                            // (SourceDiscoveryUnavailableException → ApplyCompileFailure), so the
                            // "starved cross-silo discovery ⇒ phantom CS0246 ⇒ false regression"
                            // class arrives here as a non-verdict — issue #1218. Do NOT add a
                            // classification for it below: the distinction is made where the
                            // knowledge is (the compiler saw WHICH query died), and this branch
                            // is what carries it into the non-gating bucket.
                            CompilationStatus.Unavailable => new PreWarmOutcome(
                                typePath, PreWarmStatus.TimedOut, d.CompilationError),
                            _ => new PreWarmOutcome(typePath, PreWarmStatus.Compiled)
                        };
                    })))
            .Take(1)
            .Timeout(budget)
            .Catch<PreWarmOutcome, Exception>(ex => Observable.Return(
                ex is TimeoutException
                    ? new PreWarmOutcome(typePath, PreWarmStatus.TimedOut)
                    : new PreWarmOutcome(typePath, PreWarmStatus.Faulted, ex.Message)))
            .Select(o => o with { Duration = clock.Elapsed })
            .Do(o => logger?.LogInformation(
                "DynamicTypePreWarmer: {TypePath} → {Status}{Detail} — {Cost}",
                o.TypePath, o.Status,
                string.IsNullOrEmpty(o.Detail) ? "" : $" ({o.Detail})",
                o.DescribeCost()));
    }
}
