using System;
using System.Reactive.Linq;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 A cross-hub <c>stream.Update</c> must PERSIST at the owning per-node hub even when
/// the caller never subscribed to / read the node first. The UpdateNodeRequest deletion
/// surfaced a foundation nuance: the cache's <c>UpdateRemote</c> opened the owner read
/// under the SYSTEM identity (for the patch baseline) but the optimistic emit could land
/// before the owner applied the patch, and a fresh read-back returned the stale value.
/// </summary>
public class CrossHubWritePersistenceTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    [Fact(Timeout = 60000)]
    public void StreamUpdate_CrossHub_NoPriorSubscription_Persists()
    {
        var factory = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var path = $"{TestPartition}/CrossHubWrite-{Guid.NewGuid():N}";

        // FromPath splits the namespace ("TestData") from the id — `new MeshNode(path)` would
        // bake the slash into the Id with an EMPTY namespace, which the PartitionWriteGuard
        // (correctly) treats as a malformed top-level node and rejects. TestData is a registered
        // partition namespace, so the nested create is allowed.
        var node = MeshNode.FromPath(path) with
        {
            Name = "Original",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        };
        factory.CreateNode(node).Should().Within(60.Seconds()).Emit();

        // Write via stream.Update WITHOUT any prior read/subscription on `path`.
        Mesh.GetMeshNodeStream(path)
            .Update(n => n with { Name = "Updated" })
            .Should().Within(15.Seconds()).Emit();

        // Read back — must reflect the PERSISTED update, not the stale create.
        var after = ReadNode(path)
            .Should().Within(30.Seconds()).Match(n => n is not null && n.Name == "Updated");
        after!.Name.Should().Be("Updated",
            "a cross-hub stream.Update must persist even without a prior subscription/read");
    }
}
