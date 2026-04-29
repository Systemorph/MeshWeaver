using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Acme.Test;

/// <summary>
/// Diagnostic for the path-resolution failure observed in
/// <see cref="TodoDataChangeWorkflowTest.MultipleTodoHubs_CanBeAccessedIndependently"/>:
/// reading <c>ACME/ProductLaunch/Todo/DefinePersona</c> succeeds while
/// <c>ACME/ProductLaunch/Todo/LaunchEvent</c> fails with "No node found at X.
/// Closest ancestor is 'ACME/ProductLaunch'". Both files exist on disk in the
/// same directory and both serialize as MeshNode-shaped JSON; the difference is
/// that LaunchEvent.json has the "minimal" schema (no top-level <c>path</c>,
/// <c>state</c>, <c>lastModified</c>, <c>version</c>) while DefinePersona.json
/// has the "full" schema with all of those.
///
/// This test isolates the failure mode at each layer (raw IO → JsonSerializer
/// → IStorageAdapter → IMeshStorage → MeshCatalog) so we can pinpoint *which*
/// layer drops LaunchEvent.
/// </summary>
public class AcmePathResolutionDiagnosticTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var dataDirectory = TestPaths.SamplesGraphData;
        return builder
            .UseMonolithMesh()
            .AddPartitionedFileSystemPersistence(dataDirectory)
            .AddAcme()
            .AddGraph();
    }

    /// <summary>
    /// Reads both files via raw <see cref="JsonSerializer.Deserialize{T}(string, JsonSerializerOptions)"/>
    /// using the hub's serialization options. If LaunchEvent fails here while
    /// DefinePersona succeeds, the bug is in the polymorphic content
    /// deserialization (<c>$type</c> discriminator for the <c>Todo</c> record
    /// not registered when loaded from a file with no top-level state/version).
    /// </summary>
    [Fact]
    public async Task RawJsonDeserialize_BothFiles_ProduceMeshNode()
    {
        var dataRoot = TestPaths.SamplesGraphData;
        var defJson = await File.ReadAllTextAsync(
            Path.Combine(dataRoot, "ACME", "ProductLaunch", "Todo", "DefinePersona.json"));
        var launchJson = await File.ReadAllTextAsync(
            Path.Combine(dataRoot, "ACME", "ProductLaunch", "Todo", "LaunchEvent.json"));

        var options = Mesh.JsonSerializerOptions;

        Output.WriteLine("Attempting DefinePersona.json deserialization...");
        var defNode = JsonSerializer.Deserialize<MeshNode>(defJson, options);
        Output.WriteLine($"  result: Path={defNode?.Path} Content={defNode?.Content?.GetType().Name}");

        Output.WriteLine("Attempting LaunchEvent.json deserialization...");
        var launchNode = JsonSerializer.Deserialize<MeshNode>(launchJson, options);
        Output.WriteLine($"  result: Path={launchNode?.Path} Content={launchNode?.Content?.GetType().Name}");

        defNode.Should().NotBeNull("DefinePersona must deserialize");
        launchNode.Should().NotBeNull(
            "LaunchEvent must deserialize too — both files have the same MeshNode shape, " +
            "and a missing top-level state/version/lastModified/path is no excuse for failing");
    }

    /// <summary>
    /// Hits the per-node hub via <see cref="MonolithMeshTestBase.ReadNodeAsync(string)"/>
    /// (GetDataRequest → MeshNodeReference). If LaunchEvent returns null while
    /// DefinePersona returns a node, the bug is downstream of the storage adapter
    /// (routing / catalog / per-hub data source).
    /// </summary>
    [Fact]
    public async Task ReadNodeAsync_BothPaths_ReturnNonNull()
    {
        var defNode = await ReadNodeAsync("ACME/ProductLaunch/Todo/DefinePersona");
        Output.WriteLine($"DefinePersona via ReadNodeAsync: {(defNode == null ? "<null>" : defNode.Path)}");
        var launchNode = await ReadNodeAsync("ACME/ProductLaunch/Todo/LaunchEvent");
        Output.WriteLine($"LaunchEvent via ReadNodeAsync: {(launchNode == null ? "<null>" : launchNode.Path)}");

        defNode.Should().NotBeNull("DefinePersona must resolve via the per-node hub's MeshNodeReference reducer");
        launchNode.Should().NotBeNull(
            "LaunchEvent must resolve too — both .json files live in the same partition directory " +
            "and have the same MeshNode shape");
    }

    /// <summary>
    /// Reverse order: LaunchEvent first, then DefinePersona. If only the FIRST one
    /// in either order resolves, the bug is order-dependent (e.g. catalog state
    /// mutated by the first lookup making the second one match a stale prefix).
    /// </summary>
    [Fact]
    public async Task ReadNodeAsync_ReverseOrder_BothPathsReturnNonNull()
    {
        var launchNode = await ReadNodeAsync("ACME/ProductLaunch/Todo/LaunchEvent");
        Output.WriteLine($"LaunchEvent via ReadNodeAsync: {(launchNode == null ? "<null>" : launchNode.Path)}");
        var defNode = await ReadNodeAsync("ACME/ProductLaunch/Todo/DefinePersona");
        Output.WriteLine($"DefinePersona via ReadNodeAsync: {(defNode == null ? "<null>" : defNode.Path)}");

        launchNode.Should().NotBeNull("LaunchEvent must resolve regardless of access order");
        defNode.Should().NotBeNull("DefinePersona must resolve regardless of access order");
    }

    /// <summary>
    /// Hits the routing service's <c>ResolvePath</c> through <see cref="IPathResolver"/>.
    /// If LaunchEvent gets a non-empty <c>Remainder</c> (i.e. the resolver finds
    /// only the closest ancestor), the bug is in <see cref="MeshCatalog.ResolvePathCore"/>'s
    /// segment walk — the underlying <see cref="IMeshStorage.GetNode"/> probably
    /// returned null for some reason, even though the file exists.
    /// </summary>
    [Fact]
    public async Task PathResolver_BothPaths_ResolveToExactNodeNoRemainder()
    {
        var defResolution = await PathResolver.ResolvePath("ACME/ProductLaunch/Todo/DefinePersona").FirstAsync();
        Output.WriteLine($"DefinePersona resolution: prefix={defResolution?.Prefix} remainder={defResolution?.Remainder}");

        var launchResolution = await PathResolver.ResolvePath("ACME/ProductLaunch/Todo/LaunchEvent").FirstAsync();
        Output.WriteLine($"LaunchEvent resolution: prefix={launchResolution?.Prefix} remainder={launchResolution?.Remainder}");

        defResolution!.Prefix.Should().Be("ACME/ProductLaunch/Todo/DefinePersona");
        defResolution.Remainder.Should().BeNullOrEmpty(
            "DefinePersona path is exact — Remainder must be empty");

        launchResolution!.Prefix.Should().Be("ACME/ProductLaunch/Todo/LaunchEvent",
            "LaunchEvent must resolve to itself, not to its ancestor 'ACME/ProductLaunch'");
        launchResolution.Remainder.Should().BeNullOrEmpty(
            "LaunchEvent path is exact — Remainder must be empty (a non-empty Remainder is the bug we're chasing)");
    }
}
