using System.Collections.Immutable;
using System.Diagnostics;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeshWeaver.Observability.Test;

/// <summary>
/// Pins the IDENTITY that <see cref="LogIncidentIngestService.Report"/>'s writes carry — the question
/// issue #1314 raised about its <c>Observable.Using(ImpersonateAsSystem, _ =&gt;
/// ReadExisting(path).SelectMany(…))</c> shape.
///
/// <para><b>The concern.</b> An <c>AsyncLocal</c> scope opened at Subscribe covers only what runs
/// synchronously on the subscribing thread. The <c>SelectMany</c> SELECTOR — where <c>Create</c> posts
/// its <c>CreateOrUpdateNodeRequest</c> and <c>Fold</c> calls <c>UpdateIncident</c>, both of which
/// snapshot the identity EAGERLY at the call — runs wherever the read emits. In
/// MeshWeaver.Reinsurance#46 exactly that shape lost the identity for every stage after the first.</para>
///
/// <para><b>What the measurement showed instead (see the probes below, whose numbers are written to
/// test output).</b> The hop is real — the selector runs on a different thread, milliseconds later —
/// but the identity survives it anyway, because the read is NOT a bare observable: a mesh-node read
/// goes through <c>MeshNodeStreamCache.GetStreamRaw</c>, which captures <c>AccessService.Context</c>
/// SYNCHRONOUSLY at the <c>GetMeshNodeStream(path)</c> call (inside the scope, on the subscribing
/// thread) and re-stamps it around every emission via <c>CarryAccessContext</c>. The selector runs
/// inside that per-callback scope. #46's first stage was a chain of write pipelines pulled by
/// <c>Concat</c>, which has no such wrap over the enumerable's projection — hence the different
/// outcome.</para>
///
/// <para><b>Why this stays as a regression pin rather than being deleted with the issue.</b> The
/// guarantee is the read pipeline's wrap, not anything the ingest service does itself. Remove the
/// wrap, add an unwrapped stage between the read and the write, or move either write out of the
/// selector's callback, and the identity silently reverts to the emission thread's ambient. These
/// assertions fail the moment that happens.</para>
///
/// <para>🚨 <b>Mutation-tested, and the two paths are NOT equally strong.</b> Deleting the
/// <c>Observable.Using</c> from <c>Report</c> turns
/// <see cref="Recurrence_IsAttributedToSystem_NotTheEmissionThreadsAmbient"/> RED with exactly the
/// predicted wrong value (<c>LastModifiedBy=Roland</c>, ambient <c>(null)</c> at the fold) — so the
/// impersonation demonstrably does real work there and this test pins it. The same mutation leaves
/// <see cref="FirstSighting_IsAttributedToSystem_NotTheEmissionThreadsAmbient"/> GREEN: on the
/// absent-node route the emission already carries the framework's own infrastructure system identity,
/// so <c>CreatedBy</c> comes out right for a second, independent reason. That assertion is still the
/// correct end-to-end invariant, but do not read it as proof that <c>Report</c>'s scope is what
/// supplies it.</para>
///
/// <para>🚨 <b>Every assertion is on IDENTITY, never on "the write landed".</b> The xUnit host sets a
/// standing DevLogin identity, so <c>AccessService.CircuitContext</c> resolves to
/// <see cref="TestUsers.Admin"/> ("Roland") on EVERY thread: with the scope lost the writes still land,
/// attributed to Roland instead of <c>system-security</c>. That is how the same defect class reported
/// <c>ok=6/6</c> in #46 while mis-attributing all six writes. Only <see cref="MeshNode.CreatedBy"/> /
/// <see cref="MeshNode.LastModifiedBy"/> — stamped by the framework from the write's
/// <c>AccessContext</c>, i.e. what the owner authorises and audits against — tell the two apart.</para>
/// </summary>
public class LogIncidentIngestAccessContextTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 12, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(30);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder).AddLogWatch();

    /// <summary>
    /// Every log line the service emits, tagged with the thread and the ambient identity live AT THAT
    /// LINE. The "first sighting" line is written inside <c>Create</c>, immediately before the post is
    /// built, so it samples the exact ambient the post's identity is resolved from — with zero
    /// perturbation of the pipeline. An instance <see cref="ReplaySubject{T}"/>, never static.
    /// </summary>
    private readonly ReplaySubject<string> _serviceLogSamples = new();

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private LogIncidentIngestService Ingest() =>
        new(Mesh,
            Mesh.ServiceProvider.GetRequiredService<IOptions<LogWatchOptions>>(),
            new AmbientRecordingLogger(Access, _serviceLogSamples));

    private sealed class AmbientRecordingLogger(AccessService access, IObserver<string> samples)
        : ILogger<LogIncidentIngestService>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => samples.OnNext(
                $"thread={Environment.CurrentManagedThreadId} "
                + $"ambient={access.Context?.ObjectId ?? "(null)"} "
                + $"circuit={access.CircuitContext?.ObjectId ?? "(null)"} :: {formatter(state, exception)}");
    }

    private static LogIncidentReport Report(string fingerprint, long occurrences, DateTimeOffset at,
        string pod = "portal-a") => new()
    {
        Fingerprint = fingerprint,
        Category = "MeshWeaver.Data.MeshDataSource",
        Severity = LogSeverity.Error,
        ExceptionType = "System.InvalidOperationException",
        NormalizedMessage = "Update failed for node {path}",
        TopFrame = "MeshWeaver.Data.MeshDataSource.Apply(MeshNode node)",
        Namespace = "memex",
        Pods = ImmutableList.Create(pod),
        Occurrences = occurrences,
        FirstSeen = at,
        LastSeen = at,
        Samples = ImmutableList.Create(new LogSample(at, pod, "fail: boom")),
    };

    /// <summary>The incident NODE — the audit stamps live on it, not in its content.</summary>
    private Task<MeshNode> ReadIncidentNode(string fingerprint, Func<MeshNode, bool> until) =>
        Mesh.GetWorkspace()
            .GetMeshNodeStream(LogIncidentNodeType.IncidentPath(fingerprint))
            .Where(node => node is not null && until(node))
            .FirstAsync()
            .Timeout(StepTimeout)
            .ToTask(TestContext.Current.CancellationToken);

    private async Task DumpServiceLog(string tag)
    {
        // The run is over, so the sample set is complete: close the subject and take ALL of it.
        _serviceLogSamples.OnCompleted();
        var lines = await _serviceLogSamples.ToList().Timeout(StepTimeout)
            .ToTask(TestContext.Current.CancellationToken);
        foreach (var line in lines)
            Output.WriteLine($"[{tag}/service-log] {line}");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  The mechanism, measured
    // ══════════════════════════════════════════════════════════════════════════

    private sealed record HopSample(
        bool NodePresent, int SubscribeThread, int SelectorThread,
        double SubscribeMs, double SelectorMs,
        string? AmbientAtSubscribe, string? AmbientInSelector,
        bool RanInline);

    /// <summary>
    /// Records whether a later stage ran INLINE — synchronously nested inside the Subscribe call,
    /// on the subscribing thread, with the <c>Observable.Using</c> scope still on that stack.
    ///
    /// <para>🚨 This replaces the thread-identity check these probes used to make
    /// (<c>selectorThread != subscribeThread</c>), which was both WRONG and FLAKY.
    /// <b>Flaky</b>: a pool thread that has handed its continuation back to the pool can be handed
    /// the next one, so a genuinely deferred stage routinely lands on the same thread — main,
    /// 2026-08-12, shard 4 measured <c>subscribeThread=4@0.2ms selectorThread=4@6.2ms</c>: deferred
    /// by 6 ms and reported as "did not hop". <b>Wrong</b>: this class's own negative control
    /// already establishes that an <c>AsyncLocal</c> DOES reach a pool hop scheduled from inside the
    /// scope, so a thread change never told us anything about the identity in the first place. The
    /// property the probes actually need is "the stage was not run synchronously inside the open
    /// scope", and that is what this measures.</para>
    ///
    /// <para><b>Exact, not sampled.</b> <see cref="Enter"/> and <see cref="Exit"/> run on the
    /// subscribing thread either side of the Subscribe call, so a stage that reads
    /// <see cref="Observe"/> on that same thread is ordered against them by program order: nested
    /// inside ⇒ <c>true</c>, after the call returned ⇒ <c>false</c>. A stage on any OTHER thread
    /// cannot be nested inside the call by construction, so it reads <c>false</c> regardless of
    /// what it races with. There is no interleaving that yields a wrong answer.</para>
    /// </summary>
    private sealed class InlineDetector
    {
        private int _subscribingThread;
        private bool _inSubscribe;

        public void Enter()
        {
            _subscribingThread = Environment.CurrentManagedThreadId;
            Volatile.Write(ref _inSubscribe, true);
        }

        public void Exit() => Volatile.Write(ref _inSubscribe, false);

        public bool Observe() =>
            Environment.CurrentManagedThreadId == _subscribingThread && Volatile.Read(ref _inSubscribe);
    }

    /// <summary>
    /// Subscribes <paramref name="probe"/> and waits for its first emission.
    ///
    /// <para>🚨 <paramref name="suppressFlow"/> is the experiment that separates the TWO ways an
    /// identity can reach a later stage. An <c>AsyncLocal</c> rides the <c>ExecutionContext</c>, and
    /// a pool hop scheduled from inside the scope CAPTURES that context — so "the identity survived a
    /// thread hop" alone does not distinguish a framework guarantee from an accident of scheduling.
    /// It matters which, because the accident does NOT hold for an emission whose thread captured its
    /// context elsewhere and earlier — a hub action block, a long-lived workspace emission scheduler —
    /// which is precisely what a production read emits from. Subscribing under
    /// <see cref="ExecutionContext.SuppressFlow"/> models that case: nothing scheduled from here
    /// inherits the ambient, so anything the later stage still sees must have been carried
    /// explicitly.</para>
    /// </summary>
    private static async Task Run<T>(IObservable<T> probe, bool suppressFlow, InlineDetector inline)
    {
        var ct = TestContext.Current.CancellationToken;
        Task<T> pending;
        // Enter/Exit bracket the Subscribe call itself, so any stage that runs nested inside it is
        // distinguishable from one that runs after it returned. See InlineDetector.
        inline.Enter();
        try
        {
            // ToTask() subscribes eagerly, so the subscription (and everything it schedules) is
            // created inside the suppression scope; only the await happens outside it.
            if (suppressFlow)
            {
                using (ExecutionContext.SuppressFlow())
                    pending = probe.FirstAsync().Timeout(StepTimeout).ToTask(ct);
            }
            else
            {
                pending = probe.FirstAsync().Timeout(StepTimeout).ToTask(ct);
            }
        }
        finally
        {
            inline.Exit();
        }
        await pending;
    }

    /// <summary>
    /// Replays <c>Report</c>'s EXACT shape over the same <c>GetMeshNodeStream</c> read, and reports
    /// where each stage ran and what identity was ambient there.
    ///
    /// <para>🚨 Only ever run against a path the measurement under test does NOT use: the read warms
    /// the shared stream cache for its path, which turns a later cold read into a synchronous replay
    /// and would silently rig the very question being asked.</para>
    /// </summary>
    private async Task<HopSample> ProbeScopeReach(string label, string path, bool suppressFlow = false)
    {
        var access = Access;
        var clock = Stopwatch.StartNew();
        var inline = new InlineDetector();
        var subscribeThread = 0;
        var selectorThread = 0;
        var subscribeAt = TimeSpan.Zero;
        var selectorAt = TimeSpan.Zero;
        string? ambientAtSubscribe = null;
        string? ambientInSelector = null;
        var sawNode = false;
        var ranInline = false;

        // ReadExisting's read, verbatim: a missing node surfaces as an error on the stream, which the
        // catch turns into "absent".
        IObservable<MeshNode?> ReadExistingLike() =>
            Mesh.GetWorkspace().GetMeshNodeStream(path)
                .Where(node => node is not null)
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(20))
                .Select(node => (MeshNode?)node)
                .Catch((Exception ex) => ex is TimeoutException
                    ? Observable.Throw<MeshNode?>(ex)
                    : Observable.Return<MeshNode?>(null));

        var probe = Observable.Using(
            () => access.ImpersonateAsSystem(),
            _ =>
            {
                subscribeThread = Environment.CurrentManagedThreadId;
                subscribeAt = clock.Elapsed;
                ambientAtSubscribe = access.Context?.ObjectId;
                return ReadExistingLike().SelectMany(existing =>
                {
                    selectorThread = Environment.CurrentManagedThreadId;
                    selectorAt = clock.Elapsed;
                    ambientInSelector = access.Context?.ObjectId;
                    sawNode = existing is not null;
                    ranInline = inline.Observe();
                    return Observable.Return(existing);
                });
            });

        await Run(probe, suppressFlow, inline);

        var sample = new HopSample(sawNode, subscribeThread, selectorThread,
            subscribeAt.TotalMilliseconds, selectorAt.TotalMilliseconds,
            ambientAtSubscribe, ambientInSelector, ranInline);
        Output.WriteLine(
            $"[{label}] nodePresent={sample.NodePresent} "
            + $"subscribeThread={sample.SubscribeThread}@{sample.SubscribeMs:F1}ms "
            + $"selectorThread={sample.SelectorThread}@{sample.SelectorMs:F1}ms "
            + $"ambientAtSubscribe={sample.AmbientAtSubscribe ?? "(null)"} "
            + $"ambientInSelector={sample.AmbientInSelector ?? "(null)"} "
            + $"ranInline={sample.RanInline}");
        return sample;
    }

    /// <summary>
    /// 🚨 NEGATIVE CONTROL for <see cref="ProbeScopeReach"/>. Without it, "the identity survived the
    /// hop" is indistinguishable from "this probe cannot see a lost identity", and the measurement
    /// proves nothing.
    ///
    /// <para>Same shape, but the first stage is a bare <c>ObserveOn(TaskPool)</c> hop instead of a
    /// mesh-node read — no framework wrap, so nothing re-stamps AsyncLocal at the emission — and the
    /// subscribe runs under <see cref="ExecutionContext.SuppressFlow"/>, so the ambient cannot ride
    /// there on a captured context either. The selector MUST see null. Observing that here, and NOT on
    /// the real read run the same way, is what localises the difference to the read pipeline rather
    /// than to the probe.</para>
    ///
    /// <para><b>Measured, and worth recording:</b> WITHOUT the suppression this control reports the
    /// system identity — an <c>AsyncLocal</c> DOES reach a pool hop scheduled from inside the scope.
    /// So "different thread" alone never proved a lost identity, in this pipeline or in
    /// MeshWeaver.Reinsurance#46.</para>
    /// </summary>
    private async Task ProbeUnwrappedHop()
    {
        var access = Access;
        var inline = new InlineDetector();
        var subscribeThread = 0;
        var selectorThread = 0;
        string? ambientAtSubscribe = null;
        string? ambientInSelector = null;
        var ranInline = false;

        var probe = Observable.Using(
            () => access.ImpersonateAsSystem(),
            _ =>
            {
                subscribeThread = Environment.CurrentManagedThreadId;
                ambientAtSubscribe = access.Context?.ObjectId;
                return Observable.Return(0)
                    .ObserveOn(TaskPoolScheduler.Default)
                    .SelectMany(_ =>
                    {
                        selectorThread = Environment.CurrentManagedThreadId;
                        ambientInSelector = access.Context?.ObjectId;
                        ranInline = inline.Observe();
                        return Observable.Return(0);
                    });
            });

        await Run(probe, suppressFlow: true, inline);

        Output.WriteLine(
            $"[control/unwrapped-hop/no-ec-flow] subscribeThread={subscribeThread} "
            + $"selectorThread={selectorThread} "
            + $"ambientAtSubscribe={ambientAtSubscribe ?? "(null)"} "
            + $"ambientInSelector={ambientInSelector ?? "(null)"} "
            + $"ranInline={ranInline}");

        ranInline.Should().BeFalse(
            "the control must actually DEFER past the Subscribe call, or it controls for nothing — "
            + "a stage that ran inside the still-open scope would see the ambient for free and the "
            + "null below would mean nothing. (This used to compare thread ids, which is a wrong "
            + "proxy in both directions: the pool routinely reuses the subscribing thread for a "
            + "deferred continuation.)");
        ambientAtSubscribe.Should().Be(WellKnownUsers.System, "the scope is open at subscribe");
        ambientInSelector.Should().BeNull(
            because: "with the ExecutionContext not flowing, a bare AsyncLocal scope cannot reach a "
                     + "stage subscribed from the first stage's emission — issue #1314's premise, and "
                     + "the proof that this probe can SEE a lost identity. If this ever reports the "
                     + "system identity the probe has gone blind and every 'identity survived' result "
                     + "here is worthless");
    }

    /// <summary>
    /// 🚨 POSITIVE CONTROL for <see cref="InlineDetector"/>. Every "did not run inline" guard in
    /// this class is an assertion that something did NOT happen, and such an assertion is worthless
    /// unless the detector can be shown to report the thing when it DOES happen.
    ///
    /// <para>Same measurement path (<see cref="Run"/>), but with nothing between Subscribe and the
    /// selector: <c>Observable.Return</c> emits on the immediate scheduler, so the selector runs
    /// nested inside the Subscribe call by construction. This MUST report inline. If it ever stops
    /// doing so the detector has gone blind and the guards are all vacuously green.</para>
    /// </summary>
    private async Task ProbeInlineDetectorSeesInline()
    {
        var access = Access;
        var inline = new InlineDetector();
        var ranInline = false;

        var probe = Observable.Using(
            () => access.ImpersonateAsSystem(),
            _ => Observable.Return(0).SelectMany(_ =>
            {
                ranInline = inline.Observe();
                return Observable.Return(0);
            }));

        await Run(probe, suppressFlow: false, inline);

        Output.WriteLine($"[control/inline-detector] ranInline={ranInline}");
        ranInline.Should().BeTrue(
            "a stage with no scheduler between it and Subscribe runs INSIDE the Subscribe call, so "
            + "the detector must say so — otherwise every 'did not run inline' guard here is a "
            + "test that cannot fail");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  First sighting — the Create path
    // ══════════════════════════════════════════════════════════════════════════

    [Fact(Timeout = 120000)]
    public async Task FirstSighting_IsAttributedToSystem_NotTheEmissionThreadsAmbient()
    {
        const string fingerprint = "ctx0000000000c1";

        // COLD: nothing has touched this path, so ReadExisting takes the real absent-node route.
        // Defer, so the thread that actually SUBSCRIBES Report — where its Observable.Using opens the
        // scope — is recorded and comparable with the threads the service logs from.
        var reportSubscribeThread = 0;
        await Observable.Defer(() =>
            {
                reportSubscribeThread = Environment.CurrentManagedThreadId;
                return Ingest().Report(Report(fingerprint, 3, T0));
            })
            .FirstAsync().Timeout(StepTimeout).ToTask(TestContext.Current.CancellationToken);

        var node = await ReadIncidentNode(fingerprint, n => !string.IsNullOrEmpty(n.CreatedBy));
        Output.WriteLine($"[create] reportSubscribeThread={reportSubscribeThread}");
        await DumpServiceLog("create");
        Output.WriteLine($"[create] CreatedBy={node.CreatedBy ?? "(null)"} "
                         + $"LastModifiedBy={node.LastModifiedBy ?? "(null)"}");

        // Measured AFTER the run, on a path the run never touched, so it cannot have warmed the cache
        // the assertion above depends on.
        await ProbeInlineDetectorSeesInline();
        await ProbeUnwrappedHop();
        var hop = await ProbeScopeReach("create/cold-absent-node",
            LogIncidentNodeType.IncidentPath("ctx00000000probe1"));
        // The same read with the ExecutionContext NOT flowing — a production emission comes off a hub
        // action block whose context was captured long before this scope existed.
        var hopNoFlow = await ProbeScopeReach("create/cold-absent-node/no-ec-flow",
            LogIncidentNodeType.IncidentPath("ctx00000000probe1b"), suppressFlow: true);

        hop.NodePresent.Should().BeFalse("the probe path was never created — this is the absent route");
        hopNoFlow.NodePresent.Should().BeFalse("likewise for the suppressed-flow probe's own path");

        // 🚨 The non-vacuity guard for the identity assertion that follows, and it belongs on
        // hopNoFlow — the probe that MAKES that assertion. It used to be asserted on `hop`, which
        // carries no identity assertion at all, and it used to be phrased as "the selector ran on a
        // different thread". Both halves were wrong:
        //
        //   • Wrong PROBE. Guarding `hop` guarded nothing; `hopNoFlow` is where a stage that ran
        //     inside the still-open scope would make "the selector saw system-security" true for
        //     free and prove nothing about the framework carrying it.
        //   • Wrong PROPERTY. A deferred continuation is routinely handed back to the very thread
        //     that subscribed — main, 2026-08-12 shard 4 measured subscribe and selector both on
        //     thread 4, six milliseconds apart, and the "did not hop" assertion failed on a run
        //     where the pipeline had behaved perfectly. This class's own negative control already
        //     records that an AsyncLocal reaches a pool hop scheduled from inside the scope, so a
        //     thread change was never evidence about the identity either.
        //
        // RanInline is exact where the thread comparison was a proxy: it is true only for a stage
        // nested inside the Subscribe call on the subscribing thread, and ProbeInlineDetectorSeesInline
        // above proves it reports true when that really happens.
        hopNoFlow.RanInline.Should().BeFalse(
            because: "the absent-node read completes AFTER Subscribe returns, so the selector is "
                     + "genuinely outside the AsyncLocal scope's synchronous reach — the premise of "
                     + "issue #1314 holds; only the conclusion does not. Without this, the identity "
                     + "below could simply be the scope still being on the stack");
        hopNoFlow.AmbientInSelector.Should().Be(WellKnownUsers.System,
            because: "with the ExecutionContext suppressed the ambient CANNOT have ridden a captured "
                     + "context, so what the selector sees was carried explicitly: "
                     + "MeshNodeStreamCache.GetStreamRaw captures the identity synchronously at the "
                     + "GetMeshNodeStream call (inside the scope) and re-stamps it around every "
                     + "emission via CarryAccessContext. THIS is the assertion that makes the identity "
                     + "assertions below a guarantee rather than a coincidence of scheduling");

        node.CreatedBy.Should().Be(WellKnownUsers.System,
            because: "the incident is platform infrastructure written into the Admin partition, so the "
                     + "CreateOrUpdateNodeRequest must carry the SYSTEM identity. Were it resolved from "
                     + "the raw ambient of the selector's thread it would stamp "
                     + $"'{TestUsers.Admin.ObjectId}' here (CircuitContext's standing host identity) and "
                     + "whatever the emission thread happens to carry in production");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Repeat — the Fold path
    // ══════════════════════════════════════════════════════════════════════════

    [Fact(Timeout = 120000)]
    public async Task Recurrence_IsAttributedToSystem_NotTheEmissionThreadsAmbient()
    {
        const string fingerprint = "ctx0000000000f1";
        var ingest = Ingest();

        await ingest.Report(Report(fingerprint, 3, T0))
            .FirstAsync().Timeout(StepTimeout).ToTask(TestContext.Current.CancellationToken);
        await ReadIncidentNode(fingerprint, n => !string.IsNullOrEmpty(n.CreatedBy));

        var second = await ingest.Report(Report(fingerprint, 7, T0.AddMinutes(2), "portal-b"))
            .FirstAsync().Timeout(StepTimeout).ToTask(TestContext.Current.CancellationToken);
        second.Created.Should().BeFalse("the second burst folds into the existing incident");

        var node = await ReadIncidentNode(fingerprint,
            n => n.ContentAs<LogIncident>(Mesh.JsonSerializerOptions) is { Occurrences: 10 });
        await DumpServiceLog("fold");
        Output.WriteLine($"[fold] CreatedBy={node.CreatedBy ?? "(null)"} "
                         + $"LastModifiedBy={node.LastModifiedBy ?? "(null)"}");

        // A COLD read of a node that EXISTS — the fold's own route, measured on a path this test's
        // writes never touched (created directly, then read for the first time).
        const string probeFingerprint = "ctx00000000probe2";
        var probePath = LogIncidentNodeType.IncidentPath(probeFingerprint);
        using (Access.ImpersonateAsSystem())
        {
            await NodeFactory.CreateNode(new MeshNode(probeFingerprint, LogIncidentNodeType.IncidentNamespace)
            {
                NodeType = LogIncidentNodeType.NodeType,
                Name = "probe",
                State = MeshNodeState.Active,
                Content = new LogIncident { Fingerprint = probeFingerprint },
            }).Should().Within(StepTimeout).Emit();
        }

        await ProbeInlineDetectorSeesInline();
        await ProbeUnwrappedHop();
        var hop = await ProbeScopeReach("fold/cold-present-node", probePath, suppressFlow: true);

        hop.NodePresent.Should().BeTrue("the probe node was created before the read");
        // The same non-vacuity guard the create path carries, for the same reason: an identity seen
        // by a stage still running inside the Subscribe call would prove nothing about the read
        // pipeline carrying it. Exact (see InlineDetector), never a thread-id proxy.
        hop.RanInline.Should().BeFalse(
            because: "the present-node read must still DEFER past Subscribe, or the identity below "
                     + "is just the scope being on the stack rather than CarryAccessContext doing "
                     + "its job");
        hop.AmbientInSelector.Should().Be(WellKnownUsers.System,
            because: "the present-node read carries the same CarryAccessContext wrap as the absent one "
                     + "— measured with the ExecutionContext suppressed, so nothing could have ridden a "
                     + "captured context — and the fold's write is therefore built under the declared "
                     + "system identity");

        node.LastModifiedBy.Should().Be(WellKnownUsers.System,
            because: "the recurrence fold is the same platform infrastructure write, and its identity is "
                     + "snapshotted EAGERLY when UpdateIncident is CALLED — inside the SelectMany "
                     + "selector, wherever the node stream emitted");
    }
}
