using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.AI;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The step-up receipt (Refs #4305, <c>Doc/Architecture/ApprovalStepUp</c>) on a real monolith mesh:
/// a receipt minted for (user, action, hash) is consumed once for exactly that and refused for
/// anything else — another user, another action, another hash, after expiry, a second time, or
/// with a seal another instance's key made. Each refusal test carries its NEGATIVE CONTROL: the
/// same receipt, consumed the way it was minted for, is accepted — so a refusal cannot pass by the
/// service refusing everything.
/// </summary>
public class ApprovalStepUpReceiptTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Alice = "stepup-alice";
    private const string Bob = "stepup-bob";
    private const string Action = "Ops/InstanceAction/roll-1";
    private const string Plan = "sha256:plan-a";

    private sealed class FixedMasterKey(byte fill) : IMasterKeyProvider
    {
        private readonly byte[] key = Enumerable.Repeat(fill, 32).ToArray();
        public byte[]? GetMasterKey() => key;
    }

    private static IConfiguration Config(bool enabled) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [StepUpOptions.EnabledKey] = enabled ? "true" : "false",
            [StepUpOptions.ReceiptLifetimeKey] = "300",
        }).Build();

    private DateTimeOffset now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private StepUpService Service(bool enabled = true, byte keyFill = 7) => new(
        Mesh, Config(enabled), new FixedMasterKey(keyFill),
        Mesh.ServiceProvider.GetRequiredService<ILogger<StepUpService>>())
    {
        Now = () => now,
    };

    private static Task<T> Run<T>(IObservable<T> source, CancellationToken ct) =>
        source.Take(1).Timeout(TestTimeouts.WriteConvergence).Await(ct);

    private Task<StepUpReceipt> MintFor(StepUpService service, string user, CancellationToken ct, params StepUpTarget[] targets) =>
        Run(service.Mint(user, StepUpMethod.Entra, targets.Length == 0
            ? [new StepUpTarget { ActionPath = Action, Binding = Plan }]
            : targets, now, "acrs=c1"), ct);

    [Fact(Timeout = 90000)]
    public async Task AFreshReceipt_IsAcceptedOnce_AndASecondConsumptionIsAReplay()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = Service();
        var receipt = await MintFor(service, Alice, ct);

        var first = await Run(service.Consume(receipt.Id, Alice, Action, Plan), ct);
        Assert.Equal(StepUpOutcome.Accepted, first.Outcome);
        Assert.Equal(StepUpMethod.Entra, first.Method);

        var replay = await Run(service.Consume(receipt.Id, Alice, Action, Plan), ct);
        Assert.Equal(StepUpOutcome.Replayed, replay.Outcome);
    }

    [Fact(Timeout = 90000)]
    public async Task ConcurrentConsumers_ExactlyOneWins()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = Service();
        var receipt = await MintFor(service, Alice, ct);

        var verdicts = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => Run(service.Consume(receipt.Id, Alice, Action, Plan), ct)));

        Assert.Single(verdicts, v => v.Outcome == StepUpOutcome.Accepted);
        Assert.All(verdicts.Where(v => v.Outcome != StepUpOutcome.Accepted),
            v => Assert.Equal(StepUpOutcome.Replayed, v.Outcome));
    }

    [Fact(Timeout = 90000)]
    public async Task WrongActionBinding_IsRefused_WithoutBurningTheReceipt()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = Service();
        var receipt = await MintFor(service, Alice, ct);

        Assert.Equal(StepUpOutcome.WrongAction,
            (await Run(service.Consume(receipt.Id, Alice, "Ops/InstanceAction/other", Plan), ct)).Outcome);
        Assert.Equal(StepUpOutcome.WrongBinding,
            (await Run(service.Consume(receipt.Id, Alice, Action, "sha256:plan-b"), ct)).Outcome);
        Assert.Equal(StepUpOutcome.WrongUser,
            (await Run(service.Consume(receipt.Id, Bob, Action, Plan), ct)).Outcome);

        // Negative control: the refusals consumed nothing — the bound use still succeeds.
        Assert.Equal(StepUpOutcome.Accepted,
            (await Run(service.Consume(receipt.Id, Alice, Action, Plan), ct)).Outcome);
    }

    [Fact(Timeout = 90000)]
    public async Task AnExpiredReceipt_IsRefused_AndTheSameReceiptInTime_IsAccepted()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = Service();
        var receipt = await MintFor(service, Alice, ct,
            new StepUpTarget { ActionPath = Action, Binding = Plan },
            new StepUpTarget { ActionPath = Action + "-2", Binding = Plan });

        // Negative control first, inside the lifetime: one target accepted.
        now = receipt.IssuedAt.AddSeconds(299);
        Assert.Equal(StepUpOutcome.Accepted,
            (await Run(service.Consume(receipt.Id, Alice, Action, Plan), ct)).Outcome);

        // Then the other target, one second past the lifetime.
        now = receipt.ExpiresAt.AddSeconds(1);
        Assert.Equal(StepUpOutcome.Expired,
            (await Run(service.Consume(receipt.Id, Alice, Action + "-2", Plan), ct)).Outcome);
    }

    [Fact(Timeout = 90000)]
    public async Task AReceiptSealedByAnotherInstancesKey_IsInvalid()
    {
        var ct = TestContext.Current.CancellationToken;
        var receipt = await MintFor(Service(keyFill: 7), Alice, ct);

        Assert.Equal(StepUpOutcome.Invalid,
            (await Run(Service(keyFill: 9).Consume(receipt.Id, Alice, Action, Plan), ct)).Outcome);
        // Negative control: the minting instance's key verifies it.
        Assert.Equal(StepUpOutcome.Accepted,
            (await Run(Service(keyFill: 7).Consume(receipt.Id, Alice, Action, Plan), ct)).Outcome);
    }

    [Fact(Timeout = 90000)]
    public async Task MissingAndMalformedReceipts_AreMissing_AndDisabledIsNotRequired()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = Service();
        Assert.Equal(StepUpOutcome.Missing, (await Run(service.Consume(null, Alice, Action, Plan), ct)).Outcome);
        Assert.Equal(StepUpOutcome.Missing, (await Run(service.Consume("../../Admin/x", Alice, Action, Plan), ct)).Outcome);

        // Off until declared: a disabled instance reads nothing and asks for nothing.
        var off = await Run(Service(enabled: false).Consume(null, Alice, Action, Plan), ct);
        Assert.Equal(StepUpOutcome.NotRequired, off.Outcome);
        Assert.True(off.Counts);
        // …and an enabled one never treats "no receipt" as a pass.
        Assert.False((await Run(service.Consume(null, Alice, Action, Plan), ct)).Counts);
    }

    [Fact(Timeout = 90000)]
    public async Task CheckDoesNotConsume()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = Service();
        var receipt = await MintFor(service, Alice, ct);

        Assert.Equal(StepUpOutcome.Accepted, (await Run(service.Check(receipt.Id, Alice, Action, Plan), ct)).Outcome);
        Assert.Equal(StepUpOutcome.Accepted, (await Run(service.Check(receipt.Id, Alice, Action, Plan), ct)).Outcome);
        Assert.Equal(StepUpOutcome.Accepted, (await Run(service.Consume(receipt.Id, Alice, Action, Plan), ct)).Outcome);
    }

    [Fact(Timeout = 90000)]
    public async Task TheRegisteredServiceIsTheMeshes_AndTheRuleAdmitsSystemAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.IsType<StepUpService>(Mesh.ServiceProvider.GetRequiredService<IStepUpService>());

        var receipt = await MintFor(Service(), Alice, ct);
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        // As System the receipt is there (positive control) …
        var asSystem = await Run(access.RunAsSystem(() => Mesh.GetMeshNode(StepUpPaths.Receipt(receipt.Id))), ct);
        Assert.NotNull(asSystem);
        // … and the access rule admits System alone.
        var rule = new StepUpNodeTypes.SystemOnlyAccessRule(StepUpPaths.ReceiptNodeType);
        Assert.False(await Run(rule.HasAccess(null!, Alice), ct));
        Assert.True(await Run(rule.HasAccess(null!, WellKnownUsers.System), ct));
    }

    // ── The core Approval record carries the receipts map (generic Approvals/Approval satellite) ──

    private static Approval PendingApproval() => new()
    {
        Id = "appr-1",
        PrimaryNodePath = "stepup/Doc1",
        Requester = Alice,
        Approver = Bob,
        Purpose = "publish the report",
        CreatedAt = new DateTimeOffset(2026, 10, 9, 11, 0, 0, TimeSpan.Zero),
    };

    /// <summary>A record shaped like <see cref="Approval"/> BEFORE it declared the map — the negative control.</summary>
    public sealed record ApprovalWithoutReceipts(string Id, string Purpose);

    /// <summary>
    /// The step-up endpoint stamps through <see cref="StepUpPaths.Stamp"/> with the HUB's serializer
    /// options (StepUpController.MintAndStamp). On a typed core <see cref="Approval"/> the stamp must
    /// survive — and survive a store round trip — or the approval's control plane never sees a receipt.
    /// Negative control: a record that does not declare the map drops the stamp (the pre-change shape).
    /// </summary>
    [Fact]
    public void TheEndpointStamp_SurvivesOnTheCoreApproval_AndItsRoundTrip()
    {
        var options = Mesh.JsonSerializerOptions;
        var decided = PendingApproval() with { Status = ApprovalStatus.Approved };

        var stamped = Assert.IsType<Approval>(StepUpPaths.Stamp(decided, Bob, "r-bob", options));
        Assert.Equal("r-bob", StepUpPaths.ReceiptFor(stamped.StepUpReceipts, Bob));
        Assert.Equal(ApprovalStatus.Approved, stamped.Status);

        var stampedTwice = Assert.IsType<Approval>(StepUpPaths.Stamp(stamped, "carol", "r-carol", options));
        Assert.Equal("r-bob", StepUpPaths.ReceiptFor(stampedTwice.StepUpReceipts, Bob));
        Assert.Equal("r-carol", StepUpPaths.ReceiptFor(stampedTwice.StepUpReceipts, "carol"));

        var stored = JsonSerializer.Serialize<object>(stamped, options);
        var reread = Assert.IsType<Approval>(JsonSerializer.Deserialize<object>(stored, options));
        Assert.Equal("r-bob", StepUpPaths.ReceiptFor(reread.StepUpReceipts, Bob));
        Assert.Equal(stamped, reread);

        var legacy = new ApprovalWithoutReceipts("appr-1", "publish the report");
        var legacyStamped = Assert.IsType<ApprovalWithoutReceipts>(StepUpPaths.Stamp(legacy, Bob, "r-bob", options));
        Assert.DoesNotContain("r-bob", JsonSerializer.Serialize(legacyStamped, options));
    }

    /// <summary>
    /// Two reads of one stored approval are EQUAL once a receipt is stamped — the map compares by its
    /// entries, not by reference. Negative controls: a different receipt, a missing map, other terms.
    /// </summary>
    [Fact]
    public void ApprovalEquality_ComparesTheReceiptMapByItsEntries()
    {
        var a = PendingApproval() with { StepUpReceipts = ImmutableDictionary<string, string>.Empty.Add(Bob, "r-1") };
        var b = PendingApproval() with { StepUpReceipts = ImmutableDictionary<string, string>.Empty.Add(Bob, "r-1") };
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());

        Assert.NotEqual(a, b with { StepUpReceipts = ImmutableDictionary<string, string>.Empty.Add(Bob, "r-2") });
        Assert.NotEqual(a, b with { StepUpReceipts = null });
        Assert.NotEqual(a, b with { Purpose = "something else" });
        Assert.Equal(PendingApproval(), PendingApproval());

        // Symmetric whatever the maps' key comparers: "BOB" in an ordinal map and "bob" in a
        // case-insensitive one differ in BOTH directions (each map's own lookup would disagree).
        var ordinalUpper = PendingApproval() with { StepUpReceipts = ImmutableDictionary<string, string>.Empty.Add("BOB", "r-1") };
        var ignoreCaseLower = PendingApproval() with
        {
            StepUpReceipts = ImmutableDictionary.Create<string, string>(StringComparer.OrdinalIgnoreCase).Add("bob", "r-1"),
        };
        Assert.False(ordinalUpper.Equals(ignoreCaseLower));
        Assert.False(ignoreCaseLower.Equals(ordinalUpper));
        Assert.True((PendingApproval() with
        {
            StepUpReceipts = ImmutableDictionary.Create<string, string>(StringComparer.OrdinalIgnoreCase).Add("BOB", "r-1"),
        }).Equals(ordinalUpper), "same ordinal entries are equal across comparers");
    }

    /// <summary>
    /// The binding covers the terms and the decision — never the receipts, the decision date or the
    /// creation time, which change without the approver deciding anything new.
    /// </summary>
    [Fact]
    public void TheApprovalBinding_CoversTheTermsAndTheDecision_NotTheReceipts()
    {
        var approve = PendingApproval() with { Status = ApprovalStatus.Approved };
        var binding = approve.StepUpBinding();
        Assert.StartsWith("approval:sha256:", binding);

        Assert.Equal(binding, (approve with
        {
            StepUpReceipts = ImmutableDictionary<string, string>.Empty.Add(Bob, "r-1"),
            ApprovalDate = DateTimeOffset.UnixEpoch,
            CreatedAt = DateTimeOffset.UnixEpoch,
        }).StepUpBinding());

        Assert.NotEqual(binding, (approve with { Status = ApprovalStatus.Rejected }).StepUpBinding());
        Assert.NotEqual(binding, (approve with { Purpose = "publish another report" }).StepUpBinding());
        Assert.NotEqual(binding, (approve with { Approver = "carol" }).StepUpBinding());
        Assert.NotEqual(binding, (approve with { PrimaryNodePath = "stepup/Doc2" }).StepUpBinding());
        Assert.NotEqual(binding, (approve with { DueDate = DateTimeOffset.UnixEpoch }).StepUpBinding());
        Assert.NotEqual(binding, (approve with { Id = "appr-2" }).StepUpBinding());
        // Length-prefixed: moving characters between adjacent fields is a different binding.
        Assert.NotEqual((approve with { Requester = "ab", Approver = "c" }).StepUpBinding(),
            (approve with { Requester = "a", Approver = "bc" }).StepUpBinding());
    }

    /// <summary>
    /// End to end on the real service: a receipt minted for an approval's path and binding — what the
    /// step-up endpoint stamps — is accepted for that decision, and refused when it is bound to
    /// different content (other terms, or the opposite decision).
    /// </summary>
    [Fact(Timeout = 90000)]
    public async Task AnApprovalReceipt_ConfirmsThatDecisionOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = Service();
        const string path = "stepup/Doc1/_Approval/appr-1";
        var approve = PendingApproval() with { Status = ApprovalStatus.Approved };

        var forOtherContent = await MintFor(service, Bob, ct,
            new StepUpTarget { ActionPath = path, Binding = (approve with { Purpose = "something else" }).StepUpBinding() });
        Assert.Equal(StepUpOutcome.WrongBinding, (await Run(service.Consume(forOtherContent.Id, Bob, path, approve.StepUpBinding()), ct)).Outcome);

        var forReject = await MintFor(service, Bob, ct,
            new StepUpTarget { ActionPath = path, Binding = (approve with { Status = ApprovalStatus.Rejected }).StepUpBinding() });
        Assert.Equal(StepUpOutcome.WrongBinding, (await Run(service.Consume(forReject.Id, Bob, path, approve.StepUpBinding()), ct)).Outcome);

        var receipt = await MintFor(service, Bob, ct, new StepUpTarget { ActionPath = path, Binding = approve.StepUpBinding() });
        var stamped = Assert.IsType<Approval>(StepUpPaths.Stamp(approve, Bob, receipt.Id, Mesh.JsonSerializerOptions));
        var verdict = await Run(service.Consume(StepUpPaths.ReceiptFor(stamped.StepUpReceipts, Bob), Bob, path, stamped.StepUpBinding()), ct);
        Assert.Equal(StepUpOutcome.Accepted, verdict.Outcome);
    }

}

/// <summary>The pure halves: the seal, the stamp, the options.</summary>
public class ApprovalStepUpPureTest
{
    // A fresh array per use — a static byte[] would be process-wide mutable state.
    private static byte[] Key => StepUpSeal.DeriveKey(Enumerable.Repeat((byte)3, 32).ToArray());

    private static StepUpReceipt Receipt() => new()
    {
        Id = StepUpSeal.NewId(),
        UserId = "alice",
        Method = StepUpMethod.Passkey,
        Targets = [new StepUpTarget { ActionPath = "A/b", Binding = "h1" }],
        IssuedAt = DateTimeOffset.UnixEpoch.AddDays(1),
        ExpiresAt = DateTimeOffset.UnixEpoch.AddDays(1).AddMinutes(5),
        AuthenticatedAt = DateTimeOffset.UnixEpoch.AddDays(1),
        Evidence = "credential=x",
    };

    [Fact]
    public void EveryFieldIsUnderTheSeal()
    {
        var sealedReceipt = Receipt() with { };
        sealedReceipt = sealedReceipt with { Seal = StepUpSeal.Compute(sealedReceipt, Key) };
        Assert.True(StepUpSeal.Verify(sealedReceipt, Key));

        Assert.False(StepUpSeal.Verify(sealedReceipt with { UserId = "bob" }, Key));
        Assert.False(StepUpSeal.Verify(sealedReceipt with { Method = StepUpMethod.Totp }, Key));
        Assert.False(StepUpSeal.Verify(sealedReceipt with { ExpiresAt = sealedReceipt.ExpiresAt.AddHours(1) }, Key));
        Assert.False(StepUpSeal.Verify(sealedReceipt with { Targets = [new StepUpTarget { ActionPath = "A/b", Binding = "h2" }] }, Key));
        Assert.False(StepUpSeal.Verify(sealedReceipt with { Seal = "not-base64!" }, Key));
    }

    [Fact]
    public void TheMaterialIsCanonical_NoTwoReceiptsShareBytes()
    {
        var one = Receipt() with { Targets = [new StepUpTarget { ActionPath = "a", Binding = "b\nc\u001fd" }] };
        var two = one with { Targets = [new StepUpTarget { ActionPath = "a", Binding = "b" }, new StepUpTarget { ActionPath = "c", Binding = "d" }] };
        Assert.NotEqual(StepUpSeal.Material(one), StepUpSeal.Material(two));
        Assert.NotEqual(StepUpSeal.Material(one with { Evidence = null }), StepUpSeal.Material(one with { Evidence = "" }));
        Assert.NotEqual(StepUpSeal.Material(one), StepUpSeal.Material(one with { ExpiresAt = one.ExpiresAt.AddTicks(1) }));
        // Negative control: the same receipt gives the same bytes.
        Assert.Equal(StepUpSeal.Material(one), StepUpSeal.Material(one with { }));
    }

    public sealed record Approvable(string Name, ImmutableDictionary<string, string>? StepUpReceipts = null);

    [Fact]
    public void TheStampAddsTheApproversReceipt_AndKeepsOthers()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var content = new Approvable("x", ImmutableDictionary<string, string>.Empty.Add("bob", "r-bob"));

        var stamped = StepUpPaths.Stamp(content, "alice", "r-alice", options);

        var typed = Assert.IsType<Approvable>(stamped);
        Assert.Equal("r-alice", StepUpPaths.ReceiptFor(typed.StepUpReceipts, "alice"));
        Assert.Equal("r-bob", StepUpPaths.ReceiptFor(typed.StepUpReceipts, "bob"));
        Assert.Null(StepUpPaths.ReceiptFor(typed.StepUpReceipts, "carol"));
    }

    [Fact]
    public void OptionsAreOffUntilDeclared()
    {
        var none = StepUpOptions.From(new ConfigurationBuilder().Build());
        Assert.False(none.Enabled);
        Assert.Null(none.EntraAuthenticationContext);
        Assert.Equal(TimeSpan.FromSeconds(120), none.MaxAuthAge);

        var declared = StepUpOptions.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [StepUpOptions.EnabledKey] = "true",
            [StepUpOptions.EntraContextKey] = " c1 ",
            [StepUpOptions.EntraPhishingResistantAmrKey] = "fido, hwk",
            [StepUpOptions.MaxAuthAgeKey] = "60",
        }).Build());
        Assert.True(declared.Enabled);
        Assert.Equal("c1", declared.EntraAuthenticationContext);
        Assert.Equal(TimeSpan.FromSeconds(60), declared.MaxAuthAge);
        Assert.Contains("FIDO", declared.EntraPhishingResistantAmr);
        Assert.DoesNotContain("ngcmfa", declared.EntraPhishingResistantAmr);
    }
}
