using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 <b>ADOPTION MUST BE REACHABLE AT ANY TIME</b> — issue #1782 gap 4.
///
/// <para>A prebuilt NodeType assembly used to be able to arrive at exactly two moments: boot
/// default-install, and an install / git-sync push. Every OTHER route into a compile — a first
/// access, a release request, a self-heal kick, a framework-stale rebuild — went straight to
/// Roslyn without ever asking the deployment's bundle sources whether the assembly already
/// existed. That was survivable while every instance pre-baked its whole type set at boot. Once
/// instance-level pre-bake gave way to lazy compile-on-access (#1746), it made the fetch path
/// unreachable at precisely the moment it became the PRIMARY way assemblies arrive.</para>
///
/// <para>The failure mode is quiet, which is why it needs a test rather than a metric: a type
/// that should have been adopted simply compiles instead. It works. It is just slow, and it
/// burns Roslyn on the per-NodeType hub's single-threaded action block for an assembly the
/// deployment already had — the exact cost the prod measurement of adoption (80 compiles /
/// 64.8 s → 0 compiles, 84 adopted, 32.1 s) exists to keep at zero.</para>
///
/// <para>Both tests drive a deliberately non-compiling NodeType, because that makes the compile
/// itself an <b>observable event</b>: <c>NodeTypeCompileParkRegistry</c> counts the attempts, so
/// "Roslyn ran exactly once" is an assertion rather than an inference.</para>
/// </summary>
public class NodeTypeOnDemandAdoptionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Partition = "AdoptOnDemandTest";
    private const string NodeTypeId = "AdoptOnDemandBroken";
    private const string NodeTypePath = $"{Partition}/{NodeTypeId}";

    /// <summary>Instance state, per test — a fresh mesh and a fresh recorder each time.
    /// Initialized before the base constructor calls <see cref="ConfigureMesh"/>.</summary>
    private readonly RecordingPrebuiltConsumer consumer = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
                services.AddSingleton<IPrebuiltAssemblyConsumer>(consumer));

    /// <summary>
    /// 🚨 THE GAP. A compile miss must ask the bundle sources FIRST — and when they have
    /// nothing, must fall through to exactly the behaviour that existed before. Both halves
    /// matter: an adoption attempt that swallowed the compile would be worse than not attempting.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ACompileMiss_AsksTheBundleSources_BeforeDispatchingRoslyn()
    {
        consumer.AdoptedToReport = 0;
        var parkRegistry = Mesh.ServiceProvider.GetRequiredService<NodeTypeCompileParkRegistry>();

        await CreateBrokenNodeTypeAndWaitForError();

        consumer.Asked.Should().Equal([NodeTypePath],
            "the compile miss must consult the deployment's bundle sources for THIS type before "
            + "dispatching Roslyn — that consultation is the whole of #1782 gap 4, and before it "
            + "existed a first access could only ever compile");

        parkRegistry.GetCompileAttemptCount(NodeTypePath).Should().Be(1,
            "an adoption MISS must fall straight through to the compile that would have happened "
            + "anyway — no suppression, and no second attempt either");
    }

    /// <summary>
    /// 🚨 THE CONTROL, and the reason the implementation does not trust the returned count.
    ///
    /// <para>A consumer that reports an adoption it never wrote back is not hypothetical — it is
    /// what a partially-failed seed, a store that lost the bytes, or a future registry-backed
    /// consumer that resolves an entitlement but cannot land the file all look like from here.
    /// If the count alone were believed, this type would sit at <c>Pending</c> for ever: every
    /// settle-waiter would hang to its timeout and every instance page with it. A STRANDED type
    /// is the one outcome worse than a redundant compile, so the gate is the node's own
    /// <c>HasUsableBuild</c>, never the number the consumer returned.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AnAdoptionThatNeverWroteBack_MustNotStrandTheType()
    {
        consumer.AdoptedToReport = 7;   // a confident lie: nothing was actually stamped
        var parkRegistry = Mesh.ServiceProvider.GetRequiredService<NodeTypeCompileParkRegistry>();

        await CreateBrokenNodeTypeAndWaitForError();

        consumer.Asked.Should().Equal([NodeTypePath], "the consultation still happens");

        parkRegistry.GetCompileAttemptCount(NodeTypePath).Should().Be(1,
            "the reported adoption did not produce a usable build, so the type must compile — "
            + "reaching a terminal state is the proof it was not left stranded at Pending");
    }

    /// <summary>Creates a NodeType whose Configuration is not valid C# and waits for the compile
    /// to settle at Error — the terminal state that proves the type was not stranded.</summary>
    private async Task CreateBrokenNodeTypeAndWaitForError()
    {
        await NodeFactory.CreateNode(new MeshNode(NodeTypeId, Partition)
        {
            Name = "Adopt-On-Demand Broken Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Deliberately non-compiling NodeType (on-demand adoption test).",
                Configuration = "config => this is not valid C# at all ((await ("
            }
        }).Should().Emit();

        await Mesh.GetWorkspace().GetMeshNodeStream(NodeTypePath)
            .Should().Within(90.Seconds())
            .Match(n => n.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Error);
        Output.WriteLine($"NodeType settled at Error; bundle sources were asked for: "
            + $"[{string.Join(", ", consumer.Asked)}]");
    }

    /// <summary>
    /// Records what the compile path asked for, and reports whatever the test tells it to. It
    /// deliberately never writes anything back — the "did it actually land" question belongs to
    /// the node, and pinning that separation is the point of the control test above.
    /// </summary>
    private sealed class RecordingPrebuiltConsumer : IPrebuiltAssemblyConsumer
    {
        private readonly ConcurrentQueue<string> asked = new();

        /// <summary>What <see cref="SeedForTypes"/> claims to have adopted.</summary>
        public int AdoptedToReport { get; set; }

        public IReadOnlyList<string> Asked => asked.ToArray();

        public IObservable<int> SeedForTypes(IReadOnlyCollection<string> typePaths)
        {
            foreach (var path in typePaths)
                asked.Enqueue(path);
            return Observable.Return(AdoptedToReport);
        }
    }
}
