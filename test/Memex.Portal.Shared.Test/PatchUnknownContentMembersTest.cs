#pragma warning disable CS1591

using System;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
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
/// An MCP <c>patch</c> answered <c>"Patched: {path}"</c> while writing nothing: the content keys
/// it carried (<c>aliases</c>, <c>domains</c>, <c>matchCaseSensitive</c> on a Crm/Counterparty node
/// whose NodeType had just gained them) were not declared by the TYPE the portal had bound, so the
/// typed deserialisation dropped them without error, the landed-write check projected them as
/// "expect absent", and the live node satisfied that at once — version unchanged, keys absent on
/// read-back. The rule pinned here: a write carrying an undeclared content member is REFUSED naming
/// the members and the type, and a patch that changes nothing says <c>No change</c>, never
/// <c>Patched</c>. Runs against a REAL mesh with a real typed content (<see cref="UpdatePolicyContent"/>).
/// </summary>
public class PatchUnknownContentMembersTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly TimeSpan Budget = TestTimeouts.Convergence;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddUpdatePolicyType();

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    [Fact]
    public async Task Patch_WithUndeclaredContentMembers_IsRefusedNamingThem_AndWritesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var before = await Seed(UpdatePolicyKind.Stable, ct);

        var result = await Run(new MeshOperations(Mesh).Patch(
            UpdatePolicyNodeType.NodePath,
            """{"content":{"aliases":["ACME AG"],"domains":["acme.ch"],"policy":"None"}}"""), ct);

        Output.WriteLine($"Patch tool returned: {result}");
        result.Should().StartWith("Error:", "an undeclared member would be dropped silently");
        result.Should().NotContain("Patched");
        result.Should().Contain("'aliases'").And.Contain("'domains'")
            .And.Contain(nameof(UpdatePolicyContent));
        result.Should().NotContain("'policy'", "policy IS declared and must not be named");

        var after = await ReadNode(ct);
        after.Version.Should().Be(before.Version, "a refused patch writes nothing — not even the declared key");
        UpdatePolicyNodeType.Parse(after, Mesh.JsonSerializerOptions).Policy.Should().Be(UpdatePolicyKind.Stable);
    }

    [Fact]
    public async Task Patch_ThatChangesNothing_ReportsNoChange_NotPatched()
    {
        var ct = TestContext.Current.CancellationToken;
        var before = await Seed(UpdatePolicyKind.Stable, ct);

        var result = await Run(new MeshOperations(Mesh).Patch(
            UpdatePolicyNodeType.NodePath, """{"content":{"policy":"Stable"}}"""), ct);

        Output.WriteLine($"Patch tool returned: {result}");
        result.Should().StartWith("No change:");
        result.Should().Contain(UpdatePolicyNodeType.NodePath);
        (await ReadNode(ct)).Version.Should().Be(before.Version);
    }

    [Fact]
    public async Task Patch_ThatChangesADeclaredMember_StillReportsPatched_AndBumpsTheVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        var before = await Seed(UpdatePolicyKind.Stable, ct);

        var result = await Run(new MeshOperations(Mesh).Patch(
            UpdatePolicyNodeType.NodePath, """{"content":{"policy":"None"}}"""), ct);

        Output.WriteLine($"Patch tool returned: {result}");
        result.Should().StartWith("Patched:");
        var after = await ReadNode(ct, n => UpdatePolicyNodeType.Parse(n, Mesh.JsonSerializerOptions).Policy
                                        == UpdatePolicyKind.None);
        after.Version.Should().BeGreaterThan(before.Version);
        result.Should().Contain($"v{before.Version} →", "the success line carries the version delta");
    }

    [Fact]
    public async Task Update_WithUndeclaredContentMembers_IsRefusedNamingThem()
    {
        var ct = TestContext.Current.CancellationToken;
        var before = await Seed(UpdatePolicyKind.Stable, ct);
        var nodeJson = JsonSerializer.SerializeToNode(before, Mesh.JsonSerializerOptions)!.AsObject();
        nodeJson["content"]!.AsObject()["matchCaseSensitive"] = true;

        var result = await Run(new MeshOperations(Mesh).Update(new JsonArray(nodeJson).ToJsonString()), ct);

        Output.WriteLine($"Update tool returned: {result}");
        result.Should().StartWith("Error:");
        result.Should().Contain("'matchCaseSensitive'").And.Contain(nameof(UpdatePolicyContent));
        result.Should().NotContain("Updated:");
        (await ReadNode(ct)).Version.Should().Be(before.Version);
    }

    [Fact]
    public void UnknownContentMembers_JudgesOnlyTypedContent_AndIgnoresWireMetadata()
    {
        var options = Mesh.JsonSerializerOptions;
        var delta = JsonNode.Parse("""{"$type":"UpdatePolicyContent","policy":"None","aliases":[]}""")!.AsObject();

        MeshOperations.UnknownContentMembers(delta, new UpdatePolicyContent(), options)
            .Should().Equal("aliases");
        MeshOperations.UnknownContentMembers(delta, JsonDocument.Parse("{}").RootElement, options)
            .Should().BeEmpty("untyped content keeps every key, so nothing is dropped");
        MeshOperations.UnknownContentMembers(delta, null, options).Should().BeEmpty();
    }

    // ── helpers ──

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
