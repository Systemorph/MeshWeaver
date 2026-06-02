using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
    [Fact(Timeout = 60000)]
    public void CodeEdit_ExplicitRelease_IsUpToDate_RecompilesOnSourceChange()
    {
        // 1. Create the NodeType.
        NodeFactory.CreateNode(new MeshNode("CodeEditType", TestPartition)
        {
            Name = "Code Edit Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Regression test for the explicit compile flow.",
                Configuration = "config => config.AddDefaultLayoutAreas().AddLayout(layout => layout.WithView(\"Overview\", CodeEditLayoutAreas.Overview))",
                ShowChildrenInDetails = false,
            }
        }).Should().Within(30.Seconds()).Emit();

        // 2. Create the V1 source.
        NodeFactory.CreateNode(new MeshNode("code", $"{TestPartition}/CodeEditType/Source")
        {
            Name = "Code",
            NodeType = "Code",
            Content = new CodeConfiguration { Code = CodeV1, Language = "csharp" }
        }).Should().Within(30.Seconds()).Emit();

        // 3. Trigger V1 compilation. The per-NodeType hub's
        // InstallCompileWatcher kickoff also flips Pending â†’ Compile on first
        // activation when HasUsableBuild is false; either path is acceptable so
        // long as a real V1 Release lands. We send CreateReleaseRequest both as
        // the canonical explicit trigger AND to wait for the compile to settle.
        // AlreadyUpToDate may legitimately be true here if the kickoff beat us
        // to first compile â€” what we check is that V1 release is produced.
        var v1Response = SendCreateRelease(NodeTypePath, force: false);
        v1Response.Success.Should().BeTrue("CreateReleaseRequest should succeed");

        var v1Release = WaitForNewRelease(NodeTypePath, knownReleases: []);
        Output.WriteLine($"=== V1 release at {v1Release} ===");

        // 4. CreateReleaseRequest again without changes â†’ AlreadyUpToDate.
        var dupResponse = SendCreateRelease(NodeTypePath, force: false);
        dupResponse.AlreadyUpToDate.Should().BeTrue("sources unchanged since V1 compile");

        // 5. Create an instance and verify it serves V1.
        NodeFactory.CreateNode(new MeshNode("instance1", $"{TestPartition}/CodeEditType")
        {
            Name = "Instance 1",
            NodeType = NodeTypePath,
        }).Should().Within(30.Seconds()).Emit();
        var v1Html = ReadOverview(InstancePath);
        v1Html.Should().Contain("MARKER_V1", "V1 release must be served");

        // 6. Modify the source to V2. Live remote stream â€” path is known, no
        // index lag (per CqrsAndContentAccess.md).
        var sourceClient = GetClient(c => c.AddData());
        var codeNode = sourceClient.GetWorkspace()
            .GetMeshNodeStream($"{TestPartition}/CodeEditType/Source/code")
            .Should().Within(TimeSpan.FromSeconds(15)).Match(n => n is not null);
        NodeFactory.UpdateNode(codeNode with
        {
            Content = new CodeConfiguration { Code = CodeV2, Language = "csharp" }
        }).Should().Within(30.Seconds()).Emit();

        // 7. Explicitly trigger V2 compilation â€” sources changed, should recompile.
        var v2Response = SendCreateRelease(NodeTypePath, force: false);
        v2Response.Success.Should().BeTrue("CreateReleaseRequest should succeed after source change");
        v2Response.AlreadyUpToDate.Should().BeFalse("source was modified, must recompile");

        var v2Release = WaitForNewRelease(NodeTypePath, knownReleases: [v1Release]);
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
        WaitForMeshHubView(NodeTypePath,
            n => n?.Content is NodeTypeDefinition def
                && def.LatestReleasePath == v2Release
                && def.CompilationStatus == CompilationStatus.Ok);

        NodeFactory.CreateNode(new MeshNode("instance2", $"{TestPartition}/CodeEditType")
        {
            Name = "Instance 2",
            NodeType = NodeTypePath,
        }).Should().Within(30.Seconds()).Emit();
        // Wait for the V2 marker explicitly: the per-instance hub may render a stale
        // (V1) snapshot first while the new release's HubConfiguration is still
        // propagating, then re-render once V2 is wired. Plain ReadOverview
        // takes the first HtmlControl emission and would race that pre-V2 tick.
        var v2Html = ReadOverviewMatching(Instance2Path, html => html.Contains("MARKER_V2"));
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
    [Fact(Timeout = 60000)]
    public void NodeType_RequestedReleasePath_PinsToHistoricalRelease()
    {
        var workspace = Mesh.GetWorkspace();
        var pinTypePath = $"{TestPartition}/PinType";
        var sourceCodePath = $"{TestPartition}/PinType/Source/code";

        // 1. Create the NodeType.
        NodeFactory.CreateNode(new MeshNode("PinType", TestPartition)
        {
            Name = "Pin Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Regression test for RequestedReleasePath pinning.",
                Configuration = "config => config.AddDefaultLayoutAreas().AddLayout(layout => layout.WithView(\"Overview\", CodeEditLayoutAreas.Overview))",
                ShowChildrenInDetails = false,
            }
        }).Should().Within(30.Seconds()).Emit();

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
        NodeFactory.CreateNode(new MeshNode("code", $"{TestPartition}/PinType/Source")
        {
            Name = "Code",
            NodeType = "Code",
            Content = new CodeConfiguration { Code = CodeV1, Language = "csharp" }
        }).Should().Within(30.Seconds()).Emit();
        var v1Release = WaitForLatestRelease(pinTypePath, knownRelease: null);
        Output.WriteLine($"=== Pinned-test V1 release at {v1Release} ===");
        // V1 compile settled → IsDirty must be false on the NodeType view.
        WaitForIsDirty(pinTypePath, expected: false);

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
        workspace.GetMeshNodeStream(sourceCodePath).Update(curr =>
        {
            if (curr is null) return curr!;
            return curr with { Content = new CodeConfiguration { Code = CodeV2, Language = "csharp" } };
        }).Should().Within(30.Seconds()).Emit();
        // Source edit → sources watcher emits → IsDirty flips to true.
        WaitForIsDirty(pinTypePath, expected: true);

        var v2TriggerAt = DateTimeOffset.UtcNow;
        TriggerRecompile(pinTypePath, v2TriggerAt);
        var v2Release = WaitForLatestRelease(pinTypePath, knownRelease: v1Release);
        v2Release.Should().NotBe(v1Release);
        Output.WriteLine($"=== Pinned-test V2 release at {v2Release} ===");
        // V2 compile settled → IsDirty back to false.
        WaitForIsDirty(pinTypePath, expected: false);

        // 4. Pin to V1 release via stream.Update. The handle goes straight to
        //    the owning hub's workspace; no UpdateNodeRequest forwarding
        //    round-trip, no race with the compile pipeline.
        workspace.GetMeshNodeStream(pinTypePath).Update(curr =>
        {
            if (curr?.Content is not NodeTypeDefinition def) return curr!;
            return curr with { Content = def with { RequestedReleasePath = v1Release } };
        }).Should().Within(30.Seconds()).Emit();

        // Wait for the pin write to be observable on the mesh-hub-cached
        // stream BEFORE creating the instance. EnrichWithNodeType reads the
        // NodeType from meshHub.GetWorkspace().GetMeshNodeStream(...) and that
        // cached view lags OWN by the OWN→MESH DataChangedEvent propagation.
        // Without this gate, an instance created in the lag window resolves
        // against the pre-write snapshot and binds to the wrong release for
        // its entire lifetime (HubConfiguration captured once at activation).
        WaitForMeshHubView(pinTypePath,
            n => n?.Content is NodeTypeDefinition d && d.RequestedReleasePath == v1Release);

        // 5. Fresh instance â€” pinned path means V1 must be served even though V2 is latest.
        NodeFactory.CreateNode(new MeshNode("pinnedInstance", $"{TestPartition}/PinType")
        {
            Name = "Pinned Instance",
            NodeType = pinTypePath,
        }).Should().Within(30.Seconds()).Emit();
        // Strict predicate: wait specifically for MARKER_V1. Loose
        // (V1||V2) predicates snap the first HtmlControl emission, which
        // can be a stale layout-area tick before the V1 HubConfiguration
        // closure is fully wired into the per-instance hub. The
        // V2-recompile test below uses the same strict-marker pattern.
        var pinnedHtml = ReadOverviewMatching(
            $"{TestPartition}/PinType/pinnedInstance",
            html => html.Contains("MARKER_V1"));
        pinnedHtml.Should().Contain("MARKER_V1",
            "RequestedReleasePath pins to V1 â€” instance must serve V1 even though V2 is latest");
        pinnedHtml.Should().NotContain("MARKER_V2",
            "pinned release V1 must not leak V2's body");

        // 6. Clear the pin via stream.Update — fresh instance serves V2 (latest) again.
        workspace.GetMeshNodeStream(pinTypePath).Update(curr =>
        {
            if (curr?.Content is not NodeTypeDefinition def) return curr!;
            return curr with { Content = def with { RequestedReleasePath = null } };
        }).Should().Within(30.Seconds()).Emit();

        // Wait for the pin-clear AND V2 latest to be observable on the
        // mesh-hub-cached stream. EnrichWithNodeType reads the NodeType
        // from meshHub.GetWorkspace().GetMeshNodeStream(nodeType) at
        // activation — if RequestedReleasePath is observed null but the
        // assembly fields are still pre-V2 (the V2 DataChangedEvent
        // hasn't landed on the mesh hub's cache yet), the slow-path
        // resolves the wrong release. Gating on V2 release path +
        // Status=Ok closes that window.
        WaitForMeshHubView(pinTypePath,
            n => n?.Content is NodeTypeDefinition d
                && d.RequestedReleasePath == null
                && d.LatestReleasePath == v2Release
                && d.CompilationStatus == CompilationStatus.Ok
                && !string.IsNullOrEmpty(d.LatestAssemblyPath));

        NodeFactory.CreateNode(new MeshNode("unpinnedInstance", $"{TestPartition}/PinType")
        {
            Name = "Unpinned Instance",
            NodeType = pinTypePath,
        }).Should().Within(30.Seconds()).Emit();
        // Strict predicate: wait specifically for MARKER_V2. See note on
        // the pinned read above (same pattern as
        // CodeEdit_ExplicitRelease_IsUpToDate_RecompilesOnSourceChange
        // step 8).
        var unpinnedHtml = ReadOverviewMatching(
            $"{TestPartition}/PinType/unpinnedInstance",
            html => html.Contains("MARKER_V2"));
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
    public void IsDirty_FlipsTrueOnSourceEdit_FalseAfterCompile()
    {
        var workspace = Mesh.GetWorkspace();
        var typePath = $"{TestPartition}/DirtyType";
        var sourcePath = $"{TestPartition}/DirtyType/Source/code";

        NodeFactory.CreateNode(new MeshNode("DirtyType", TestPartition)
        {
            Name = "Dirty Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "IsDirty state-machine regression.",
                Configuration = "config => config.AddDefaultLayoutAreas().AddLayout(layout => layout.WithView(\"Overview\", CodeEditLayoutAreas.Overview))",
                ShowChildrenInDetails = false,
            }
        }).Should().Within(30.Seconds()).Emit();

        NodeFactory.CreateNode(new MeshNode("code", $"{TestPartition}/DirtyType/Source")
        {
            Name = "Code",
            NodeType = "Code",
            Content = new CodeConfiguration { Code = CodeV1, Language = "csharp" }
        }).Should().Within(30.Seconds()).Emit();
        // V1 compile completes — IsDirty=false on the success write-back.
        WaitForLatestRelease(typePath, knownRelease: null);
        WaitForIsDirty(typePath, expected: false);

        // Snapshot CurrentSourceVersions before the edit. After the edit
        // lands and the watcher emits, the snapshot must DIFFER and IsDirty
        // must flip true.
        var before = Mesh.GetWorkspace().GetMeshNodeStream(typePath)
            .Should().Within(TimeSpan.FromSeconds(15))
            .Match(n => n?.Content is NodeTypeDefinition d && d.CurrentSourceVersions?.Count > 0);
        var beforeVersions = ((NodeTypeDefinition)before.Content!).CurrentSourceVersions!;
        beforeVersions.Should().NotBeEmpty("V1 compile must have populated CurrentSourceVersions");

        // Edit the source — this must propagate to CurrentSourceVersions.
        workspace.GetMeshNodeStream(sourcePath).Update(curr =>
            curr with { Content = new CodeConfiguration { Code = CodeV2, Language = "csharp" } })
            .Should().Within(30.Seconds()).Emit();

        // Sources watcher emits → IsDirty=true.
        WaitForIsDirty(typePath, expected: true);

        // CurrentSourceVersions must reflect the new content (different
        // tick value for the same path).
        var afterEdit = Mesh.GetWorkspace().GetMeshNodeStream(typePath)
            .Should().Within(TimeSpan.FromSeconds(15))
            .Match(n => n?.Content is NodeTypeDefinition d
                && d.IsDirty
                && d.CurrentSourceVersions?.Count > 0);
        var afterVersions = ((NodeTypeDefinition)afterEdit.Content!).CurrentSourceVersions!;
        afterVersions.Should().HaveSameCount(beforeVersions, "edit modified content, not the path set");
        afterVersions[sourcePath].Should().NotBe(beforeVersions[sourcePath],
            "the source path's version stamp must change when the source content changes");

        // Trigger recompile and confirm IsDirty resets to false.
        TriggerRecompile(typePath, DateTimeOffset.UtcNow);
        WaitForLatestRelease(typePath, knownRelease: null);
        WaitForIsDirty(typePath, expected: false);
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
    public void FailedCompile_PreservesErrorLogAndDoesNotCreateRelease()
    {
        var workspace = Mesh.GetWorkspace();
        var typePath = $"{TestPartition}/BrokenType";
        var sourcePath = $"{TestPartition}/BrokenType/Source/code";

        NodeFactory.CreateNode(new MeshNode("BrokenType", TestPartition)
        {
            Name = "Broken Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Failed-compile regression.",
                Configuration = "config => config.AddDefaultLayoutAreas().AddLayout(layout => layout.WithView(\"Overview\", CodeEditLayoutAreas.Overview))",
                ShowChildrenInDetails = false,
            }
        }).Should().Within(30.Seconds()).Emit();

        // Plant a deliberately uncompilable source so the first compile fails.
        NodeFactory.CreateNode(new MeshNode("code", $"{TestPartition}/BrokenType/Source")
        {
            Name = "Code",
            NodeType = "Code",
            Content = new CodeConfiguration
            {
                Code = "this is not valid C#;",  // Roslyn rejects.
                Language = "csharp"
            }
        }).Should().Within(30.Seconds()).Emit();

        // Explicitly trigger the compile via RequestedReleaseAt. Before
        // 2026-05-21 the InstallCompileWatcher kickoff auto-triggered on
        // grain activation; that path was deleted because it spawned
        // recompiles under transient AccessContexts in prod. Recompile is
        // now ALWAYS an explicit action — UI button or test trigger.
        TriggerRecompile(typePath, DateTimeOffset.UtcNow);

        // Wait for compile to settle (Status=Error). No release should be created.
        var failed = Mesh.GetWorkspace().GetMeshNodeStream(typePath)
            .Should().Within(TimeSpan.FromSeconds(40))
            .Match(n => n?.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Error);
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
        workspace.GetMeshNodeStream(sourcePath).Update(curr =>
            curr with { Content = new CodeConfiguration { Code = CodeV1, Language = "csharp" } })
            .Should().Within(30.Seconds()).Emit();

        // Source edit → sources watcher → IsDirty=true (even from Error state).
        WaitForIsDirty(typePath, expected: true);

        TriggerRecompile(typePath, DateTimeOffset.UtcNow);

        // After successful recompile: Status=Ok, LatestReleasePath set, IsDirty=false.
        Mesh.GetWorkspace().GetMeshNodeStream(typePath)
            .Should().Within(TimeSpan.FromSeconds(40))
            .Match(n => n?.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Ok
                && !string.IsNullOrEmpty(d.LatestReleasePath)
                && !d.IsDirty);
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
    public void PressingCompileButton_SetsRequestedReleaseAt_AndProducesNewRelease()
    {
        var workspace = Mesh.GetWorkspace();
        var typePath = $"{TestPartition}/ButtonType";

        NodeFactory.CreateNode(new MeshNode("ButtonType", TestPartition)
        {
            Name = "Button Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Compile-button regression.",
                Configuration = "config => config.AddDefaultLayoutAreas().AddLayout(layout => layout.WithView(\"Overview\", CodeEditLayoutAreas.Overview))",
                ShowChildrenInDetails = false,
            }
        }).Should().Within(30.Seconds()).Emit();
        NodeFactory.CreateNode(new MeshNode("code", $"{TestPartition}/ButtonType/Source")
        {
            Name = "Code",
            NodeType = "Code",
            Content = new CodeConfiguration { Code = CodeV1, Language = "csharp" }
        }).Should().Within(30.Seconds()).Emit();

        // V1 compile settles (Status=Ok, first release present).
        var v1Release = WaitForLatestRelease(typePath, knownRelease: null);
        WaitForIsDirty(typePath, expected: false);

        // Snapshot RequestedReleaseAt before — should be null since the kickoff
        // compile fires off the Pending flip directly, not via the release
        // trigger.
        var beforeNode = Mesh.GetWorkspace().GetMeshNodeStream(typePath)
            .Should().Within(TimeSpan.FromSeconds(10)).Match(n => n?.Content is NodeTypeDefinition);
        ((NodeTypeDefinition)beforeNode.Content!).RequestedReleaseAt
            .Should().BeNull("kickoff compile uses the Pending flip directly, not the release trigger");

        // Simulate the click — replicates BuildCompileStatusPanel's handler
        // body verbatim. Any drift between the button and this test means
        // the UI affordance broke.
        var triggerAt = DateTimeOffset.UtcNow;
        workspace.GetMeshNodeStream(typePath).Update(curr =>
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
        }).Should().Within(30.Seconds()).Emit();

        // 1. The property flip is observable on the mesh-hub view.
        var afterNode = Mesh.GetWorkspace().GetMeshNodeStream(typePath)
            .Should().Within(TimeSpan.FromSeconds(10))
            .Match(n => n?.Content is NodeTypeDefinition d
                && d.RequestedReleaseAt == triggerAt
                && d.RequestedReleaseForce);
        ((NodeTypeDefinition)afterNode.Content!).RequestedReleaseAt.Should().Be(triggerAt);

        // 2. The trigger drives a fresh compile → a new Release path lands.
        var v2Release = WaitForLatestRelease(typePath, knownRelease: v1Release);
        v2Release.Should().NotBe(v1Release, "button click must produce a fresh release distinct from the kickoff one");

        // 3. After the recompile settles, LastReleaseRequestHandledAt catches up
        //    to the trigger so a future click with a newer timestamp is again
        //    dispatchable (the watcher uses strict-greater-than as the gate).
        Mesh.GetWorkspace().GetMeshNodeStream(typePath)
            .Should().Within(TimeSpan.FromSeconds(15))
            .Match(n => n?.Content is NodeTypeDefinition d
                && d.LastReleaseRequestHandledAt is { } handled
                && handled >= triggerAt);
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
    private void TriggerRecompile(string nodeTypePath, DateTimeOffset triggerAt)
    {
        var workspace = Mesh.GetWorkspace();
        workspace.GetMeshNodeStream(nodeTypePath).Update(curr =>
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
        }).Should().Within(30.Seconds()).Emit();
    }

    /// <summary>
    /// Waits for the NodeType's <see cref="NodeTypeDefinition.LatestReleasePath"/>
    /// to settle on a value different from <paramref name="knownRelease"/> with
    /// <c>CompilationStatus = Ok</c>. Read on the SHARED <see cref="Mesh"/>
    /// workspace — one cached <c>MeshNodeStreamHandle</c> per path across the
    /// whole test, so multiple waits don't pile new <c>SubscribeRequest</c>s
    /// on the per-NodeType hub.
    /// </summary>
    private string WaitForLatestRelease(string nodeTypePath, string? knownRelease)
    {
        var workspace = Mesh.GetWorkspace();
        var node = workspace.GetMeshNodeStream(nodeTypePath)
            .Should().Within(50.Seconds())
            .Match(n => n?.Content is NodeTypeDefinition def
                && def.CompilationStatus == CompilationStatus.Ok
                && !string.IsNullOrEmpty(def.LatestReleasePath)
                && def.LatestReleasePath != knownRelease);
        return ((NodeTypeDefinition)node.Content!).LatestReleasePath!;
    }

    private CreateReleaseResponse SendCreateRelease(string nodeTypePath, bool force)
    {
        var reader = GetClient(c => c.AddData());
        var response = reader
            .Observe(new CreateReleaseRequest(Force: force), o => o.WithTarget(new Address(nodeTypePath)))
            .Select(d => d.Message)
            .Should().Within(TimeSpan.FromSeconds(30)).Emit();
        // Wait for compile to complete (status = Ok or Error) before returning.
        // Live remote stream (GetMeshNodeStream(path)) â€” NOT Query, which
        // is index-lagged and can miss the post-compile tick (per the CQRS
        // feedback note + Doc/Architecture/CqrsAndContentAccess.md). Path is
        // known here, so the live stream is the right primitive.
        if (!response.AlreadyUpToDate && response.Success)
        {
            reader.GetWorkspace().GetMeshNodeStream(nodeTypePath)
                .Should().Within(TimeSpan.FromSeconds(45))
                .Match(n => n?.Content is NodeTypeDefinition def
                    && (def.CompilationStatus == CompilationStatus.Ok
                        || def.CompilationStatus == CompilationStatus.Error));
        }
        return response;
    }

    private string ReadOverview(string path)
        => ReadOverviewMatching(path, _ => true);

    /// <summary>
    /// Reads the Overview area and waits for an <see cref="HtmlControl"/> whose
    /// data matches <paramref name="matches"/>. Used by V2 reads where the per-node
    /// hub may emit a stale (V1) snapshot first while the new release's
    /// HubConfiguration is still propagating â€” taking <c>FirstAsync(x is HtmlControl)</c>
    /// would race the first stale emission and fail the assertion before the
    /// V2-bound re-render lands.
    /// </summary>
    private string ReadOverviewMatching(string path, Func<string, bool> matches)
    {
        var client = GetClient(c => c.AddData());
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference("Overview");
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(path), reference);

        var control = stream
            .GetControlStream(reference.Area!)
            .Should().Within(30.Seconds())
            .Match(x => x is HtmlControl h && matches(h.Data?.ToString() ?? string.Empty));

        return (control as HtmlControl)?.Data?.ToString() ?? string.Empty;
    }

    /// <summary>
    /// Waits for a fresh <c>Release</c> MeshNode whose path differs from any in
    /// <paramref name="knownReleases"/>. Reads <see cref="NodeTypeDefinition.LatestReleasePath"/>
    /// off the live <see cref="GetMeshNodeStream"/> â€” atomic with the post-compile
    /// status flip, so by the time CompilationStatus settles to Ok the new path
    /// is already on the NodeType. Avoids the lagged <c>Query</c> namespace
    /// scan over <c>Release/*</c>.
    /// </summary>
    private string WaitForNewRelease(string nodeTypePath, HashSet<string> knownReleases)
    {
        var reader = GetClient(c => c.AddData());
        var node = reader.GetWorkspace().GetMeshNodeStream(nodeTypePath)
            .Should().Within(TimeSpan.FromSeconds(50))
            .Match(n => n?.Content is NodeTypeDefinition def
                && !string.IsNullOrEmpty(def.LatestReleasePath)
                && !knownReleases.Contains(def.LatestReleasePath!));
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
    private void WaitForMeshHubView(string path, Func<MeshNode?, bool> predicate)
    {
        Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Should().Within(TimeSpan.FromSeconds(30)).Match(predicate);
    }

    /// <summary>
    /// Waits for <see cref="NodeTypeDefinition.IsDirty"/> on the NodeType at
    /// <paramref name="nodeTypePath"/> to reach <paramref name="expected"/>.
    /// The flag is written by the per-NodeType hub's sources watcher every
    /// time the synced query over Sources+Tests emits — observing it
    /// confirms the watcher has seen the post-update state and the next
    /// compile will read the fresh source set.
    /// </summary>
    private void WaitForIsDirty(string nodeTypePath, bool expected) =>
        WaitForMeshHubView(nodeTypePath,
            n => n?.Content is NodeTypeDefinition d && d.IsDirty == expected);
}
