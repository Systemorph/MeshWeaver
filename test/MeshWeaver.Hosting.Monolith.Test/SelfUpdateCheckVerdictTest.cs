using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.SelfUpdate;
using MeshWeaver.Data;
using MeshWeaver.GitSync;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.SelfUpdate;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 <b>#2553 — "memex self-update is silent: 3 builds behind, roll floor satisfied 5 h ago, and
/// ZERO SelfUpdate log lines in 6.7 h", and #2494 — "self-update is EVENT-DRIVEN (no poll)".</b>
///
/// <para>Both issues are the same defect seen from two ends: a self-update check could complete
/// having reported NOTHING, and the check could stop happening at all without reporting that
/// either. An install three builds behind and an install perfectly up to date produced identical
/// evidence — healthy pods, no errors, an Updates tab reading "No newer version detected yet" —
/// so nobody could tell them apart, and the fleet sat behind for a week.</para>
///
/// <para>These tests assert the DELIVERY, never a return value. A verdict object that nothing
/// emits is worth exactly as much as the silence it replaced, so every case here asserts either a
/// log line that actually reached a logger or a field that actually landed on the policy node.</para>
/// </summary>
public class SelfUpdateCheckVerdictTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddUpdatePolicyType().AddGitHubSyncTypes();

    private const string NewerTag = "3.0.0";

    /// <summary>Every line the service emitted, with its level — the DELIVERY under test.</summary>
    private sealed class CapturingLogger : ILogger<SelfUpdateHostedService>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _lines = new();
        private readonly ReplaySubject<(LogLevel Level, string Message)> _emitted = new();

        public IObservable<(LogLevel Level, string Message)> Emitted => _emitted;
        public IReadOnlyList<(LogLevel Level, string Message)> Lines => _lines.ToArray();

        /// <summary>Only the once-per-check verdict lines — never the startup banner.</summary>
        public IReadOnlyList<(LogLevel Level, string Message)> Checks =>
            _lines.Where(l => l.Message.Contains("[SelfUpdate] check (", StringComparison.Ordinal)).ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var line = (logLevel, formatter(state, exception));
            _lines.Enqueue(line);
            _emitted.OnNext(line);
        }
    }

    /// <summary>Counts every registry listing, so "did this install ask again?" is measurable.</summary>
    private sealed class CountingTagLister(params string[] tags) : IAcrTagLister
    {
        private int _calls;
        private readonly ReplaySubject<int> _listed = new();

        public int Calls => Volatile.Read(ref _calls);
        public IObservable<int> Listed => _listed;

        public Task<IReadOnlyList<string>> ListTagsAsync(string repository, CancellationToken ct)
        {
            _listed.OnNext(Interlocked.Increment(ref _calls));
            return Task.FromResult<IReadOnlyList<string>>(tags);
        }
    }

    private sealed class PatchingUpdater : IDeploymentUpdater
    {
        public bool CanPatch => true;
        public Task<DateTimeOffset?> LastRolledAtAsync(CancellationToken ct) =>
            Task.FromResult<DateTimeOffset?>(null);
        public Task PatchToVersionAsync(string versionTag, CancellationToken ct) => Task.CompletedTask;
    }

    private static SelfUpdateOptions Options(
        UpdatePolicyKind policy = UpdatePolicyKind.Continuous, TimeSpan? safetyNet = null) => new()
        {
            RetryInterval = TimeSpan.FromMilliseconds(500),
            EventCoalesceWindow = TimeSpan.FromMilliseconds(50),
            MinRollInterval = TimeSpan.Zero,
            DefaultPolicy = policy,
            // Short enough for a test; production's default is an hour. Delete this initializer to
            // compile the file against pre-fix code — the option does not exist there, which is the
            // defect #2494 reports.
            SafetyNetCheckInterval = safetyNet ?? TimeSpan.FromHours(1),
        };

    private SelfUpdateHostedService NewService(
        CapturingLogger logger, IAcrTagLister acr, IDeploymentUpdater updater, SelfUpdateOptions options) =>
        new(Mesh, acr, updater, options, logger);

    /// <summary>
    /// Runs the startup pass and waits for the FIRST check verdict to be reported — a positive
    /// signal, never a fixed delay.
    /// </summary>
    private async Task<CapturingLogger> RunOneCheckAsync(
        IAcrTagLister acr, IDeploymentUpdater updater, SelfUpdateOptions options)
    {
        var logger = new CapturingLogger();
        var service = NewService(logger, acr, updater, options);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await logger.Emitted
                .Where(l => l.Message.Contains("[SelfUpdate] check (", StringComparison.Ordinal))
                .FirstAsync()
                .Timeout(TimeSpan.FromSeconds(30))
                .Await(TestContext.Current.CancellationToken);
            return logger;
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// 🚨 THE CASE THE WHOLE ISSUE IS ABOUT. A check that finds nothing newer is the normal, happy
    /// outcome of most checks — and it used to be a bare Rx <c>Where</c>, so it produced no log
    /// line, no node write, nothing. "We asked and the answer was no" and "nothing ever asked" were
    /// therefore the same observation from outside the process, which is exactly why memex could sit
    /// three builds behind while looking healthy.
    ///
    /// <para>Fails on pre-fix code with zero check lines.</para>
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ACheckThatFindsNothingNewer_SaysSoAnyway()
    {
        var logger = await RunOneCheckAsync(new CountingTagLister(), new PatchingUpdater(), Options());

        logger.Checks.Should().ContainSingle(
            "a check must report exactly one verdict — the absence of a line is what made a stalled "
            + "install indistinguishable from a current one");
        logger.Checks[0].Message.Should().Contain("no newer release");
        logger.Checks[0].Message.Should().Contain("Startup");
    }

    /// <summary>
    /// The other formerly-silent exit: <c>Admin/UpdatePolicy = None</c>. It is a DECISION an
    /// administrator took, and it deserves to look different from a broken updater — it used to be
    /// the single most silent path in the service, a <c>Where</c> in the trigger pipeline that
    /// discarded the check before anything could observe it.
    ///
    /// <para>Fails on pre-fix code with zero check lines.</para>
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task APolicyOfNone_IsADecisionThatReportsItself()
    {
        var acr = new CountingTagLister(NewerTag);
        var logger = await RunOneCheckAsync(
            acr, new PatchingUpdater(), Options(policy: UpdatePolicyKind.None));

        logger.Checks.Should().ContainSingle();
        logger.Checks[0].Message.Should().Contain("updates are disabled");
        acr.Calls.Should().Be(0, "a policy of None still costs no registry call");
    }

    /// <summary>
    /// The DURABLE half, and the one that survives a deployment forgetting a log level — which is
    /// what actually happened: every line this service had to say was <c>LogInformation</c>, the
    /// portal image caps its whole logger prefix at <c>Warning</c>, and no deployment had added the
    /// category. So a log-only fix would have shipped straight back into the same void.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task EveryCheck_StampsItsVerdictOnThePolicyNode()
    {
        await RunOneCheckAsync(new CountingTagLister(), new PatchingUpdater(), Options());

        var content = await Mesh.GetWorkspace()
            .GetMeshNodeStream(UpdatePolicyNodeType.NodePath)
            .Select(node => UpdatePolicyNodeType.ParseContent(node.Content, Mesh.JsonSerializerOptions))
            .Where(c => c.LastCheckedAt is not null)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(30))
            .Await(TestContext.Current.CancellationToken);

        content.LastCheckVerdict.Should().Contain("no newer release");
        content.LastCheckTrigger.Should().Be(nameof(SelfUpdateTrigger.Startup));
    }

    /// <summary>
    /// 🚨 #2494 — <b>the delivery fix.</b> With no build-completion event and no policy change,
    /// pre-fix code checks exactly ONCE (the startup pass) and then never again for the life of the
    /// pod. That is the state prod was in: the event channel was misconfigured, every joint of it
    /// failed silently, and an install that had stopped checking was indistinguishable from one
    /// with nothing to do.
    ///
    /// <para>The safety net is not the removed poll returning — it cannot change the roll cadence
    /// (a safety-net check passes through <see cref="SelfUpdateOptions.MinRollInterval"/> like any
    /// other). It bounds how long a broken driver can hide.</para>
    ///
    /// <para>Fails on pre-fix code: the lister is called once and stays there.</para>
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task WithNoBuildEventAtAll_TheInstallStillChecksAgain()
    {
        var acr = new CountingTagLister();
        var logger = new CapturingLogger();
        var service = NewService(logger, acr, new PatchingUpdater(),
            Options(safetyNet: TimeSpan.FromMilliseconds(300)));
        await service.StartAsync(CancellationToken.None);
        try
        {
            // A positive signal: a REPORTED check whose trigger is the safety net. Nothing
            // published and nothing changed the policy, so that is the only thing that can produce
            // it. Waiting on the verdict rather than on the listing also removes the race between
            // the two — the registry call happens first, the report follows.
            await logger.Emitted
                .Where(l => l.Message.Contains("[SelfUpdate] check (", StringComparison.Ordinal)
                    && l.Message.Contains(nameof(SelfUpdateTrigger.SafetyNet), StringComparison.Ordinal))
                .FirstAsync()
                .Timeout(TimeSpan.FromSeconds(30))
                .Await(TestContext.Current.CancellationToken);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        acr.Calls.Should().BeGreaterThanOrEqualTo(2,
            "the startup pass is one listing; a second one can only have come from the safety net");
        logger.Checks.Should().Contain(
            l => l.Message.Contains(nameof(SelfUpdateTrigger.SafetyNet), StringComparison.Ordinal));
    }

    /// <summary>
    /// 🚨 #2494 — <b>the diagnosis fix, and the only place in the product that can make it.</b>
    /// Three facts have to coincide before "your event channel is dead" is a fair thing to say:
    /// the check was woken by the safety net, this install has seen no build-completion event at
    /// all, AND a newer release was in fact waiting. Each clause matters — "no events yet" alone is
    /// routine on an install whose modules rarely build, and warning about it would train people to
    /// ignore the line that means something.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ASafetyNetCheckThatFindsAReleaseNobodyAnnounced_WarnsAboutTheEventChannel()
    {
        var logger = new CapturingLogger();
        var service = NewService(logger, new CountingTagLister(NewerTag), new PatchingUpdater(),
            Options(safetyNet: TimeSpan.FromMilliseconds(300)));
        await service.StartAsync(CancellationToken.None);
        try
        {
            var warning = await logger.Emitted
                .Where(l => l.Level == LogLevel.Warning
                    && l.Message.Contains("NO build-completion event", StringComparison.Ordinal))
                .FirstAsync()
                .Timeout(TimeSpan.FromSeconds(30))
                .Await(TestContext.Current.CancellationToken);

            warning.Message.Should().Contain("WebhookInbox",
                "the report must name the chain to check, not this service");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// The control for the test above: a check woken by a real build completion must NOT warn about
    /// the event channel, however many releases it finds. A report that fires on the healthy path
    /// is noise, and noise is what taught everyone to ignore the last one.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ABuildCompletionDrivenCheck_NeverWarnsAboutTheEventChannel()
    {
        var logger = new CapturingLogger();
        var service = NewService(logger, new CountingTagLister(NewerTag), new PatchingUpdater(),
            // Safety net off: the ONLY stimulus in this test is a real event.
            Options(safetyNet: TimeSpan.Zero));
        await service.StartAsync(CancellationToken.None);
        try
        {
            // 🚨 Publish only once the watch is LIVE. StartAsync subscribes via SubscribeOn(TaskPool)
            // and the policy source now waits for a real read of Admin/UpdatePolicy (#2731/#2797), so
            // StartAsync returns well before BuildCompletionTicks is established — and a publication
            // that races that subscription is absorbed as the watch's BASELINE rather than seen as a
            // new build (NewBuildEvents, by design). The startup check line is the positive signal
            // that the Merge — and therefore the watch — has been subscribed.
            await logger.Emitted
                .Where(l => l.Message.Contains("[SelfUpdate] check (", StringComparison.Ordinal))
                .FirstAsync()
                .Timeout(TimeSpan.FromSeconds(30))
                .Await(TestContext.Current.CancellationToken);

            await SelfUpdateEventDriver.PublishBuildAsync(Mesh, "MeshWeaver", 4242);
            await logger.Emitted
                .Where(l => l.Message.Contains(nameof(SelfUpdateTrigger.BuildCompletion), StringComparison.Ordinal))
                .FirstAsync()
                .Timeout(TimeSpan.FromSeconds(30))
                .Await(TestContext.Current.CancellationToken);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        logger.Lines.Should().NotContain(
            l => l.Message.Contains("NO build-completion event", StringComparison.Ordinal));
    }

    /// <summary>
    /// 🚨 <b>#2731 / #2797 — a PINNED install rolled on every pod restart.</b>
    ///
    /// <para><c>CreatePolicySource</c> used to prepend the <em>configured default</em>
    /// (<c>Continuous</c>) to the policy stream before the persisted node value arrived, and the
    /// startup pass ran a full <see cref="SelfUpdateHostedService"/> evaluation on that synthetic
    /// value — ACR listing, candidate selection, and the Deployment PATCH. On memex-cloud, whose
    /// <c>Admin/UpdatePolicy</c> has read <c>None</c> since 2026-08-28, that rolled the portal
    /// twice on 2026-08-30; the persisted <c>None</c> arrived a second later as a
    /// <c>PolicyChange</c> and logged "updates are disabled" AFTER the roll had been issued.</para>
    ///
    /// <para>The discriminating signal is the REGISTRY LISTING, not a log-line spelling: the
    /// <c>None</c> branch of <c>RunOnce</c> returns its verdict without ever listing tags, so
    /// <c>acr.Calls == 0</c> at the moment the first verdict is reported proves the startup pass
    /// ran under the PERSISTED policy. Pre-fix this test fails on both assertions — the first
    /// verdict is the Continuous one and the registry has been listed.</para>
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task StartupCheck_OnAPinnedInstall_RunsUnderThePersistedPolicy_NotTheConfiguredDefault()
    {
        var ct = TestContext.Current.CancellationToken;

        // The install is pinned by an administrator BEFORE the poller ever starts — the memex-cloud
        // shape. CreateNode, never GetMeshNodeStream(path).Update: the node does not exist yet, and
        // a point read of an absent path answers a routing NotFound that terminates the stream.
        await Mesh.ServiceProvider.GetRequiredService<IMeshService>()
            .CreateNode(new MeshNode(UpdatePolicyNodeType.NodeId, UpdatePolicyNodeType.AdminPartition)
            {
                NodeType = UpdatePolicyNodeType.NodeType,
                Name = "Update Policy",
                State = MeshNodeState.Active,
                Content = new UpdatePolicyContent { Policy = UpdatePolicyKind.None },
            })
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(30))
            .Await(ct);

        var acr = new CountingTagLister(NewerTag);
        // The deployment default disagrees with the node — the wrong-policy tell. A pre-fix run
        // would list NewerTag and patch to it.
        var logger = await RunOneCheckAsync(acr, new PatchingUpdater(),
            Options(policy: UpdatePolicyKind.Continuous));

        logger.Checks[0].Message.Should().Contain("disabled",
            "the FIRST check must be decided by the persisted policy (None), never by the "
            + "configured default that the install has overridden");
        acr.Calls.Should().Be(0,
            "the None branch never lists the registry, so any listing at all proves an evaluation "
            + "ran under a policy this install never set (#2731/#2797)");
    }
}
