#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>The boot-time content-type sweep must not report ITSELF as a fault.</b>
///
/// <para><see cref="ContentTypeRegistrationSweep"/> builds one throwaway probe hub per static
/// NodeType definition so <c>WithContentType</c> runs as a side effect of the configuration build.
/// The probe is <c>AsTransientNodeProbe(startDataSources: false)</c> and disposed in the same
/// breath — it has no mesh node, no started data sources, and microseconds to live.</para>
///
/// <para>A NodeType that adopts the Activity Control Plane installs
/// <see cref="ActivityControlPlaneExtensions.WatchControlPlane"/> from its own
/// <c>WithInitialization</c>, and that watcher subscribes to the hub's OWN MeshNode stream. On the
/// probe there is no such stream, so every mesh start wrote an ERROR-level line per swept
/// Activity-shaped NodeType — <c>ActivityControlPlane subscription faulted on
/// content-type-registration/… — re-establishing</c> — and armed a 1 s re-establish timer against
/// a hub that was already gone (Systemorph/MeshWeaver#2990). Information and above ships to Loki;
/// Error is what operators alert on.</para>
///
/// <para>Both NodeType shapes below are real: the platform's own <c>kernel</c> type carries an ACP
/// and NO mesh data source (the ERROR), while an Activity NodeType that also declares a content
/// type gets far enough to build the stream and instead opens a <c>sync/</c> sub-hub into the
/// probe's own disposal (the WARNING). One root, two faces — a probe must not run the node control
/// plane at all, which is what <c>AsTransientNodeProbe</c> already promised and
/// <c>MeshDataSource.SubscribeToOwnDeletionInit</c> already honours.</para>
///
/// <para>🚨 <b>The probe's init turn RACES its own <c>Dispose()</c>, and both outcomes must be
/// fault-free.</b> <c>ProbeRegister</c> builds the hub (which posts <c>InitializeHubRequest</c>)
/// and disposes it on the next line. When the action block reaches the init turn first, the
/// <c>WithInitialization</c> observables run and the ACP install above is exercised against the
/// #2990 guards. When <c>Dispose()</c> lands first, <c>MessageHub.HandleInitialize</c> skips the
/// remaining BuildupActions altogether — a hub that is leaving installs nothing (#3109,
/// <c>Doc/Architecture/HubDisposalModel</c> → "The rule for initialization") — and the install
/// never runs. The tests therefore take their determinism signal from the probe's
/// <c>DisposalCompleted</c>, captured in the SYNCHRONOUS <c>WithInitialization</c> overload that
/// runs inside <c>Build</c> (before the sweep can dispose the hub), not from the install point: the
/// <c>ShutdownRequest</c> that <c>Dispose()</c> posts is queued BEHIND the init turn, so by the
/// time disposal completes the init turn has ended (run or skipped) and the teardown has written
/// whatever it writes. Content-type REGISTRATION is unaffected by the skip either way — it happens
/// in <c>DataContext.Initialize</c>, a synchronous buildup action inside <c>Build</c>, which is what
/// makes the sweep's build-and-dispose shape sufficient.</para>
/// </summary>
public class ContentTypeProbeControlPlaneTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The kernel's shape: an ACP watcher and no mesh data source.</summary>
    private const string BareActivityNodeType = "AcpProbeGadget";

    /// <summary>The other Activity shape: an ACP watcher plus a declared content type.</summary>
    private const string TypedActivityNodeType = "AcpProbeGadgetWithContent";

    public record AcpGadgetContent
    {
        public string? Label { get; init; }
    }

    /// <summary>
    /// Emits once the swept probe of <see cref="BareActivityNodeType"/> has COMPLETED its disposal.
    /// That ordering is the whole determinism of this test: the init turn is queued at
    /// <c>Build</c>, the <c>ShutdownRequest</c> behind it, so disposal completing proves the init
    /// turn has ended — either it ran (and <c>SubscribeWithReEstablish</c> established
    /// synchronously, <c>MeshNodeStreamHandle.Subscribe</c> reporting a failed
    /// <c>AcquireStream</c> through <c>OnError</c> on the calling thread) or it was skipped because
    /// teardown had begun (#3109). Either way, by the time this emits the Error line has either
    /// been written or provably will not be. An assertion on a log's ABSENCE with no such signal
    /// passes whenever it runs first, which is most of the time.
    /// </summary>
    private readonly AsyncSubject<Unit> bareProbeDisposed = new();

    /// <summary>Same signal for <see cref="TypedActivityNodeType"/>.</summary>
    private readonly AsyncSubject<Unit> typedProbeDisposed = new();

    /// <summary>
    /// Which way the init-turn-vs-dispose race went on each probe: 1 when the ACP install point
    /// was reached (the init turn won), 0 when the BuildupActions were skipped (dispose won).
    /// Diagnostic only — both outcomes are legitimate and both must be fault-free.
    /// </summary>
    private int bareInstallRan;
    private int typedInstallRan;

    private readonly RecordingLoggerProvider recorder = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services => services.AddSingleton<ILoggerProvider>(recorder))
            .AddMeshNodes(
                new MeshNode(BareActivityNodeType)
                {
                    Name = "ACP Probe Gadget",
                    HubConfiguration = config => InstallControlPlane(
                        config.AddData(), bareProbeDisposed, () => Interlocked.Exchange(ref bareInstallRan, 1))
                },
                new MeshNode(TypedActivityNodeType)
                {
                    Name = "ACP Probe Gadget With Content",
                    HubConfiguration = config => InstallControlPlane(
                        config.AddMeshDataSource(source => source.WithContentType<AcpGadgetContent>()),
                        typedProbeDisposed, () => Interlocked.Exchange(ref typedInstallRan, 1))
                });

    /// <summary>
    /// Faithful copy of <c>KernelContainer</c>'s Activity Control Plane install: the OBSERVABLE
    /// initialization overload, running on the <c>InitializeHubRequest</c> turn, registering the
    /// watcher for the hub's disposal. The SYNCHRONOUS overload beside it is test instrumentation
    /// only: it runs inside <c>Build</c>, the one point that is guaranteed to precede the sweep's
    /// <c>Dispose()</c>, and forwards the probe's <c>DisposalCompleted</c> to the test.
    /// </summary>
    private static MessageHubConfiguration InstallControlPlane(
        MessageHubConfiguration config, AsyncSubject<Unit> probeDisposed, Action installRan)
        => config
            .WithInitialization((IMessageHub hub) =>
            {
                hub.DisposalCompleted.Take(1).Subscribe(probeDisposed);
            })
            .WithInitialization(hub => Observable.Defer(() =>
            {
                hub.RegisterForDisposal(hub.WatchControlPlane(_ => { }));
                installRan();
                return Observable.Return(Unit.Default);
            }));

    /// <summary>
    /// 🚨 THE assertion of #2990: a clean mesh start writes no Error-level record about a faulted
    /// control-plane subscription. Nothing is wrong with the mesh — the line reported the boot-time
    /// content-type sweep against itself.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task BootSweep_OfActivityShapedNodeTypes_LogsNoControlPlaneFault()
    {
        await ProbesWereBuiltAndTornDown();

        var faults = recorder.Records
            .Where(r => r.Level >= LogLevel.Error
                        && r.Message.Contains("ActivityControlPlane subscription faulted",
                            StringComparison.Ordinal))
            .ToArray();

        faults.Should().BeEmpty(
            "the boot-time content-type sweep builds a throwaway probe with no mesh node and no "
            + "started data sources — reporting its own watcher as a transient fault is an "
            + "ERROR-level line about a non-event, and it arms a 1 s re-establish against a hub "
            + "that is already gone (#2990)");
    }

    /// <summary>
    /// The same root one step earlier: an Activity NodeType that also declares a content type gets
    /// far enough for the own-node reduce to construct a <see cref="ISynchronizationStream"/>,
    /// whose constructor always calls <c>GetHostedHub(sync/{id}, Always)</c> — into the probe's own
    /// disposal. That is the <c>ProbeHubCostTest</c> warning <c>startDataSources: false</c> was
    /// introduced to remove, reinstated by the one watcher that was still installed.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task BootSweep_OfActivityShapedNodeTypes_OpensNoSyncSubHubOnAProbe()
    {
        await ProbesWereBuiltAndTornDown();

        var rejected = recorder.Records
            .Where(r => r.Message.Contains("Rejecting hosted hub creation", StringComparison.Ordinal)
                        && r.Message.Contains("in Host content-type-registration/",
                            StringComparison.Ordinal))
            .ToArray();

        rejected.Should().BeEmpty(
            "a registration probe exists to have its configuration BUILT and nothing else — the "
            + "control plane is the last machinery that still opened a sync/ sub-hub into the "
            + "probe's own disposal (#2990)");
    }

    /// <summary>
    /// 🚨 The registration probe is a TRANSIENT PROBE by every measure — same marker, same
    /// create-and-dispose-in-one-breath lifetime, same impossibility of ever carrying a node — but
    /// it minted its address as a bare literal instead of from
    /// <see cref="TransientProbeAddresses"/>, so the one guard keyed off the ADDRESS rather than
    /// the configuration marker did not recognise it.
    ///
    /// <para>That guard is <c>MeshNodeStreamCache.GetStreamRaw</c>, and it is the seam in-mesh
    /// NodeType content actually reaches for. Unguarded, a read of such a path evaluates the
    /// caller's permissions on a synthetic address ("lacks Read permission on …") or routes and
    /// dies on "No node found at …" — either of which faults the virtual data source whose
    /// provider issued it (#2894). This is the same assertion
    /// <c>NodeTypeModelProbeTest.StreamCache_ReadOfAProbeOwnAddress_CompletesEmptyForAnUnprivilegedUser</c>
    /// makes for <c>$model-probe</c>, for the producer that had not joined the contract.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task StreamCache_ReadOfARegistrationProbeAddress_CompletesEmpty()
    {
        var cache = Mesh.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        var probePath =
            $"{TransientProbeAddresses.ContentTypeRegistrationProbePrefix}{Guid.NewGuid():N}";

        var emitted = await cache.GetStream(probePath, Mesh.JsonSerializerOptions)
            .ToArray().Should().Within(TestTimeouts.Quick).Emit();

        emitted.Should().BeEmpty(
            "there is no node at a registration probe's synthetic address and there never will be "
            + "— every probe producer mints its address FROM TransientProbeAddresses so that "
            + "IsProbeAddress stays exhaustive (#2990)");
    }

    /// <summary>
    /// Waits for both swept probes to finish disposing — the positive signal that each was built
    /// (so the NodeType WAS swept) and that its init turn has ended, run or skipped. A faulted
    /// probe teardown surfaces here as the subject's error.
    /// </summary>
    private async Task ProbesWereBuiltAndTornDown()
    {
        await bareProbeDisposed.Should().Within(TestTimeouts.Quick)
            .Emit("the sweep must probe the ACP NodeType that carries no data source and dispose "
                  + "that probe, or this test asserts nothing");
        await typedProbeDisposed.Should().Within(TestTimeouts.Quick)
            .Emit("the sweep must probe the ACP NodeType that declares a content type and dispose "
                  + "that probe, or this test asserts nothing");

        Output.WriteLine(
            $"    init turn reached the ACP install point: bare={Volatile.Read(ref bareInstallRan) == 1}, "
            + $"typed={Volatile.Read(ref typedInstallRan) == 1} (false = skipped, teardown had begun)");
        foreach (var r in recorder.Records)
            Output.WriteLine($"    {r.Level} {r.Category}: {r.Message}"
                             + (r.Error is null ? "" : $"  << {r.Error.GetType().Name}: {r.Error.Message}"));
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Category, string Message, Exception? Error)> records = new();

        public IReadOnlyList<(LogLevel Level, string Category, string Message, Exception? Error)> Records
            => records.ToArray();

        public ILogger CreateLogger(string categoryName) => new Recorder(categoryName, records);
        public void Dispose() { }

        private sealed class Recorder(
            string category,
            ConcurrentQueue<(LogLevel, string, string, Exception?)> sink) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => Disposable.Empty;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                sink.Enqueue((logLevel, category, formatter(state, exception), exception));
            }
        }
    }
}
