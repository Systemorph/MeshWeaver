using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>A probe must answer inside its own timeout, which means it may not do the work</b>
/// (MeshWeaver#4655 — the half #4608's memo left open).
///
/// <para>Memoising the module-volume walk behind a fingerprint fixed "every probe pays for it" and
/// left the two occasions that actually decide a rollout still paying it in full: the <b>FIRST</b>
/// probe of a fresh pod — which is the <c>startupProbe</c>, and a startup timeout is the one a
/// container cannot recover from — and the first probe after anything LANDS, i.e. exactly when the
/// module lane is doing the thing this check reports. On memex.systemorph.com the walk is
/// 6.5–10.1 s against a five-second probe budget, so both of those are a timeout rather than a slow
/// answer. This test pins the shape that removes them: the reading is TAKEN on an
/// <see cref="IIoPool"/> and the probe READS it.</para>
///
/// <para><b>Why a pool that refuses to run is the instrument.</b> A stopwatch here would measure a
/// developer's SSD, where the whole walk is microseconds and the defect is invisible. A pool that
/// never runs the work makes the property exact instead: with no reading obtainable, a probe that
/// walks the volume would block forever or answer from the volume, and one that reads a reading
/// answers immediately with an explicit "not measured". <see cref="PendingModuleActivations.DiskReads"/>
/// counts the walks, so the assertion is a number rather than a duration.</para>
///
/// <para>🚨 <b>And the answer it gives while it has no reading must REFUSE.</b> An unread volume
/// says nothing about whether a required module is here, and the branch that would otherwise run —
/// "the image lists it and it does not resolve, therefore the build lost the pack" — would be
/// reading a <c>false</c> that means "nobody asked". Two of the tests below are about the verdict,
/// not the cost: unmeasured classifies as <see cref="RequiredModuleState.Unmeasured"/>, and
/// <see cref="RequiredModuleStatus.Absent"/> counts it, so a caller that has never heard of the
/// state still stalls the rollout rather than completing it over evidence nobody has.</para>
/// </summary>
public class ProbeAnswersWithoutWalkingTheVolumeTest : IDisposable
{
    private const string StoreModule = "MeshWeaver.Social";

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-probe-reading-" + Guid.NewGuid().ToString("N"));
    private readonly ModuleLandingService landing;

    public ProbeAnswersWithoutWalkingTheVolumeTest()
    {
        Directory.CreateDirectory(root);
        landing = new ModuleLandingService(baseDirectory: root);
    }

    public void Dispose()
    {
        landing.Dispose();
        try { Directory.Delete(root, recursive: true); }
        catch { /* temp cleanup is the OS's problem, never a test failure */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 🚨 The property, stated as a count: a probe taken before any reading exists performs ZERO
    /// walks of the volume and returns anyway. This is the first of the two occasions the memo left
    /// exposed — the startup probe of a fresh pod — and it is the unrecoverable one.
    /// </summary>
    [Fact]
    public async Task AProbeTakenBeforeAnyReading_WalksNothing_AndStillAnswers()
    {
        await LandWave(StoreModule);
        var pool = new ControllableIoPool { Runs = false };
        var pending = new PendingModuleActivations(root) { IoPool = pool };

        var inputs = pending.ReadProbeInputs();

        Assert.Equal(0, pending.DiskReads);
        Assert.Null(inputs.Activation);
        Assert.Same(ModuleProbeInputs.VolumeNotRead, inputs.ResolvesFromDeployment);
        Assert.Contains(inputs.Unreadable, reason =>
            reason.Contains("has not been read", StringComparison.OrdinalIgnoreCase));
        Assert.True(pool.Scheduled > 0,
            "the probe answered without a reading and without asking for one — the next probe "
            + "would answer 'not measured' too, for ever");
    }

    /// <summary>
    /// 🚨 The SECOND occasion, and the one a memo cannot help with: a landing moves the fingerprint,
    /// so the held reading is out of date — and the probe STILL does not walk. It answers from the
    /// reading it has, which is at most one probe interval old, and asks the pool for a new one.
    ///
    /// <para>Without this the fix would be "the first probe is slow instead of all of them", which
    /// is a rollout gate that goes over budget precisely when a module is being delivered.</para>
    /// </summary>
    [Fact]
    public async Task AProbeAfterALanding_StillWalksNothing_AndAnswersFromTheReadingItHas()
    {
        await LandWave(StoreModule);
        var pool = new ControllableIoPool();
        var pending = new PendingModuleActivations(root) { IoPool = pool };

        // One reading, taken the way production takes it — on the pool, never by a probe.
        await pending.Refresh().Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);
        Assert.Equal(1, pending.DiskReads);
        var known = pending.ReadProbeInputs();
        Assert.Single(EnabledNames(known));

        // A landing moves all three fingerprint directories, so the reading is now stale.
        await LandWave("MeshWeaver.Speech");
        pool.Runs = false;

        var afterLanding = pending.ReadProbeInputs();

        Assert.Equal(1, pending.DiskReads);
        Assert.Single(EnabledNames(afterLanding));
        Assert.NotNull(afterLanding.Activation);
    }

    /// <summary>
    /// 🚨 The VERDICT half. An unread volume must not be reported as a finding: the entry is
    /// <see cref="RequiredModuleState.Unmeasured"/>, its reason says so, and it is NOT
    /// <see cref="RequiredModuleState.ExpectedLater"/> — which would be a pod that has measured
    /// nothing telling a rollout to carry on.
    /// </summary>
    [Fact]
    public void AnUnreadVolume_IsUnmeasured_AndRefuses()
    {
        var inputs = ModuleProbeInputs.NotRead(root);

        var verdicts = RequiredModuleStatus.Classify(
            [StoreModule + ".dll"],
            baselineEntries: [],
            loadedAssemblyNames: ImmutableHashSet<string>.Empty,
            inputs.ResolvesFromDeployment,
            inputs.Activation,
            inputs.LandedDllExists,
            _ => null,
            []);

        var verdict = Assert.Single(verdicts);
        Assert.Equal(RequiredModuleState.Unmeasured, verdict.State);
        Assert.Contains("has not read the module volume", verdict.Reason, StringComparison.OrdinalIgnoreCase);

        // 🚨 The bucket, not just the state: a surface that only knows the older states reads
        // Absent + Incompatible + ExpectedLater, and it must land on a REFUSAL rather than on the
        // one bucket that lets a rollout complete.
        Assert.Contains(RequiredModuleStatus.Absent(verdicts), v => v.Name == StoreModule);
        Assert.Empty(RequiredModuleStatus.ExpectedLater(verdicts));
        Assert.Empty(RequiredModuleStatus.Incompatible(verdicts));
        Assert.Contains(RequiredModuleStatus.Unmeasured(verdicts), v => v.Name == StoreModule);
    }

    /// <summary>
    /// 🚨 The NEGATIVE CONTROL for the state above, without which "unmeasured" would be a blanket
    /// that refuses every rollout until a reading lands. What this process has LOADED is in-process
    /// state, it costs nothing, and it settles the common case — so a required module that is
    /// loaded here is <see cref="RequiredModuleState.Present"/> even with no reading at all.
    /// </summary>
    [Fact]
    public void AModuleLoadedInThisProcess_IsPresent_EvenWithNoReading()
    {
        var inputs = ModuleProbeInputs.NotRead(root);

        var verdicts = RequiredModuleStatus.Classify(
            [StoreModule + ".dll"],
            baselineEntries: [StoreModule + ".dll"],
            loadedAssemblyNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { StoreModule },
            inputs.ResolvesFromDeployment,
            inputs.Activation,
            inputs.LandedDllExists,
            _ => null,
            []);

        Assert.Equal(RequiredModuleState.Present, Assert.Single(verdicts).State);
        Assert.Empty(RequiredModuleStatus.Absent(verdicts));
    }

    /// <summary>
    /// 🚨 The reading is taken at HOST START, so in production the window in which a probe answers
    /// "not measured" is the boot itself rather than the first probe. This is the background half of
    /// "a health check reads a registry; it does not do the work": the work has an owner, and the
    /// owner is not a probe.
    /// </summary>
    [Fact]
    public async Task TheFirstReading_IsTakenAtHostStart_NotByAProbe()
    {
        await LandWave(StoreModule);
        var pool = new ControllableIoPool();
        var pending = new PendingModuleActivations(root) { IoPool = pool };
        using var service = new ModuleVolumeReadingHostedService(pending);

        Assert.Equal(0, pending.DiskReads);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.Taken.Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);

        // From here a probe never has to measure: the reading exists before the first one arrives.
        pool.Runs = false;
        var inputs = pending.ReadProbeInputs();

        Assert.Equal(1, pending.DiskReads);
        Assert.NotNull(inputs.Activation);
        Assert.NotSame(ModuleProbeInputs.VolumeNotRead, inputs.ResolvesFromDeployment);
        Assert.Contains(StoreModule, EnabledNames(inputs), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 🚨 <b>Every caller shares ONE walk — including the host-start reading, which overlaps the
    /// first probes BY CONSTRUCTION</b> (Copilot's review of #4700).
    ///
    /// <para>The host-start reading used to call <c>Refresh()</c> directly, past the collapse. On a
    /// booting pod the first <c>/health</c> arrives while that reading is still crawling the share,
    /// so the probe's own request found nothing in flight and started a SECOND concurrent walk of
    /// the slow volume — doubling the load exactly during the boot this change exists to rescue,
    /// and leaving the two snapshots free to land in either order.</para>
    ///
    /// <para>A second walk is observable as a COUNT, which is why the pool counts what it is handed
    /// rather than what it ran: with the pool refusing to run, the first reading never completes, so
    /// anything arriving after it is unambiguously "did this start another one?".</para>
    /// </summary>
    [Fact]
    public async Task ProbesDuringTheHostStartReading_JoinIt_RatherThanStartingASecondWalk()
    {
        await LandWave(StoreModule);
        var pool = new ControllableIoPool { Runs = false };
        var pending = new PendingModuleActivations(root) { IoPool = pool };
        using var service = new ModuleVolumeReadingHostedService(pending);

        await service.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, pool.Scheduled);

        // Two probes land while that reading is still in flight. Each asks for a reading; neither
        // may start one.
        pending.ReadProbeInputs();
        pending.ReadProbeInputs();

        Assert.Equal(1, pool.Scheduled);
        Assert.Equal(0, pending.DiskReads);
    }

    /// <summary>
    /// 🚨 <b>The pool's cancellation REACHES the walk</b> (Copilot's review of #4700). It used to be
    /// discarded — <c>ReadDisk</c> took no token and enumerated the share regardless — so a teardown
    /// only unsubscribed and left a pool worker holding the whole slow walk.
    ///
    /// <para>An already-cancelled token is the exact instrument: a walk that observes it performs no
    /// enumeration at all (<c>DiskReads</c> stays 0) and ends as a cancellation; one that discards
    /// it reads the volume and completes, which this assertion cannot mistake for the other.</para>
    /// </summary>
    [Fact]
    public async Task ThePoolsCancellation_ReachesTheWalk_RatherThanBeingDiscarded()
    {
        await LandWave(StoreModule);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var pool = new ControllableIoPool { Token = cancelled.Token };
        var pending = new PendingModuleActivations(root) { IoPool = pool };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pending.Refresh().Timeout(TestTimeouts.Convergence)
                .Await(TestContext.Current.CancellationToken));

        Assert.Equal(1, pool.Scheduled);
        Assert.Equal(0, pending.DiskReads);

        // 🚨 And the cancellation is not latched: the promise cache evicts a faulted reading, so the
        // pod that survives a cancelled sweep can still take one. Without that, one teardown-shaped
        // blip would leave every later probe answering "not measured" for the life of the process.
        pool.Token = CancellationToken.None;
        await pending.Refresh().Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);
        Assert.Equal(1, pending.DiskReads);
        Assert.NotNull(pending.ReadProbeInputs().Activation);
    }

    private static IReadOnlyList<string> EnabledNames(ModuleProbeInputs inputs) =>
        (inputs.Activation?.Entries ?? []).Select(m => m.Name).ToList();

    private async Task LandWave(params string[] names)
    {
        foreach (var name in names)
            await landing.LandModule(name, [(name + ".dll", RealAssemblyBytes)])
                .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
        await landing.ProposeModuleSet().Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);
    }

    private static byte[] RealAssemblyBytes =>
        File.ReadAllBytes(typeof(MeshWeaver.Plugin.Packaging.BundleReader).Assembly.Location);

    /// <summary>
    /// A pool that can be told to stop running what it is handed — the instrument this test needs,
    /// because "the probe did not walk the volume" is only provable when walking was impossible.
    /// It also COUNTS what it was handed, so "answered without asking for a reading" and "answered
    /// from the reading it had" stay different sentences.
    /// </summary>
    private sealed class ControllableIoPool : IIoPool
    {
        private int scheduled;

        /// <summary>Whether handed-over work actually runs. <c>false</c> hands back a leaf that
        /// never produces, which is what a hung volume looks like from the caller's side.</summary>
        public bool Runs { get; set; } = true;

        /// <summary>How many blocking leaves were handed over, run or not.</summary>
        public int Scheduled => Volatile.Read(ref scheduled);

        /// <summary>Nothing is bounded here; the cap is not what this test measures.</summary>
        public int CurrentInFlight => 0;

        /// <summary>The token handed to the work — the seam that says whether the subject
        /// RECEIVES the pool's cancellation or discards it.</summary>
        public CancellationToken Token { get; set; } = CancellationToken.None;

        public IObservable<T> InvokeBlocking<T>(Func<CancellationToken, T> work)
            => Observable.Create<T>(observer =>
            {
                Interlocked.Increment(ref scheduled);
                if (!Runs)
                    return Disposable.Empty;
                // Faithful to the real pool: a throwing leaf becomes OnError, never an exception
                // out of Subscribe — otherwise a cancelled walk would look like a broken promise
                // rather than a cancelled one.
                try
                {
                    var value = work(Token);
                    observer.OnNext(value);
                    observer.OnCompleted();
                }
                catch (Exception exception)
                {
                    observer.OnError(exception);
                }

                return Disposable.Empty;
            });

        public IObservable<T> Invoke<T>(Func<CancellationToken, Task<T>> io)
            => throw new NotSupportedException("this test's subject uses blocking leaves only");

        public IObservable<T> InvokeStream<T>(Func<CancellationToken, IAsyncEnumerable<T>> source)
            => throw new NotSupportedException("this test's subject uses blocking leaves only");

        public IObservable<T> SubscribeThroughPool<T>(IObservable<T> source)
            => throw new NotSupportedException("this test's subject uses blocking leaves only");
    }
}
