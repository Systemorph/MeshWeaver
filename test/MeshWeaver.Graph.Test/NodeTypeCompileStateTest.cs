using System.Collections.Immutable;
using System.Text.Json;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Services.LanguageServer;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins the compile-state SATELLITE value (issue #748, phase 1): <see cref="NodeTypeCompileState"/>
/// carries exactly the operational members the sync seams mask
/// (<see cref="NodeTypeOperationalContent.MemberNames"/>), projects faithfully from the
/// definition, and round-trips through the satellite node shape
/// (<see cref="NodeTypeCompileStateMirror.StateNode"/> / <see cref="NodeTypeCompileStateMirror.Parse"/>).
/// If the two member sets ever drift, the satellite would silently stop carrying a field the
/// node no longer syncs — the drift MUST fail a test, not surface in production.
/// </summary>
public class NodeTypeCompileStateTest
{
    private static readonly JsonSerializerOptions CamelCase =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public void StateMembers_AreExactlyTheMaskedOperationalMembers()
    {
        var stateMembers = typeof(NodeTypeCompileState).GetProperties()
            .Where(p => p.Name != nameof(NodeTypeCompileState.IsEmpty))
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var masked in NodeTypeOperationalContent.MemberNames)
            Assert.True(stateMembers.Contains(masked),
                $"'{masked}' is masked by the sync seams but missing from NodeTypeCompileState — "
                + "the satellite would silently drop it.");
        foreach (var member in stateMembers)
            Assert.True(NodeTypeOperationalContent.MemberNames.Contains(member),
                $"'{member}' is carried by NodeTypeCompileState but not masked by the sync seams — "
                + "either mask it or it is not operational state.");
    }

    [Fact]
    public void FromDefinition_ProjectsEveryField_AndNullPassesThrough()
    {
        Assert.Null(NodeTypeCompileState.FromDefinition(null));

        var definition = new NodeTypeDefinition
        {
            Configuration = "config => config",
            CompilationStatus = CompilationStatus.Ok,
            CompilationError = "none",
            LastCompileStartedAt = new DateTimeOffset(2026, 8, 2, 8, 53, 45, TimeSpan.Zero),
            LastCompileSucceededAt = new DateTimeOffset(2026, 8, 2, 8, 53, 46, TimeSpan.Zero),
            LastCompiledVersion = 1095,
            LastCompilationActivityPath = "Store/Plugin/_Activity/compile-x",
            LatestReleasePath = "Store/Plugin/Release/x",
            RequestedReleaseAt = new DateTimeOffset(2026, 8, 2, 8, 53, 45, TimeSpan.Zero),
            RequestedReleaseForce = true,
            RequestedReleaseBy = "rbuergi",
            LastReleaseRequestHandledAt = new DateTimeOffset(2026, 8, 2, 8, 53, 45, TimeSpan.Zero),
            ReleaseNotes = "notes",
            LatestAssemblyCollection = "local",
            LatestAssemblyPath = "Store_Plugin/v1095.dll",
            CompiledSources = new Dictionary<string, long> { ["a"] = 1 },
            CurrentSourceVersions = new Dictionary<string, long> { ["a"] = 2 },
            CompiledFrameworkVersion = "37cd668a",
        };
        var state = NodeTypeCompileState.FromDefinition(definition)!;
        Assert.Equal(CompilationStatus.Ok, state.CompilationStatus);
        Assert.Equal("none", state.CompilationError);
        Assert.Equal(1095, state.LastCompiledVersion);
        Assert.Equal("Store/Plugin/_Activity/compile-x", state.LastCompilationActivityPath);
        Assert.Equal("Store/Plugin/Release/x", state.LatestReleasePath);
        Assert.True(state.RequestedReleaseForce);
        Assert.Equal("rbuergi", state.RequestedReleaseBy);
        Assert.Equal("notes", state.ReleaseNotes);
        Assert.Equal("local", state.LatestAssemblyCollection);
        Assert.Equal("Store_Plugin/v1095.dll", state.LatestAssemblyPath);
        Assert.Equal(1, state.CompiledSources!["a"]);
        Assert.Equal(2, state.CurrentSourceVersions!["a"]);
        Assert.Equal("37cd668a", state.CompiledFrameworkVersion);
        Assert.False(state.IsEmpty);
    }

    [Fact]
    public void IsEmpty_TrueOnlyWhenNothingIsRecorded()
    {
        Assert.True(NodeTypeCompileState.FromDefinition(
            new NodeTypeDefinition { Configuration = "authored only" })!.IsEmpty);
        Assert.False(NodeTypeCompileState.FromDefinition(
            new NodeTypeDefinition { CompilationStatus = CompilationStatus.Pending })!.IsEmpty);
        Assert.False(NodeTypeCompileState.FromDefinition(
            new NodeTypeDefinition { RequestedReleaseForce = true })!.IsEmpty);
        Assert.False(NodeTypeCompileState.FromDefinition(
            new NodeTypeDefinition { CurrentSourceVersions = new Dictionary<string, long>() })!.IsEmpty);
    }

    [Fact]
    public void FromDefinition_ReadsJsonObjectContent_TheShapeAFreshNodeCarries()
    {
        // A just-created node's own stream carries the builder's JsonObject content until the
        // pipeline re-types it; ContentAs must recover it (the silent-null found 2026-08-02).
        var node = new MeshNode("Widget", "MirrorSpace")
        {
            NodeType = MeshNode.NodeTypePath,
            Content = System.Text.Json.Nodes.JsonNode.Parse(
                """
                {"$type":"NodeTypeDefinition","configuration":"config => config",
                 "lastCompiledVersion":1082,"latestAssemblyPath":"Widget/v1082.dll"}
                """)!.AsObject(),
        };
        var definition = node.ContentAs<NodeTypeDefinition>(CamelCase);
        Assert.NotNull(definition);
        Assert.Equal(1082, definition!.LastCompiledVersion);
        var state = NodeTypeCompileState.FromDefinition(definition)!;
        Assert.False(state.IsEmpty);
        Assert.Equal("Widget/v1082.dll", state.LatestAssemblyPath);
    }

    [Fact]
    public void StateNode_RoundTrips_ThroughParse()
    {
        var state = new NodeTypeCompileState
        {
            CompilationStatus = CompilationStatus.Ok,
            LastCompiledVersion = 1095,
            LatestAssemblyPath = "Store_Plugin/v1095.dll",
            CompiledSources = new Dictionary<string, long> { ["Store/Plugin/Source/PluginGate"] = 639209062054682760 },
            CompilationDiagnostics =
            [
                new DiagnosticInfo("CS0103", DiagnosticSeverity.Error, "The name 'x' does not exist",
                    Location: null),
            ],
        };
        var node = NodeTypeCompileStateMirror.StateNode("Store/Plugin", state, CamelCase);
        Assert.Equal("Store/Plugin/_Activity/compile-state", node.Path);
        Assert.Equal("Store/Plugin", node.MainNode);
        Assert.Equal(ActivityNodeType.NodeType, node.NodeType);

        var parsed = NodeTypeCompileStateMirror.Parse(node, CamelCase)!;
        Assert.Equal(CompilationStatus.Ok, parsed.CompilationStatus);
        Assert.Equal(1095, parsed.LastCompiledVersion);
        Assert.Equal("Store_Plugin/v1095.dll", parsed.LatestAssemblyPath);
        Assert.Equal(639209062054682760, parsed.CompiledSources!["Store/Plugin/Source/PluginGate"]);
        Assert.Single(parsed.CompilationDiagnostics!);
        Assert.Equal("CS0103", parsed.CompilationDiagnostics![0].Id);

        Assert.Null(NodeTypeCompileStateMirror.Parse(null, CamelCase));
        Assert.Null(NodeTypeCompileStateMirror.Parse(
            new MeshNode("x", "y") { NodeType = "Markdown", Content = "not an activity" }, CamelCase));
    }
}
