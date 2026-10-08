#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.ServiceDefaults;
using Memex.Portal.Shared.SelfUpdate;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.SelfUpdate;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>Policy <c>control-first-never-silent</c> — a self-update that FAILS is published on
/// <c>/health</c>, where the CD arming reads it.</b>
///
/// <para>Measured 2026-10-07/08: control.systemorph.com's <c>Admin/UpdatePolicy.lastCheckVerdict</c>
/// read <c>check FAILED: CredentialUnavailableException … ManagedIdentityCredential authentication
/// unavailable. The requested identity has not been assigned to this resource</c> on every check
/// (Plugins#2994), control took no build for 10+ hours, control-first therefore offered memex and
/// memex-cloud nothing — and control's public <c>/health</c> said nothing about any of it. The
/// failure lived on one authenticated node nobody watches.</para>
///
/// <para>These tests drive the REAL poller through a registry read that faults exactly that way and
/// assert the delivery on all three surfaces the one classification
/// (<see cref="SelfUpdateVerdict.IsFailure"/>) feeds: the census the <c>/health</c> entry reads, the
/// <c>/health</c> body line in the exact shape <c>.github/scripts/arm-promoted-set.py</c> parses
/// (<c>self_update: Degraded — …</c>), and <c>Admin/UpdatePolicy.lastCheckFailed</c>. The control is
/// the same driver with a registry that answers: Healthy, and still PRINTED (a census entry), never
/// silent.</para>
///
/// <para><b>Fails on unfixed code:</b> before this change the poller recorded nothing outside the
/// node, so <see cref="SelfUpdateCheckCensus.Last"/> stays null and the body has no
/// <c>self_update</c> line at all.</para>
/// </summary>
public class SelfUpdateFailureIsPublicTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static TimeSpan Budget => TestTimeouts.Convergence;

    private static string InstalledTag => ShippedReleaseSeed.InstalledPlatformVersion.Split('+')[0];

    /// <summary>The exception text measured on control (the managed-identity leg of the chain).</summary>
    private const string MeasuredCredentialFault =
        "DefaultAzureCredential failed to retrieve a token from the included credentials.\n"
        + "- ManagedIdentityCredential authentication unavailable. The requested identity has not been assigned to this resource.";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddUpdatePolicyType().AddGitHubSyncTypes()
            .ConfigureServices(services =>
            {
                // Exactly what AddSelfUpdate registers; this suite constructs the poller by hand.
                services.TryAddSingleton<SelfUpdateCheckCensus>();
                return services;
            });

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private SelfUpdateCheckCensus Census => Mesh.ServiceProvider.GetRequiredService<SelfUpdateCheckCensus>();

    [Fact(Timeout = 240_000)]
    public async Task AFailingCheck_IsDegradedOnHealth_InTheShapeTheArmingParses()
    {
        await Seed(TestContext.Current.CancellationToken);
        await using var run = await Start(new FaultingTagLister(MeasuredCredentialFault));
        await run.FirstCheck;

        var reading = Census.Last;
        reading.Should().NotBeNull("the reporting site records every check into the census /health reads");
        reading!.Failed.Should().BeTrue("a check that faulted is the failure control-first must not hide");
        reading.Outcome.Should().Be(nameof(SelfUpdateOutcome.CheckFailed));

        var entry = SelfUpdateHealthCheck.Evaluate(Census, DateTimeOffset.UtcNow);
        entry.Status.Should().Be(HealthStatus.Degraded, "Degraded — a 200, never a reason to pull the pod");

        var line = HealthLine(entry);
        line.Should().StartWith("self_update: Degraded — check FAILED: InvalidOperationException: DefaultAzureCredential failed",
            "arm-promoted-set.py reads `self_update: <status> — <text>` off the public body");
        line.Should().NotContain("\n", "a /health body is parsed line by line");

        var content = await WaitForContent(c => c.LastCheckFailed is not null, TestContext.Current.CancellationToken);
        content.LastCheckFailed.Should().BeTrue("the fleet console flags the same classification off the node");
        content.LastCheckVerdict.Should().Contain("The requested identity has not been assigned to this resource",
            "the node keeps the FULL verdict; /health carries its first line");
    }

    /// <summary>The control: the same driver, a registry that answers — Healthy, and still printed.</summary>
    [Fact(Timeout = 240_000)]
    public async Task ACleanCheck_IsHealthyOnHealth_AndStillPrinted()
    {
        await Seed(TestContext.Current.CancellationToken);
        await using var run = await Start(new FixedTagLister(["1.9.0-ci.0", InstalledTag]));
        await run.FirstCheck;

        Census.Last!.Failed.Should().BeFalse();
        var entry = SelfUpdateHealthCheck.Evaluate(Census, DateTimeOffset.UtcNow);
        entry.Status.Should().Be(HealthStatus.Healthy);
        HealthLine(entry).Should().StartWith("self_update: Healthy — no newer release",
            "a census entry prints whatever its status: 'checked and nothing newer' must read differently from silence");

        var content = await WaitForContent(c => c.LastCheckFailed is not null, TestContext.Current.CancellationToken);
        content.LastCheckFailed.Should().BeFalse();
    }

    /// <summary>No reading and no census are each said as what they are — never as a clean check.</summary>
    [Fact]
    public void AnAbsentReading_IsSaidAsAbsent()
    {
        SelfUpdateHealthCheck.Evaluate(new SelfUpdateCheckCensus(), DateTimeOffset.UtcNow).Description
            .Should().Contain("absence of measurement");
        SelfUpdateHealthCheck.Evaluate(null, DateTimeOffset.UtcNow).Description
            .Should().Contain("no self-updater is registered");
    }

    /// <summary>The one classification: the warning set, the node flag and /health agree.</summary>
    [Theory]
    [InlineData(SelfUpdateOutcome.CheckFailed, true)]
    [InlineData(SelfUpdateOutcome.NoOutcome, true)]
    [InlineData(SelfUpdateOutcome.HandoverFailed, true)]
    [InlineData(SelfUpdateOutcome.RestartHandoverFailed, true)]
    [InlineData(SelfUpdateOutcome.InstalledTagWithdrawn, true)]
    [InlineData(SelfUpdateOutcome.RestartUnavailable, true)]
    [InlineData(SelfUpdateOutcome.MigrationFailed, true)]
    [InlineData(SelfUpdateOutcome.MigrationUnavailable, true)]
    [InlineData(SelfUpdateOutcome.NoNewerRelease, false)]
    [InlineData(SelfUpdateOutcome.HandedOver, false)]
    [InlineData(SelfUpdateOutcome.Applied, false)]
    [InlineData(SelfUpdateOutcome.Held, false)]
    [InlineData(SelfUpdateOutcome.Deferred, false)]
    [InlineData(SelfUpdateOutcome.UpdatesDisabled, false)]
    public void TheFailureClassification(SelfUpdateOutcome outcome, bool failure) =>
        new SelfUpdateVerdict(outcome, "x").IsFailure.Should().Be(failure);

    /// <summary>A whole credential-chain dump becomes ONE bounded line.</summary>
    [Fact]
    public void AMultiLineVerdict_BecomesOneBoundedLine()
    {
        SelfUpdateCheckCensus.OneLine("check FAILED: X\n- a\n- b").Should().Be("check FAILED: X");
        SelfUpdateCheckCensus.OneLine(new string('y', 2000)).Length.Should().Be(SelfUpdateCheckCensus.MaxLineLength);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Harness
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>The <c>/health</c> body line for <paramref name="entry"/>, through the real body writer.</summary>
    private static string HealthLine(HealthCheckResult entry)
    {
        var report = new HealthReport(
            new Dictionary<string, HealthReportEntry>
            {
                [SelfUpdateCheckCensus.HealthCheckName] = new(entry.Status, entry.Description, TimeSpan.Zero, null, null,
                    [ProbeEndpoints.CensusTag]),
            },
            TimeSpan.Zero);
        return Memex.Portal.ServiceDefaults.ServiceDefaults.HealthBodyLines(report, Memex.Portal.ServiceDefaults.ServiceDefaults.StartupStatus(report))
            .Single(l => l.StartsWith(SelfUpdateCheckCensus.HealthCheckName + ":", StringComparison.Ordinal));
    }

    private sealed class FaultingTagLister(string message) : IAcrTagLister
    {
        public Task<IReadOnlyList<string>> ListTagsAsync(string repository, CancellationToken ct) =>
            Task.FromException<IReadOnlyList<string>>(new InvalidOperationException(message));
    }

    private sealed class FixedTagLister(IReadOnlyList<string> tags) : IAcrTagLister
    {
        public Task<IReadOnlyList<string>> ListTagsAsync(string repository, CancellationToken ct) =>
            Task.FromResult(tags);
    }

    private sealed class InertUpdater : IDeploymentUpdater
    {
        public bool CanPatch => false;

        public Task<DateTimeOffset?> LastRolledAtAsync(CancellationToken ct) => Task.FromResult<DateTimeOffset?>(null);

        public Task PatchToVersionAsync(string versionTag, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class AlwaysAvailable(IMessageHub hub, IConfiguration configuration)
        : ReleaseAvailabilityService(hub, configuration)
    {
        public override IObservable<UpdatabilityVerdict> IsUpdatable(string? targetVersion) =>
            Observable.Return(UpdatabilityVerdict.NotEnforced("this test host consumes no CI bakes"));
    }

    /// <summary>The poller with its fleet-paced triggers silenced: only the startup check runs.</summary>
    private sealed class DrivenSelfUpdateService(
        IMessageHub hub, IAcrTagLister acr, SelfUpdateOptions options,
        ILogger<SelfUpdateHostedService>? logger, ReleaseAvailabilityService gate)
        : SelfUpdateHostedService(hub, acr, new InertUpdater(), options, logger)
    {
        public IObservable<Unit> Evaluations => ChecksReported;

        protected override ReleaseAvailabilityService? ResolveAvailabilityGate() => gate;

        protected override ComboVerificationGate? ResolveComboGate() => null;

        protected override ModuleLandingService? ResolveLandingService() => null;

        protected override IObservable<SelfUpdateTrigger> BuildCompletionTicks() => Observable.Never<SelfUpdateTrigger>();

        protected override IObservable<SelfUpdateTrigger> SafetyNetTicks() => Observable.Never<SelfUpdateTrigger>();
    }

    private sealed class Run(SelfUpdateHostedService service, IObservable<Unit> checks, IDisposable connection)
        : IAsyncDisposable
    {
        public Task FirstCheck => checks.FirstAsync().Timeout(Budget).Await(TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
            await service.StopAsync(CancellationToken.None);
            connection.Dispose();
        }
    }

    private async Task<Run> Start(IAcrTagLister lister)
    {
        var service = new DrivenSelfUpdateService(
            Mesh, lister,
            new SelfUpdateOptions
            {
                RetryInterval = TimeSpan.FromMilliseconds(500),
                EventCoalesceWindow = TimeSpan.FromMilliseconds(50),
                DefaultPolicy = UpdatePolicyKind.Continuous,
                DefaultPattern = "*-ci*",
            },
            Mesh.ServiceProvider.GetService<ILogger<SelfUpdateHostedService>>(),
            new AlwaysAvailable(Mesh, new ConfigurationBuilder().Build()));
        var checks = service.Evaluations.Replay();
        var connection = checks.Connect();
        await service.StartAsync(CancellationToken.None);
        return new Run(service, checks, connection);
    }

    private Task Seed(CancellationToken cancellationToken)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var node = new MeshNode(UpdatePolicyNodeType.NodeId, UpdatePolicyNodeType.AdminPartition)
        {
            NodeType = UpdatePolicyNodeType.NodeType,
            Name = "Update Policy",
            State = MeshNodeState.Active,
            Content = new UpdatePolicyContent { Policy = UpdatePolicyKind.Continuous, Pattern = "*-ci*" },
        };
        return Observable.Create<MeshNode>(observer =>
            {
                using (Access.ImpersonateAsSystem())
                    return (IDisposable)meshService.CreateNode(node).Subscribe(observer);
            })
            .FirstAsync()
            .Timeout(Budget)
            .Await(cancellationToken);
    }

    private Task<UpdatePolicyContent> WaitForContent(
        Func<UpdatePolicyContent, bool> predicate, CancellationToken cancellationToken) =>
        Observable.Create<UpdatePolicyContent>(observer =>
            {
                using (Access.ImpersonateAsSystem())
                    return Mesh.GetWorkspace()
                        .GetMeshNodeStream(UpdatePolicyNodeType.NodePath)
                        .Where(node => node is not null)
                        .Select(node => UpdatePolicyNodeType.Parse(node, Mesh.JsonSerializerOptions))
                        .Subscribe(observer);
            })
            .Where(predicate)
            .FirstAsync()
            .Timeout(Budget)
            .Await(cancellationToken);
}
