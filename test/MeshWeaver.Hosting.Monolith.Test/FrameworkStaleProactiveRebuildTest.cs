using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Repro for issue #464, Defect 1: after a platform self-update changes the framework version,
/// a dynamic NodeType whose cached assembly was built against the PREVIOUS framework must be
/// rebuilt PROACTIVELY by its OWN hub — without waiting for an instance to be activated and
/// without a manual Compile click.
///
/// <para><b>Root cause.</b> A framework-stale NodeType is persisted as
/// <see cref="CompilationStatus.Ok"/> with the OLD <see cref="NodeTypeDefinition.CompiledFrameworkVersion"/>,
/// so nothing re-drives it: the first-build kickoff needs a <c>null</c> status, the recovery
/// kickoff needs <c>Compiling</c>, and the framework-stale self-heal in
/// <c>NodeTypeEnrichmentHelpers</c> only fires when an INSTANCE of the type is activated. A
/// NodeType with no live instances therefore stays stale (and <c>compile</c> / CreateRelease
/// up-to-date checks report it clean) — a runtime <c>MissingMethodException</c> timebomb — until
/// an operator manually rebuilds it. The fix is the owner-side, level-triggered kickoff in
/// <c>NodeTypeCompilationHelpers.InstallCompileWatcher</c>.</para>
///
/// <para>🚨 <b>The stale RECORD is only half the trigger — the other half is the STORE, and
/// getting that wrong is what made this test a flake (issue #1368).</b> Since "Don't rebuild what
/// the share already has" the kickoff asks <see cref="IAssemblyStore"/> before rebuilding, because
/// <see cref="NodeTypeDefinition.CompiledFrameworkVersion"/> only says which framework the last
/// WRITE-BACK came from — not whether bytes for the LIVE framework exist. The store answers the
/// question that matters: its key carries the live framework tag, so a hit IS a usable build.
/// A real self-update changes that tag, so every previously-cached DLL becomes invisible to the
/// live lookup — the record AND the store go stale together. A test that forges only the record
/// leaves genuine live-framework bytes on the volume, and the correct response to THAT state is
/// to decline the rebuild. The two cases below therefore pin BOTH directions of the contract, and
/// each one arranges the store to match the record it forges.</para>
/// </summary>
public class FrameworkStaleProactiveRebuildTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override bool ShareMeshAcrossTests => true;

    // Two real Roslyn compiles (baseline + the proactive framework-stale rebuild) — widen the
    // watchdog to match, like FrameworkStaleInstanceRenderTest.
    protected override TimeSpan TestSoftDeadline => TimeSpan.FromSeconds(90);
    protected override TimeSpan TestHardDeadline => TimeSpan.FromSeconds(180);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph();

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();
    private IAssemblyStore AssemblyStore => Mesh.ServiceProvider.GetRequiredService<IAssemblyStore>();

    /// <summary>
    /// #464: the record is framework-stale AND the store holds no bytes for the live framework —
    /// the true post-self-update state. The NodeType's OWN hub must rebuild it, with no instance
    /// activated and no user click.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task FrameworkStaleNodeType_ProactivelyRebuilds_WithoutInstanceActivation()
    {
        var (nodeTypePath, realFv, baselineSucceededAt) = await CompileBaselineType("ProactiveStale");
        var workspace = Mesh.GetWorkspace();

        // Make the store MISS for the live framework, exactly as a self-update does. The store key
        // embeds the framework tag (FileSystemAssemblyStore.FrameworkTag), so after a framework
        // change every cached DLL still on the volume carries the OLD tag and the live-tag lookup
        // finds nothing. We cannot change the running process's tag, so we remove the cached bytes
        // — observationally identical through IAssemblyStore, which is all the kickoff can see.
        EvictFromAssemblyStore(nodeTypePath);

        var bogusFv = await ForceFrameworkStale(nodeTypePath);

        // The NodeType's OWN hub must proactively rebuild and re-stamp the CURRENT framework
        // version — Status back to Ok, a usable assembly, and a STRICTLY NEWER
        // LastCompileSucceededAt than the baseline (proving a genuine fresh compile, not a
        // replayed old Ok) — WITHOUT any instance activation. Before the fix nothing re-drives
        // the stale-Ok type, so this times out.
        var rebuilt = await workspace.GetMeshNodeStream(nodeTypePath)
            .Should().Within(90.Seconds())
            .Match(n => n.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Ok
                && d.CompiledFrameworkVersion == realFv
                && !string.IsNullOrEmpty(d.LatestAssemblyPath)
                && d.LastCompileSucceededAt is { } s && s > baselineSucceededAt);
        Output.WriteLine($"NodeType proactively rebuilt against the current framework version (was '{bogusFv}').");

        // The rebuild must leave a record whose store key names REAL bytes — see
        // AssertRecordNamesStoredBytes for why this is the assertion that matters.
        await AssertRecordNamesStoredBytes(nodeTypePath, rebuilt, "after the proactive rebuild");
    }

    /// <summary>
    /// The other direction, and the one that was silently unpinned: the record is framework-stale
    /// but the store ALREADY holds a build for the LIVE framework — a peer replica or a bake
    /// service compiled it without this hub seeing the write-back. Rebuilding here is not merely
    /// wasted work: with two images live at once each pod rebuilds to stamp its own version, sees
    /// the other's stamp, and rebuilds back — a recompile storm across the pods currently serving
    /// production. The kickoff must DECLINE.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task FrameworkStaleRecord_WithLiveFrameworkBytesInStore_DoesNotRebuild()
    {
        var (nodeTypePath, _, _) = await CompileBaselineType("StaleRecordFreshBytes");
        var workspace = Mesh.GetWorkspace();

        // NO eviction — the baseline compile's bytes stay in the store under the live framework
        // tag. That is the whole premise, so assert it rather than assume it: a vacuous version of
        // this test (bytes actually absent) would pass for the wrong reason.
        var baseline = await workspace.GetMeshNodeStream(nodeTypePath).Should().Within(30.Seconds())
            .Match(n => n.Content is NodeTypeDefinition { CompilationStatus: CompilationStatus.Ok });
        await AssertRecordNamesStoredBytes(nodeTypePath, baseline, "before forging the stale record");

        var bogusFv = await ForceFrameworkStale(nodeTypePath);

        // No rebuild: the node must stay settled Ok carrying the forged stamp. There is no
        // positive signal for "a decision to do nothing", so this is the sanctioned bounded
        // negative observation — but it is NOT vacuous: the assertion above proves the store hit
        // the kickoff relies on is real, and the sibling test proves the same kickoff DOES fire
        // when the store misses. Any Pending/Compiling flip, or a re-stamped framework version,
        // fails this immediately.
        await workspace.GetMeshNodeStream(nodeTypePath)
            .Where(n => n.Content is NodeTypeDefinition d
                && (d.CompilationStatus != CompilationStatus.Ok
                    || d.CompiledFrameworkVersion != bogusFv))
            .Should().NotEmit(20.Seconds(),
                "the assembly store already holds a build for the live framework, so the "
                + "framework-stale kickoff must decline instead of storming a rebuild");
        Output.WriteLine("Framework-stale kickoff correctly declined — the store already had live-framework bytes.");
    }

    /// <summary>
    /// Creates a NodeType with a trivial source file and waits for its first build to settle Ok,
    /// returning the path, the REAL (live) framework version it stamped, and its success time.
    /// </summary>
    private async Task<(string Path, string RealFrameworkVersion, DateTimeOffset SucceededAt)>
        CompileBaselineType(string prefix)
    {
        var typeId = $"{prefix}{Guid.NewGuid():N}";
        var nodeTypePath = $"type/{typeId}";

        var source = $$"""
            public record {{typeId}} { public string Title { get; init; } = string.Empty; }

            public static class {{typeId}}Config
            {
                public static MeshWeaver.Messaging.MessageHubConfiguration Configure(
                    MeshWeaver.Messaging.MessageHubConfiguration config) => config;
            }
            """;

        var typeNode = MeshNode.FromPath(nodeTypePath) with
        {
            Name = typeId,
            NodeType = MeshNode.NodeTypePath,
            State = MeshNodeState.Active,
            Content = new NodeTypeDefinition
            {
                Configuration = $"config => config.WithContentType<{typeId}>()"
            }
        };
        await MeshService.CreateNode(typeNode).Should().Emit();
        await MeshService.CreateNode(new MeshNode("code", $"{nodeTypePath}/Source")
        {
            NodeType = "Code",
            Name = "code",
            State = MeshNodeState.Active,
            Content = new CodeConfiguration { Code = source, Language = "csharp" }
        }).Should().Emit();

        await Mesh.Observe(new GetCompilationPathRequest(), o => o.WithTarget(new Address(nodeTypePath)))
            .Should().Within(90.Seconds()).Emit();
        var okNode = await Mesh.GetWorkspace().GetMeshNodeStream(nodeTypePath)
            .Should().Within(60.Seconds())
            .Match(n => n.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Ok
                && d.LastCompileSucceededAt is not null
                && !string.IsNullOrEmpty(d.LatestAssemblyPath)
                && !string.IsNullOrEmpty(d.CompiledFrameworkVersion));
        var def = (NodeTypeDefinition)okNode.Content!;
        Output.WriteLine(
            $"Baseline compile Ok for {nodeTypePath} — real framework version "
            + $"'{def.CompiledFrameworkVersion}', succeededAt {def.LastCompileSucceededAt!.Value:O}, "
            + $"store key v{def.LastCompiledVersion}, assembly '{def.LatestAssemblyPath}'.");
        return (nodeTypePath, def.CompiledFrameworkVersion!, def.LastCompileSucceededAt!.Value);
    }

    /// <summary>
    /// Stamps a bogus <see cref="NodeTypeDefinition.CompiledFrameworkVersion"/> while leaving
    /// Status=Ok and the assembly fields intact — what a binary redeploy leaves behind. NO instance
    /// of the type is ever activated, so the reactive enrichment self-heal never runs: only the
    /// proactive OWNER-side kickoff can act on this.
    /// </summary>
    private async Task<string> ForceFrameworkStale(string nodeTypePath)
    {
        var workspace = Mesh.GetWorkspace();
        var bogusFv = $"STALE-{Guid.NewGuid():N}";
        await workspace.GetMeshNodeStream(nodeTypePath)
            .Update(curr => curr.Content is NodeTypeDefinition d
                ? curr with { Content = d with { CompiledFrameworkVersion = bogusFv } }
                : curr)
            .Should().Emit();
        // 🚧 Barrier: confirm the bogus stamp actually LANDED before waiting on what follows.
        // GetMeshNodeStream replays the latest snapshot, so without this a convergence Match could
        // match the pre-stamp baseline Ok (still carrying the real framework version) and pass
        // without any rebuild — masking a disabled kickoff.
        await workspace.GetMeshNodeStream(nodeTypePath)
            .Should().Within(20.Seconds())
            .Match(n => n.Content is NodeTypeDefinition d && d.CompiledFrameworkVersion == bogusFv);
        Output.WriteLine($"Forced framework-stale on {nodeTypePath} (bogus framework version '{bogusFv}').");
        return bogusFv;
    }

    /// <summary>
    /// 🚨 The record's <see cref="NodeTypeDefinition.LastCompiledVersion"/> is one half of the
    /// <see cref="IAssemblyStore"/> key <c>(nodeTypePath, version)</c>; it MUST name bytes that
    /// exist. When it does not, every consumer that resolves an assembly through it misses:
    /// activation falls back to the default config (no MeshNodeReference reducer — the instance
    /// page renders nothing), the bake gate files a baked type as having no bytes, and the
    /// framework-stale kickoff's store probe wrongly concludes a rebuild is needed.
    ///
    /// <para>This is the regression pin for issue #1368, where the <c>GetCompilationPathRequest</c>
    /// write-back stamped the node's CURRENT version instead of the version the upload used, and
    /// raced the activity write-back (which stamps correctly) for the last word.</para>
    /// </summary>
    private async Task AssertRecordNamesStoredBytes(string nodeTypePath, MeshNode node, string when)
    {
        var def = (NodeTypeDefinition)node.Content!;
        def.LastCompiledVersion.Should().NotBeNull(
            $"the compile write-back must record the assembly-store key version ({when})");
        var resolved = await AssemblyStore
            .TryGetAssemblyPath(nodeTypePath, def.LastCompiledVersion!.Value)
            .Should().Within(30.Seconds()).Emit();
        resolved.Should().NotBeNullOrEmpty(
            $"LastCompiledVersion={def.LastCompiledVersion} must name assembly-store bytes that "
            + $"exist ({when}); the record points at '{def.LatestAssemblyPath}'");
        Output.WriteLine($"Store key v{def.LastCompiledVersion} resolves to '{resolved}' ({when}).");
    }

    /// <summary>
    /// Removes this NodeType's cached assemblies from the filesystem assembly store, reproducing
    /// what a framework-tag change does to the live lookup (see the class remarks).
    /// </summary>
    private void EvictFromAssemblyStore(string nodeTypePath)
    {
        var store = (FileSystemAssemblyStore)AssemblyStore;
        var typeId = nodeTypePath.Split('/').Last();
        var dirs = Directory.EnumerateDirectories(store.RootDirectory, $"*{typeId}*").ToList();
        dirs.Should().NotBeEmpty(
            $"the baseline compile must have cached '{nodeTypePath}' under {store.RootDirectory} — "
            + "without bytes to evict this test's premise is not established");
        foreach (var dir in dirs)
            Directory.Delete(dir, recursive: true);
        Output.WriteLine($"Evicted {dirs.Count} assembly-store director(ies) for {nodeTypePath}.");
    }
}
