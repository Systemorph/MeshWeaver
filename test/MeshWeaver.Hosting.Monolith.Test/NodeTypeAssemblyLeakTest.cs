using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 End-to-end memory-leak guard for dynamically-compiled NodeTypes.
///
/// <para>Compiling a NodeType loads its assembly into a <b>collectible</b>
/// <c>NodeAssemblyLoadContext</c>. Those contexts used to be held by the top-level
/// <c>CompilationCacheService</c> singleton for the entire process lifetime (its root
/// container is never disposed — <c>TestBase</c> deliberately skips the SP dispose
/// because it broke 40+ tests reading singletons post-dispose). So every compiled
/// NodeType assembly accumulated across the whole testhost, which is the dominant
/// driver of the late-project CI OOM / GC-stall flakes.</para>
///
/// <para>The fix gives each ALC a per-node lifetime: <c>MeshDataSource.SubscribeToOwnDeletion</c>
/// registers a hub-disposal callback that calls <c>UnloadNodeContexts</c>, so when a
/// node hub tears down (which <c>Mesh.Dispose()</c> does for every hosted hub) its
/// collectible context is unloaded and becomes GC-collectable. This test is the
/// deterministic, dependency-free equivalent of "dotMemory delta == 0": compile a real
/// NodeType, take a <see cref="WeakReference"/> to its <c>DynamicNode_*</c> context,
/// drop every strong ref, dispose the mesh, force GC, and assert the context was
/// collected. If a survivor remains the leak is back — either the unload hook didn't
/// fire or a second managed reference (e.g. a TypeRegistry that outlives the node)
/// still pins the generated type.</para>
/// </summary>
public class NodeTypeAssemblyLeakTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph();

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    /// <summary>
    /// Compile a NodeType and return ONLY weak references to the collectible
    /// <c>DynamicNode_*</c> contexts the compile created. <see cref="MethodImplOptions.NoInlining"/>
    /// plus the strong locals dying with this frame guarantees nothing on the caller's
    /// stack pins the assembly across the subsequent GC. The compile response carries
    /// only the assembly-location string, not the context.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private List<WeakReference> CompileNodeTypeAndWeakRefContexts(string nodeTypeId)
    {
        var before = AssemblyLoadContext.All
            .Where(a => a.Name?.StartsWith("DynamicNode_", StringComparison.Ordinal) == true)
            .ToHashSet();

        var nodeTypePath = $"type/{nodeTypeId}";
        var typeNode = MeshNode.FromPath(nodeTypePath) with
        {
            Name = nodeTypeId,
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition { Configuration = $"config => config.WithContentType<{nodeTypeId}>()" },
            State = MeshNodeState.Active,
        };

        var response = MeshService.CreateNode(typeNode)
            .SelectMany(_ => MeshService.CreateNode(new MeshNode("code", $"{nodeTypePath}/Source")
            {
                NodeType = "Code",
                Name = "code",
                Content = new CodeConfiguration
                {
                    Code = $"public record {nodeTypeId} {{ public string Id {{ get; init; }} = string.Empty; }}",
                    Language = "csharp",
                },
                State = MeshNodeState.Active,
            }))
            .SelectMany(_ => MessageHubExtensions.Observe(
                Mesh,
                (IRequest<GetCompilationPathResponse>)new GetCompilationPathRequest(),
                o => o.WithTarget(new Address(nodeTypePath))))
            .Select(d => d.Message)
            .Should().Within(60.Seconds()).Emit();

        response.Success.Should().BeTrue($"compile must succeed; error: {response.Error}");
        response.AssemblyLocation.Should().NotBeNullOrEmpty();

        var newContexts = AssemblyLoadContext.All
            .Where(a => a.Name?.StartsWith("DynamicNode_", StringComparison.Ordinal) == true
                        && !before.Contains(a))
            .ToList();

        Output.WriteLine($"[diag] new DynamicNode_* contexts after compile: " +
            $"{string.Join(", ", newContexts.Select(a => a.Name))}");

        return newContexts.Select(a => new WeakReference(a)).ToList();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForceCollect(IReadOnlyCollection<WeakReference> weakRefs)
    {
        for (var i = 0; i < 12 && weakRefs.Any(w => w.IsAlive); i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        }
    }

    [Fact]
    public void NodeTypeAssemblyContext_IsCollected_AfterMeshDisposal()
    {
        var weakRefs = CompileNodeTypeAndWeakRefContexts("LeakProbeStory");
        weakRefs.Should().NotBeEmpty(
            "the compile must have created at least one collectible DynamicNode_* context");
        weakRefs.Should().OnlyContain(w => w.IsAlive,
            "the contexts are loaded and held while the mesh is alive");

        // Tear the mesh down: every hosted per-node hub disposes, firing the
        // SubscribeToOwnDeletion → UnloadNodeContexts hook that drops its ALC.
        Mesh.Dispose();
        Mesh.Disposal?.Wait(TimeSpan.FromSeconds(30));

        ForceCollect(weakRefs);

        var survivors = weakRefs.Count(w => w.IsAlive);
        survivors.Should().Be(0,
            $"every dynamic-NodeType AssemblyLoadContext must be GC-collected once the mesh and its " +
            $"per-node hubs dispose — {survivors}/{weakRefs.Count} survived, which is the ALC leak " +
            $"(unload hook didn't fire, or a singleton TypeRegistry still pins the generated type)");
    }
}
