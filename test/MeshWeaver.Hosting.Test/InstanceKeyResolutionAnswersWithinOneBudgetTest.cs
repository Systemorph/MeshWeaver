using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Reactive.Testing;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>An instance-key resolution answers within ONE read budget, not one per leg (#7484).</b>
///
/// <para>The key exchange (<c>POST /api/instances/token</c>) resolves a presented key through three
/// reads in sequence — index, instance, grant — and each was bounded by the ten-second read budget on
/// its own. Three slow legs, each inside its bound, therefore answered after up to thirty seconds,
/// while the registry's authentication hook gives the exchange ten: the hook had hung up and reported a
/// bare 500 before the exchange's correct retryable 503 existed. The legs now share the budget.</para>
///
/// <para>Virtual time throughout (<see cref="TestScheduler"/>): the legs are delayed on it and the
/// deadline runs on it, so nothing here depends on wall-clock speed.</para>
/// </summary>
public class InstanceKeyResolutionAnswersWithinOneBudgetTest(ITestOutputHelper output) : HubTestBase(output)
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);
    private const string InstancePath = "acme/_Instance/prod";
    private const string InstanceId = "prod";

    private (InstanceRegistryAuthenticator Authenticator, string Header) Arrange(TestScheduler scheduler, TimeSpan perLeg)
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

        // Each leg answers after `perLeg` — inside its own budget every time.
        IObservable<NodeReadOutcome> Read(string path) =>
            Observable.Return(
                    path == indexPath ? NodeReadOutcome.Present(index)
                    : path == InstancePath ? NodeReadOutcome.Present(instance)
                    : NodeReadOutcome.Absent) // the grant: none yet, the normal state of a new instance
                .Delay(perLeg, scheduler);

        var authenticator = new InstanceRegistryAuthenticator(GetHost(), NullLogger<InstanceRegistryAuthenticator>.Instance)
        {
            ReadOverride = Read,
            ReadBudget = Budget,
            DeadlineScheduler = scheduler,
        };
        return (authenticator, InstanceKeys.AuthorizationHeader(rawKey));
    }

    [Fact]
    public void ThreeLegsEachInsideTheirBudget_ButNotTogether_AnswerRetryableWithinOneBudget()
    {
        var scheduler = new TestScheduler();
        var (authenticator, header) = Arrange(scheduler, TimeSpan.FromSeconds(4));
        var outcomes = new List<InstanceAuthResult>();
        Exception? fault = null;

        using var _ = authenticator.AuthenticateOutcome(header).Subscribe(outcomes.Add, ex => fault = ex);

        scheduler.AdvanceBy((Budget + TimeSpan.FromMilliseconds(1)).Ticks);

        fault.Should().BeNull();
        outcomes.Should().ContainSingle(
            "a resolution must answer within ONE read budget — the caller waits that long and no longer, so an answer "
            + "after three legs of four seconds reaches nobody");
        outcomes[0].IsUnavailable.Should().BeTrue("an unfinished resolution is retryable, never 'unknown key'");
        outcomes[0].Instance.Should().BeNull();

        // Nothing later: the deadline's answer is THE answer, the legs finishing afterwards add none.
        scheduler.AdvanceBy(TimeSpan.FromSeconds(30).Ticks);
        outcomes.Should().ContainSingle();
    }

    [Fact]
    public void ThreeLegsInsideOneBudgetTogether_ResolveTheInstance()
    {
        // Negative control: the deadline cuts only a resolution that did not finish, never one that did.
        var scheduler = new TestScheduler();
        var (authenticator, header) = Arrange(scheduler, TimeSpan.FromSeconds(3));
        var outcomes = new List<InstanceAuthResult>();
        Exception? fault = null;

        using var _ = authenticator.AuthenticateOutcome(header).Subscribe(outcomes.Add, ex => fault = ex);

        scheduler.AdvanceBy(TimeSpan.FromSeconds(9).Ticks + 1);
        fault.Should().BeNull();
        outcomes.Should().ContainSingle();
        outcomes[0].IsUnavailable.Should().BeFalse();
        outcomes[0].Instance.Should().NotBeNull();
        outcomes[0].Instance!.Instance.InstanceId.Should().Be(InstanceId);

        scheduler.AdvanceBy(TimeSpan.FromSeconds(30).Ticks);
        outcomes.Should().ContainSingle("a resolved key is never answered a second time by the deadline");
    }
}
