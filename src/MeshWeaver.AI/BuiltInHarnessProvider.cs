using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.AI;

/// <summary>
/// Projects every registered <see cref="IHarness"/> (from this assembly and the
/// Claude Code / Copilot assemblies) into a read-only catalog node under the
/// <c>Harness</c> partition, plus a public-read access policy. This is the single
/// source of truth the harness picker binds to — add a harness DLL and its node
/// appears automatically.
/// </summary>
public sealed class BuiltInHarnessProvider(IEnumerable<IHarness> harnesses) : IStaticNodeProvider
{
    /// <summary>
    /// Returns the harness catalog nodes: a public-read access policy plus one read-only
    /// node per registered <c>IHarness</c>, ordered by the harness definition's order.
    /// </summary>
    /// <returns>The harness catalog MeshNodes.</returns>
    public IEnumerable<MeshNode> GetStaticNodes()
    {
        // World-readable catalog, unmodifiable — same shape as the agent catalog.
        yield return new MeshNode("_Policy", HarnessNodeType.RootNamespace)
        {
            NodeType = "PartitionAccessPolicy",
            Name = "Access Policy",
            Content = new PartitionAccessPolicy
            {
                PublicRead = true,
                Create = false,
                Update = false,
                Delete = false,
                Comment = false,
                Thread = false
            }
        };

        foreach (var harness in harnesses.OrderBy(h => h.Definition.Order))
        {
            var def = harness.Definition;
            yield return new MeshNode(def.Id, HarnessNodeType.RootNamespace)
            {
                NodeType = HarnessNodeType.NodeType,
                Name = def.DisplayName ?? def.Id,
                Icon = def.Icon,
                // Carry the harness Order onto the node so the default-composer selection (and the picker)
                // can honor it: MeshWeaver=-1 leads (default), ClaudeCode=1, Copilot=2. Without this every harness
                // node has Order=null, so ObserveDefaultComposer's OrderBy(n => n.Order ?? 0) is a no-op and
                // the default falls to an ARBITRARY harness (a CLI one). A composer stuck on a CLI harness
                // shows the harness's slash-commands in the / menu instead of the nodeType:Skill catalog —
                // i.e. "no skills auto-expand". The agent/model providers already stamp Order; harness didn't.
                Order = def.Order,
                Content = def
            };
        }
    }
}
