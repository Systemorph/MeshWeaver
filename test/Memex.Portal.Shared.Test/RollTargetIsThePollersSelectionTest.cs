#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.Api;
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
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <c>/api/plugins/roll-target</c> names the release the POLLER would take (#4413).
///
/// <para>Measured: combo verification asked <c>memex</c> for its roll target, was told
/// <c>3.1.11003</c>, and failed <c>docker pull memex-portal-ai:3.1.11003 … manifest unknown</c>.
/// The route selected over the published root's <c>_releases/&lt;version&gt;</c> markers, which
/// main-cd's <c>publish-bake</c> writes after <c>promote</c> and before <c>arm</c> writes the portal
/// tag — so a set the fleet had not armed was "the roll target" while the poller, which selects over
/// the registry's portal tags, could never take it. These tests drive the real route over a real
/// poller and a real mesh, with the availability gate answering the MARKER universe when it is asked
/// to choose on its own: before the fix the route answered the marker-only release.</para>
/// </summary>
public class RollTargetIsThePollersSelectionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>A release whose portal tag exists in the registry (armed).</summary>
    private const string Armed = "9999.0.0-ci.1";

    /// <summary>A release with a published-root marker and NO portal tag (promoted, not armed).</summary>
    private const string MarkerOnly = "9999.0.0-ci.2";

    private static TimeSpan Budget => TestTimeouts.Convergence;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddUpdatePolicyType();

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    [Fact(Timeout = 240_000)]
    public async Task TheRollTarget_IsTheNewestPortalTag_NeverAMarkerWithoutAnImage()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(ct);

        var root = await AskRollTarget(new MarkerUniverseGate(Mesh), new Registry([Armed]), ct);

        root.GetProperty("selected").GetString().Should().Be(Armed,
            "the route names what the poller would roll to — a release with a portal image — and "
            + $"never {MarkerOnly}, whose marker exists while its portal tag does not");
        root.GetProperty("shouldUpdate").GetBoolean().Should().BeTrue();
        root.GetProperty("indeterminate").GetBoolean().Should().BeFalse();
    }

    [Fact(Timeout = 240_000)]
    public async Task AGateThatSelectsNothing_StillNamesTheTagThePollerTakes()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(ct);

        var root = await AskRollTarget(new NotEnforcedGate(Mesh), new Registry([Armed]), ct);

        root.GetProperty("selected").GetString().Should().Be(Armed,
            "a deployment that consumes no CI bakes still rolls to the newest admitted tag, so that "
            + "is the target to verify — 'nothing to verify' would pass a roll nobody checked");
        root.GetProperty("advisories").EnumerateArray().Should().NotBeEmpty(
            "why no package was checked is said on the selection");
    }

    [Fact(Timeout = 240_000)]
    public async Task AListingThatFaults_IsIndeterminate_NeverAnEmptyChoice()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(ct);

        var root = await AskRollTarget(new MarkerUniverseGate(Mesh), new Registry(null), ct);

        root.GetProperty("selected").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("indeterminate").GetBoolean().Should().BeTrue(
            "a registry that could not be listed is 'could not look', never 'nothing to roll to'");
        root.GetProperty("environment").GetString().Should().Contain("registry unreachable");
    }

    [Fact(Timeout = 240_000)]
    public async Task NothingNewerInTheRegistry_SelectsNothing_AndSaysSo()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(ct);

        var root = await AskRollTarget(
            new MarkerUniverseGate(Mesh), new Registry([ShippedReleaseSeed.InstalledPlatformVersion]), ct);

        root.GetProperty("selected").ValueKind.Should().Be(JsonValueKind.Null,
            "the running tag is the newest the registry lists, so the poller rolls nowhere");
        root.GetProperty("indeterminate").GetBoolean().Should().BeFalse();
        root.GetProperty("kind").GetString().Should().Be(nameof(RollSelectionKind.NoCandidates));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Harness
    // ══════════════════════════════════════════════════════════════════════════

    private async Task<JsonElement> AskRollTarget(
        ReleaseAvailabilityService gate, IAcrTagLister registry, CancellationToken ct)
    {
        var poller = new GatedPoller(Mesh, registry, gate);
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddLogging()
                .AddSingleton(gate)
                .AddSingleton<SelfUpdateHostedService>(poller)
                .BuildServiceProvider(),
        };
        using var body = new MemoryStream();
        http.Response.Body = body;

        var result = await ReleaseGateEndpoints.Choose(http, current: null)
            .FirstAsync().Timeout(Budget).Await(ct);
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        using var json = JsonDocument.Parse(body.ToArray());
        Output.WriteLine(json.RootElement.ToString());
        return json.RootElement.Clone();
    }

    /// <summary>The registry's portal tag listing; a null listing is a registry that faults.</summary>
    private sealed class Registry(ImmutableArray<string>? tags) : IAcrTagLister
    {
        public Task<IReadOnlyList<string>> ListTagsAsync(string repository, CancellationToken ct) =>
            tags is { } listed
                ? Task.FromResult<IReadOnlyList<string>>(listed)
                : Task.FromException<IReadOnlyList<string>>(new InvalidOperationException("registry unreachable"));
    }

    private static UpdatabilityVerdict Updatable(string version) =>
        ReleaseAvailability.IsUpdatable(
            new ReleaseTarget(version, "sid"), [new RequiredPackage("Crm", "Crm")], ReleaseArtifacts.Of(["Crm"]));

    /// <summary>
    /// The availability gate as it behaves on an instance whose published root carries a marker
    /// for <see cref="MarkerOnly"/>: asked to choose ON ITS OWN (no candidates), it selects over
    /// the markers and names <see cref="MarkerOnly"/>; handed the poller's candidates, it walks them.
    /// </summary>
    private sealed class MarkerUniverseGate(IMessageHub hub)
        : ReleaseAvailabilityService(hub, new ConfigurationBuilder().Build())
    {
        public override IObservable<UpdatabilityVerdict> IsUpdatable(string? targetVersion) =>
            Observable.Return(Updatable(targetVersion ?? Armed));

        public override IObservable<RollSelectionOutcome> SelectRollTarget(
            string? currentVersion,
            IReadOnlyList<string>? candidatesNewestFirst = null,
            Func<string, string?>? condemned = null,
            UpdatePolicyKind policy = UpdatePolicyKind.Continuous,
            bool requireCiGreen = true) =>
            RollSelection.Select(
                new RollSelectionInputs(
                    "test", currentVersion,
                    [.. candidatesNewestFirst ?? [MarkerOnly, Armed]],
                    PluginInventory.Of([new RequiredPackage("Crm", "Crm")], "test")),
                version => Observable.Return(Updatable(version)));
    }

    /// <summary>The gate on a deployment that consumes no CI bakes: it selects nothing.</summary>
    private sealed class NotEnforcedGate(IMessageHub hub)
        : ReleaseAvailabilityService(hub, new ConfigurationBuilder().Build())
    {
        public override IObservable<RollSelectionOutcome> SelectRollTarget(
            string? currentVersion,
            IReadOnlyList<string>? candidatesNewestFirst = null,
            Func<string, string?>? condemned = null,
            UpdatePolicyKind policy = UpdatePolicyKind.Continuous,
            bool requireCiGreen = true) =>
            Observable.Return(new RollSelectionOutcome(
                RollSelectionKind.NotEnforced, null, currentVersion, 0, 0, [],
                "test: no release is selected — this deployment consumes no CI bakes"));
    }

    private sealed class NoPatch : IDeploymentUpdater
    {
        public bool CanPatch => false;

        public Task<DateTimeOffset?> LastRolledAtAsync(CancellationToken ct) =>
            Task.FromResult<DateTimeOffset?>(null);

        public Task PatchToVersionAsync(string versionTag, CancellationToken ct) =>
            throw new InvalidOperationException("the roll-target route must never patch");
    }

    private sealed class GatedPoller(IMessageHub hub, IAcrTagLister registry, ReleaseAvailabilityService gate)
        : SelfUpdateHostedService(hub, registry, new NoPatch(), new SelfUpdateOptions
        {
            DefaultPolicy = UpdatePolicyKind.Continuous,
            DefaultPattern = "*-ci*",
        })
    {
        protected override ReleaseAvailabilityService? ResolveAvailabilityGate() => gate;
    }

    /// <summary>Seeds the policy node: Continuous, following the ci line.</summary>
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
}
