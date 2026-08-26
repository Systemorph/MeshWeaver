using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Governance guard for the self-referential-satellite defect class (#2383): a node filed as a
/// <c>_</c>-prefixed SIBLING of another node — <c>{ns}/_Policy</c>, <c>{ns}/_Provider</c>,
/// <c>{ns}/_GitSync</c>, <c>Plugins/_DefaultInstallLedger</c> — must carry
/// <see cref="MeshNode.MainNode"/> pointing at the node it belongs to, never at ITSELF.
///
/// <para><b>Why a guard and not just correct mint sites.</b> <see cref="MeshNode.MainNode"/>
/// self-defaults to the node's own path, so getting this right is an act of memory at every writer.
/// Careful hand-fixing of the sites found by grepping <c>new MeshNode("_…</c> corrected six of them
/// and missed seven — the target-typed spelling <c>new("_Policy", ns)</c> does not match that
/// pattern, and two writers are raw SQL. A control that enumerates the nodes the mesh ACTUALLY
/// serves cannot be fooled by a spelling.</para>
///
/// <para><b>What a self-referential satellite costs.</b> <c>MainNode == Path</c> is the framework's
/// literal definition of a MAIN node, so the node becomes content: <c>is:main</c> KEEPS exactly the
/// rows where <c>MainNode == Path</c> (<c>PostgreSqlSqlGenerator</c> emits
/// <c>n.main_node = n.path</c>; <c>StaticNodeQueryProvider</c> skips a node when
/// <c>MainNode != Path</c>), and <c>search_across_schemas</c> hard-filters every union branch on the
/// same predicate — unconditionally, not only when the caller asks for <c>is:main</c>. A satellite
/// is normally excluded PRECISELY because its MainNode differs from its path; one pointing at itself
/// passes instead. An internal governance node therefore lists under "Contents" on the page and
/// appears in mesh-wide search. It also mis-classifies the node on copy/move
/// (<c>MeshExtensions</c> splits <c>IncludeSatellites</c> from <c>IncludeDescendants</c> on
/// <c>MainNode != Path</c>) and, for the types that delegate permissions, defeats
/// <c>SatelliteAccessRule</c>'s delegation — its degenerate branch falls back to a path-based check
/// the moment <c>MainNode</c> equals the node's own path.</para>
/// </summary>
public class SatelliteMainNodeMintGuard(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// The seeds this sweep MUST have reached. Without this the test is vacuous: a composition that
    /// registered none of the providers would sweep an empty set and report success, which is the
    /// "a verification step that cannot fail is not a verification step" trap. Each entry is a
    /// distinct mint site in a distinct assembly, so the list also documents the inventory.
    /// </summary>
    private static readonly string[] RequiredInventory =
    [
        "Role/_Policy",      // MeshWeaver.Graph  — RoleNodeType.BuiltInRolesProvider
        "License/_Policy",   // MeshWeaver.Graph  — LicenseNodeType.CreatePolicy
        "_Setting/_Policy",  // MeshWeaver.Graph  — GlobalSettingsNodeType.CreatePolicy
        "Doc/_Policy",       // MeshWeaver.Documentation — DocumentationNodeProvider
    ];

    [Fact]
    public void EveryStaticallySeededSatelliteCarriesItsMainNode()
    {
        // Both seeding routes at once: AddMeshNodes(...) surfaces through StaticMeshNodeListProvider,
        // and every IStaticNodeProvider registers here too. DocumentationNodeProvider is added
        // explicitly — it is a legacy provider the test mesh does not register, and it holds the
        // SECOND Doc policy mint.
        var nodes = Mesh.ServiceProvider.GetServices<IStaticNodeProvider>()
            .SelectMany(p => p.GetStaticNodes())
            .Concat(new DocumentationNodeProvider(Mesh.ServiceProvider).GetStaticNodes())
            .ToList();

        var seen = nodes.Select(n => n.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = RequiredInventory.Where(p => !seen.Contains(p)).ToList();
        Assert.True(missing.Count == 0,
            "The sweep never reached " + string.Join(", ", missing)
            + " — it would have passed having verified nothing. Either the composition stopped "
            + "registering that seed (a separate defect) or the path was renamed; fix the inventory "
            + "deliberately, never by deleting the entry.");

        var offenders = nodes
            // The SAME classifier the create/upsert normalization uses — one definition, so the
            // guard's inventory and the framework's behaviour cannot drift apart.
            .Where(SatelliteTableMapping.IsSiblingSatellite)
            .Where(n => string.Equals(n.MainNode, n.Path, StringComparison.Ordinal))
            .Select(n => n.Path)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "These satellites are minted pointing at THEMSELVES, which makes them main nodes by the "
            + "framework's own definition (is:main keeps exactly the rows where MainNode == Path) — "
            + "they list as content and show up in mesh-wide search:\n  "
            + string.Join("\n  ", offenders)
            + "\nMint them with MeshNode.Satellite(id, mainNode) — never `new MeshNode(id, ns)`, "
            + "whose MainNode defaults to the node's own path.");
    }

    /// <summary>
    /// The framework half: a sibling satellite written through the create handler gets its MainNode
    /// DERIVED, so a writer that forgets the field can no longer persist the defect shape. Static
    /// seeds bypass this handler entirely — which is why the sweep above exists as well.
    /// </summary>
    [Fact]
    public async Task CreatingASiblingSatellite_DerivesItsMainNode()
    {
        var created = await NodeFactory.CreateNode(
            // Deliberately the RAW constructor — MainNode arrives equal to the node's own path,
            // exactly as every unfixed mint site produces it.
            new MeshNode("_Policy", TestPartition)
            {
                NodeType = "PartitionAccessPolicy",
                Name = "Access Policy",
                Content = new PartitionAccessPolicy { Create = false, Update = false, Delete = false },
            }).Should().Emit();

        Assert.Equal($"{TestPartition}/_Policy", created.Path);
        Assert.Equal(TestPartition, created.MainNode);
        Assert.NotEqual(created.Path, created.MainNode);
    }

    /// <summary>
    /// The other direction, and the one that bounds the blast radius: ordinary content is NOT
    /// touched. A main node must keep <c>MainNode == Path</c> or it drops out of every <c>is:main</c>
    /// listing and out of <c>search_across_schemas</c> — i.e. the fix would hide real content.
    /// </summary>
    [Fact]
    public async Task CreatingOrdinaryContent_LeavesItAMainNode()
    {
        var id = $"Page{Guid.NewGuid():N}"[..12];
        var created = await NodeFactory.CreateNode(
            new MeshNode(id, TestPartition) { NodeType = "Markdown", Name = "A page" })
            .Should().Emit();

        Assert.Equal($"{TestPartition}/{id}", created.Path);
        Assert.Equal(created.Path, created.MainNode);
    }

    /// <summary>
    /// An explicitly-chosen MainNode is never rewritten. The derivation triggers only on the
    /// record's never-set default (<c>MainNode == Path</c>), so a writer that deliberately points a
    /// node at a parent keeps its choice.
    /// </summary>
    [Fact]
    public async Task AnExplicitMainNode_IsPreserved()
    {
        var id = $"Cfg{Guid.NewGuid():N}"[..10];
        var created = await NodeFactory.CreateNode(
            new MeshNode($"_{id}", TestPartition)
            {
                NodeType = "Markdown",
                Name = "Explicitly parented",
                MainNode = TestPartition,
            }).Should().Emit();

        Assert.Equal(TestPartition, created.MainNode);
    }
}
