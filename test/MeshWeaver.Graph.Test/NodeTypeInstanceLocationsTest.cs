using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins parts 1–2 of Plugins#1127 (#3039): a NodeType can DECLARE where its instances live, the
/// declaration survives JSON and a package-file install, and the mesh projects it
/// <c>nodeType → locations</c> for the storage planner — statically for registered definitions,
/// dynamically for installed ones whose hub is live — while every undeclared type answers null
/// (fail-open: fan out over everything).
/// </summary>
public class NodeTypeInstanceLocationsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string StaticType = "LocatedProbe";

    private static IReadOnlyList<string> StaticLocations => ["namespace:Admin/Menu", "namespace:Admin|Store"];

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        builder.AddMeshNodes(new MeshNode(StaticType)
        {
            Name = "Located probe",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "A static NodeType that declares where its instances live.",
                InstanceLocations = StaticLocations,
            },
        });
        return base.ConfigureMesh(builder);
    }

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private INodeTypeInstanceLocations Projection =>
        Mesh.ServiceProvider.GetRequiredService<INodeTypeInstanceLocations>();

    [Fact]
    public void ADeclaration_RoundTripsThroughJson()
    {
        var node = new MeshNode("Widget", TestPartition)
        {
            Name = "Widget",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition { InstanceLocations = ["namespace:Admin/Menu", "path:Ops/Mail"] },
        };

        var json = JsonSerializer.Serialize(node, Mesh.JsonSerializerOptions);
        json.Should().Contain("\"instanceLocations\"",
            "the field is authored in node files under the serializer's camelCase name");

        var back = JsonSerializer.Deserialize<MeshNode>(json, Mesh.JsonSerializerOptions)!;
        back.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!.InstanceLocations
            .Should().Equal("namespace:Admin/Menu", "path:Ops/Mail");
    }

    /// <summary>
    /// The install shape: a node repo ships the declaration as a <c>.json</c> node file; the same
    /// parser the repo importer uses materialises it; the create persists it; the definition's own
    /// hub then publishes it into the projection — the dynamic lane.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task ADeclaration_SurvivesAPackageFileParse_AnInstall_AndIsProjected()
    {
        var id = "Located" + Guid.NewGuid().ToString("N")[..8];
        var path = $"{TestPartition}/{id}";
        var location = $"namespace:{TestPartition}|Admin";
        var file =
            $$$"""
            {"$type":"MeshNode","id":"{{{id}}}","namespace":"{{{TestPartition}}}","nodeType":"NodeType","name":"{{{id}}}","state":"Active",
             "content":{"$type":"NodeTypeDefinition","description":"declares where its instances live","instanceLocations":["{{{location}}}"]}}
            """;

        var parsed = new JsonFileParser(Mesh.JsonSerializerOptions).Parse($"{id}.json", file, $"{id}.json");
        parsed.Should().NotBeNull("a node file carrying $type/id/nodeType is a node");
        parsed!.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!.InstanceLocations
            .Should().Equal(location);

        await MeshService.CreateNode(parsed!).Take(1)
            .Should().Within(60.Seconds()).Emit("the declaration installs like any other NodeType node");

        var stored = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null).FirstAsync().Timeout(60.Seconds()).Await();
        stored!.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!.InstanceLocations
            .Should().Equal(new[] { location }, "the declaration must round-trip through persistence");

        // The dynamic lane: the definition's own hub (activated by the read above) publishes the
        // declaration. Sanctioned re-query poll — the projection is a synchronous lookup, not a stream.
        var projected = await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Select(_ => Projection.LocationsFor(path))
            .Where(locations => locations is not null)
            .FirstAsync().Timeout(60.Seconds()).Await();
        projected.Should().Equal(location);
    }

    [Fact]
    public void AStaticDeclaration_IsProjected_AndUndeclaredTypesFailOpen()
    {
        Projection.LocationsFor(StaticType).Should().Equal(StaticLocations,
            "a definition registered on the builder is projected from the static fold");
        Projection.LocationsFor("Markdown").Should().BeNull(
            "a type that declares nothing answers null — fan out over everything");
        Projection.LocationsFor("NoSuchType").Should().BeNull("an unknown type is answered slowly, never partially");
        Projection.LocationsFor("").Should().BeNull();
    }

    /// <summary>
    /// The static half of the authoring gate: an in-process declaration for a fold type has no write
    /// boundary to refuse it at, so the fold itself throws, naming the type and the reason.
    /// </summary>
    [Fact]
    public void TheStaticFold_RefusesAFoldTypeDeclaration_NamingTheReason()
    {
        var role = new MeshNode(SecurityQueries.RoleNodeType)
        {
            Name = "Role",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition { InstanceLocations = ["namespace:Admin"] },
        };

        var refusal = Assert.Throws<InvalidOperationException>(() =>
            NodeTypeInstanceLocations.FromStaticNodes([role], Mesh.JsonSerializerOptions, gatedNodeTypes: null));

        refusal.Message.Should().Contain("'Role'").And.Contain("UnanchoredSecurityReads");
    }
}
