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
/// <para>The fix is at the decision: it is drawn only once the published snapshot shows the
/// versions the sources' owners hold (<c>NodeTypeCompilationHelpers.AwaitSourceSnapshotCurrent</c>).</para>
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

    /// <summary>
    /// A source that LEAVES the source set inside the window — retyped away from <c>Code</c> — is
    /// decided, never waited on forever. Its owner still holds a NEWER node, but the converged
    /// snapshot drops the path instead of showing that version; a gate demanding the owner's
    /// version would hang (review of #6963). The request must build the set without it.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ASourceRetypedOutOfTheSetRightBeforeARequest_IsDecidedAndBuiltWithoutIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateType("r1");
        var extraPath = $"{TypePath}/Source/extra";
        await MeshService.CreateNode(new MeshNode("extra", $"{TypePath}/Source")
            {
                NodeType = "Code",
                Name = "extra",
                State = MeshNodeState.Active,
                Content = new CodeConfiguration { Language = "csharp", Code = "public static class EditThenReleaseExtra { }" },
            })
            .Should().Within(60.Seconds()).Emit(cancellationToken: ct);
        await Mesh.GetMeshNodeStream(TypePath)
            .Update<NodeTypeDefinition>(d => d with { RequestedReleaseAt = DateTimeOffset.UtcNow, RequestedReleaseBy = "test" })
            .Should().Within(60.Seconds()).Emit(cancellationToken: ct);
        await Settled(d => d.CompiledSources?.ContainsKey(extraPath) == true
            && d.LastReleaseRequestHandledAt >= d.RequestedReleaseAt && !d.IsDirty,
            "the control: both sources are built");

        await Mesh.GetMeshNodeStream(extraPath)
            .Update(n => n with { NodeType = "Markdown" })
            .Should().Within(60.Seconds()).Emit(cancellationToken: ct);
        await Mesh.GetMeshNodeStream(TypePath)
            .Update<NodeTypeDefinition>(d => d with { RequestedReleaseAt = DateTimeOffset.UtcNow, RequestedReleaseBy = "test" })
            .Should().Within(60.Seconds()).Emit(cancellationToken: ct);

        await Settled(d => d.CompiledSources?.ContainsKey(extraPath) == false
            && d.LastReleaseRequestHandledAt >= d.RequestedReleaseAt && !d.IsDirty,
            "a source that left the set must not hold the request's decision hostage");
    }

    /// <summary>
    /// The decision rule itself, deterministically — the end-to-end case above only reaches the
    /// window when the request is observed before the snapshot drops the path. A snapshot that no
    /// longer lists a source has converged past the request (every path read was listed when the
    /// request was observed), so it SHOWS the live state; a snapshot still listing the path at an
    /// older version, or listing an owner-deleted path, does not.
    /// </summary>
    [Fact]
    public void TheSnapshotGate_AcceptsALeftPath_AndRefusesAStaleOrDeletedOne()
    {
        const string a = "t/Source/a", b = "t/Source/b";
        var live = System.Collections.Immutable.ImmutableDictionary<string, long?>.Empty
            .Add(a, 200L).Add(b, 300L);

        // b left the set (retyped / excluded) while its owner holds a newer node: converged.
        NodeTypeCompilationHelpers.SnapshotShows(
                new System.Collections.Generic.Dictionary<string, long> { [a] = 200L }, live)
            .Should().BeTrue("a path the snapshot no longer lists has left the source set — waiting for it would never end");

        // b still listed at its pre-request version: stale — the race this gate exists for.
        NodeTypeCompilationHelpers.SnapshotShows(
                new System.Collections.Generic.Dictionary<string, long> { [a] = 200L, [b] = 100L }, live)
            .Should().BeFalse("a listed source behind its owner is the pre-edit snapshot");

        // b deleted at its owner but still listed: not yet converged.
        var deleted = live.SetItem(b, null);
        NodeTypeCompilationHelpers.SnapshotShows(
                new System.Collections.Generic.Dictionary<string, long> { [a] = 200L, [b] = 300L }, deleted)
            .Should().BeFalse("an owner-deleted source must leave the snapshot first");
        NodeTypeCompilationHelpers.SnapshotShows(
                new System.Collections.Generic.Dictionary<string, long> { [a] = 200L }, deleted)
            .Should().BeTrue("…and once it has, the snapshot is current");
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
