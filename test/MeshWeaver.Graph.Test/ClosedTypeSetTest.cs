using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Compiler;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The CLOSED TYPE SET (<see cref="ClosedTypeSet"/>) — P1 of the control instance (Memex
/// <c>docs/control-instance.md</c> §3.1): every NodeType the process can activate is defined by its
/// image, and no type definition is read from the database.
///
/// <para>Driven through the real <see cref="NodeTypeEnrichmentHelpers.EnrichWithNodeType"/> and the
/// real pre-warm probe on a real Monolith mesh. The mesh holds BOTH kinds of type: one registered in
/// code (the image's) and one stored as a NodeType row in a user-shaped partition, whose
/// configuration does not compile — the 2026-09-25 incident's shape, where one abandoned in-mesh
/// type refused readiness on every pod. The open-mesh twin
/// (<see cref="OpenTypeSetControlTest"/>) persists the same row and shows the same instruments DO
/// see it there, so a closed-mesh zero is a verdict and not an instrument that sees nothing.</para>
/// </summary>
public class ClosedTypeSetTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    /// <summary>The image's type: registered in code, two segments deep like <c>Hosting/Deployment</c>.</summary>
    internal const string CodeTypePath = "ClosedSetImage/Widget";

    /// <summary>What the code type's configuration stamps, so the test can tell it applied.</summary>
    internal sealed record ImageMarker(string Type);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .WithClosedTypeSet()
            .AddMeshNodes(CodeType());

    internal static MeshNode CodeType() => new(CodeTypePath)
    {
        Name = "Widget (image)",
        NodeType = MeshNode.NodeTypePath,
        Content = new NodeTypeDefinition { Description = "registered in code by the image" },
        HubConfiguration = config => config.Set(new ImageMarker(CodeTypePath)),
    };

    /// <summary>A NodeType ROW in a user-shaped partition whose configuration cannot compile.</summary>
    internal static MeshNode BrokenRow(string partition, string id) => new(id, partition)
    {
        Name = id,
        NodeType = MeshNode.NodeTypePath,
        State = MeshNodeState.Active,
        Content = new NodeTypeDefinition
        {
            Description = "abandoned demo type — its configuration names a method that does not exist",
            Configuration = "config => config.ThisMethodDoesNotExist()",
        },
    };

    /// <summary>
    /// The image's own type activates with its own configuration — the closed set is not an outage
    /// for the types it is closed AROUND.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ACodeRegisteredType_IsServed_FromTheImage()
    {
        var ct = TestContext.Current.CancellationToken;
        var applied = await Enrich(Mesh, new MeshNode("w1", TestPartition) { NodeType = CodeTypePath }, ct);

        applied.Get<ImageMarker>().Should().NotBeNull(
            "the code registration's HubConfiguration is what a closed mesh serves");
        applied.Get<UnhandledMessageNack>().Should().BeNull(
            "a type the image registers is not refused");
    }

    /// <summary>
    /// 🚨 P1. A type that exists ONLY as a database row is refused at activation, named — and its
    /// row is never compiled: its compile status stays unset, because nothing asked Roslyn.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ADatabaseDefinedType_IsRefused_Named_AndNeverCompiled()
    {
        var ct = TestContext.Current.CancellationToken;
        var rowPath = $"{TestPartition}/Broken";
        await CreateAsSystem(Mesh, BrokenRow(TestPartition, "Broken"), ct);

        var applied = await Enrich(Mesh, new MeshNode("b1", TestPartition) { NodeType = rowPath }, ct);

        var nack = applied.Get<UnhandledMessageNack>();
        nack.Should().NotBeNull(
            "the instance must still ACTIVATE — with a refusal — so callers get a terminal verdict "
            + "instead of parking on a hub that never comes up");
        nack!.ErrorType.Should().Be(ErrorType.CompilationFailed);
        nack.Reason.Should().Contain("closed type set",
            "the refusal names the cause, not a compile error the source never had");
        nack.Reason.Should().Contain(rowPath, "and the type it refused");
        applied.Get<ImageMarker>().Should().BeNull();
    }

    /// <summary>
    /// 🚨 P1, the compile half. A NodeType row whose source is perfectly GOOD is still never handed
    /// to Roslyn on a closed mesh: the compile dispatch parks it at <c>Error</c> with the closed-set
    /// reason, exactly where <c>Modules:RequirePrebuilt</c> parks a type with no bundle. The open
    /// twin compiles the identical row to <c>Ok</c>, which is what makes this a verdict.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ACompileOfADatabaseRow_IsParked_WithTheClosedSetReason()
    {
        var ct = TestContext.Current.CancellationToken;
        var settled = await CreateCompilableRowAndSettle(Mesh, $"{TestPartition}/GoodSource", ct);

        settled.CompilationStatus.Should().Be(CompilationStatus.Error,
            "a closed mesh parks the compile instead of running it");
        settled.CompilationError.Should().Contain("closed type set",
            "and the park names the cause — not a compile error the source never had");
    }

    /// <summary>Creates a NodeType row with a compiling source and waits for its compile to settle.</summary>
    internal static async Task<NodeTypeDefinition> CreateCompilableRowAndSettle(
        IMessageHub mesh, string typePath, CancellationToken ct)
    {
        var meshService = mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var type = MeshNode.FromPath(typePath) with
        {
            Name = typePath[(typePath.LastIndexOf('/') + 1)..],
            NodeType = MeshNode.NodeTypePath,
            State = MeshNodeState.Active,
            Content = new NodeTypeDefinition
            {
                Description = "a database-defined type whose source compiles",
                Configuration = "config => config",
            },
        };
        var source = new MeshNode("Main", $"{typePath}/{CodeConventions.SourceSubNamespace}")
        {
            NodeType = CodeConventions.CodeNodeType,
            Name = "Main",
            State = MeshNodeState.Active,
            LastModified = DateTimeOffset.UtcNow,
            Content = new CodeConfiguration
            {
                Language = "csharp",
                Code = "/// <summary>Compiles.</summary>\npublic static class ClosedSetGoodSource { /// <summary>One.</summary>\n public static int One() => 1; }",
            },
        };
        await meshService.CreateNode(type)
            .SelectMany(_ => meshService.CreateNode(source))
            .FirstAsync().Timeout(TimeSpan.FromSeconds(120)).Await(ct);

        return (await mesh.GetMeshNodeStream(typePath)
            .Select(n => n?.ContentAs<NodeTypeDefinition>(mesh.JsonSerializerOptions))
            .Where(d => d is { CompilationStatus: CompilationStatus.Ok or CompilationStatus.Error })
            .FirstAsync().Timeout(TimeSpan.FromSeconds(240)).Await(ct))!;
    }

    /// <summary>
    /// 🚨 P2's precondition: the pre-warm sweep and the bake probe — the two passes that enumerate
    /// every NodeType row in every partition, and the input the readiness gate reads — see NOTHING
    /// on a closed mesh, even with a broken row present. So no row can occupy, slow or fail them.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task TheSweepAndTheProbe_EnumerateNoDatabaseType()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateAsSystem(Mesh, BrokenRow(TestPartition, "BrokenForSweep"), ct);

        var report = await DynamicTypePreWarmer.ProbeDynamicTypes(Mesh)
            .FirstAsync().Timeout(Budget).Await(ct);
        report.Entries.Should().BeEmpty("a closed type set has no dynamic types to probe");

        var warmed = await DynamicTypePreWarmer.WarmDynamicTypes(Mesh)
            .ToList().Timeout(Budget).Await(ct);
        warmed.Should().BeEmpty("and none to warm");
    }

    internal static async Task<MessageHubConfiguration> Enrich(
        IMessageHub mesh, MeshNode instance, CancellationToken ct)
    {
        var enriched = await NodeTypeEnrichmentHelpers
            .EnrichWithNodeType(mesh, new MeshConfiguration(Array.Empty<MeshNode>()),
                compilationService: null, instance)
            .Take(1)
            .Should().Within(Budget).Emit("enrichment always emits — worst case an overlay", ct);
        enriched!.HubConfiguration.Should().NotBeNull();
        return enriched.HubConfiguration!(new MessageHubConfiguration(null, new Address("probe", instance.Id)));
    }

    internal static Task CreateAsSystem(IMessageHub mesh, MeshNode node, CancellationToken ct)
    {
        var meshService = mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = mesh.ServiceProvider.GetService<AccessService>();
        return access.RunAsSystem(() => meshService.CreateNode(node))
            .FirstAsync().Timeout(Budget).Await(ct);
    }
}

/// <summary>
/// The control for <see cref="ClosedTypeSetTest"/>: the SAME broken row on an OPEN mesh is seen by
/// the probe and is not refused as "not part of the closed type set". Without it, an empty report or
/// a refusal on the closed mesh could be an instrument that sees nothing.
/// </summary>
public class OpenTypeSetControlTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The probe enumerates the row on an open mesh — the closed mesh's empty report is a verdict.</summary>
    [Fact(Timeout = 60_000)]
    public async Task TheProbe_SeesTheRow_WhenTheSetIsOpen()
    {
        var ct = TestContext.Current.CancellationToken;
        Mesh.ServiceProvider.IsClosedTypeSet().Should().BeFalse("open is the default");
        await ClosedTypeSetTest.CreateAsSystem(Mesh, ClosedTypeSetTest.BrokenRow(TestPartition, "BrokenOpen"), ct);

        var report = await DynamicTypePreWarmer.ProbeDynamicTypes(Mesh)
            .FirstAsync().Timeout(ClosedTypeSetTest.Budget).Await(ct);
        report.Entries.Select(e => e.TypePath).Should().Contain($"{TestPartition}/BrokenOpen",
            "on an open mesh a NodeType row with source IS a dynamic type");
    }

    /// <summary>The identical good row compiles to <c>Ok</c> on an open mesh — the park above is the closed set's doing.</summary>
    [Fact(Timeout = 300_000)]
    public async Task TheSameRow_Compiles_WhenTheSetIsOpen()
    {
        var ct = TestContext.Current.CancellationToken;
        var settled = await ClosedTypeSetTest.CreateCompilableRowAndSettle(Mesh, $"{TestPartition}/GoodSourceOpen", ct);
        settled.CompilationStatus.Should().Be(CompilationStatus.Ok, settled.CompilationError ?? "");
    }

    /// <summary>An open mesh resolves a database type through its row, never through the closed-set refusal.</summary>
    [Fact(Timeout = 60_000)]
    public async Task ADatabaseDefinedType_IsNotRefusedAsOutsideTheSet_WhenTheSetIsOpen()
    {
        var ct = TestContext.Current.CancellationToken;
        await ClosedTypeSetTest.CreateAsSystem(Mesh, ClosedTypeSetTest.BrokenRow(TestPartition, "BrokenOpen2"), ct);

        var applied = await ClosedTypeSetTest.Enrich(
            Mesh, new MeshNode("o1", TestPartition) { NodeType = $"{TestPartition}/BrokenOpen2" }, ct);

        (applied.Get<UnhandledMessageNack>()?.Reason ?? "").Should().NotContain("closed type set",
            "the refusal belongs to a closed mesh only");
    }
}
