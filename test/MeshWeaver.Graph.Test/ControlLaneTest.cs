#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Graph.ControlLane;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The control→instance lane end to end, over TWO meshes (Doc/Architecture/ControlLane): THIS test's
/// mesh is the CONTROL instance — it holds the target's own key as
/// <c>Hosting:PlatformWebhookSecret:lane-target</c> and receives reports in a real, signed webhook
/// inbox — and <see cref="TargetMesh"/> is the TARGET, with <c>ControlLane:Key</c> armed and the
/// platform's operations registered. Requests travel through <see cref="ControlLaneClient.Send"/>
/// into the target's <see cref="ControlLaneReceiver"/> (the same code the HTTP endpoint calls), and
/// reports travel back through <see cref="WebhookInbox.Deliver"/> on the control mesh, where they
/// must verify as the target's OWN key.
///
/// <para>🚨 The negatives are the point: a bad signature, a replay, an expired request, a request
/// for another deployment and a plan that is not the approved one must each touch NOTHING.</para>
/// </summary>
public class ControlLaneTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Deployment = "lane-target";
    private const string LaneKey = "lane-target-own-key-7f3c1b9e";
    private const string FleetSecret = "the-fleet-wide-inbox-secret";
    private const string OtherDeploymentKey = "another-deployments-own-key";
    private const string ControlInbox = "TestData/LaneInbox";
    private const string ControlAction = "Ops/Actions/lane-test";

    private static readonly Uri Endpoint = ControlLaneClient.EndpointOf("lane-target.example")!;

    private TargetMesh? target;

    /// <summary>Every report the control inbox ACCEPTED and verified as the target's own key.</summary>
    private readonly ReplaySubject<ControlLaneReport> reports = new();

    private static IConfiguration ControlConfiguration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [ControlLaneKeys.ControlKeySection] = FleetSecret,
            [ControlLaneKeys.ControlKeyOf(Deployment)] = LaneKey,
            [ControlLaneKeys.ControlKeyOf("other-deployment")] = OtherDeploymentKey,
            ["WebhookInbox:Targets:0"] = ControlInbox,
            ["WebhookInbox:Targets:0:SecretConfigKey"] = ControlLaneKeys.ControlKeySection,
        }).Build();

    private static IConfiguration TargetConfiguration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [ControlLaneKeys.DeploymentKey] = Deployment,
            [ControlLaneKeys.TargetKey] = LaneKey,
            // The target's OWN inbox verifies with the fleet secret — the shape of a fleet portal.
            [ControlLaneKeys.ControlKeySection] = FleetSecret,
            ["WebhookInbox:Targets:0"] = "TestData/OwnInbox",
            ["WebhookInbox:Targets:0:SecretConfigKey"] = ControlLaneKeys.ControlKeySection,
        }).Build();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddWebhookInbox()
            .AddControlLane()
            .ConfigureServices(services => services
                .AddSingleton(ControlConfiguration())
                .AddSingleton<IControlLaneTransport>(new InProcessTransport(this)));

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        await AsSystem(Mesh, () => MeshQuery.CreateOrUpdateNode(new MeshNode(ControlInbox)
        {
            Name = "Lane inbox", NodeType = "Markdown", Content = new MarkdownContent { Content = "# inbox" },
        }));
        target = new TargetMesh(Output, this);
        await target.InitializeAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        if (target is not null)
            await target.DisposeAsync();
        reports.Dispose();
        await base.DisposeAsync();
    }

    // ───────────────────────────── the happy paths ─────────────────────────────

    [Fact(Timeout = 120000)]
    public async Task DeleteSpace_DryRunThenApprovedPlan_DeletesTheSpaceOnTheTarget_AndReportsSigned()
    {
        var ct = TestContext.Current.CancellationToken;
        await target!.SeedSpace("Doomed");

        var dry = Request(ControlLaneOperation.DeleteSpace, "Doomed", dryRun: true);
        (await Send(dry, ct)).Verdict.Should().Be(ControlLaneVerdict.Accepted);
        var planned = await Terminal(dry, ct);
        planned.Status.Should().Be(ControlLaneStatus.Planned, planned.Message);
        planned.Plan.Should().NotBeNull();
        planned.Plan!.Steps.Select(s => s.Name).Should().Contain("Tear down the partition",
            "a space's content goes with its store in ONE teardown, never a per-node delete (policy governed-action-preflight)");
        planned.Plan.Steps.Select(s => s.Name).Should().NotContain("Delete content");
        planned.PlanDigest.Should().Be(planned.Plan.Digest());
        (await target.Exists("Doomed/Page")).Should().BeTrue("a dry run changes nothing");

        var real = Request(ControlLaneOperation.DeleteSpace, "Doomed", dryRun: false, planned.PlanDigest);
        (await Send(real, ct)).Verdict.Should().Be(ControlLaneVerdict.Accepted);
        var done = await Terminal(real, ct);
        done.Status.Should().Be(ControlLaneStatus.Done, done.Message);
        (await ReportsOf(real, ct)).Should().Contain(r => r.Status == ControlLaneStatus.Progress && r.Message.Contains("[DeleteSpace]"),
            "the verified deletion's audit line travels back to the control action");

        (await target.Exists("Doomed/Page")).Should().BeFalse();
        (await target.Exists("Doomed")).Should().BeFalse();
        var ledger = await target.Ledger(real.RequestId);
        ledger.Should().NotBeNull("the target keeps its own audit record of every request");
        ledger!.Status.Should().Be(ControlLaneStatus.Done);
        ledger.Request.ApprovedBy.Should().Be("approver@example.com");
        ledger.Log.Should().Contain(l => l.Contains("[DeleteSpace]"));
    }

    [Fact(Timeout = 120000)]
    public async Task Recycle_ThroughTheLane_RecyclesTheAddressOnTheTarget()
    {
        var ct = TestContext.Current.CancellationToken;
        await target!.SeedPage("TestData/Recyclable");

        var dry = Request(ControlLaneOperation.Recycle, "TestData/Recyclable", dryRun: true);
        (await Send(dry, ct)).Verdict.Should().Be(ControlLaneVerdict.Accepted);
        var planned = await Terminal(dry, ct);
        planned.Status.Should().Be(ControlLaneStatus.Planned, planned.Message);

        var real = Request(ControlLaneOperation.Recycle, "TestData/Recyclable", dryRun: false, planned.PlanDigest);
        (await Send(real, ct)).Verdict.Should().Be(ControlLaneVerdict.Accepted);
        var done = await Terminal(real, ct);
        done.Status.Should().Be(ControlLaneStatus.Done, done.Message);
        (await ReportsOf(real, ct)).Should().Contain(r => r.Message.Contains("FRESH activation"));
    }

    // ───────────────────────────── the negatives ─────────────────────────────

    [Fact(Timeout = 120000)]
    public async Task ABadSignature_IsRefused_AndRecordsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await target!.SeedSpace("Kept1");
        var request = Request(ControlLaneOperation.DeleteSpace, "Kept1", dryRun: true);
        var body = ControlLaneWire.Body(request);

        foreach (var wrongKey in new[] { "not-the-key", FleetSecret, OtherDeploymentKey })
        {
            var receipt = await target.Receiver.Receive(body, ControlLaneWire.Sign(body, wrongKey))
                .FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
            receipt.Verdict.Should().Be(ControlLaneVerdict.SignatureInvalid,
                "only this deployment's own key commands it — not the fleet secret, not another deployment's key");
        }
        var unsigned = await target.Receiver.Receive(body, null)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
        unsigned.Verdict.Should().Be(ControlLaneVerdict.SignatureInvalid);

        (await target.Ledger(request.RequestId)).Should().BeNull("a refused delivery leaves no record behind");
        (await target.Exists("Kept1/Page")).Should().BeTrue();
    }

    [Fact(Timeout = 120000)]
    public async Task AReplayedRequest_IsRefused_TheSecondTime()
    {
        var ct = TestContext.Current.CancellationToken;
        await target!.SeedSpace("Kept2");
        var request = Request(ControlLaneOperation.DeleteSpace, "Kept2", dryRun: true);

        (await Send(request, ct)).Verdict.Should().Be(ControlLaneVerdict.Accepted);
        var replay = await Send(request, ct);
        replay.Verdict.Should().Be(ControlLaneVerdict.Replayed, replay.Why ?? "");
        replay.Why.Should().Contain("409");
        (await Terminal(request, ct)).Status.Should().Be(ControlLaneStatus.Planned);
    }

    [Fact(Timeout = 120000)]
    public async Task AnExpiredRequest_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await target!.SeedSpace("Kept3");
        var now = DateTimeOffset.UtcNow;
        var expired = Request(ControlLaneOperation.DeleteSpace, "Kept3", dryRun: true) with
        {
            IssuedAt = now.AddMinutes(-12),
            ExpiresAt = now.AddMinutes(-2),
        };
        var tooLong = Request(ControlLaneOperation.DeleteSpace, "Kept3", dryRun: true) with
        {
            ExpiresAt = now.AddHours(2),
        };

        (await Send(expired, ct)).Verdict.Should().Be(ControlLaneVerdict.Expired);
        (await Send(tooLong, ct)).Verdict.Should().Be(ControlLaneVerdict.Expired,
            "a request is valid for at most the lane's maximum lifetime, however it is signed");
        (await target.Ledger(expired.RequestId)).Should().BeNull();
        (await target.Exists("Kept3/Page")).Should().BeTrue();
    }

    [Fact(Timeout = 120000)]
    public async Task APlanThatIsNotTheApprovedOne_IsRefused_AndTouchesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await target!.SeedSpace("Kept4");
        var request = Request(ControlLaneOperation.DeleteSpace, "Kept4", dryRun: false,
            "sha256:" + new string('0', 64));

        (await Send(request, ct)).Verdict.Should().Be(ControlLaneVerdict.Accepted,
            "the request is authentic — it is the PLAN that does not match");
        var outcome = await Terminal(request, ct);
        outcome.Status.Should().Be(ControlLaneStatus.Refused);
        outcome.Message.Should().Contain("is not the plan that was approved");
        (await target.Exists("Kept4/Page")).Should().BeTrue("a plan mismatch touches nothing");
        (await target.Ledger(request.RequestId))!.Status.Should().Be(ControlLaneStatus.Refused);
    }

    [Fact(Timeout = 120000)]
    public async Task ARequestForAnotherDeployment_OrWithoutApproval_OrForAProtectedPartition_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var elsewhere = Request(ControlLaneOperation.Recycle, "TestData", dryRun: true) with { Deployment = "other-deployment" };
        var body = ControlLaneWire.Body(elsewhere);
        (await target!.Receiver.Receive(body, ControlLaneWire.Sign(body, LaneKey))
                .FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken))
            .Verdict.Should().Be(ControlLaneVerdict.WrongDeployment);

        (await Send(Request(ControlLaneOperation.DeleteSpace, "Admin", dryRun: false, "sha256:" + new string('a', 64)) with
            { ApprovedBy = null }, ct))
            .Verdict.Should().Be(ControlLaneVerdict.Refused, "a real run needs an approver");
        (await Send(Request("Shell", "TestData", dryRun: true), ct))
            .Verdict.Should().Be(ControlLaneVerdict.Refused, "an operation no registered executor claims is refused by name");
        (await Send(Request(ControlLaneOperation.DeleteSpace, "Kept5", dryRun: true) with { Confirmation = "kept5" }, ct))
            .Verdict.Should().Be(ControlLaneVerdict.Refused, "a space deletion names its target twice, exactly");

        var admin = Request(ControlLaneOperation.DeleteSpace, "Admin", dryRun: true);
        (await Send(admin, ct)).Verdict.Should().Be(ControlLaneVerdict.Accepted);
        var refused = await Terminal(admin, ct);
        refused.Status.Should().Be(ControlLaneStatus.Refused);
        refused.Message.Should().Contain("system or fleet partition");
    }

    // ───────────────────────────── the keys (pure) ─────────────────────────────

    [Fact]
    public void TheLane_RefusesToArm_WithoutItsOwnKey_OrWithAnInboxSecret()
    {
        ControlLaneKeys.ArmingRefusal(TargetConfiguration()).Should().BeNull();
        ControlLaneKeys.ArmingRefusal(Config((ControlLaneKeys.DeploymentKey, Deployment)))
            .Should().Contain(ControlLaneKeys.TargetKey);
        ControlLaneKeys.ArmingRefusal(Config((ControlLaneKeys.TargetKey, LaneKey)))
            .Should().Contain(ControlLaneKeys.DeploymentKey);
        ControlLaneKeys.ArmingRefusal(Config(
                (ControlLaneKeys.DeploymentKey, Deployment), (ControlLaneKeys.TargetKey, FleetSecret),
                ("WebhookInbox:Targets:0", "Hosting/PlatformBuilds"),
                ("WebhookInbox:Targets:0:SecretConfigKey", ControlLaneKeys.ControlKeySection),
                (ControlLaneKeys.ControlKeySection, FleetSecret)))
            .Should().Contain("REFUSES to arm", "a lane armed with an inbox secret would take commands from every sender of it");
    }

    [Fact]
    public void TheControlSide_SignsOnlyWithTheDeploymentsOwnKey()
    {
        ControlLaneKeys.ControlKeyFor(ControlConfiguration(), Deployment).Key.Should().Be(LaneKey);
        ControlLaneKeys.ControlKeyFor(ControlConfiguration(), "never-provisioned").Refusal
            .Should().Contain("never the fleet-wide inbox secret");
        ControlLaneKeys.ControlKeyFor(Config(
                (ControlLaneKeys.ControlKeySection, FleetSecret), (ControlLaneKeys.ControlKeyOf(Deployment), FleetSecret)),
                Deployment).Refusal
            .Should().Contain("equals the fleet-wide inbox secret");
    }

    [Fact]
    public void TwoExecutorsClaimingOneOperation_AreRefusedAtConstruction()
    {
        var act = () => new ControlLaneReceiver(Mesh, [new DeleteSpaceOperation(), new DeleteSpaceOperation()],
            new InboxSink(this), Microsoft.Extensions.Logging.Abstractions.NullLogger<ControlLaneReceiver>.Instance);
        var refused = Record.Exception(act);
        refused.Should().BeOfType<InvalidOperationException>();
        refused!.Message.Should().Contain("claimed by 2 executors");
    }

    [Fact]
    public void TheRecord_MustClaimTheLaneKey_AndOneKeySlotPerDeployment()
    {
        ControlLaneKeys.BindingRefusal(Deployment, null, null).Should().Contain("declares no controlLaneKeySecret");
        ControlLaneKeys.BindingRefusal(Deployment, "x-ControlLaneKey", null).Should().BeNull();
        ControlLaneKeys.BindingRefusal(Deployment, "x-Key", "x-Key").Should().BeNull();
        ControlLaneKeys.BindingRefusal(Deployment, "x-ControlLaneKey", "x-AnnouncementKey")
            .Should().Contain("ONE key per deployment");
    }

    [Fact]
    public void AReport_CannotBeReadAsARequest_AndVerifiesOnlyWithItsOwnDeploymentsKey()
    {
        var report = new ControlLaneReport
        {
            Deployment = Deployment, RequestId = ControlLaneClient.NewRequestId(), Action = ControlAction,
            Operation = ControlLaneOperation.DeleteSpace, Target = "X", Status = ControlLaneStatus.Done, At = DateTimeOffset.UtcNow,
        };
        var body = ControlLaneWire.Body(report);
        ControlLaneWire.ParseRequest(body).Should().BeNull("one key serves both directions, so the body says which it is");
        ControlLaneWire.ParseReport(ControlLaneWire.Body(Request(ControlLaneOperation.Recycle, "X", true))).Should().BeNull();

        ControlLaneClient.VerifyReport(ControlConfiguration(), ControlLaneWire.Sign(body, LaneKey), body).Report.Should().NotBeNull();
        ControlLaneClient.VerifyReport(ControlConfiguration(), ControlLaneWire.Sign(body, FleetSecret), body).Refusal
            .Should().Contain("does not verify with that deployment's own key");
        ControlLaneClient.VerifyReport(ControlConfiguration(), ControlLaneWire.Sign(body, OtherDeploymentKey), body).Refusal
            .Should().Contain("does not verify with that deployment's own key");

        var drifted = report with
        {
            Plan = ControlLanePlan.Of(ControlLaneOperation.DeleteSpace, Deployment, [("Step", "command", true)]),
            PlanDigest = "sha256:" + new string('b', 64),
        };
        var driftedBody = ControlLaneWire.Body(drifted);
        ControlLaneClient.VerifyReport(ControlConfiguration(), ControlLaneWire.Sign(driftedBody, LaneKey), driftedBody).Refusal
            .Should().Contain("drifted");
    }

    [Fact]
    public void ThePlanDigest_IsTheActionPlanV1Encoding()
    {
        // Pinned against the in-mesh ActionPlanSnapshot.Digest (MeshWeaver.Plugins) for the same inputs:
        // kind, deployment, no namespace, no image, executor, one step. Both sides must agree byte for byte.
        var plan = ControlLanePlan.Of("Recycle", "memex-cloud", [("Recycle", "hub.RecycleNode(\"A\", reason) as system", false)]);
        plan.Digest().Should().Be(Sha("action-plan/v1;7:Recycle;11:memex-cloud;~;~;12:control-lane;1:1;1:1;4:safe;7:Recycle;"
                                      + "38:hub.RecycleNode(\"A\", reason) as system;"));
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private static string Sha(string text) =>
        "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    private static ControlLaneRequest Request(string operation, string targetPath, bool dryRun, string? digest = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new ControlLaneRequest
        {
            Kind = ControlLaneRequest.RequestKind,
            RequestId = ControlLaneClient.NewRequestId(),
            Deployment = Deployment,
            Operation = operation,
            Target = targetPath,
            Confirmation = targetPath,
            DryRun = dryRun,
            PlanDigest = digest,
            Reason = "control lane test",
            RequestedBy = "requester@example.com",
            ApprovedBy = dryRun ? null : "approver@example.com",
            ApprovedAt = dryRun ? null : now,
            Action = ControlAction,
            IssuedAt = now,
            ExpiresAt = now + ControlLaneClient.DefaultLifetime,
        };
    }

    private Task<ControlLaneReceipt> Send(ControlLaneRequest request, CancellationToken ct) =>
        ControlLaneClient.Send(Mesh, request, Endpoint)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

    private Task<ControlLaneReport> Terminal(ControlLaneRequest request, CancellationToken ct) =>
        reports.Where(r => r.RequestId == request.RequestId && ControlLaneStatus.IsTerminal(r.Status, request.DryRun))
            .FirstAsync().Timeout(TimeSpan.FromSeconds(60)).Await(ct);

    private Task<IList<ControlLaneReport>> ReportsOf(ControlLaneRequest request, CancellationToken ct) =>
        reports.Where(r => r.RequestId == request.RequestId).TakeUntil(r => ControlLaneStatus.IsTerminal(r.Status, request.DryRun))
            .ToList().Timeout(TimeSpan.FromSeconds(60)).Await(ct);

    // The work is COMPOSED inside the system scope: a write primitive captures the caller's identity
    // when it is called, not when it is subscribed.
    private static Task<T> AsSystem<T>(IMessageHub hub, Func<IObservable<T>> work) =>
        hub.ServiceProvider.GetRequiredService<AccessService>().RunAsSystem(work)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

    /// <summary>Hands the signed body to the TARGET's receiver and answers exactly as the endpoint does.</summary>
    private sealed class InProcessTransport(ControlLaneTest test) : IControlLaneTransport
    {
        public IObservable<ControlLaneAnswer> Post(Uri endpoint, string body, string signature) =>
            test.target!.Receiver.Receive(body, signature)
                .Select(receipt => ControlLaneReceiver.Answer(receipt, TargetConfiguration()));
    }

    /// <summary>Delivers the target's reports into the CONTROL mesh's real, signed webhook inbox.</summary>
    private sealed class InboxSink(ControlLaneTest test) : IControlLaneReportSink
    {
        public IObservable<System.Reactive.Unit> Send(IMessageHub hub, ControlLaneReport report, string body, string signature)
        {
            var configuration = ControlConfiguration();
            return WebhookInbox.Deliver(test.Mesh, WebhookInbox.ReadTargets(configuration), ControlInbox, "application/json",
                    [new(ControlLaneWire.SignatureHeader, signature)], body)
                .Take(1)
                .Select(result =>
                {
                    if (result.Status != WebhookInbox.DeliveryStatus.Accepted || !result.SignatureVerified
                        || result.SenderKey != Deployment)
                        throw new InvalidOperationException(
                            $"the control inbox did not accept the report as '{Deployment}''s own: {result.Status}, sender {result.SenderKey}");
                    var (verified, refusal) = ControlLaneClient.VerifyReport(configuration, signature, body);
                    if (verified is null)
                        throw new InvalidOperationException(refusal);
                    test.reports.OnNext(verified);
                    return System.Reactive.Unit.Default;
                });
        }
    }

    /// <summary>The TARGET instance: a second, independent mesh with the lane armed.</summary>
    private sealed class TargetMesh(ITestOutputHelper output, ControlLaneTest control) : MonolithMeshTestBase(output)
    {
        protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
            => base.ConfigureMesh(builder)
                .AddWebhookInbox()
                .AddControlLane()
                .ConfigureServices(services => services
                    .AddSingleton(TargetConfiguration())
                    .AddSingleton<IControlLaneReportSink>(new InboxSink(control)));

        public ControlLaneReceiver Receiver => Mesh.ServiceProvider.GetRequiredService<ControlLaneReceiver>();

        public async Task SeedSpace(string space)
        {
            await SeedTopLevel(new MeshNode(space) { Name = space, NodeType = "Space" });
            await SeedPage($"{space}/Page");
        }

        public Task SeedPage(string path) =>
            AsSystem(Mesh, () => MeshQuery.CreateOrUpdateNode(new MeshNode(path)
            {
                Name = path, NodeType = "Markdown", Content = new MarkdownContent { Content = "# page" },
            }));

        public async Task<bool> Exists(string path)
        {
            var reading = await AsSystem(Mesh, () => MeshReading.Read(MeshQuery, $"path:{path} limit:1"));
            reading.IsAnswer.Should().BeTrue(reading.WhyNotAnAnswer ?? "");
            return reading.Rows.Any(r => r.Path == path);
        }

        public async Task<ControlLaneRecord?> Ledger(string requestId)
        {
            var path = ControlLaneAdmission.LedgerPath(requestId);
            var reading = await AsSystem(Mesh, () => MeshReading.Read(MeshQuery, $"path:{path} limit:1"));
            return reading.Rows.FirstOrDefault(r => r.Path == path)?.ContentAs<ControlLaneRecord>(Mesh.JsonSerializerOptions);
        }
    }
}
