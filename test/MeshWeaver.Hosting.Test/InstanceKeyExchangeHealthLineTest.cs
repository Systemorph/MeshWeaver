using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Reactive.Testing;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚦 <b>The key exchange has its own line on <c>/health</c> (#7721).</b>
///
/// <para>In #7484 the exchange gave registered keys no usable answer for about two hours, and the only
/// symptom on <c>/health</c> was an overdue fleet watch. The authenticator now counts what every
/// instance-key resolution answered, and <see cref="InstanceKeyExchangeHealthCheck"/> reads that count:
/// Degraded while the NEWEST resolution was unavailable, Healthy with the counts otherwise.</para>
///
/// <para>Virtual time throughout: the read legs and the resolution deadline run on a
/// <see cref="TestScheduler"/>, and the tally stamps its outcomes from the same scheduler.</para>
/// </summary>
public class InstanceKeyExchangeHealthLineTest(ITestOutputHelper output) : HubTestBase(output)
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);
    private const string InstancePath = "acme/_Instance/prod";
    private const string InstanceId = "prod";

    /// <summary>How the read legs behave for one arrangement.</summary>
    private enum Legs { Answer, Stall, UnknownKey }

    private sealed class Arrangement
    {
        public required InstanceRegistryAuthenticator Authenticator { get; init; }
        public required string Header { get; init; }
        public Legs Mode { get; set; }
    }

    private Arrangement Arrange(TestScheduler scheduler)
    {
        var rawKey = InstanceKeys.Generate();
        var hash = InstanceKeys.Hash(rawKey);
        var indexPath = $"{MeshWeaverInstanceNodeType.IndexNamespace}/{InstanceKeys.HashPrefix(hash)}";
        var index = MeshNode.FromPath(indexPath) with
        {
            Content = new MeshWeaverInstanceIndex { KeyHash = hash, InstancePath = InstancePath, InstanceId = InstanceId },
        };
        var instance = MeshNode.FromPath(InstancePath) with
        {
            Content = new MeshWeaverInstance { InstanceId = InstanceId, KeyHash = hash },
        };

        Arrangement? arrangement = null;
        IObservable<NodeReadOutcome> Read(string path) => arrangement!.Mode switch
        {
            // No frame ever: the shared deadline answers Unavailable.
            Legs.Stall => Observable.Never<NodeReadOutcome>(),
            // The index has no entry for this hash: a definitive negative.
            Legs.UnknownKey => Observable.Return(NodeReadOutcome.Absent),
            _ => Observable.Return(
                path == indexPath ? NodeReadOutcome.Present(index)
                : path == InstancePath ? NodeReadOutcome.Present(instance)
                : NodeReadOutcome.Absent), // the grant: none yet, the normal state of a new instance
        };

        arrangement = new Arrangement
        {
            Authenticator = new InstanceRegistryAuthenticator(GetHost(), NullLogger<InstanceRegistryAuthenticator>.Instance)
            {
                ReadOverride = Read,
                ReadBudget = Budget,
                DeadlineScheduler = scheduler,
            },
            Header = InstanceKeys.AuthorizationHeader(rawKey),
        };
        return arrangement;
    }

    /// <summary>One resolution, driven to its answer on virtual time.</summary>
    private static InstanceAuthResult ResolveOnce(Arrangement arrangement, TestScheduler scheduler, Legs mode)
    {
        arrangement.Mode = mode;
        var outcomes = new List<InstanceAuthResult>();
        Exception? fault = null;
        using var _ = arrangement.Authenticator.AuthenticateOutcome(arrangement.Header).Subscribe(outcomes.Add, ex => fault = ex);
        scheduler.AdvanceBy((Budget + TimeSpan.FromMilliseconds(1)).Ticks);
        fault.Should().BeNull();
        outcomes.Should().ContainSingle();
        return outcomes[0];
    }

    [Fact]
    public void AStalledResolution_IsCountedUnavailable_AndDegradesTheLine()
    {
        var scheduler = new TestScheduler();
        var arrangement = Arrange(scheduler);

        ResolveOnce(arrangement, scheduler, Legs.Answer).Instance.Should().NotBeNull();
        ResolveOnce(arrangement, scheduler, Legs.Stall).IsUnavailable.Should().BeTrue();

        var reading = arrangement.Authenticator.Outcomes.Snapshot;
        reading.Resolved.Should().Be(1);
        reading.Unavailable.Should().Be(1);
        reading.Refused.Should().Be(0);
        reading.LatestUnavailable.Should().BeTrue();
        reading.LastUnavailableReason.Should().Contain("reached no verdict");

        var verdict = InstanceKeyExchangeHealthCheck.Evaluate(reading, scheduler.Now);
        verdict.Status.Should().Be(HealthStatus.Degraded,
            "the newest key resolution reached no verdict: the exchange is telling its callers to retry right now");
        verdict.Description.Should().Contain("1 resolved").And.Contain("1 unavailable");
    }

    [Fact]
    public void AResolutionAfterTheStall_ClearsTheLine_ButKeepsTheCount()
    {
        // Negative control for the degraded verdict: the same unavailable answer, followed by one the
        // read path DID reach, is no longer current.
        var scheduler = new TestScheduler();
        var arrangement = Arrange(scheduler);

        ResolveOnce(arrangement, scheduler, Legs.Stall);
        ResolveOnce(arrangement, scheduler, Legs.Answer);

        var reading = arrangement.Authenticator.Outcomes.Snapshot;
        var verdict = InstanceKeyExchangeHealthCheck.Evaluate(reading, scheduler.Now);
        verdict.Status.Should().Be(HealthStatus.Healthy);
        verdict.Description.Should().Contain("1 unavailable").And.Contain("last unavailable");
    }

    [Fact]
    public void AnUnknownKey_IsCountedRefused_NotUnavailable()
    {
        var scheduler = new TestScheduler();
        var arrangement = Arrange(scheduler);

        var outcome = ResolveOnce(arrangement, scheduler, Legs.UnknownKey);
        outcome.IsUnavailable.Should().BeFalse();
        outcome.Instance.Should().BeNull();

        var reading = arrangement.Authenticator.Outcomes.Snapshot;
        reading.Refused.Should().Be(1);
        reading.Unavailable.Should().Be(0);
        InstanceKeyExchangeHealthCheck.Evaluate(reading, scheduler.Now).Status.Should().Be(HealthStatus.Healthy,
            "a refused key is a verdict the read path reached, not an exchange that cannot answer");
    }

    [Fact]
    public void AnOldStallWithNoTrafficAfter_IsReportedNotDegraded()
    {
        var scheduler = new TestScheduler();
        var arrangement = Arrange(scheduler);

        ResolveOnce(arrangement, scheduler, Legs.Stall);
        scheduler.AdvanceBy((InstanceKeyExchangeHealthCheck.CurrentFor + TimeSpan.FromSeconds(1)).Ticks);

        var verdict = InstanceKeyExchangeHealthCheck.Evaluate(arrangement.Authenticator.Outcomes.Snapshot, scheduler.Now);
        verdict.Status.Should().Be(HealthStatus.Healthy, "nobody is waiting on an answer given that long ago");
        verdict.Description.Should().Contain("1 unavailable").And.Contain("none resolved");
    }

    [Fact]
    public void ARequestWithNoCredential_IsNotAKeyResolution()
    {
        var scheduler = new TestScheduler();
        var arrangement = Arrange(scheduler);
        var outcomes = new List<InstanceAuthResult>();

        using (arrangement.Authenticator.AuthenticateOutcome(null).Subscribe(outcomes.Add))
            scheduler.AdvanceBy(1);

        outcomes.Should().ContainSingle().Which.Instance.Should().BeNull();
        var reading = arrangement.Authenticator.Outcomes.Snapshot;
        reading.Total.Should().Be(0);
        InstanceKeyExchangeHealthCheck.Evaluate(reading, scheduler.Now).Description
            .Should().Contain("nothing measured yet");
    }
}
