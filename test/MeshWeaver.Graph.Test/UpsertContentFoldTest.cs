using System;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>#4928 — the upsert verb could not express a counter, so the one write path with owner-side
/// existence and the ghost-hydration repair was unavailable to the callers that need it.</b>
///
/// <para>Before this, a caller wanting both CREATE-IF-MISSING and <c>count + 1</c> had no shape:
/// full-instance mode takes <c>Content</c> wholesale from a caller read that is stale by
/// construction, and patch mode is declared but refused by the handler. The documented fallback —
/// decide create-vs-update from the query index, then <c>stream.Update</c> — is exactly what
/// <c>MeshExtensions.cs:3315</c> says the verb exists to retire, and it is #1174 (425 occurrences of
/// a 30 s <c>BaseStateTimeoutException</c> on the cold-login path).</para>
///
/// <para><b>What a fold changes, stated precisely so the test cannot over-claim.</b> A fold carries
/// the RULE and the OPERAND (<c>Sum 1</c>), never a result (<c>accessCount: 6</c>). So the caller no
/// longer has to have read the node, and the arithmetic happens against <c>live</c> inside the
/// owner-serialised merge. It does NOT make the counter atomic across mirrors — the write still
/// leaves as an RFC 7396 merge patch carrying the folded value. <see cref="ContentFolds"/> says so in the one place a
/// reader will look, and this test deliberately does not assert the stronger property.</para>
/// </summary>
public class UpsertContentFoldTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Content shaped like the caller this work exists for (<c>UserActivityRecord</c>).</summary>
    public record Tally
    {
        /// <summary>The counter the fold bumps.</summary>
        public int AccessCount { get; init; }

        /// <summary>Create-once: later writes must not move it.</summary>
        public DateTimeOffset FirstAccessedAt { get; init; }

        /// <summary>Monotonic: later writes may only advance it.</summary>
        public DateTimeOffset LastAccessedAt { get; init; }

        /// <summary>An ordinary member, to prove folds do not disturb what they do not name.</summary>
        public string? Label { get; init; }

        /// <summary>A member whose serialized name is overridden, to pin the naming path.</summary>
        [JsonPropertyName("renamed_note")]
        public string? Note { get; init; }
    }

    /// <summary>
    /// <c>Tally</c> is registered on the hub, and the node carries NO NodeType. Both are needed and
    /// for different reasons: without the registration the content crosses the hub boundary as an
    /// untyped <c>JsonElement</c> and reads back as null (<c>ContentAs&lt;T&gt;</c>'s whole subject),
    /// and with a BUILT-IN NodeType <c>ContentDiscriminatorValidator</c> correctly refuses content
    /// whose discriminator that type does not declare. A real caller has a NodeType that declares
    /// its content shape; a fold test has no business inventing one.
    /// </summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .ConfigureHub(c => c.WithType(typeof(Tally), nameof(Tally)));

    private JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    private static string NewPath() => $"TestData/fold-{Guid.NewGuid():N}";

    private JsonSerializerOptions Options => Mesh.JsonSerializerOptions;

    // ---------------------------------------------------------------- end to end

    /// <summary>
    /// 🚨 THE ACCEPTANCE CRITERION of #4928, in one test: the SAME request shape both creates the
    /// node when it is absent and folds onto it when it is present — and the caller never reads.
    ///
    /// <para>The second and third calls send a body that still says <c>AccessCount = 1</c>. If the
    /// fold were not applied the count would stay 1 (full-instance mode takes content wholesale);
    /// if the fold were applied to the INCOMING value rather than the stored one it would reach 2
    /// and stop. Only a fold against <c>live</c> reaches 3, so the assertion discriminates all
    /// three behaviours rather than merely observing a number.</para>
    /// </summary>
    [Fact]
    public async Task OneUpsertShape_CreatesWhenAbsent_ThenFoldsOntoTheStoredValue()
    {
        var path = NewPath();
        var first = DateTimeOffset.UtcNow.AddHours(-3);

        var created = await Upsert(path, new Tally
        {
            AccessCount = 1, FirstAccessedAt = first, LastAccessedAt = first, Label = "seed",
        }, first);
        created.Success.Should().BeTrue(created.Error ?? "the create leg must land");
        created.WasCreated.Should().BeTrue("the node did not exist, so the upsert must have created it");

        // Two more tracks. Each sends AccessCount = 1 — a caller that has NOT read.
        var secondAt = first.AddHours(1);
        var thirdAt = first.AddHours(2);
        await Upsert(path, new Tally
        {
            AccessCount = 1, FirstAccessedAt = secondAt, LastAccessedAt = secondAt, Label = "second",
        }, secondAt);
        var third = await Upsert(path, new Tally
        {
            AccessCount = 1, FirstAccessedAt = thirdAt, LastAccessedAt = thirdAt, Label = "third",
        }, thirdAt);
        third.Success.Should().BeTrue(third.Error ?? "the update leg must land");
        third.WasCreated.Should().BeFalse("the node existed by now");

        var stored = await ReadTally(path);
        stored.AccessCount.Should().Be(3,
            "three upserts each folding Sum 1 onto the STORED count — 1 would mean the fold never "
            + "ran, 2 would mean it folded onto the incoming value instead of the live one");
        stored.FirstAccessedAt.Should().Be(first,
            "KeepExisting must hold the create-once member against two later writes that each "
            + "carried a newer value");
        stored.LastAccessedAt.Should().Be(thirdAt, "Max must advance to the newest");
        stored.Label.Should().Be("third",
            "a member NO fold names is still taken wholesale from the incoming content — folds "
            + "override only what they name");
    }

    /// <summary>
    /// The create leg must NOT apply folds: there is nothing to fold onto, so the seed is verbatim.
    /// Written separately because the tempting implementation — fold against an absent live node —
    /// turns <c>Sum 1</c> into a silent <c>0 + 1</c> and would make a seed of <c>AccessCount = 7</c>
    /// land as 1.
    /// </summary>
    [Fact]
    public async Task TheCreateLegTakesTheSeedVerbatim()
    {
        var path = NewPath();
        var at = DateTimeOffset.UtcNow;
        var response = await Upsert(path, new Tally
        {
            AccessCount = 7, FirstAccessedAt = at, LastAccessedAt = at,
        }, at);

        response.Success.Should().BeTrue(response.Error ?? "the create must land");
        (await ReadTally(path)).AccessCount.Should().Be(7,
            "the seed states the intended INITIAL value; a create leg that folded would write 1");
    }

    // ---------------------------------------------------------------- pure semantics

    /// <summary>
    /// A fold names a member the stored content does not carry — the case a new counter on an
    /// existing node hits. <c>Sum</c> treats absent as 0 rather than refusing, which is what makes
    /// adding a counter to a live node a one-line change instead of a migration.
    /// </summary>
    [Fact]
    public void AnAbsentStoredMemberSumsFromZero()
    {
        var live = new MeshNode("n", "TestData") { Content = new JsonObject { ["label"] = "old" } };
        var folded = ContentFolds.Apply(
            new JsonObject { ["accessCount"] = 5 },
            live,
            [new ContentFold("accessCount", FoldRule.Sum) { Operand = Element(1) }],
            Options);

        ((JsonObject)folded!)["accessCount"]!.GetValue<decimal>().Should().Be(1,
            "absent stored value is 0, so the result is the operand — NOT the incoming 5, which is "
            + "the caller's un-read guess");
    }

    /// <summary>
    /// 🚨 A fold that cannot apply REFUSES rather than writing something nobody asked for. The
    /// alternative — silently leaving the member at the caller's value — is the exact class of
    /// silent wrong answer this whole design exists to remove.
    /// </summary>
    [Fact]
    public void AFoldOverANonNumericValueRefusesLoudly()
    {
        var live = new MeshNode("n", "TestData")
        {
            Content = new JsonObject { ["accessCount"] = "not a number" },
        };

        var refusal = Record.Exception(() => ContentFolds.Apply(
            new JsonObject { ["accessCount"] = 1 },
            live,
            [new ContentFold("accessCount", FoldRule.Sum) { Operand = Element(1) }],
            Options));

        refusal.Should().BeOfType<InvalidOperationException>(
            "an inapplicable fold refuses rather than writing the caller's un-folded guess");
        refusal!.Message.Should().Contain("accessCount",
            "the refusal must name the member, or an operator cannot act on it");
    }

    /// <summary>
    /// The member name comes from the COMPILER, and honours <see cref="JsonPropertyNameAttribute"/>
    /// ahead of the naming policy — so a fold on a renamed property addresses the name the content
    /// is actually stored under, and a renamed C# property is a compile error rather than a fold
    /// that silently matches nothing.
    /// </summary>
    [Fact]
    public void TheBuilderDerivesSerializedNames()
    {
        var folds = new ContentFoldBuilder<Tally>(Options)
            .Sum(t => t.AccessCount, 1)
            .KeepExisting(t => t.Note)
            .Folds;

        folds[0].MemberName.Should().Be(
            Options.PropertyNamingPolicy?.ConvertName(nameof(Tally.AccessCount)) ?? nameof(Tally.AccessCount),
            "the naming policy decides the stored spelling");
        folds[1].MemberName.Should().Be("renamed_note",
            "an explicit JsonPropertyName outranks the policy — it is what the content really holds");
        folds[1].Operand.Should().BeNull("KeepExisting takes no operand");
    }

    /// <summary>
    /// A fold addresses a MEMBER, so anything that is not a property access is refused at the call
    /// site. Pins that the builder stays a lowering of member accesses and does not quietly grow
    /// into a general expression interpreter.
    /// </summary>
    [Fact]
    public void TheBuilderRefusesAnythingThatIsNotAPropertyAccess()
    {
        var refusal = Record.Exception(
            () => new ContentFoldBuilder<Tally>(Options).Sum(t => t.AccessCount + 1, 1));

        refusal.Should().BeOfType<ArgumentException>(
            "a fold addresses a member, so a computed expression is refused at the call site");
        refusal!.Message.Should().Contain("not a property access");
    }

    /// <summary>
    /// 🚨 Folds travel ON THE WIRE — the upsert is an <c>IRequest</c> routed to the owning hub — so a
    /// shape that cannot round-trip through the hub's serializer is not a feature, it is a delivery
    /// failure at the far end with no mention of folds in it. Pinned here because that is exactly how
    /// this first landed: the failure surfaced as a bare "Deserialization failed" on a nodeops
    /// delivery, naming neither the property nor the type.
    /// </summary>
    [Fact]
    public void AFoldRoundTripsThroughTheHubSerializer()
    {
        var request = new CreateOrUpdateNodeRequest(MeshNode.FromPath("TestData/rt"))
            .WithFolds<Tally>(Options, f => f
                .Sum(t => t.AccessCount, 1)
                .KeepExisting(t => t.FirstAccessedAt));

        var json = JsonSerializer.Serialize(request, Options);
        Output.WriteLine(json);
        var back = JsonSerializer.Deserialize<CreateOrUpdateNodeRequest>(json, Options);

        back!.Folds.Should().NotBeNull("the folds must survive the trip to the owning hub");
        back.Folds!.Count.Should().Be(2);
        back.Folds[0].Rule.Should().Be(FoldRule.Sum);
        back.Folds[0].Operand!.Value.GetDecimal().Should().Be(1, "the OPERAND is what travels");
        back.Folds[1].Rule.Should().Be(FoldRule.KeepExisting);
        back.Folds[1].Operand.Should().BeNull();
    }

    // ---------------------------------------------------------------- helpers

    private async Task<CreateOrUpdateNodeResponse> Upsert(string path, Tally tally, DateTimeOffset at)
    {
        var node = MeshNode.FromPath(path) with
        {
            Name = "fold",
            State = MeshNodeState.Active,
            Content = tally,
        };
        var request = new CreateOrUpdateNodeRequest(node)
            .WithFolds<Tally>(Options, f => f
                .Sum(t => t.AccessCount, 1)
                .KeepExisting(t => t.FirstAccessedAt)
                .Max(t => t.LastAccessedAt, at));

        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var response = await access
            .RunAsSystem(() => ObserveNodeOperation(request))
            .FirstAsync()
            .Select(d => d.Message)
            .Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);
        Output.WriteLine($"upsert {path}: success={response.Success} created={response.WasCreated} "
            + $"error={response.Error}");
        return response;
    }

    private async Task<Tally> ReadTally(string path)
    {
        var node = await Mesh.GetWorkspace()
            .GetMeshNodeStream(path)
            .Where(n => n?.Content is not null)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);
        return node.ContentAs<Tally>(Options)
               ?? throw new InvalidOperationException($"content at '{path}' did not read back as {nameof(Tally)}");
    }
}
