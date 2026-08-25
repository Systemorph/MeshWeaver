using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

#pragma warning disable CS1591

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 <b>The adopt-only gate on FIRST ACCESS</b> (MeshWeaver#2193 §A, the deepest seam): on a mesh
/// that sets <c>Modules:RequirePrebuilt</c>, a NodeType touched without an adopted assembly must
/// PARK with a named refusal — never enter Roslyn.
///
/// <para>The shape pinned here is the one nobody can observe from the install lane: a perfectly
/// valid type (its configuration compiles fine anywhere else) whose bundle simply never landed
/// for this identity. The first-build kickoff flips it to Pending; the compile watcher's gate
/// must then (a) settle it at <c>Error</c> with a message naming the policy key, the type, the
/// framework identity and the fix, (b) park it so no later trigger can storm, and (c) — the
/// observable proof — never start a compile: the park registry's attempt counter stays at ZERO
/// (it increments only inside the real dispatch). The flag-off contract is the whole existing
/// suite, where the same type compiles.</para>
/// </summary>
public class RequirePrebuiltParkTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Partition = "RequirePrebuiltTest";
    private const string NodeTypeId = "NeverBaked";
    private const string NodeTypePath = $"{Partition}/{NodeTypeId}";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .ConfigureServices(services =>
                // The deployment opt-in under test — the same in-memory registration idiom the
                // Content tests use.
                services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        [PrebuiltAssemblySeeder.RequirePrebuiltConfigKey] = "true",
                    })
                    .Build()));

    [Fact(Timeout = 120_000)]
    public async Task AValidTypeWithNoAdoptedAssembly_ParksWithTheNamedRefusal_AndNeverCompiles()
    {
        var workspace = Mesh.GetWorkspace();
        var parkRegistry = Mesh.ServiceProvider.GetRequiredService<NodeTypeCompileParkRegistry>();

        // A type that WOULD compile anywhere the flag is off — the point is that it is refused
        // for lack of a bundle, not for lack of correctness.
        await NodeFactory.CreateNode(new MeshNode(NodeTypeId, Partition)
        {
            Name = "Never baked type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Valid configuration, no prebuilt assembly for this lane.",
                Configuration = "config => config",
            }
        }).Should().Emit();

        // (a) It settles at Error with the NAMED refusal — never sits at Pending, never hangs.
        var settled = await workspace.GetMeshNodeStream(NodeTypePath)
            .Should().Within(60.Seconds())
            .Match(n => n.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Error);
        var def = (NodeTypeDefinition)settled.Content!;
        def.CompilationError.Should().NotBeNull();
        def.CompilationError.Should().Contain(PrebuiltAssemblySeeder.RequirePrebuiltConfigKey,
            "the refusal must name the policy that refused");
        def.CompilationError.Should().Contain(NodeTypePath, "…and the type");
        def.CompilationError.Should().Contain(PrebuiltAssemblySeeder.LiveFrameworkMvid,
            "…and the lane the assembly is missing for");
        def.CompilationError.Should().Contain($"'{Partition}'",
            "…and the package whose bundle would fix it");

        // (b) PARKED — bounded and visible, like any terminal failure.
        parkRegistry.IsParked(NodeTypePath).Should().BeTrue(
            "a require-prebuilt refusal must park the type so no later trigger can storm");
        parkRegistry.GetParkedError(NodeTypePath).Should().Contain(
            PrebuiltAssemblySeeder.RequirePrebuiltConfigKey);

        // (c) NO COMPILE EVER STARTED — the counter increments only inside the real dispatch.
        parkRegistry.GetCompileAttemptCount(NodeTypePath).Should().Be(0,
            "the adopt-only gate refuses BEFORE Roslyn; a single attempt would mean it compiled");
    }
}
