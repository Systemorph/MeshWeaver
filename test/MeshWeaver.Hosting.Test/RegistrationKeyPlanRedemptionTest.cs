using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🎟️ <b>A registration key raises an ALREADY-REGISTERED instance's plan — upgrade only.</b>
///
/// <para>A fleet machine registers OPEN (free) the moment it is installed; its enrolment hands it the
/// plan-bearing key only afterwards. The registration lane ignored that key once a credential was
/// stored, so every fleet machine came up on <c>free</c>, refused the enterprise Hosting package, and a
/// global administrator raised its plan by hand (twice in one week). The redemption applies the key's
/// plan to the existing instance: raises, never lowers, spends the key, records who changed the plan.</para>
/// </summary>
public class RegistrationKeyPlanRedemptionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Minter = "fleet-key-minter";

    private static readonly PlanTierRanks Ladder = PlanTierRanks.From(new[]
    {
        ("free", 0, false), ("personal", 10, false), ("pro", 20, false),
        ("dedicated", 25, true), ("enterprise", 30, false),
    });

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder).AddPluginCatalog();

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private InstancePlanService Plans => Mesh.ServiceProvider.GetRequiredService<InstancePlanService>();

    // ───────────────────────── the pure upgrade rule ─────────────────────────

    [Theory]
    [InlineData(null, "enterprise", PlanRedemptionOutcome.Upgraded)]
    [InlineData("", "pro", PlanRedemptionOutcome.Upgraded)]
    [InlineData("free", "enterprise", PlanRedemptionOutcome.Upgraded)]
    [InlineData("pro", "enterprise", PlanRedemptionOutcome.Upgraded)]
    [InlineData("pro", "dedicated", PlanRedemptionOutcome.Upgraded)]
    [InlineData("enterprise", "dedicated", PlanRedemptionOutcome.Upgraded)]
    [InlineData("gold", "personal", PlanRedemptionOutcome.Upgraded)]
    [InlineData("enterprise", "enterprise", PlanRedemptionOutcome.Unchanged)]
    [InlineData("Enterprise", "enterprise", PlanRedemptionOutcome.Unchanged)]
    [InlineData(null, "free", PlanRedemptionOutcome.Unchanged)]
    [InlineData("dedicated", "dedicated", PlanRedemptionOutcome.Unchanged)]
    [InlineData("enterprise", "pro", PlanRedemptionOutcome.RefusedLower)]
    [InlineData("pro", "free", PlanRedemptionOutcome.RefusedLower)]
    [InlineData("dedicated", "enterprise", PlanRedemptionOutcome.RefusedLower)]
    [InlineData("free", null, PlanRedemptionOutcome.RefusedNoPlan)]
    [InlineData("free", "  ", PlanRedemptionOutcome.RefusedNoPlan)]
    [InlineData("free", "platinum", PlanRedemptionOutcome.RefusedNoPlan)]
    public void TheUpgradeRule_RaisesNeverLowers(string? instancePlan, string? keyPlan, PlanRedemptionOutcome expected)
    {
        var (outcome, reason) = RegistrationKeyPlanUpgrade.Decide(instancePlan, keyPlan, Ladder);
        Assert.Equal(expected, outcome);
        Assert.False(string.IsNullOrWhiteSpace(reason), "every decision names its reason for the log");
    }

    [Fact]
    public void WithNoLadder_NoKeyRaisesAnything()
    {
        var (outcome, _) = RegistrationKeyPlanUpgrade.Decide("free", "enterprise", PlanTierRanks.Empty);
        Assert.Equal(PlanRedemptionOutcome.RefusedNoPlan, outcome);
    }

    // ───────────────────────── the instance side: when is a redemption owed ─────────────────────────

    [Fact]
    public void ARedemption_IsOwedOnce_ForAKeyTheCredentialHasNotPresented()
    {
        var key = RegistrationKeys.Generate();
        var stored = new PluginRegistryCredential { InstanceId = "fleet-mac", Plan = "free" };

        Assert.True(InstanceAutoRegistrationService.OwesPlanRedemption(key, stored),
            "a registered installation with a key it never presented redeems it");
        Assert.True(InstanceAutoRegistrationService.OwesPlanRedemption($"  {key} ", stored), "the key is trimmed");
        Assert.True(InstanceAutoRegistrationService.OwesPlanRedemption(key,
                stored with { PresentedKeyHash = InstanceKeys.Hash(RegistrationKeys.Generate()) }),
            "a NEW key after an earlier one is owed too");
        Assert.False(InstanceAutoRegistrationService.OwesPlanRedemption(key,
                stored with { PresentedKeyHash = InstanceKeys.Hash(key) }),
            "the key the credential records as presented is never presented again");
        Assert.False(InstanceAutoRegistrationService.OwesPlanRedemption(key, null),
            "no credential: the key REGISTERS the installation, it is not redeemed");
        Assert.False(InstanceAutoRegistrationService.OwesPlanRedemption(null, stored), "no key: nothing to redeem");
        Assert.False(InstanceAutoRegistrationService.OwesPlanRedemption("", stored), "a blank key: nothing to redeem");
        Assert.False(InstanceAutoRegistrationService.OwesPlanRedemption(InstanceKeys.Generate(), stored),
            "an instance key (mwi_) is not a registration key");
    }

    // ───────────────────────── the registry side: redeeming against a real record ─────────────────────────

    private async Task<string> RegisteredInstance(string instanceId, string? plan)
    {
        var ct = TestContext.Current.CancellationToken;
        var path = $"{TestPartition}/{MeshWeaverInstanceNodeType.NodeType}/{instanceId}";
        await Access.RunAsSystem(() => NodeFactory.CreateNode(MeshNode.FromPath(path) with
            {
                NodeType = MeshWeaverInstanceNodeType.NodeType,
                Content = new MeshWeaverInstance { InstanceId = instanceId, Plan = plan },
            }))
            .Should().Within(TestTimeouts.Convergence)
            .Emit($"seeding {path}", cancellationToken: ct);
        return path;
    }

    private Task<RegistrationKeyMintResult> Mint(string? plan, int? maxUsages = 1, DateTimeOffset? expiresAt = null) =>
        Mesh.ServiceProvider.GetRequiredService<RegistrationKeyService>()
            .Mint(Minter, Minter, "", "fleet enrolment", expiresAt, plan, maxUsages)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("minting a registration key", cancellationToken: TestContext.Current.CancellationToken);

    private Task<PlanRedemption> Redeem(string path, string rawKey) =>
        Plans.RedeemRegistrationKey(path, rawKey, Ladder)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("a redemption always reaches an outcome", cancellationToken: TestContext.Current.CancellationToken);

    private Task<MeshWeaverInstance?> Record(string path) =>
        Access.RunAsSystem(() => Mesh.GetMeshNode(path, TimeSpan.FromSeconds(10)).Take(1))
            .Select(node => node?.ContentAs<MeshWeaverInstance>(Mesh.JsonSerializerOptions))
            .Should().Within(TestTimeouts.Convergence)
            .Emit($"reading {path}", cancellationToken: TestContext.Current.CancellationToken);

    private Task<RegistrationKey?> KeyRecord(string keyPath) =>
        Access.RunAsSystem(() => Mesh.GetMeshNode(keyPath, TimeSpan.FromSeconds(10)).Take(1))
            .Select(node => node?.ContentAs<RegistrationKey>(Mesh.JsonSerializerOptions))
            .Should().Within(TestTimeouts.Convergence)
            .Emit($"reading {keyPath}", cancellationToken: TestContext.Current.CancellationToken);

    /// <summary>THE FLEET CASE: an instance registered OPEN on free redeems a single-use enterprise key.
    /// It moves to enterprise, the change names the key, the key is spent — and a second redemption of
    /// the same key is refused like any spent coupon.</summary>
    [Fact]
    public async Task AFreeInstance_RedeemsASingleUseKey_AndMovesUp_AndTheKeyIsSpent()
    {
        var path = await RegisteredInstance("fleet-mac-free", "free");
        var key = await Mint("enterprise");

        var first = await Redeem(path, key.RawKey);
        Assert.Equal(PlanRedemptionOutcome.Upgraded, first.Outcome);
        Assert.True(first.Applied);
        Assert.Equal("enterprise", first.Plan);

        var record = await Record(path);
        Assert.Equal("enterprise", record?.Plan);
        Assert.NotNull(record?.PlanSetAt);
        Assert.Equal($"registration key {key.Node.Path}", record?.PlanSetVia);
        Assert.Equal(1, (await KeyRecord(key.Node.Path))?.UsageCount);

        var again = await Redeem(path, key.RawKey);
        Assert.Equal(PlanRedemptionOutcome.InvalidKey, again.Outcome);
        Assert.False(again.Applied);
        Assert.Equal("enterprise", (await Record(path))?.Plan);
    }

    /// <summary>A key for a LOWER plan never lowers the instance and is not used — it stays valid for
    /// the instance it was minted for.</summary>
    [Fact]
    public async Task ALowerKey_IsRefused_AndNeitherThePlanNorTheKeyChanges()
    {
        var path = await RegisteredInstance("fleet-mac-enterprise", "enterprise");
        var key = await Mint("pro");

        var answer = await Redeem(path, key.RawKey);
        Assert.Equal(PlanRedemptionOutcome.RefusedLower, answer.Outcome);
        Assert.False(answer.Applied);

        var record = await Record(path);
        Assert.Equal("enterprise", record?.Plan);
        Assert.Null(record?.PlanSetVia);
        Assert.Equal(0, (await KeyRecord(key.Node.Path))?.UsageCount);
    }

    /// <summary>An instance already on the key's plan (a plan an admin raised by hand before the key
    /// arrived): unchanged, and the single-use key is SPENT so no live key is left behind.</summary>
    [Fact]
    public async Task AnInstanceAlreadyOnThePlan_IsUnchanged_AndTheKeyIsSpent()
    {
        var path = await RegisteredInstance("fleet-mac-raised", "enterprise");
        var key = await Mint("enterprise");

        var answer = await Redeem(path, key.RawKey);
        Assert.Equal(PlanRedemptionOutcome.Unchanged, answer.Outcome);
        Assert.True(answer.Applied);
        Assert.Null((await Record(path))?.PlanSetVia);
        Assert.Equal(1, (await KeyRecord(key.Node.Path))?.UsageCount);
    }

    /// <summary>A key with no plan, an expired key and a revoked key raise nothing and use nothing.</summary>
    [Fact]
    public async Task APlanlessExpiredOrRevokedKey_RaisesNothing()
    {
        var path = await RegisteredInstance("fleet-mac-refusals", "free");

        var planless = await Mint(null);
        Assert.Equal(PlanRedemptionOutcome.RefusedNoPlan, (await Redeem(path, planless.RawKey)).Outcome);
        Assert.Equal(0, (await KeyRecord(planless.Node.Path))?.UsageCount);

        var expired = await Mint("enterprise", expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Equal(PlanRedemptionOutcome.InvalidKey, (await Redeem(path, expired.RawKey)).Outcome);

        var revoked = await Mint("enterprise");
        await Access.RunAs(new AccessContext { ObjectId = Minter, Name = Minter },
                () => Mesh.ServiceProvider.GetRequiredService<RegistrationKeyService>().SetRevoked(revoked.Node.Path, true))
            .Should().Within(TestTimeouts.Convergence)
            .Emit("revoking the key", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(PlanRedemptionOutcome.InvalidKey, (await Redeem(path, revoked.RawKey)).Outcome);

        Assert.Equal(PlanRedemptionOutcome.InvalidKey, (await Redeem(path, "mwr_not-a-minted-key")).Outcome);
        Assert.Equal("free", (await Record(path))?.Plan);
    }
}
