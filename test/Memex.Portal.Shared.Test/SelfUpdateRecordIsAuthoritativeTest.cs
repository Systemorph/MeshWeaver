using System;
using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.SelfUpdate;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.SelfUpdate;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>The deployment record's update policy is AUTHORITATIVE when it declares one</b> — policy
/// <c>self-update-record-authoritative</c>.
///
/// <para>The build instance's record said <c>Continuous</c> + <c>3.0.0-ci*</c>; its
/// <c>Admin/UpdatePolicy</c>, created before the record's policy was rendered into configuration,
/// said <c>Stable</c> with no pattern — and the configuration keys were seed-only, so every check
/// answered "none newer than the installed 3.0.0-ci.9412" while 3.0.0-ci.9564 was armed
/// (Doc/Architecture/SelfUpdateFreeze, "The build instance").</para>
///
/// <para><b>Fails on unfixed code:</b> <see cref="AStaleNode_ConvergesToTheDeclaredPolicy_WhenThePollerStarts"/>
/// waits for a <c>Continuous</c> node that never comes. The negatives are what prove the change is
/// scoped: no declared key leaves an admin's node alone, an already-matching node is not written,
/// and the convergence touches the policy and pattern ONLY.</para>
///
/// <para>Real monolith mesh throughout — the hub, the workspace, the node and every
/// <c>stream.Update</c> are real; only the registry listing and the k8s patcher are the platform's
/// own "unavailable" fallbacks.</para>
/// </summary>
public class SelfUpdateRecordIsAuthoritativeTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string FleetPattern = "3.0.0-ci*";
    private const string CandidateTag = "3.0.0-ci.9564";

    private static TimeSpan Budget => TestTimeouts.Convergence;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddUpdatePolicyType();

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private static IConfiguration Record(string? policy, string? pattern) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [UpdatePolicyNodeType.DeclaredPolicyConfigKey] = policy,
            [UpdatePolicyNodeType.DeclaredPatternConfigKey] = pattern,
        }).Build();

    // ══════════════════════════════════════════════════════════════════════════
    //  (a) The acceptance — through the poller, as production runs it
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The build instance's state: node <c>Stable</c>, no pattern; record <c>Continuous</c> +
    /// <c>3.0.0-ci*</c>. Starting the poller converges the node to the record.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task AStaleNode_ConvergesToTheDeclaredPolicy_WhenThePollerStarts()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(new UpdatePolicyContent { Policy = UpdatePolicyKind.Stable }, ct);

        var converged = await WithPoller(
            Record("Continuous", FleetPattern),
            _ => WaitForContent(c => c.DeclaredPolicy == UpdatePolicyKind.Continuous));

        converged.Pattern.Should().Be(FleetPattern, "the record renders the pattern together with the policy");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  (b) Nothing declared — seed-only, exactly as before
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A non-fleet install: no <c>SelfUpdate:DefaultPolicy</c> key. The options still carry a
    /// default (the binder's <c>Stable</c>, here an explicit <c>Continuous</c> to make the point
    /// sharper) — and the admin's existing <c>None</c> is left exactly as it was, after a full
    /// check has run.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task NoDeclaredPolicy_LeavesTheExistingNodeAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(new UpdatePolicyContent { Policy = UpdatePolicyKind.None, Pattern = "9.9.*" }, ct);

        UpdatePolicyNodeType.DeclaredByDeployment(Record(null, FleetPattern))
            .Should().BeNull("a pattern without a declared policy declares nothing");
        UpdatePolicyNodeType.DeclaredByDeployment(Record("   ", null))
            .Should().BeNull("a blank policy key is an unset one");

        // A reported check is only reachable AFTER the seeding stage (where convergence runs) has
        // completed and the live policy has been read — so it is the positive signal that the
        // stage ran and chose not to write.
        var after = await WithPoller(
            Record(null, null),
            async service =>
            {
                await service.Evaluations.FirstAsync().Timeout(Budget).Await(TestContext.Current.CancellationToken);
                return await CurrentContent();
            });

        after.DeclaredPolicy.Should().Be(UpdatePolicyKind.None, "an admin's choice stands where no record declares one");
        after.Pattern.Should().Be("9.9.*");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  (c) Already equal — no write
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A node that already carries the record's policy and pattern is not written — its
    /// <see cref="MeshNode.Version"/> does not move.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task AnAlreadyMatchingNode_IsNotWritten()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(new UpdatePolicyContent { Policy = UpdatePolicyKind.Continuous, Pattern = FleetPattern }, ct);
        var versionBefore = (await CurrentNode()).Version;

        var declared = UpdatePolicyNodeType.DeclaredByDeployment(Record("continuous", " 3.0.0-ci* "));
        declared.Should().Be(new UpdatePolicyNodeType.DeploymentDeclaration(UpdatePolicyKind.Continuous, FleetPattern),
            "the policy name is read case-insensitively and the pattern normalised");

        if (declared is not { } matching)
            throw new InvalidOperationException("the declaration was asserted non-null above");
        await Converge(matching);

        (await CurrentNode()).Version.Should().Be(versionBefore, "a node that already carries the record's values is not written");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  (d) Only policy and pattern move
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The convergence moves the policy and the pattern ONLY: <c>RequireCiGreen</c> and every
    /// bookkeeping field (latest tag, last verdict and trigger, combo verdicts) survive it.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task Convergence_PreservesEveryOtherField()
    {
        var ct = TestContext.Current.CancellationToken;
        var verdictAt = new DateTimeOffset(2026, 9, 28, 15, 0, 0, TimeSpan.Zero);
        await Seed(new UpdatePolicyContent
        {
            Policy = UpdatePolicyKind.Stable,
            RequireCiGreen = false,
            LatestAvailableTag = "3.0.0-ci.9412",
            LastCheckVerdict = "no newer release",
            LastCheckTrigger = "SafetyNet",
            ComboVerifications =
            [
                new ComboVerification
                {
                    CandidateTag = CandidateTag,
                    Verdict = ComboVerdictKind.Red,
                    VerifiedAt = verdictAt,
                },
            ],
        }, ct);

        await Converge(new UpdatePolicyNodeType.DeploymentDeclaration(UpdatePolicyKind.Continuous, FleetPattern));

        var after = await WaitForContent(c => c.DeclaredPolicy == UpdatePolicyKind.Continuous);
        after.Pattern.Should().Be(FleetPattern);
        after.RequireCiGreen.Should().BeFalse("the record declares policy and pattern, nothing else");
        after.LatestAvailableTag.Should().Be("3.0.0-ci.9412");
        after.LastCheckVerdict.Should().Be("no newer release");
        after.LastCheckTrigger.Should().Be("SafetyNet");
        after.ComboVerifications.Should().ContainSingle()
            .Which.CandidateTag.Should().Be(CandidateTag);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Helpers
    // ══════════════════════════════════════════════════════════════════════════

    private sealed class SeamedService(IMessageHub hub, IConfiguration configuration, ILogger<SelfUpdateHostedService>? logger)
        : SelfUpdateHostedService(
            hub,
            new UnavailableUpdateMechanics.NoRegistry(logger),
            new UnavailableUpdateMechanics.DetectOnly(),
            new SelfUpdateOptions
            {
                RetryInterval = TimeSpan.FromMilliseconds(500),
                EventCoalesceWindow = TimeSpan.FromMilliseconds(50),
                // What the options binder would carry — the service must NOT treat it as declared.
                DefaultPolicy = UpdatePolicyKind.Continuous,
                DefaultPattern = "*-ci*",
            },
            logger)
    {
        public IObservable<Unit> Evaluations => ChecksReported;

        protected override IConfiguration? ResolveConfiguration() => configuration;
    }

    private async Task<UpdatePolicyContent> WithPoller(IConfiguration configuration, Func<SeamedService, Task<UpdatePolicyContent>> wait)
    {
        var service = new SeamedService(Mesh, configuration,
            Mesh.ServiceProvider.GetService<ILogger<SelfUpdateHostedService>>());
        await service.StartAsync(CancellationToken.None);
        try
        {
            return await wait(service);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private Task Converge(UpdatePolicyNodeType.DeploymentDeclaration declared) =>
        UpdatePolicyNodeType.ConvergeToDeclaration(Mesh, Access, declared)
            .Timeout(Budget)
            .Await(TestContext.Current.CancellationToken);

    private Task<MeshNode> Seed(UpdatePolicyContent content, CancellationToken cancellationToken)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var node = new MeshNode(UpdatePolicyNodeType.NodeId, UpdatePolicyNodeType.AdminPartition)
        {
            NodeType = UpdatePolicyNodeType.NodeType,
            Name = "Update Policy",
            State = MeshNodeState.Active,
            Content = content,
        };
        return Observable.Create<MeshNode>(observer =>
            {
                using (Access.ImpersonateAsSystem())
                    return meshService.CreateNode(node).Subscribe(observer);
            })
            .FirstAsync()
            .Timeout(Budget)
            .Await(cancellationToken);
    }

    private IObservable<MeshNode> NodeStream() =>
        Observable.Create<MeshNode>(observer =>
        {
            using (Access.ImpersonateAsSystem())
                return Mesh.GetWorkspace()
                    .GetMeshNodeStream(UpdatePolicyNodeType.NodePath)
                    .SelectMany(node => node is { } present
                        ? Observable.Return(present)
                        : Observable.Empty<MeshNode>())
                    .Subscribe(observer);
        });

    private Task<MeshNode> CurrentNode() =>
        NodeStream().FirstAsync().Timeout(Budget).Await(TestContext.Current.CancellationToken);

    private Task<UpdatePolicyContent> CurrentContent() =>
        WaitForContent(_ => true);

    private Task<UpdatePolicyContent> WaitForContent(Func<UpdatePolicyContent, bool> predicate) =>
        NodeStream()
            .Select(node => UpdatePolicyNodeType.Parse(node, Mesh.JsonSerializerOptions))
            .Where(predicate)
            .FirstAsync()
            .Timeout(Budget)
            .Await(TestContext.Current.CancellationToken);
}
