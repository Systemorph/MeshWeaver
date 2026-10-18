using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// 🚨 <b>A release request builds every source edit written BEFORE it.</b>
///
/// <para>The release-request watcher decided "satisfied by the existing current build" from the
/// type's published source snapshot (<see cref="NodeTypeDefinition.CurrentSourceVersions"/>), which
/// trails a source write by the synced source query's latency. A request written right after an
/// edit — "edit, then press Compile" — was therefore stamped handled against the PREVIOUS snapshot
/// and dispatched nothing; the snapshot then arrived, and the record sat at <c>Ok</c> +
/// <c>IsDirty</c> with its request spent and the edit never built. Measured on CI as the
/// platform-compatibility ladder's rung 4 (<c>IsDirty = True</c>,
/// <c>LastReleaseRequestHandledAt == RequestedReleaseAt</c>, the old assembly) and, before the fix,
/// reproduced here on every run: the second edit-then-request was absorbed.</para>
///
/// <para>The negative control is the other half of the rule: an edit with NO request after it is
/// never compiled — a healthy type does not auto-compile on an edit.</para>
/// </summary>
public class AReleaseRequestBuildsTheEditWrittenBeforeItTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly string TypePath = "type/EditThenRelease" + Guid.NewGuid().ToString("N")[..8];

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private static string SourceOf(string revision) => $$"""
        public record EditThenReleaseContent { public string Title { get; init; } = ""; }
        public static class EditThenReleaseApi { public static string Revision() => "{{revision}}"; }
        """;

    /// <summary>Each revision is written and IMMEDIATELY released; every one must be built.</summary>
    [Fact(Timeout = 180_000)]
    public async Task EveryReleaseRequestedRightAfterAnEdit_BuildsThatEdit()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateType("r1");
        var built = await Settled(d => d.LatestAssemblyPath is not null);

        for (var i = 2; i <= 6; i++)
        {
            var revision = "r" + i;
            var before = built.LatestAssemblyPath;
            await Mesh.GetMeshNodeStream($"{TypePath}/Source/api")
                .Update<CodeConfiguration>(c => c with { Code = SourceOf(revision) })
                .Should().Within(60.Seconds()).Emit(cancellationToken: ct);
            await Mesh.GetMeshNodeStream(TypePath)
                .Update<NodeTypeDefinition>(d => d with { RequestedReleaseAt = DateTimeOffset.UtcNow, RequestedReleaseBy = "test" })
                .Should().Within(60.Seconds()).Emit(cancellationToken: ct);

            built = await Settled(d => !d.IsDirty
                && d.LatestAssemblyPath != before
                && d.LastReleaseRequestHandledAt >= d.RequestedReleaseAt,
                $"{revision}: the request written after the edit must build it — "
                + "never be stamped handled against the snapshot from before the edit");
        }
    }

    /// <summary>NEGATIVE CONTROL — an edit nobody released stays unbuilt (dirty, same assembly).</summary>
    [Fact(Timeout = 120_000)]
    public async Task AnEditWithNoReleaseRequestAfterIt_IsNotCompiled()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateType("r1");
        await Mesh.GetMeshNodeStream(TypePath)
            .Update<NodeTypeDefinition>(d => d with { RequestedReleaseAt = DateTimeOffset.UtcNow, RequestedReleaseBy = "test" })
            .Should().Within(60.Seconds()).Emit(cancellationToken: ct);
        var built = await Settled(d => d.LatestAssemblyPath is not null
            && d.LastReleaseRequestHandledAt >= d.RequestedReleaseAt && !d.IsDirty);

        await Mesh.GetMeshNodeStream($"{TypePath}/Source/api")
            .Update<CodeConfiguration>(c => c with { Code = SourceOf("r2") })
            .Should().Within(60.Seconds()).Emit(cancellationToken: ct);
        await Settled(d => d.IsDirty, "the edit reaches the snapshot");

        await Mesh.GetMeshNodeStream(TypePath)
            .Where(n => n.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions) is { } d
                && (d.CompilationStatus is CompilationStatus.Pending or CompilationStatus.Compiling
                    || d.LatestAssemblyPath != built.LatestAssemblyPath))
            .Should().NotEmit(5.Seconds(), "an edit written AFTER the last request is not that request's to build",
                cancellationToken: ct);
    }

    private async Task CreateType(string revision)
    {
        await MeshService.CreateNode(MeshNode.FromPath(TypePath) with
            {
                Name = TypePath[(TypePath.LastIndexOf('/') + 1)..],
                NodeType = MeshNode.NodeTypePath,
                State = MeshNodeState.Active,
                Content = new NodeTypeDefinition
                {
                    Configuration = "config => config.WithContentType<EditThenReleaseContent>()",
                },
            })
            .SelectMany(_ => MeshService.CreateNode(new MeshNode("api", $"{TypePath}/Source")
            {
                NodeType = "Code",
                Name = "api",
                State = MeshNodeState.Active,
                Content = new CodeConfiguration { Language = "csharp", Code = SourceOf(revision) },
            }))
            .Should().Within(60.Seconds()).Emit(cancellationToken: TestContext.Current.CancellationToken);
    }

    private async Task<NodeTypeDefinition> Settled(Func<NodeTypeDefinition, bool> predicate, string because = "")
    {
        var node = await Mesh.GetMeshNodeStream(TypePath)
            .Should().Within(60.Seconds())
            .Match(n => n.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)
                    is { CompilationStatus: CompilationStatus.Ok } d && predicate(d),
                because, TestContext.Current.CancellationToken);
        return node.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!;
    }
}
