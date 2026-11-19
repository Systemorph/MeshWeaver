using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Updates.Test;

/// <summary>
/// Scenario 13 — an update that changes ONLY a SHARED source folder (#8413). The type
/// <c>{App}/Run</c> declares <c>shared=@{App}/Source</c>; the update rewrites a file in that folder
/// and nothing in the type's own <c>Source</c>. The record must stop reading "up to date"
/// (IsDirty), the update channel's recompile step must produce a NEW build, and the activation after
/// a dispose must serve it.
/// </summary>
public class SharedSourceUpdateTest(ITestOutputHelper output) : UpdateScenarioBase(output)
{
    private async Task<(string TypePath, string SharedPath, NodeTypeDefinition Build)> CreateSharedType(string marker)
    {
        var app = $"{TestPartition}/App{Suffix}";
        var typePath = $"{app}/Run";
        await NodeFactory.CreateNode(new MeshNode($"App{Suffix}", TestPartition)
        {
            Name = "App",
            NodeType = "Markdown",
        }).Should().Within(Step).Emit(cancellationToken: TestContext.Current.CancellationToken);
        await NodeFactory.CreateNode(new MeshNode("code", $"{app}/Source")
        {
            Name = "code",
            NodeType = "Code",
            Content = new CodeConfiguration { Code = Code(marker), Language = "csharp" },
        }).Should().Within(Step).Emit(cancellationToken: TestContext.Current.CancellationToken);
        await NodeFactory.CreateNode(new MeshNode("Run", app)
        {
            Name = "Run",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Updates-suite probe whose code lives in a SHARED source folder.",
                Configuration = ProbeConfiguration,
                Sources = ["namespace:Source scope:subtree", $"shared=@{app}/Source"],
            },
        }).Should().Within(Step).Emit(cancellationToken: TestContext.Current.CancellationToken);

        await GetClient(c => c.AddData())
            .Observe(new CreateReleaseRequest(Force: false), o => o.WithTarget(new Address(typePath)))
            .Take(1)
            .Should().Within(Step).Emit("the first release must be answered",
                cancellationToken: TestContext.Current.CancellationToken);
        var build = await DefinitionWhere(typePath,
            d => d.CompilationStatus == CompilationStatus.Ok && !string.IsNullOrEmpty(d.LatestAssemblyPath),
            $"build {marker} must compile from the shared source");
        return (typePath, $"{app}/Source/code", build);
    }

    private async Task UpdateShared(string sharedPath, string code)
    {
        var current = await Mesh.GetWorkspace().GetMeshNodeStream(sharedPath)
            .Where(n => n is not null).Take(1)
            .Should().Within(Step).Emit(cancellationToken: TestContext.Current.CancellationToken);
        await NodeFactory.UpdateNode(current! with
        {
            Content = new CodeConfiguration { Code = code, Language = "csharp" },
        }).Should().Within(Step).Emit(cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>A shared-source edit makes the consumer DIRTY — the record never reads "up to date"
    /// over a source set its build did not compile.</summary>
    [Fact(Timeout = 180_000)]
    public async Task ASharedSourceEdit_MakesTheConsumerDirty()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var (typePath, sharedPath, _) = await CreateSharedType("N");
        await UpdateShared(sharedPath, Code("N1"));
        var dirty = await DefinitionWhere(typePath, d => d.IsDirty,
            "an edit of a shared= source must make the consuming type dirty");
        dirty.IsDirty.Should().BeTrue();
    }

    /// <summary>The update channel's recompile step (ReleaseAffectedNodeTypes over the changed
    /// shared path) yields a new build, and the activation after a dispose serves it.</summary>
    [Fact(Timeout = 240_000)]
    public async Task ASharedSourceUpdate_IsReleased_AndBoundAfterTheDispose()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var (typePath, sharedPath, n) = await CreateSharedType("N");
        var instance = await CreateInstance(typePath, "inst");
        await AssertServes(instance, "N", "the instance activates on build N");

        await UpdateShared(sharedPath, Code("N1"));
        var released = await Mesh.ReleaseAffectedNodeTypes([sharedPath])
            .Take(1)
            .Should().Within(Step).Emit("the recompile closure must be derived",
                cancellationToken: TestContext.Current.CancellationToken);
        released.Should().Contain(typePath, "the consumer of the shared folder is in the closure");

        var n1 = await DefinitionWhere(typePath,
            d => d.CompilationStatus == CompilationStatus.Ok
                 && !d.IsDirty
                 && !string.Equals(d.LatestAssemblyPath, n.LatestAssemblyPath, StringComparison.Ordinal),
            "the release must produce a NEW build of the shared source");
        n1.LatestAssemblyPath.Should().NotBe(n.LatestAssemblyPath);

        await Recycle(instance);
        await AssertServes(instance, "N1", "after the dispose the build of the updated shared source serves");
    }
}
