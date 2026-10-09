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
}

/// <summary>The pure halves: the seal, the stamp, the options.</summary>
public class ApprovalStepUpPureTest
{
    private static readonly byte[] Key = StepUpSeal.DeriveKey(Enumerable.Repeat((byte)3, 32).ToArray());

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
