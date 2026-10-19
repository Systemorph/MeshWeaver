using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Domain;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Completion;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>Stage vocabulary of the probe content — a closed C# enum, so the schema lists its members.</summary>
public enum CompletionProbeStage
{
    /// <summary>Being drafted.</summary>
    Draft = 1,
    /// <summary>Live.</summary>
    Active,
    /// <summary>Done with.</summary>
    Archived,
}

/// <summary>The probe NodeType's content: an enum, a boolean, and a <c>[MeshNode]</c> reference.</summary>
public record CompletionProbeContent
{
    /// <summary>Where the record stands.</summary>
    public CompletionProbeStage Stage { get; init; } = CompletionProbeStage.Draft;

    /// <summary>Whether the record is pinned.</summary>
    public bool Pinned { get; init; }

    /// <summary>The kind of record — a member of the probe dimension.</summary>
    [MeshNode("nodeType:" + QueryCompleterMeshTest.DimensionType)]
    public string? Kind { get; init; }
}

/// <summary>
/// The reactive half of query autocomplete against a real mesh: <see cref="QueryCompleter"/> learns the
/// content type of the NodeType named in the box's hidden query from the mesh-wide content-type registry
/// (activating one record of the type through its node stream when the type is not registered yet),
/// reads it through the generator the mesh publishes schemas with, resolves a <c>[MeshNode]</c>
/// property's members through the <c>x-meshNode</c> extension that generator writes, and adds name
/// matches for a free-text word.
/// </summary>
public class QueryCompleterMeshTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The probe content NodeType.</summary>
    public const string ProbeType = "CompletionProbe";

    /// <summary>The probe dimension NodeType whose members are <see cref="CompletionProbeContent.Kind"/>'s values.</summary>
    public const string DimensionType = "CompletionProbeKind";

    private const string Hidden = "nodeType:" + ProbeType + " namespace:" + TestPartition;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        builder.AddMeshNodes(
            new MeshNode(ProbeType)
            {
                Name = "Completion probe",
                HubConfiguration = config => config.WithContentType<CompletionProbeContent>(),
            },
            new MeshNode("Client", TestPartition) { Name = "Client", NodeType = DimensionType },
            new MeshNode("Partner", TestPartition) { Name = "Partner", NodeType = DimensionType },
            new MeshNode("Zanzibar", TestPartition) { Name = "Zanzibar Quarterly Report", NodeType = "Markdown" },
            // One record of the probe type: the content type is learned from a hub of that type.
            new MeshNode("FirstProbe", TestPartition) { Name = "First probe", NodeType = ProbeType, Content = new CompletionProbeContent() });
        builder.ConfigureHub(config => config.WithType<CompletionProbeContent>(nameof(CompletionProbeContent)));
        return base.ConfigureMesh(builder);
    }

    private Task<QueryCompletionResult> Complete(string textWithCaret, System.Threading.CancellationToken ct, string? hidden = Hidden)
    {
        var caret = textWithCaret.IndexOf('^');
        var completer = new QueryCompleter(Mesh.ServiceProvider.GetRequiredService<IMeshService>(), Mesh);
        return completer
            .Complete(new QueryCompletionRequest(textWithCaret.Remove(caret, 1), caret, hidden, TestPartition))
            .LastAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);
    }

    [Fact(Timeout = 120_000)]
    public async Task AContentPropertyIsOfferedFromTheSchemaOfTheNodeTypeInContext()
    {
        var result = await Complete("content.^", TestContext.Current.CancellationToken);

        result.Items.Select(i => i.InsertText).Should().Contain(["stage:", "pinned:", "kind:"],
            "the properties come from the content type of the hidden query's nodeType");
        result.ReplaceStart.Should().Be("content.".Length);
    }

    [Fact(Timeout = 120_000)]
    public async Task AnEnumPropertyOffersItsMembers()
    {
        var result = await Complete("content.stage:^", TestContext.Current.CancellationToken);

        result.Items.Select(i => i.Label).Order().ToArray().Should().Equal("Active", "Archived", "Draft");
    }

    [Fact(Timeout = 120_000)]
    public async Task AMeshNodeReferenceOffersTheMembersOfItsQuery()
    {
        var result = await Complete("content.kind:^", TestContext.Current.CancellationToken);

        result.Items.Select(i => i.InsertText).Order().ToArray().Should().Equal(
            new[] { $"{TestPartition}/Client", $"{TestPartition}/Partner" },
            "the schema carries [MeshNode(\"nodeType:" + DimensionType + "\")] as x-meshNode, and its hits are the values");
    }

    [Fact(Timeout = 120_000)]
    public async Task WithoutANodeTypeInContextNoContentPropertyIsInvented()
    {
        var result = await Complete("content.^", TestContext.Current.CancellationToken, hidden: "namespace:" + TestPartition);

        result.Items.Should().BeEmpty();
    }

    [Fact(Timeout = 120_000)]
    public async Task AFreeTextWordOffersTheNodesItNames()
    {
        var result = await Complete("Zanzi^", TestContext.Current.CancellationToken, hidden: "namespace:" + TestPartition);

        result.Items.Should().Contain(i => i.Path == $"{TestPartition}/Zanzibar"
                                           && i.InsertText == $"path:{TestPartition}/Zanzibar",
            "a free-text word is matched against node names, and accepting the hit narrows the query to that node");
    }

    /// <summary>
    /// <c>@{node}/schema/</c> describes the node's CONTENT type — the type its data source declares
    /// (<see cref="MeshWeaver.Data.IContentTypeSource"/>) — and carries a reference's queries as
    /// <c>x-meshNode</c>. It used to describe "the first registered type that is not MeshNode", which for a
    /// node whose content has no type source of its own was MeshNode itself.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ANodeAddressedSchemaDescribesTheContentTypeWithItsReferenceQueries()
    {
        var answer = await new MeshWeaver.AI.MeshOperations(Mesh).Get($"{TestPartition}/FirstProbe/schema/")
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);

        answer.Should().Contain(nameof(CompletionProbeContent), "the schema names the content type");
        answer.Should().Contain("x-meshNode", "the [MeshNode] reference states its members' query");
        answer.Should().Contain("nodeType:" + DimensionType);
    }
}
