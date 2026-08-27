using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Who owns a TOP-LEVEL path that a built-in NodeType and a durable package node both name.
///
/// <para>Every AI catalog ships an in-memory type-definition MeshNode whose path IS its
/// discriminator — <c>Agent</c>, <c>Skill</c>, <c>Harness</c>, <c>Provider</c> — and those are
/// exactly the paths the Store's own packages are published at. Two claimants, one path. The static
/// one wins every serve seam by construction (<c>MeshDataSource.WithMeshNodes</c> seeds the per-node
/// hub from it via <c>WithInitialData</c>, bypassing persistence entirely), so when it is served the
/// durable row is not merely out-ranked, it is unreachable: <c>/Agent</c> and <c>/Skill</c> rendered
/// the built-in NodeType's name and icon with no description and no JSON-LD, because the node the
/// page resolved to was the type definition (#2517). #1209 is the same collision on the INSTALL
/// path, and its cure is the rule this pins: when the deployment serves the partition from the
/// database, the durable row owns the path and the type-def stands down to
/// <see cref="MeshNode.IsDefinitionOnly"/> — still supplying its HubConfiguration BY NAME, never
/// serving as the runtime node.</para>
///
/// <para>🚨 The deployment states that through <c>Features:StaticRepoSync:Partitions</c>, which the
/// AI engine reads off <see cref="MeshBuilder.Configuration"/> at INSTALL time
/// (<see cref="AiMeshModuleAttribute"/>) — so this test goes through the MODULE, with a
/// configuration shaped like the portal's appsettings, rather than calling
/// <c>AddAI(serveFromPartition)</c> with a hand-built set. The sibling guards
/// (<c>AgentPartitionSyncGateTest</c>, <c>NodeTypeCatalogTest</c>) call <c>AddAI</c> directly, which
/// was the real production entry until 16497893b moved it behind the module attribute; they stayed
/// green across that move while every deployed portal served its AI partitions in-memory. That the
/// configuration actually REACHES the builder is a separate claim, pinned by
/// <c>ModuleConfigurationVisibilityTest</c>.</para>
/// </summary>
public class AiCatalogPathOwnershipTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The distributed portal's own appsettings list, verbatim.</summary>
    private static readonly string[] ServedPartitions =
        ["Doc", "Agent", "Model", "Provider", "Harness", "Skill"];

    /// <summary>
    /// The AI engine as a deployment installs it: a configuration carrying the served-partition
    /// list, then the assembly attribute's contributions — the same two steps
    /// <c>MeshBuilder.InstallAssemblies</c> performs for <c>MeshWeaver.AI.dll</c>.
    /// </summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var configured = base.ConfigureMesh(builder);
        configured.WithConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(ServedPartitions.Select((p, i) =>
                new KeyValuePair<string, string?>($"Features:StaticRepoSync:Partitions:{i}", p)))
            .Build());
        return new AiMeshModuleAttribute().BuilderConfigurations
            .Aggregate(configured, (current, configure) => configure(current));
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Nothing static SERVES an AI catalog's top-level path, so the durable row is the only claimant
    /// — while the type definition itself survives, because it is what carries the HubConfiguration
    /// delegate for every instance of that NodeType.
    /// </summary>
    [Theory(Timeout = 60000)]
    [InlineData("Agent")]
    [InlineData("Skill")]
    [InlineData("Harness")]
    public void ADbServedCatalogPath_HasNoStaticClaimant_ButKeepsItsTypeDefinition(string path)
    {
        Mesh.ServiceProvider.FindServedStaticNode(path).Should().BeNull(
            $"'{path}' is served from the database, so nothing may serve it statically — a static "
            + "claimant seeds the per-node hub from a node that is by design NEVER persisted, which "
            + "leaves the path simultaneously unreadable, un-creatable and un-writable (#1209)");

        Mesh.ServiceProvider.DescribeStaticServeCollision(path).Should().BeNull(
            "the collision diagnostic is the one description of this fault; it must have nothing to "
            + "report once the durable row owns the path");

        var definition = Mesh.ServiceProvider.FindStaticNode(path);
        definition.Should().NotBeNull(
            $"the '{path}' type definition must remain — it supplies the HubConfiguration delegate "
            + "by name, which is not serialisable and cannot come from the database");
        definition!.IsDefinitionOnly.Should().BeTrue(
            "a definition is not a runtime node: it proves the type exists and configures instance "
            + "hubs, and is excluded from serving and from query results");
    }

    /// <summary>
    /// The SERVE path, end to end: a durable node published at a catalog's top-level path is what
    /// resolution answers with. <c>IPathResolver.ResolvePath</c> is the exact call the crawler-facing
    /// head makes (<c>SeoResolver.Resolve</c>) before reading the node's name, description, icon and
    /// JSON-LD off it — so this is the assertion that <c>/Agent</c> renders the package and not the
    /// built-in NodeType.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ADurableNodeAtACatalogPath_IsWhatTheServePathResolves()
    {
        var ct = Ct;
        // Stands in for the Store package published at this path — what matters is that it is a
        // DURABLE row at the same top-level path as the built-in "Agent" type definition, carrying
        // the name and description the page is supposed to render.
        await NodeFactory.CreateNode(new MeshNode("Agent")
        {
            Name = "Agents",
            NodeType = "Space",
            Description = "Ready-made agents you can talk to, and a place to build your own.",
            State = MeshNodeState.Active,
        }).FirstAsync().ToTask(ct);

        var resolution = await PathResolver.ResolvePath("Agent")
            .Where(r => r?.Node is not null)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(30))
            .FirstAsync()
            .ToTask(ct);

        resolution.Should().NotBeNull("'/Agent' must resolve to a node");
        resolution!.Remainder.Should().BeNullOrEmpty("the whole path was matched");
        resolution.Node!.Name.Should().Be("Agents",
            "the page must serve the AUTHORED node. 'Agent' here would be the built-in NodeType "
            + "definition winning the path — the #2517 symptom, visible as og:title=\"Agent\"");
        resolution.Node.Description.Should().NotBeNullOrEmpty(
            "the type definition carries no description, so an empty one is the tell that the "
            + "definition was served: the live pages emitted no og:description and no JSON-LD");
    }
}
