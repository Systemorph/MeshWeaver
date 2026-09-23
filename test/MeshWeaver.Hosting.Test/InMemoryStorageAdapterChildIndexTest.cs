using MeshWeaver.Messaging;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Pins the CHILDREN INDEX behind <see cref="InMemoryStorageAdapter.ListChildPaths"/> (2026-09-13):
/// the listing used to scan every key of the store per call, quadratic in the mesh size for the
/// plugin gate's subtree queries. The index must answer exactly what the scan answered — direct
/// child NODES, and IMPLIED directories (a prefix with descendants but no node of its own) — and
/// follow deletes, version-gated writes and a dictionary seeded behind the adapter's back.
/// </summary>
public class InMemoryStorageAdapterChildIndexTest
{
    private static readonly JsonSerializerOptions Options = new();

    private static MeshNode Node(string path)
    {
        var slash = path.LastIndexOf('/');
        return new MeshNode(path[(slash + 1)..], slash < 0 ? "" : path[..slash]) { Name = path, NodeType = "Markdown", State = MeshNodeState.Active };
    }

    private static async Task<(string[] Nodes, string[] Dirs)> ListAsync(InMemoryStorageAdapter adapter, string? parent)
    {
        var (nodes, dirs) = await adapter.ListChildPaths(parent).FirstAsync().Await();
        return (nodes.OrderBy(x => x).ToArray(), dirs.OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task Lists_direct_nodes_and_implied_directories_and_follows_deletes()
    {
        var adapter = new InMemoryStorageAdapter();
        foreach (var p in new[] { "Hosting", "Hosting/Build", "Hosting/Build/Source/BuildContent", "Store/Plugin", "Doc" })
            await adapter.Write(Node(p), Options).FirstAsync().Await();

        var root = await ListAsync(adapter, null);
        Assert.Equal(new[] { "Doc", "Hosting" }, root.Nodes);
        Assert.Equal(new[] { "Store" }, root.Dirs);          // Store has no node of its own, only a descendant

        var hosting = await ListAsync(adapter, "Hosting");
        Assert.Equal(new[] { "Hosting/Build" }, hosting.Nodes);
        Assert.Empty(hosting.Dirs);                           // Build IS a node, so it is not an implied directory

        var build = await ListAsync(adapter, "Hosting/Build");
        Assert.Empty(build.Nodes);
        Assert.Equal(new[] { "Hosting/Build/Source" }, build.Dirs);

        await adapter.Delete("Hosting/Build/Source/BuildContent").FirstAsync().Await();
        Assert.Empty((await ListAsync(adapter, "Hosting/Build")).Dirs);   // the implied directory went with its last descendant
        Assert.True(await adapter.DeleteIfExists("Store/Plugin").FirstAsync().Await());
        Assert.Empty((await ListAsync(adapter, null)).Dirs);              // and so did Store
    }

    [Fact]
    public async Task Version_gated_writes_and_a_seeded_dictionary_are_indexed_too()
    {
        var nodes = new System.Collections.Concurrent.ConcurrentDictionary<string, MeshNode>(System.StringComparer.OrdinalIgnoreCase);
        nodes["Seeded/Deep/Leaf"] = Node("Seeded/Deep/Leaf");            // behind the adapter's back
        var adapter = new InMemoryStorageAdapter(nodes, new(System.StringComparer.OrdinalIgnoreCase));
        Assert.Equal(new[] { "Seeded" }, (await ListAsync(adapter, null)).Dirs);   // rebuilt on first use
        Assert.True(await adapter.WriteIfVersion(Node("Seeded/Fresh"), 0, Options).FirstAsync().Await());
        Assert.Equal(new[] { "Seeded/Fresh" }, (await ListAsync(adapter, "Seeded")).Nodes);
        Assert.Equal(new[] { "Seeded/Deep" }, (await ListAsync(adapter, "Seeded")).Dirs);
        // a second adapter over the SAME dictionary sees the same index
        var twin = new InMemoryStorageAdapter(nodes, new(System.StringComparer.OrdinalIgnoreCase));
        Assert.Equal(new[] { "Seeded/Fresh" }, (await ListAsync(twin, "Seeded")).Nodes);
    }
}
