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
/// MeshWeaver.Plugins#2803: a runtime triage item, created with the
/// <see cref="SyncBehavior.ExcludeThisOnly"/> claim so a Hosting GitSync import keeps it, was
/// PRUNED as "absent from the repo". Its version history shows the write that released it: v3,
/// the triage agent recording its verdict with the full-entity <c>update</c> tool (no
/// <c>changedBy</c> — the Update verb's write). The agent sent the node without
/// <c>syncBehavior</c>; the typed deserialisation filled in the default (<c>Include</c>) and the
/// full-entity write stored it, so the claim was gone and the importer's prune guard — which
/// spares only non-Include nodes — had nothing to spare. Pinned here against a REAL mesh: a
/// full-entity update that does not NAME the claim keeps the live one, and one that names it
/// (including <c>Include</c>, the "resume sync" decision) writes it as given.
/// </summary>
public class UpdateKeepsTheSyncClaimTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly TimeSpan Budget = TestTimeouts.Convergence;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddUpdatePolicyType();

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    [Fact]
    public async Task Update_ThatOmitsSyncBehavior_KeepsTheLiveClaim()
    {
        var ct = TestContext.Current.CancellationToken;
        var before = await Seed(ct);
        before.SyncBehavior.Should().Be(SyncBehavior.ExcludeThisOnly, "the seed is a claimed runtime record");

        var nodeJson = JsonSerializer.SerializeToNode(before, Mesh.JsonSerializerOptions)!.AsObject();
        nodeJson.Remove("syncBehavior");
        nodeJson["content"]!.AsObject()["policy"] = JsonSerializer.SerializeToNode(UpdatePolicyKind.None, Mesh.JsonSerializerOptions);

        var result = await Run(new MeshOperations(Mesh).Update(new JsonArray(nodeJson).ToJsonString()), ct);
        Output.WriteLine($"Update tool returned: {result}");
        result.Should().StartWith("Updated:");

        var after = await ReadNode(ct, n => UpdatePolicyNodeType.Parse(n, Mesh.JsonSerializerOptions).Policy
                                            == UpdatePolicyKind.None);
        after.SyncBehavior.Should().Be(SyncBehavior.ExcludeThisOnly,
            "a caller that did not name the claim has not released it — releasing it lets the next "
            + "static-repo import prune a runtime record (Plugins#2803)");
    }

    [Fact]
    public async Task Update_ThatNamesInclude_ReleasesTheClaim()
    {
        var ct = TestContext.Current.CancellationToken;
        var before = await Seed(ct);

        var nodeJson = JsonSerializer.SerializeToNode(before, Mesh.JsonSerializerOptions)!.AsObject();
        nodeJson["syncBehavior"] = JsonSerializer.SerializeToNode(SyncBehavior.Include, Mesh.JsonSerializerOptions);
        nodeJson["content"]!.AsObject()["policy"] = JsonSerializer.SerializeToNode(UpdatePolicyKind.None, Mesh.JsonSerializerOptions);

        var result = await Run(new MeshOperations(Mesh).Update(new JsonArray(nodeJson).ToJsonString()), ct);
        Output.WriteLine($"Update tool returned: {result}");
        result.Should().StartWith("Updated:");

        var after = await ReadNode(ct, n => UpdatePolicyNodeType.Parse(n, Mesh.JsonSerializerOptions).Policy
                                            == UpdatePolicyKind.None);
        after.SyncBehavior.Should().Be(SyncBehavior.Include, "an explicitly stated claim is written as given");
    }

    [Fact]
    public void NamesSyncBehavior_ReadsTheKeyInAnyCasing()
    {
        MeshOperations.NamesSyncBehavior(JsonNode.Parse("""{"id":"x"}""")!.AsObject()).Should().BeFalse();
        MeshOperations.NamesSyncBehavior(JsonNode.Parse("""{"syncBehavior":"Include"}""")!.AsObject()).Should().BeTrue();
        MeshOperations.NamesSyncBehavior(JsonNode.Parse("""{"SyncBehavior":0}""")!.AsObject()).Should().BeTrue();
        MeshOperations.NamesSyncBehavior(null).Should().BeFalse();
    }

    // ── helpers ──

    private static Task<string> Run(IObservable<string> op, CancellationToken ct) =>
        op.FirstAsync().Timeout(Budget).Await(ct);

    private async Task<MeshNode> Seed(CancellationToken ct)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var node = new MeshNode(UpdatePolicyNodeType.NodeId, UpdatePolicyNodeType.AdminPartition)
        {
            NodeType = UpdatePolicyNodeType.NodeType,
            Name = "Update Policy",
            State = MeshNodeState.Active,
            Content = new UpdatePolicyContent { Policy = UpdatePolicyKind.Stable },
            SyncBehavior = SyncBehavior.ExcludeThisOnly,
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
