#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.SelfUpdate;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Graph.Configuration;
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
/// 🚨 <b>A portal that cannot patch itself hands a detected release to the control lane — and
/// patches nothing (MeshWeaver#4098, P3b of Hosting/AksOperationsViaActions).</b>
///
/// <para>The chart renders <c>SelfUpdate__CanPatch=false</c> beside NO self-patch Role. From then
/// on the poller keeps detecting (registry, policy, gates) and replaces the Kubernetes PATCH with
/// one signed <c>self-update-available</c> event into the control instance's inbox, from which the
/// control plane opens the Roll. This suite pins that against a real monolith mesh with an
/// in-process inbox standing in for the network: the event arrives, it verifies with the shared
/// secret, it names the record and the image, the updater is never asked to patch, and the
/// policy node says where the release went.</para>
///
/// <para><b>Fails on unfixed code:</b> <see cref="AControlLaneInstall_AnnouncesTheRelease_AndPatchesNothing"/>
/// sees a patched updater and an empty inbox. The negatives are what prove the guard guards: a
/// chart that still allows self-patch keeps patching and posts nothing; an inbox that refuses is
/// a FAILED hand-over, never a patch; no inbox at all is detect-only naming the key.</para>
///
/// <para>Only the documented seams are injected — the ACR listing, the k8s patcher, the
/// availability gate, the hand-over's settings and HTTP client. The hub, the workspace, the policy
/// node, every <c>stream.Update</c>, the body, the signature and the whole apply decision are real.</para>
/// </summary>
public class SelfUpdateHandsOverToTheControlLaneTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string CandidateTag = "9999.0.0-ci.1";
    private const string Deployment = "unit-fleet-instance";
    private const string InboxUrl = "https://control.example/api/hooks/Hosting/PlatformBuilds";
    private const string Secret = "unit-control-inbox-secret";

    private static TimeSpan Budget => TestTimeouts.Convergence;

    private static string Installed => ShippedReleaseSeed.InstalledPlatformVersion;

    /// <summary>The secret the POST signs with is read off the mesh's configuration at delivery time,
    /// exactly as in production; the seamed settings only say that it is PRESENT.</summary>
    private static IConfiguration Environment() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [SelfUpdateHandover.SecretKey] = Secret,
        }).Build();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddUpdatePolicyType().AddGitHubSyncTypes()
            .ConfigureServices(services => services.AddSingleton(Environment()));

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    // ══════════════════════════════════════════════════════════════════════════
    //  The acceptance
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 🚨 <b>The acceptance criterion.</b> The chart says "no self-patch" (the updater — the AKS
    /// module still installed — would still say it can), a control inbox is configured, the
    /// registry holds a newer release: ONE signed event reaches the inbox, nothing is patched, and
    /// the policy node records the hand-over.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task AControlLaneInstall_AnnouncesTheRelease_AndPatchesNothing()
    {
        await Seed(UpdatePolicyKind.Continuous, TestContext.Current.CancellationToken);
        var inbox = new FakeControlInbox(HttpStatusCode.OK);
        var updater = new RecordingUpdater();

        var content = await RunOneCheck(updater, inbox, chartCanPatch: false, PostSettings());

        updater.Tags.Should().BeEmpty("a portal on the control lane holds no credential that changes the cluster");
        inbox.Deliveries.Should().HaveCount(1, "one check, one announcement");
        var delivery = inbox.Deliveries[0];
        WebhookInbox.VerifyHmacSha256(delivery.Signature, delivery.Body, Secret).Should().BeTrue(
            "the control inbox verifies X-Hub-Signature-256 over the raw body with the shared secret");
        using var doc = JsonDocument.Parse(delivery.Body);
        var root = doc.RootElement;
        root.GetProperty("event").GetString().Should().Be(SelfUpdateHandover.ReleaseEvent);
        root.GetProperty("deployment").GetString().Should().Be(Deployment, "the control plane routes by the record id");
        root.GetProperty("newVersion").GetString().Should().Be(CandidateTag);
        root.GetProperty("newImage").GetString().Should().EndWith($"/memex-portal-ai:{CandidateTag}");
        root.GetProperty("currentVersion").GetString().Should().Be(Installed);
        root.GetProperty("policy").GetString().Should().Be(nameof(UpdatePolicyKind.Continuous));
        root.GetProperty("trigger").GetString().Should().Be(nameof(SelfUpdateTrigger.Startup));

        content.LastCheckVerdict.Should().Contain("handed to the control lane").And.Contain(InboxUrl);
        // The verdict states the rule the control plane actually applies to a ROUTED roll
        // (Plugins ActionsExecutor.AdmittedUnattended): unattended under a Continuous record whose
        // pattern admits the tag. It must never tell an operator that a PIN restores unattended —
        // records do not pin an image (policy platform-backwards-compatibility).
        content.LastCheckVerdict.Should().Contain("runs UNATTENDED when the deployment record's update policy is Continuous")
            .And.NotContain("pinned tag", "no record pins an image; the old wording described a rule no longer in force");
        content.HandedOverTag.Should().Be(CandidateTag, "the Updates tab must say where the release went");
        content.HandedOverTo.Should().Be(InboxUrl);
        content.HandedOverAt.Should().NotBeNull();
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  The negatives — what proves the guard guards
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>The control: a chart that still renders the Role (canPatch true) keeps the in-pod
    /// roll and announces nothing — the transition is the CHART's, namespace by namespace.</summary>
    [Fact(Timeout = 240_000)]
    public async Task AChartThatStillAllowsSelfPatch_Patches_AndAnnouncesNothing()
    {
        await Seed(UpdatePolicyKind.Continuous, TestContext.Current.CancellationToken);
        var inbox = new FakeControlInbox(HttpStatusCode.OK);
        var updater = new RecordingUpdater();

        var content = await RunOneCheck(updater, inbox, chartCanPatch: true, PostSettings());

        updater.Tags.Should().Equal(new[] { CandidateTag }, "self-patch is still the apply while the chart allows it");
        inbox.Deliveries.Should().BeEmpty("a self-patching install must not also open a Roll — two writers");
        content.LastCheckVerdict.Should().Contain("applied update");
        content.HandedOverTag.Should().BeNull();
    }

    /// <summary>🚨 An inbox that refuses (the pairing drifted) is a FAILED hand-over that says so —
    /// the release is known, nothing is patched, and the next check announces again.</summary>
    [Fact(Timeout = 240_000)]
    public async Task AnInboxThatRefuses_IsAFailedHandover_NeverAPatch()
    {
        await Seed(UpdatePolicyKind.Continuous, TestContext.Current.CancellationToken);
        var inbox = new FakeControlInbox(HttpStatusCode.Unauthorized);
        var updater = new RecordingUpdater();

        var content = await RunOneCheck(updater, inbox, chartCanPatch: false, PostSettings());

        updater.Tags.Should().BeEmpty("a refused hand-over must not fall back to a self-patch");
        inbox.Deliveries.Should().HaveCount(1);
        content.LastCheckVerdict.Should().Contain("hand-over to the control lane FAILED")
            .And.Contain("401")
            .And.Contain(SelfUpdateHandover.SecretKey, "a 401 names the pairing to check, never a value");
        content.HandedOverTag.Should().BeNull("a refused announcement is not a hand-over");
    }

    /// <summary>🚨 An inbox that ACCEPTS but did not verify the signature ("not-required": the control
    /// instance declares no secret key for the target) is a failed hand-over too — the receiver may
    /// still drop the event, and a recorded success would be a delivery nobody checked (#3312).</summary>
    [Fact(Timeout = 240_000)]
    public async Task AnInboxThatAcceptsWithoutVerifying_IsAFailedHandover()
    {
        await Seed(UpdatePolicyKind.Continuous, TestContext.Current.CancellationToken);
        var inbox = new FakeControlInbox(HttpStatusCode.OK, "{\"status\":\"accepted\",\"signature\":\"not-required\"}");
        var updater = new RecordingUpdater();

        var content = await RunOneCheck(updater, inbox, chartCanPatch: false, PostSettings());

        updater.Tags.Should().BeEmpty();
        inbox.Deliveries.Should().HaveCount(1);
        content.LastCheckVerdict.Should().Contain("hand-over to the control lane FAILED")
            .And.Contain("not-required")
            .And.Contain(WebhookInbox.SecretConfigKeyName);
        content.HandedOverTag.Should().BeNull("an unverified delivery is not a hand-over");
    }

    /// <summary>🚨 A verified signature on a delivery the inbox did NOT accept is not a delivery: both
    /// halves of the answer are required before a hand-over is recorded.</summary>
    [Fact(Timeout = 240_000)]
    public async Task AnInboxThatVerifiesButDoesNotAccept_IsAFailedHandover()
    {
        await Seed(UpdatePolicyKind.Continuous, TestContext.Current.CancellationToken);
        var inbox = new FakeControlInbox(HttpStatusCode.OK, "{\"status\":\"rejected\",\"signature\":\"verified\"}");
        var updater = new RecordingUpdater();

        var content = await RunOneCheck(updater, inbox, chartCanPatch: false, PostSettings());

        updater.Tags.Should().BeEmpty();
        content.LastCheckVerdict.Should().Contain("hand-over to the control lane FAILED").And.Contain("rejected");
        content.HandedOverTag.Should().BeNull();
    }

    /// <summary>No control inbox and no self-patch: detect-only, and the verdict names the KEY that
    /// would make this a control-lane install — the state build sat in on 2026-09-12 with nothing
    /// saying why.</summary>
    [Fact(Timeout = 240_000)]
    public async Task NoControlInbox_IsDetectOnly_NamingTheMissingKey()
    {
        await Seed(UpdatePolicyKind.Continuous, TestContext.Current.CancellationToken);
        var inbox = new FakeControlInbox(HttpStatusCode.OK);
        var updater = new RecordingUpdater();

        var content = await RunOneCheck(updater, inbox, chartCanPatch: false,
            new SelfUpdateHandover.Settings(Deployment, null, false, false, false, null));

        updater.Tags.Should().BeEmpty();
        inbox.Deliveries.Should().BeEmpty();
        content.LastCheckVerdict.Should().Contain("detect-and-notify")
            .And.Contain(SelfUpdateHandover.UrlKey);
    }

    /// <summary>A landed module generation pending activation on a control-lane install is handed
    /// over as a RESTART — the unattended class on the control lane — and the pods are not rolled
    /// from inside the pod.</summary>
    [Fact(Timeout = 240_000)]
    public async Task APendingRestartOnAControlLaneInstall_IsHandedOverAsARestart()
    {
        await Seed(UpdatePolicyKind.Continuous, TestContext.Current.CancellationToken);
        using var root = ModuleRoot.WithPendingRestart();
        var inbox = new FakeControlInbox(HttpStatusCode.OK);
        var updater = new RecordingUpdater();

        var content = await RunOneCheck(updater, inbox, chartCanPatch: false, PostSettings(),
            registry: ["1.9.0-ci.0", Installed.Split('+')[0]], landing: root.Landing);

        updater.Restarts.Should().Be(0, "this install does not roll its own pods");
        updater.Tags.Should().BeEmpty();
        inbox.Deliveries.Should().HaveCount(1);
        using var doc = JsonDocument.Parse(inbox.Deliveries[0].Body);
        doc.RootElement.GetProperty("event").GetString().Should().Be(SelfUpdateHandover.RestartEvent);
        doc.RootElement.GetProperty("deployment").GetString().Should().Be(Deployment);
        content.LastCheckVerdict.Should().Contain("no newer release").And.Contain("handed to the control lane");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Harness
    // ══════════════════════════════════════════════════════════════════════════

    private static SelfUpdateHandover.Settings PostSettings() =>
        new(Deployment, InboxUrl, SecretPresent: true, LocalTargetListed: false, LocalSecretPresent: false,
            "https://unit.example");

    /// <summary>The network, substituted: records every delivery and answers one status.</summary>
    private sealed class FakeControlInbox(HttpStatusCode status, string? answer = null) : HttpMessageHandler
    {
        private ImmutableList<Delivery> deliveries = ImmutableList<Delivery>.Empty;

        public ImmutableList<Delivery> Deliveries => deliveries;

        public sealed record Delivery(string Url, string Body, string? Signature);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            request.Headers.TryGetValues(WebhookInbox.SignatureHeader, out var signatures);
            ImmutableInterlocked.Update(ref deliveries, current => current.Add(
                new Delivery(request.RequestUri!.ToString(), body, signatures?.FirstOrDefault())));
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(answer ?? (status == HttpStatusCode.OK
                    ? "{\"status\":\"accepted\",\"signature\":\"verified\"}"
                    : "")),
            };
        }
    }

    /// <summary>The hand-over with its two seams supplied: the settings (so no second mesh is stood
    /// up for a one-key difference) and the HTTP client (the fake inbox). The secret the POST signs
    /// with is read from the mesh's configuration, exactly as in production.</summary>
    private sealed class SeamedHandover(IMessageHub hub, SelfUpdateHandover.Settings settings, HttpMessageHandler inbox)
        : SelfUpdateHandover(hub, null, new HttpClient(inbox))
    {
        public override Settings ReadSettings() => settings;
    }

    private sealed class FakeAcrTagLister(IReadOnlyList<string> tags) : IAcrTagLister
    {
        public Task<IReadOnlyList<string>> ListTagsAsync(string repository, CancellationToken ct) =>
            Task.FromResult(tags);
    }

    /// <summary>The k8s seam — an updater that CAN patch (the AKS module is still installed), so
    /// that only the chart's declaration decides.</summary>
    private sealed class RecordingUpdater : IDeploymentUpdater
    {
        private ImmutableList<string> tags = ImmutableList<string>.Empty;
        private int restarts;

        public ImmutableList<string> Tags => tags;
        public int Restarts => Volatile.Read(ref restarts);
        public bool CanPatch => true;

        public Task<DateTimeOffset?> LastRolledAtAsync(CancellationToken ct) => Task.FromResult<DateTimeOffset?>(null);

        public Task PatchToVersionAsync(string versionTag, CancellationToken ct)
        {
            ImmutableInterlocked.Update(ref tags, current => current.Add(versionTag));
            return Task.CompletedTask;
        }

        public Task<bool> RestartAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref restarts);
            return Task.FromResult(true);
        }
    }

    private sealed class AlwaysAvailable(IMessageHub hub, IConfiguration configuration)
        : ReleaseAvailabilityService(hub, configuration)
    {
        public override IObservable<UpdatabilityVerdict> IsUpdatable(string? targetVersion) =>
            Observable.Return(UpdatabilityVerdict.NotEnforced("this test host consumes no CI bakes"));
    }

    /// <summary>A module root in a temp directory carrying the REAL pending-restart marker.</summary>
    private sealed class ModuleRoot : IDisposable
    {
        private readonly string directory;

        public ModuleLandingService Landing { get; }

        private ModuleRoot()
        {
            directory = Path.Combine(Path.GetTempPath(), "mw-4098-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            ModuleActivationSidecar.SetPendingRestart(directory, true);
            Landing = new ModuleLandingService(logger: null, baseDirectory: directory);
        }

        public static ModuleRoot WithPendingRestart() => new();

        public void Dispose()
        {
            Landing.Dispose();
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // A temp directory that outlives the test is harmless.
            }
        }
    }

    private sealed class SeamedSelfUpdateService(
        IMessageHub hub,
        IAcrTagLister acr,
        IDeploymentUpdater updater,
        SelfUpdateOptions options,
        ILogger<SelfUpdateHostedService>? logger,
        ReleaseAvailabilityService gate,
        SelfUpdateHandover handover,
        IConfiguration configuration,
        ModuleLandingService? landing)
        : SelfUpdateHostedService(hub, acr, updater, options, logger)
    {
        public IObservable<Unit> Evaluations => ChecksReported;

        protected override ReleaseAvailabilityService? ResolveAvailabilityGate() => gate;

        protected override ComboVerificationGate? ResolveComboGate() => null;

        protected override SelfUpdateHandover ResolveHandover() => handover;

        protected override IConfiguration? ResolveConfiguration() => configuration;

        protected override ModuleLandingService? ResolveLandingService() => landing;
    }

    private static SelfUpdateOptions FastPoll(bool chartCanPatch) => new()
    {
        RetryInterval = TimeSpan.FromMilliseconds(500),
        EventCoalesceWindow = TimeSpan.FromMilliseconds(50),
        DefaultPolicy = UpdatePolicyKind.Continuous,
        DefaultPattern = "*-ci*",
        CanPatch = chartCanPatch,
        Registry = "cr.example",
    };

    private async Task<UpdatePolicyContent> RunOneCheck(
        IDeploymentUpdater updater,
        FakeControlInbox inbox,
        bool chartCanPatch,
        SelfUpdateHandover.Settings settings,
        IReadOnlyList<string>? registry = null,
        ModuleLandingService? landing = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var configuration = Mesh.ServiceProvider.GetRequiredService<IConfiguration>();
        var handover = new SeamedHandover(Mesh, settings, inbox);
        var service = new SeamedSelfUpdateService(
            Mesh, new FakeAcrTagLister(registry ?? [CandidateTag]), updater, FastPoll(chartCanPatch),
            Mesh.ServiceProvider.GetService<ILogger<SelfUpdateHostedService>>(),
            new AlwaysAvailable(Mesh, new ConfigurationBuilder().Build()),
            handover, configuration, landing);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await service.Evaluations.FirstAsync().Timeout(Budget).Await(ct);
            return await WaitForContent(c => c.LastCheckVerdict is not null);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private Task Seed(UpdatePolicyKind policy, CancellationToken cancellationToken)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var node = new MeshNode(UpdatePolicyNodeType.NodeId, UpdatePolicyNodeType.AdminPartition)
        {
            NodeType = UpdatePolicyNodeType.NodeType,
            Name = "Update Policy",
            State = MeshNodeState.Active,
            Content = new UpdatePolicyContent
            {
                Policy = policy,
                Pattern = policy == UpdatePolicyKind.Continuous ? "*-ci*" : null,
            },
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

    private Task<UpdatePolicyContent> WaitForContent(Func<UpdatePolicyContent, bool> predicate) =>
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
            .Await(TestContext.Current.CancellationToken);
}
