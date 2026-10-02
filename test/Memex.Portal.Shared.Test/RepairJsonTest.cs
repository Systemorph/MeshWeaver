#pragma warning disable CS1591

using System;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.SelfUpdate;
using MeshWeaver.AI;   // MeshOperations — its namespace is a frozen binary contract (#2370)
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.SelfUpdate;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshWeaver.Fixture;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// A model-written <c>patch</c> / <c>create</c> payload one closing brace short
/// (<c>{"content":{"policy":"None"}</c>) was refused with <i>"Expected depth to be zero at the end of
/// the JSON payload"</i>: <c>MeshOperations.RepairJson</c> only TRIMMED trailing text back to a
/// closer and never appended the closers the model left off. It now closes the containers still open
/// at the end — counted outside string literals, honouring escapes — and accepts the result only if
/// it parses; anything it cannot balance by appending is returned untouched, so the caller reports
/// the ORIGINAL error.
/// </summary>
public class RepairJsonTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly TimeSpan Budget = TestTimeouts.Convergence;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddUpdatePolicyType();

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    // ── the repair rules, measured directly ──

    [Theory]
    [InlineData("""{"content":{"policy":"None"}""", """{"content":{"policy":"None"}}""")]
    [InlineData("""{"a":{"b":{"c":1""", """{"a":{"b":{"c":1}}}""")]
    [InlineData("""[{"id":"a"},{"id":"b","tags":["x","y"]""", """[{"id":"a"},{"id":"b","tags":["x","y"]}]""")]
    [InlineData("""{"list":[1,2,[3,4]""", """{"list":[1,2,[3,4]]}""")]
    [InlineData("{\"a\":1  \n", "{\"a\":1}")]
    public void MissingClosersAtTheEnd_AreAppended(string input, string expected)
    {
        var repaired = MeshOperations.RepairJson(input);
        repaired.Should().Be(expected);
        Parses(repaired).Should().BeTrue();
    }

    [Theory]
    // A closer INSIDE a string is text, not structure — it must neither close nor be counted.
    [InlineData("""{"text":"a } and ] here","n":{"x":"{["}""", """{"text":"a } and ] here","n":{"x":"{["}}""")]
    // An escaped quote does not end the string; an escaped backslash before a quote does.
    [InlineData("""{"q":"say \"}\" now","p":{"r":"c:\\"}""", """{"q":"say \"}\" now","p":{"r":"c:\\"}}""")]
    public void ClosersInsideStrings_AreIgnored_AndEscapesHonoured(string input, string expected)
    {
        var repaired = MeshOperations.RepairJson(input);
        repaired.Should().Be(expected);
        using var doc = JsonDocument.Parse(repaired);
        doc.RootElement.EnumerateObject().Should().HaveCount(2,
            "the closers inside the string literals must still be inside them after the repair");
    }

    [Theory]
    // Ends INSIDE a string: the value was truncated — closing it would invent content.
    [InlineData("""{"content":{"text":"half a sent""")]
    // A closer of the wrong kind: structure is wrong in the middle, not just short at the end.
    [InlineData("""{"a":[1,2}""")]
    // A trailing comma: appending closers would still not parse.
    [InlineData("""{"a":1,""")]
    // Not JSON at all.
    [InlineData("""{name: unquoted""")]
    // A missing value.
    [InlineData("""{"a":""")]
    public void GenuinelyMalformedInput_IsReturnedUntouched(string input)
    {
        MeshOperations.RepairJson(input).Should().Be(input,
            "a payload that cannot be balanced by appending closers keeps its original text, so the "
            + "caller's parse reports the original error rather than one about a guessed repair");
    }

    [Theory]
    [InlineData("""{"a":1}""")]
    [InlineData("""[1,{"b":"}"}]""")]
    [InlineData("\"just a string\"")]
    [InlineData("")]
    public void ValidJson_IsNeverChanged(string input) =>
        MeshOperations.RepairJson(input).Should().Be(input);

    [Fact]
    public void TrailingJunkAfterACompleteValue_IsStillTrimmed() =>
        MeshOperations.RepairJson("{\"a\":{\"b\":1}}\n```").Should().Be("{\"a\":{\"b\":1}}");

    // ── end to end, through the tool the model calls ──

    [Fact]
    public async Task Patch_OneClosingBraceShort_IsApplied()
    {
        var ct = TestContext.Current.CancellationToken;
        var before = await Seed(UpdatePolicyKind.Stable, ct);

        var result = await Run(new MeshOperations(Mesh).Patch(
            UpdatePolicyNodeType.NodePath, """{"content":{"policy":"None"}"""), ct);

        Output.WriteLine($"Patch tool returned: {result}");
        result.Should().StartWith("Patched:");
        var after = await ReadNode(ct, n => UpdatePolicyNodeType.Parse(n, Mesh.JsonSerializerOptions).Policy
                                        == UpdatePolicyKind.None);
        after.Version.Should().BeGreaterThan(before.Version);
    }

    [Fact]
    public async Task Patch_TruncatedInsideAString_KeepsTheOriginalError_AndWritesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var before = await Seed(UpdatePolicyKind.Stable, ct);
        const string truncated = """{"content":{"policy":"No""";

        string originalError;
        try
        {
            // The same parser Patch uses, so the message is byte-identical.
            _ = System.Text.Json.Nodes.JsonNode.Parse(truncated);
            throw new InvalidOperationException("precondition: the payload must not parse");
        }
        catch (JsonException ex)
        {
            originalError = ex.Message;
        }

        var result = await Run(new MeshOperations(Mesh).Patch(UpdatePolicyNodeType.NodePath, truncated), ct);

        Output.WriteLine($"Patch tool returned: {result}");
        result.Should().StartWith("Invalid JSON:");
        result.Should().Contain(originalError, "the error describes the payload the model SENT");
        (await ReadNode(ct)).Version.Should().Be(before.Version);
    }

    // ── helpers ──

    private static bool Parses(string json)
    {
        try
        {
            JsonDocument.Parse(json).Dispose();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static Task<string> Run(IObservable<string> op, CancellationToken ct) =>
        op.FirstAsync().Timeout(Budget).Await(ct);

    private async Task<MeshNode> Seed(UpdatePolicyKind policy, CancellationToken ct)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var node = new MeshNode(UpdatePolicyNodeType.NodeId, UpdatePolicyNodeType.AdminPartition)
        {
            NodeType = UpdatePolicyNodeType.NodeType,
            Name = "Update Policy",
            State = MeshNodeState.Active,
            Content = new UpdatePolicyContent { Policy = policy },
        };
        await Observable.Create<MeshNode>(observer =>
            {
                using (Access.ImpersonateAsSystem())
                    return meshService.CreateNode(node).Subscribe(observer);
            })
            .FirstAsync()
            .Timeout(Budget)
            .Await(ct);
        return await ReadNode(ct);
    }

    private Task<MeshNode> ReadNode(CancellationToken ct, Func<MeshNode, bool>? until = null) =>
        Observable.Create<MeshNode>(observer =>
            {
                using (Access.ImpersonateAsSystem())
                    return Mesh.GetWorkspace().GetMeshNodeStream(UpdatePolicyNodeType.NodePath)
                        .Where(node => node is not null && (until is null || until(node)))
                        .Subscribe(observer);
            })
            .FirstAsync()
            .Timeout(Budget)
            .Await(ct);
}
