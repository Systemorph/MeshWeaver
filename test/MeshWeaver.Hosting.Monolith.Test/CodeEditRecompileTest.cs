using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// End-to-end tests for the explicit compile pipeline:
///   CreateReleaseRequest â†’ IsUpToDate check â†’ CompileWatcher â†’ Release node.
///
/// The automatic MeshChangeFeed â†’ TryTriggerRecompile path has been removed.
/// All compilation is now triggered explicitly via CreateReleaseRequest.
/// </summary>
public class CodeEditRecompileTest(ITestOutputHelper output) : MonolithMeshTestBase(output), IDisposable
{
    private readonly string _cacheDir = Path.Combine(
        Path.GetTempPath(), $"MeshWeaverCodeEditTest-{Guid.NewGuid():N}");

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        Directory.CreateDirectory(_cacheDir);
        return base.ConfigureMesh(builder)
            .ConfigureServices(services => services
                .Configure<CompilationCacheOptions>(o =>
                {
                    o.CacheDirectory = _cacheDir;
                    o.EnableCompilationCache = true;
                    o.EnableDiskCache = true;
                }));
    }

    public new void Dispose()
    {
        base.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (Directory.Exists(_cacheDir))
            try { Directory.Delete(_cacheDir, recursive: true); } catch { }
    }

    private const string NodeTypePath = "TestData/CodeEditType";
    private const string InstancePath = "TestData/CodeEditType/instance1";
    private const string Instance2Path = "TestData/CodeEditType/instance2";

    private const string CodeV1 = """
        using MeshWeaver.Layout.Composition;
        public static class CodeEditLayoutAreas
        {
            public static UiControl Overview(LayoutAreaHost host, RenderingContext _)
                => Controls.Html("<div id='marker'>MARKER_V1</div>");
        }
        """;

    private const string CodeV2 = """
        using MeshWeaver.Layout.Composition;
        public static class CodeEditLayoutAreas
        {
            public static UiControl Overview(LayoutAreaHost host, RenderingContext _)
                => Controls.Html("<div id='marker'>MARKER_V2</div>");
        }
        """;

    /// <summary>
    /// Explicit compile flow (the new canonical shape post-2026-05-05):
    ///   1. Create NodeType + Code source.
    ///   2. Send CreateReleaseRequest â†’ should trigger compilation (not IsUpToDate).
    ///   3. Wait for V1 release node.
    ///   4. CreateReleaseRequest again â†’ should return AlreadyUpToDate = true.
    ///   5. Modify source code to V2.
    ///   6. CreateReleaseRequest â†’ should re-compile (sources changed).
    ///   7. Wait for V2 release node.
    ///   8. Create fresh instance â†’ must serve V2 layout.
    /// </summary>
    [Fact(Timeout = 90000)]
    public async Task CodeEdit_ExplicitRelease_IsUpToDate_RecompilesOnSourceChange()
    {
        var ct = new CancellationTokenSource(75.Seconds()).Token;

        // 1. Create the NodeType.
        await NodeFactory.CreateNode(new MeshNode("CodeEditType", TestPartition)
        {
            Name = "Code Edit Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Regression test for the explicit compile flow.",
                Configuration = "config => config.AddDefaultLayoutAreas().AddLayout(layout => layout.WithView(\"Overview\", CodeEditLayoutAreas.Overview))",
                ShowChildrenInDetails = false,
            }
        });

        // 2. Create the V1 source.
        await NodeFactory.CreateNode(new MeshNode("code", $"{TestPartition}/CodeEditType/Source")
        {
            Name = "Code",
            NodeType = "Code",
            Content = new CodeConfiguration { Code = CodeV1, Language = "csharp" }
        });

        // 3. Trigger V1 compilation. The per-NodeType hub's
        // InstallCompileWatcher kickoff also flips Pending â†’ Compile on first
        // activation when HasUsableBuild is false; either path is acceptable so
        // long as a real V1 Release lands. We send CreateReleaseRequest both as
        // the canonical explicit trigger AND to wait for the compile to settle.
        // AlreadyUpToDate may legitimately be true here if the kickoff beat us
        // to first compile â€” what we check is that V1 release is produced.
        var v1Response = await SendCreateReleaseAsync(NodeTypePath, force: false, ct);
        v1Response.Success.Should().BeTrue("CreateReleaseRequest should succeed");

        var v1Release = await WaitForNewReleaseAsync(NodeTypePath, knownReleases: [], ct);
        Output.WriteLine($"=== V1 release at {v1Release} ===");

        // 4. CreateReleaseRequest again without changes â†’ AlreadyUpToDate.
        var dupResponse = await SendCreateReleaseAsync(NodeTypePath, force: false, ct);
        dupResponse.AlreadyUpToDate.Should().BeTrue("sources unchanged since V1 compile");

        // 5. Create an instance and verify it serves V1.
        await NodeFactory.CreateNode(new MeshNode("instance1", $"{TestPartition}/CodeEditType")
        {
            Name = "Instance 1",
            NodeType = NodeTypePath,
        });
        var v1Html = await ReadOverviewAsync(InstancePath, ct);
        v1Html.Should().Contain("MARKER_V1", "V1 release must be served");

        // 6. Modify the source to V2. Live remote stream â€” path is known, no
        // index lag (per CqrsAndContentAccess.md).
        var sourceClient = GetClient(c => c.AddData());
        var codeNode = await sourceClient.GetWorkspace()
            .GetMeshNodeStream($"{TestPartition}/CodeEditType/Source/code")
            .Where(n => n is not null)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(15))
            .ToTask(ct);
        await NodeFactory.UpdateNode(codeNode with
        {
            Content = new CodeConfiguration { Code = CodeV2, Language = "csharp" }
        });

        // 7. Explicitly trigger V2 compilation â€” sources changed, should recompile.
        var v2Response = await SendCreateReleaseAsync(NodeTypePath, force: false, ct);
        v2Response.Success.Should().BeTrue("CreateReleaseRequest should succeed after source change");
        v2Response.AlreadyUpToDate.Should().BeFalse("source was modified, must recompile");

        var v2Release = await WaitForNewReleaseAsync(NodeTypePath, knownReleases: [v1Release], ct);
        v2Release.Should().NotBe(v1Release, "second release must be distinct");
        Output.WriteLine($"=== V2 release at {v2Release} ===");

        // 8. Create a fresh instance and verify it serves V2.
        //
        // ðŸš¨ Wait for the MESH HUB's workspace to see the V2 release on the NodeType
        // BEFORE creating the new instance. The activation path
        // (NodeTypeEnrichmentHelpers.EnrichWithNodeType) reads the NodeType MeshNode
        // from meshHub.GetWorkspace().GetMeshNodeStream(nodeType) â€” that workspace's
        // cache is updated asynchronously by DataChangedEvent fan-out from the
        // per-NodeType hub. A subscription on a SEPARATE client (e.g. WaitForNewReleaseAsync
        // above) only confirms the per-NodeType hub flipped â€” NOT that the mesh hub's
        // cache observed the change. Creating instance2 before the mesh hub's view
        // catches up causes EnrichWithNodeType to read the stale V1 AssemblyLocation
        // and bind instance2 to the V1 assembly for its entire lifetime
        // (HubConfiguration is captured ONCE at activation).
        //
        await WaitForMeshHubViewAsync(NodeTypePath,
            n => n?.Content is NodeTypeDefinition def
                && def.LatestReleasePath == v2Release
                && def.CompilationStatus == CompilationStatus.Ok,
            ct);

        await NodeFactory.CreateNode(new MeshNode("instance2", $"{TestPartition}/CodeEditType")
        {
            Name = "Instance 2",
            NodeType = NodeTypePath,
        });
        // Wait for the V2 marker explicitly: the per-instance hub may render a stale
        // (V1) snapshot first while the new release's HubConfiguration is still
        // propagating, then re-render once V2 is wired. Plain ReadOverviewAsync
        // takes the first HtmlControl emission and would race that pre-V2 tick.
        var v2Html = await ReadOverviewMatchingAsync(Instance2Path,
            html => html.Contains("MARKER_V2"), ct);
        v2Html.Should().Contain("MARKER_V2", "V2 release must be served after recompile");
        v2Html.Should().NotContain("MARKER_V1", "stale V1 assembly must not be reused");
    }

    /// <summary>
    /// Pin to a historical release via <see cref="NodeTypeDefinition.RequestedReleasePath"/>:
    ///   1. Compile V1, capture V1 release path.
    ///   2. Modify source to V2, compile V2 (V2 is now the latest release).
    ///   3. Pin <c>RequestedReleasePath</c> to V1 on the NodeType.
    ///   4. Create a fresh instance â€” must serve V1 (the pinned release), not V2 (latest).
    ///   5. Clear the pin â†’ fresh instance must serve V2 again.
    ///
    /// All node mutations (Source update, recompile trigger, pin set / clear) go
    /// through <c>workspace.GetMeshNodeStream(path).Update(...)</c> on the shared
    /// <see cref="MonolithMeshTestBase.Mesh"/> workspace — the canonical pattern
    /// from CLAUDE.md + <c>Doc/Architecture/RequestViaStreamUpdate.md</c>. The
    /// older shape (per-call <c>GetClient(c => c.AddData())</c> +
    /// <c>NodeFactory.UpdateNode</c> + <c>CreateReleaseRequest</c>) piled up
    /// per-client MeshNodeReference subscriptions on the per-NodeType hub; CI
    /// then wedged the hub on the next instance activation
    /// (<c>EnrichWithNodeType slow path faulted</c>, <c>SubscribeRequest</c>
    /// timeouts → compilation-error overlay → MARKER_V1 never rendered).
    /// </summary>
    [Fact(Timeout = 90000)]
    public async Task NodeType_RequestedReleasePath_PinsToHistoricalRelease()
    {
        var ct = new CancellationTokenSource(75.Seconds()).Token;
        var workspace = Mesh.GetWorkspace();
        var pinTypePath = $"{TestPartition}/PinType";
        var sourceCodePath = $"{TestPartition}/PinType/Source/code";

        // 1. Create the NodeType.
        await NodeFactory.CreateNode(new MeshNode("PinType", TestPartition)
        {
            Name = "Pin Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Regression test for RequestedReleasePath pinning.",
                Configuration = "config => config.AddDefaultLayoutAreas().AddLayout(layout => layout.WithView(\"Overview\", CodeEditLayoutAreas.Overview))",
                ShowChildrenInDetails = false,
            }
        });

        // 2. V1 source — kickoff watcher compiles it on first NodeType activation.
        //    Stages observed:
        //      a. CreateNode lands the V1 Code MeshNode.
        //      b. The per-NodeType hub's sources watcher sees the new source
        //         via its synced query and writes CurrentSourceVersions onto
        //         the NodeType; IsDirty is true momentarily (CompiledSources
        //         is still null).
        //      c. The compile watcher's kickoff flips Pending → compile runs
        //         → CompiledSources stamped → IsDirty=false on the success
        //         write-back. V1 release path emitted.
        await NodeFactory.CreateNode(new MeshNode("code", $"{TestPartition}/PinType/Source")
        {
            Name = "Code",
            NodeType = "Code",
            Content = new CodeConfiguration { Code = CodeV1, Language = "csharp" }
        });
        var v1Release = await WaitForLatestReleaseAsync(pinTypePath, knownRelease: null, ct);
        Output.WriteLine($"=== Pinned-test V1 release at {v1Release} ===");
        // V1 compile settled → IsDirty must be false on the NodeType view.
        await WaitForIsDirtyAsync(pinTypePath, expected: false, ct);

        // 3. Modify source to V2 via stream.Update — atomic on the source hub.
        //    Observe the sources watcher flip IsDirty=true once the synced
        //    query emits the post-update V2 source. THIS is what fixes the
        //    stale-source race: the compile pipeline now reads each source
        //    by path via workspace.GetMeshNodeStream(path).Take(1) (live,
        //    authoritative). Waiting for IsDirty=true here just makes the
        //    sequence explicit / observable.
        //    Then trigger an explicit recompile via the canonical
        //    `RequestedReleaseAt` flip. The per-NodeType hub's
        //    `InstallReleaseRequestWatcher` reacts to the timestamp move and
        //    `InstallCompileWatcher` runs Roslyn.
        await workspace.GetMeshNodeStream(sourceCodePath).Update(curr =>
        {
            if (curr is null) return curr!;
            return curr with { Content = new CodeConfiguration { Code = CodeV2, Language = "csharp" } };
        }).FirstAsync().ToTask(ct);
        // Source edit → sources watcher emits → IsDirty flips to true.
        await WaitForIsDirtyAsync(pinTypePath, expected: true, ct);

        var v2TriggerAt = DateTimeOffset.UtcNow;
        await TriggerRecompileAsync(pinTypePath, v2TriggerAt, ct);
        var v2Release = await WaitForLatestReleaseAsync(pinTypePath, knownRelease: v1Release, ct);
        v2Release.Should().NotBe(v1Release);
        Output.WriteLine($"=== Pinned-test V2 release at {v2Release} ===");
        // V2 compile settled → IsDirty back to false.
        await WaitForIsDirtyAsync(pinTypePath, expected: false, ct);

        // 4. Pin to V1 release via stream.Update. The handle goes straight to
        //    the owning hub's workspace; no UpdateNodeRequest forwarding
        //    round-trip, no race with the compile pipeline.
        await workspace.GetMeshNodeStream(pinTypePath).Update(curr =>
        {
            if (curr?.Content is not NodeTypeDefinition def) return curr!;
            return curr with { Content = def with { RequestedReleasePath = v1Release } };
        }).FirstAsync().ToTask(ct);

        // Wait for the pin write to be observable on the mesh-hub-cached
        // stream BEFORE creating the instance. EnrichWithNodeType reads the
        // NodeType from meshHub.GetWorkspace().GetMeshNodeStream(...) and that
        // cached view lags OWN by the OWN→MESH DataChangedEvent propagation.
        // Without this gate, an instance created in the lag window resolves
        // against the pre-write snapshot and binds to the wrong release for
        // its entire lifetime (HubConfiguration captured once at activation).
        await WaitForMeshHubViewAsync(pinTypePath,
            n => n?.Content is NodeTypeDefinition d && d.RequestedReleasePath == v1Release,
            ct);

        // 5. Fresh instance â€” pinned path means V1 must be served even though V2 is latest.
        await NodeFactory.CreateNode(new MeshNode("pinnedInstance", $"{TestPartition}/PinType")
        {
            Name = "Pinned Instance",
            NodeType = pinTypePath,
        });
        // Strict predicate: wait specifically for MARKER_V1. Loose
        // (V1||V2) predicates snap the first HtmlControl emission, which
        // can be a stale layout-area tick before the V1 HubConfiguration
        // closure is fully wired into the per-instance hub. The
        // V2-recompile test below uses the same strict-marker pattern.
        var pinnedHtml = await ReadOverviewMatchingAsync(
            $"{TestPartition}/PinType/pinnedInstance",
            html => html.Contains("MARKER_V1"),
            ct);
        pinnedHtml.Should().Contain("MARKER_V1",
            "RequestedReleasePath pins to V1 â€” instance must serve V1 even though V2 is latest");
        pinnedHtml.Should().NotContain("MARKER_V2",
            "pinned release V1 must not leak V2's body");

        // 6. Clear the pin via stream.Update — fresh instance serves V2 (latest) again.
        await workspace.GetMeshNodeStream(pinTypePath).Update(curr =>
        {
            if (curr?.Content is not NodeTypeDefinition def) return curr!;
            return curr with { Content = def with { RequestedReleasePath = null } };
        }).FirstAsync().ToTask(ct);

        // Wait for the pin-clear AND V2 latest to be observable on the
        // mesh-hub-cached stream. EnrichWithNodeType reads the NodeType
        // from meshHub.GetWorkspace().GetMeshNodeStream(nodeType) at
        // activation — if RequestedReleasePath is observed null but the
        // assembly fields are still pre-V2 (the V2 DataChangedEvent
        // hasn't landed on the mesh hub's cache yet), the slow-path
        // resolves the wrong release. Gating on V2 release path +
        // Status=Ok closes that window.
        await WaitForMeshHubViewAsync(pinTypePath,
            n => n?.Content is NodeTypeDefinition d
                && d.RequestedReleasePath == null
                && d.LatestReleasePath == v2Release
                && d.CompilationStatus == CompilationStatus.Ok
                && !string.IsNullOrEmpty(d.LatestAssemblyPath),
            ct);

        await NodeFactory.CreateNode(new MeshNode("unpinnedInstance", $"{TestPartition}/PinType")
        {
            Name = "Unpinned Instance",
            NodeType = pinTypePath,
        });
        // Strict predicate: wait specifically for MARKER_V2. See note on
        // the pinned read above (same pattern as
        // CodeEdit_ExplicitRelease_IsUpToDate_RecompilesOnSourceChange
        // step 8).
        var unpinnedHtml = await ReadOverviewMatchingAsync(
            $"{TestPartition}/PinType/unpinnedInstance",
            html => html.Contains("MARKER_V2"),
            ct);
        unpinnedHtml.Should().Contain("MARKER_V2",
            "after clearing RequestedReleasePath, fresh instance must serve the latest release (V2)");
        unpinnedHtml.Should().NotContain("MARKER_V1",
            "stale V1 assembly must not be served after the pin is cleared");
    }

    /// <summary>
    /// Sources watcher contract: editing a source flips
    /// <see cref="NodeTypeDefinition.IsDirty"/> to <c>true</c>; the next
    /// successful compile flips it back to <c>false</c> and stamps
    /// <see cref="NodeTypeDefinition.CompiledSources"/> equal to
    /// <see cref="NodeTypeDefinition.CurrentSourceVersions"/>. The UI
    /// affordance (Compile button enabled / status chip in
    /// <c>NodeTypeLayoutAreas.BuildCompileStatusPanel</c>) binds to this
    /// state machine, so this is the test that gates the UI.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task IsDirty_FlipsTrueOnSourceEdit_FalseAfterCompile()
    {
        var ct = new CancellationTokenSource(45.Seconds()).Token;
        var workspace = Mesh.GetWorkspace();
        var typePath = $"{TestPartition}/DirtyType";
        var sourcePath = $"{TestPartition}/DirtyType/Source/code";

        await NodeFactory.CreateNode(new MeshNode("DirtyType", TestPartition)
        {
            Name = "Dirty Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "IsDirty state-machine regression.",
                Configuration = "config => config.AddDefaultLayoutAreas().AddLayout(layout => layout.WithView(\"Overview\", CodeEditLayoutAreas.Overview))",
                ShowChildrenInDetails = false,
            }
        });

        await NodeFactory.CreateNode(new MeshNode("code", $"{TestPartition}/DirtyType/Source")
        {
            Name = "Code",
            NodeType = "Code",
            Content = new CodeConfiguration { Code = CodeV1, Language = "csharp" }
        });
        // V1 compile completes — IsDirty=false on the success write-back.
        await WaitForLatestReleaseAsync(typePath, knownRelease: null, ct);
        await WaitForIsDirtyAsync(typePath, expected: false, ct);

        // Snapshot CurrentSourceVersions before the edit. After the edit
        // lands and the watcher emits, the snapshot must DIFFER and IsDirty
        // must flip true.
        var before = await Mesh.GetWorkspace().GetMeshNodeStream(typePath)
            .Where(n => n?.Content is NodeTypeDefinition d && d.CurrentSourceVersions?.Count > 0)
            .Take(1).Timeout(TimeSpan.FromSeconds(15)).ToTask(ct);
        var beforeVersions = ((NodeTypeDefinition)before.Content!).CurrentSourceVersions!;
        beforeVersions.Should().NotBeEmpty("V1 compile must have populated CurrentSourceVersions");

        // Edit the source — this must propagate to CurrentSourceVersions.
        await workspace.GetMeshNodeStream(sourcePath).Update(curr =>
            curr with { Content = new CodeConfiguration { Code = CodeV2, Language = "csharp" } })
            .FirstAsync().ToTask(ct);

        // Sources watcher emits → IsDirty=true.
        await WaitForIsDirtyAsync(typePath, expected: true, ct);

        // CurrentSourceVersions must reflect the new content (different
        // tick value for the same path).
        var afterEdit = await Mesh.GetWorkspace().GetMeshNodeStream(typePath)
            .Where(n => n?.Content is NodeTypeDefinition d
                && d.IsDirty
                && d.CurrentSourceVersions?.Count > 0)
            .Take(1).Timeout(TimeSpan.FromSeconds(15)).ToTask(ct);
        var afterVersions = ((NodeTypeDefinition)afterEdit.Content!).CurrentSourceVersions!;
        afterVersions.Should().HaveSameCount(beforeVersions, "edit modified content, not the path set");
        afterVersions[sourcePath].Should().NotBe(beforeVersions[sourcePath],
            "the source path's version stamp must change when the source content changes");

        // Trigger recompile and confirm IsDirty resets to false.
        await TriggerRecompileAsync(typePath, DateTimeOffset.UtcNow, ct);
        await WaitForLatestReleaseAsync(typePath, knownRelease: null, ct);
        await WaitForIsDirtyAsync(typePath, expected: false, ct);
    }

    /// <summary>
    /// Failed compile: the activity log is preserved on the NodeType
    /// (via <see cref="NodeTypeDefinition.LastCompilationActivityPath"/>)
    /// AND <see cref="NodeTypeDefinition.CompilationError"/> is populated,
    /// but <see cref="NodeTypeDefinition.LatestReleasePath"/> stays
    /// unchanged — no new Release is created for a failed compile. The
    /// UI binds the error to the Overview's compile-status panel +
    /// surfaces the activity log on the Release tab so the user can read
    /// the diagnostics without leaving the NodeType view.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task FailedCompile_PreservesErrorLogAndDoesNotCreateRelease()
    {
        var ct = new CancellationTokenSource(45.Seconds()).Token;
        var workspace = Mesh.GetWorkspace();
        var typePath = $"{TestPartition}/BrokenType";
        var sourcePath = $"{TestPartition}/BrokenType/Source/code";

        await NodeFactory.CreateNode(new MeshNode("BrokenType", TestPartition)
        {
            Name = "Broken Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Failed-compile regression.",
                Configuration = "config => config.AddDefaultLayoutAreas().AddLayout(layout => layout.WithView(\"Overview\", CodeEditLayoutAreas.Overview))",
                ShowChildrenInDetails = false,
            }
        });

        // Plant a deliberately uncompilable source so the first compile fails.
        await NodeFactory.CreateNode(new MeshNode("code", $"{TestPartition}/BrokenType/Source")
        {
            Name = "Code",
            NodeType = "Code",
            Content = new CodeConfiguration
            {
                Code = "this is not valid C#;",  // Roslyn rejects.
                Language = "csharp"
            }
        });

        // Wait for compile to settle (Status=Error). No release should be created.
        var failed = await Mesh.GetWorkspace().GetMeshNodeStream(typePath)
            .Where(n => n?.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Error)
            .Take(1).Timeout(TimeSpan.FromSeconds(40)).ToTask(ct);
        var failedDef = (NodeTypeDefinition)failed.Content!;

        failedDef.CompilationError.Should().NotBeNullOrEmpty(
            "Failed compile must persist a human-readable error so the operator can fix the source");
        failedDef.LastCompilationActivityPath.Should().NotBeNullOrEmpty(
            "Failed compile must link to its activity log so the UI can surface Roslyn diagnostics");
        failedDef.LatestReleasePath.Should().BeNullOrEmpty(
            "Failed compile must NOT create a Release MeshNode — the activity log is the only artifact");

        // Now fix the source. The next compile should succeed AND IsDirty
        // should toggle false. This proves the failed-compile state isn't
        // sticky.
        await workspace.GetMeshNodeStream(sourcePath).Update(curr =>
            curr with { Content = new CodeConfiguration { Code = CodeV1, Language = "csharp" } })
            .FirstAsync().ToTask(ct);

        // Source edit → sources watcher → IsDirty=true (even from Error state).
        await WaitForIsDirtyAsync(typePath, expected: true, ct);

        await TriggerRecompileAsync(typePath, DateTimeOffset.UtcNow, ct);

        // After successful recompile: Status=Ok, LatestReleasePath set, IsDirty=false.
        await Mesh.GetWorkspace().GetMeshNodeStream(typePath)
            .Where(n => n?.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Ok
                && !string.IsNullOrEmpty(d.LatestReleasePath)
                && !d.IsDirty)
            .Take(1).Timeout(TimeSpan.FromSeconds(40)).ToTask(ct);
    }

    /// <summary>
    /// Compile-button contract: the Overview-side panel
    /// (<c>NodeTypeLayoutAreas.BuildCompileStatusPanel</c>) renders a button
    /// whose click handler stamps
    /// <see cref="NodeTypeDefinition.RequestedReleaseAt"/> +
    /// <see cref="NodeTypeDefinition.RequestedReleaseForce"/> on the NodeType
    /// MeshNode via <c>workspace.GetMeshNodeStream(path).Update(...)</c>. The
    /// per-NodeType hub's <c>InstallReleaseRequestWatcher</c> picks the
    /// timestamp move up and runs the compile. This test simulates that click
    /// — same code path the button executes — and asserts both the property
    /// flip and the resulting release.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task PressingCompileButton_SetsRequestedReleaseAt_AndProducesNewRelease()
    {
        var ct = new CancellationTokenSource(45.Seconds()).Token;
        var workspace = Mesh.GetWorkspace();
        var typePath = $"{TestPartition}/ButtonType";

        await NodeFactory.CreateNode(new MeshNode("ButtonType", TestPartition)
        {
            Name = "Button Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Compile-button regression.",
                Configuration = "config => config.AddDefaultLayoutAreas().AddLayout(layout => layout.WithView(\"Overview\", CodeEditLayoutAreas.Overview))",
                ShowChildrenInDetails = false,
            }
        });
        await NodeFactory.CreateNode(new MeshNode("code", $"{TestPartition}/ButtonType/Source")
        {
            Name = "Code",
            NodeType = "Code",
            Content = new CodeConfiguration { Code = CodeV1, Language = "csharp" }
        });

        // V1 compile settles (Status=Ok, first release present).
        var v1Release = await WaitForLatestReleaseAsync(typePath, knownRelease: null, ct);
        await WaitForIsDirtyAsync(typePath, expected: false, ct);

        // Snapshot RequestedReleaseAt before — should be null since the kickoff
        // compile fires off the Pending flip directly, not via the release
        // trigger.
        var beforeNode = await Mesh.GetWorkspace().GetMeshNodeStream(typePath)
            .Where(n => n?.Content is NodeTypeDefinition).Take(1)
            .Timeout(TimeSpan.FromSeconds(10)).ToTask(ct);
        ((NodeTypeDefinition)beforeNode.Content!).RequestedReleaseAt
            .Should().BeNull("kickoff compile uses the Pending flip directly, not the release trigger");

        // Simulate the click — replicates BuildCompileStatusPanel's handler
        // body verbatim. Any drift between the button and this test means
        // the UI affordance broke.
        var triggerAt = DateTimeOffset.UtcNow;
        await workspace.GetMeshNodeStream(typePath).Update(curr =>
        {
            if (curr?.Content is not NodeTypeDefinition cd) return curr!;
            return curr with
            {
                Content = cd with
                {
                    RequestedReleaseAt = triggerAt,
                    RequestedReleaseForce = true
                }
            };
        }).FirstAsync().ToTask(ct);

        // 1. The property flip is observable on the mesh-hub view.
        var afterNode = await Mesh.GetWorkspace().GetMeshNodeStream(typePath)
            .Where(n => n?.Content is NodeTypeDefinition d
                && d.RequestedReleaseAt == triggerAt
                && d.RequestedReleaseForce)
            .Take(1).Timeout(TimeSpan.FromSeconds(10)).ToTask(ct);
        ((NodeTypeDefinition)afterNode.Content!).RequestedReleaseAt.Should().Be(triggerAt);

        // 2. The trigger drives a fresh compile → a new Release path lands.
        var v2Release = await WaitForLatestReleaseAsync(typePath, knownRelease: v1Release, ct);
        v2Release.Should().NotBe(v1Release, "button click must produce a fresh release distinct from the kickoff one");

        // 3. After the recompile settles, LastReleaseRequestHandledAt catches up
        //    to the trigger so a future click with a newer timestamp is again
        //    dispatchable (the watcher uses strict-greater-than as the gate).
        await Mesh.GetWorkspace().GetMeshNodeStream(typePath)
            .Where(n => n?.Content is NodeTypeDefinition d
                && d.LastReleaseRequestHandledAt is { } handled
                && handled >= triggerAt)
            .Take(1).Timeout(TimeSpan.FromSeconds(15)).ToTask(ct);
    }

    /// <summary>
    /// Drives the canonical "create release" request via
    /// <c>workspace.GetMeshNodeStream(path).Update(...)</c>: setting
    /// <see cref="NodeTypeDefinition.RequestedReleaseAt"/> +
    /// <see cref="NodeTypeDefinition.RequestedReleaseForce"/> on the NodeType.
    /// The per-NodeType hub's <c>InstallReleaseRequestWatcher</c> picks the
    /// trigger up (timestamp ≠ last-handled) and flips
    /// <c>CompilationStatus = Pending</c>; the compile watcher then runs
    /// Roslyn. This replaces <c>CreateReleaseRequest</c> + a fresh client per
    /// call — the bespoke handler used to race the watcher under CI load.
    /// </summary>
    private async Task TriggerRecompileAsync(string nodeTypePath, DateTimeOffset triggerAt, CancellationToken ct)
    {
        var workspace = Mesh.GetWorkspace();
        await workspace.GetMeshNodeStream(nodeTypePath).Update(curr =>
        {
            if (curr?.Content is not NodeTypeDefinition def) return curr!;
            return curr with
            {
                Content = def with
                {
                    RequestedReleaseAt = triggerAt,
                    RequestedReleaseForce = true,
                }
            };
        }).FirstAsync().ToTask(ct);
    }

    /// <summary>
    /// Waits for the NodeType's <see cref="NodeTypeDefinition.LatestReleasePath"/>
    /// to settle on a value different from <paramref name="knownRelease"/> with
    /// <c>CompilationStatus = Ok</c>. Read on the SHARED <see cref="Mesh"/>
    /// workspace — one cached <c>MeshNodeStreamHandle</c> per path across the
    /// whole test, so multiple waits don't pile new <c>SubscribeRequest</c>s
    /// on the per-NodeType hub.
    /// </summary>
    private async Task<string> WaitForLatestReleaseAsync(
        string nodeTypePath, string? knownRelease, CancellationToken ct)
    {
        var workspace = Mesh.GetWorkspace();
        var node = await workspace.GetMeshNodeStream(nodeTypePath)
            .Where(n => n?.Content is NodeTypeDefinition def
                && def.CompilationStatus == CompilationStatus.Ok
                && !string.IsNullOrEmpty(def.LatestReleasePath)
                && def.LatestReleasePath != knownRelease)
            .Take(1)
            .Timeout(50.Seconds())
            .ToTask(ct);
        return ((NodeTypeDefinition)node.Content!).LatestReleasePath!;
    }

    private async Task<CreateReleaseResponse> SendCreateReleaseAsync(
        string nodeTypePath, bool force, CancellationToken ct)
    {
        var reader = GetClient(c => c.AddData());
        var response = await reader
            .Observe(new CreateReleaseRequest(Force: force), o => o.WithTarget(new Address(nodeTypePath)))
            .Select(d => d.Message)
            .Timeout(TimeSpan.FromSeconds(30))
            .FirstAsync()
            .ToTask(ct);
        // Wait for compile to complete (status = Ok or Error) before returning.
        // Live remote stream (GetMeshNodeStream(path)) â€” NOT ObserveQuery, which
        // is index-lagged and can miss the post-compile tick (per the CQRS
        // feedback note + Doc/Architecture/CqrsAndContentAccess.md). Path is
        // known here, so the live stream is the right primitive.
        if (!response.AlreadyUpToDate && response.Success)
        {
            await reader.GetWorkspace().GetMeshNodeStream(nodeTypePath)
                .Where(n => n?.Content is NodeTypeDefinition def
                    && (def.CompilationStatus == CompilationStatus.Ok
                        || def.CompilationStatus == CompilationStatus.Error))
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(45))
                .ToTask(ct);
        }
        return response;
    }

    private Task<string> ReadOverviewAsync(string path, CancellationToken ct)
        => ReadOverviewMatchingAsync(path, _ => true, ct);

    /// <summary>
    /// Reads the Overview area and waits for an <see cref="HtmlControl"/> whose
    /// data matches <paramref name="matches"/>. Used by V2 reads where the per-node
    /// hub may emit a stale (V1) snapshot first while the new release's
    /// HubConfiguration is still propagating â€” taking <c>FirstAsync(x is HtmlControl)</c>
    /// would race the first stale emission and fail the assertion before the
    /// V2-bound re-render lands.
    /// </summary>
    private async Task<string> ReadOverviewMatchingAsync(string path, Func<string, bool> matches, CancellationToken ct)
    {
        var client = GetClient(c => c.AddData());
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference("Overview");
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(path), reference);

        var control = await stream
            .GetControlStream(reference.Area!)
            .Where(x => x is HtmlControl h && matches(h.Data?.ToString() ?? string.Empty))
            .Take(1)
            .Timeout(30.Seconds())
            .ToTask(ct);

        return (control as HtmlControl)?.Data?.ToString() ?? string.Empty;
    }

    /// <summary>
    /// Waits for a fresh <c>Release</c> MeshNode whose path differs from any in
    /// <paramref name="knownReleases"/>. Reads <see cref="NodeTypeDefinition.LatestReleasePath"/>
    /// off the live <see cref="GetMeshNodeStream"/> â€” atomic with the post-compile
    /// status flip, so by the time CompilationStatus settles to Ok the new path
    /// is already on the NodeType. Avoids the lagged <c>ObserveQuery</c> namespace
    /// scan over <c>Release/*</c>.
    /// </summary>
    private async Task<string> WaitForNewReleaseAsync(
        string nodeTypePath, HashSet<string> knownReleases, CancellationToken ct)
    {
        var reader = GetClient(c => c.AddData());
        var node = await reader.GetWorkspace().GetMeshNodeStream(nodeTypePath)
            .Where(n => n?.Content is NodeTypeDefinition def
                && !string.IsNullOrEmpty(def.LatestReleasePath)
                && !knownReleases.Contains(def.LatestReleasePath!))
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(50))
            .ToTask(ct);
        return ((NodeTypeDefinition)node.Content!).LatestReleasePath!;
    }

    /// <summary>
    /// Waits until the MESH HUB's workspace view of <paramref name="path"/>
    /// matches <paramref name="predicate"/>. Required before creating a
    /// per-instance hub that depends on a recent write to the NodeType MeshNode:
    /// the activation path
    /// (<c>NodeTypeEnrichmentHelpers.EnrichWithNodeType</c>) reads the NodeType
    /// MeshNode from <c>meshHub.GetWorkspace().GetMeshNodeStream(nodeType)</c>,
    /// and that cache is updated asynchronously by DataChangedEvent fan-out
    /// from the per-NodeType hub. A separate client's view (e.g. a fresh test
    /// reader subscribed via <c>GetClient(...)</c>) is a different
    /// <c>ISynchronizationStream</c> on a different scheduler â€” seeing the
    /// write there does NOT imply the mesh hub's cache has caught up. Per
    /// <c>HubConfiguration</c> being captured once at activation, an instance
    /// created before mesh hub catches up is bound to the stale snapshot for
    /// its entire lifetime.
    /// </summary>
    private async Task WaitForMeshHubViewAsync(
        string path, Func<MeshNode?, bool> predicate, CancellationToken ct)
    {
        await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(predicate)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(30))
            .ToTask(ct);
    }

    /// <summary>
    /// Waits for <see cref="NodeTypeDefinition.IsDirty"/> on the NodeType at
    /// <paramref name="nodeTypePath"/> to reach <paramref name="expected"/>.
    /// The flag is written by the per-NodeType hub's sources watcher every
    /// time the synced query over Sources+Tests emits — observing it
    /// confirms the watcher has seen the post-update state and the next
    /// compile will read the fresh source set.
    /// </summary>
    private Task WaitForIsDirtyAsync(string nodeTypePath, bool expected, CancellationToken ct) =>
        WaitForMeshHubViewAsync(nodeTypePath,
            n => n?.Content is NodeTypeDefinition d && d.IsDirty == expected,
            ct);
}
