using MeshWeaver.Blazor.Portal;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Acme.Test;

/// <summary>
/// End-to-end tests for the NodeType compile lifecycle as exercised through the
/// per-NodeType hub's "Progress" layout area and the underlying CompilationStatus
/// transitions on the NodeType MeshNode.
///
/// <para>Scenarios covered:</para>
/// <list type="number">
///   <item><b>Cold cycle.</b> Pre-condition: the IAssemblyStore has NO cached DLL
///     for <c>ACME/Project/Todo</c> (the per-test-class GUID-tagged store ensures
///     this regardless of what other tests in the same testhost did). Subscribing
///     to the NodeType hub triggers <c>InstallCompileWatcher</c>'s kickoff →
///     Pending → Compiling → Ok. The Progress area emits a non-null UiControl on
///     each transition; the NodeType MeshNode stream surfaces the same Status
///     transitions; the IAssemblyStore ends with the freshly-emitted v{N}.dll on
///     disk.</item>
///
///   <item><b>Warm cycle (cached).</b> After the cold cycle succeeded, a fresh
///     subscription must NOT see another Pending/Compiling transition: the
///     kickoff's <c>HasUsableBuild</c> check (assembly fields populated +
///     <c>CompiledFrameworkVersion</c> matches the live FrameworkVersion) returns
///     true and the kickoff returns early. The Progress area's first emission is
///     the terminal Ok state; the NodeType MeshNode stream's first emission is
///     <c>Status == Ok</c>; the IAssemblyStore is unchanged (no new v{N+1}.dll
///     written, the existing file's bytes unchanged).</item>
/// </list>
///
/// <para>Isolation. <see cref="MonolithMeshTestBase"/> registers a per-instance
/// <see cref="FileSystemAssemblyStore"/> directory (commit 526fccb01) so two test
/// classes both touching <c>ACME/Project/Todo</c> don't serve each other's
/// compiled bytes. The per-test data dir (a fresh copy of <c>samples/Graph</c>)
/// ensures no <c>compilationStatus=Ok</c> is preseeded into <c>Todo.json</c> by
/// a prior run.</para>
/// </summary>
public class NodeTypeProgressAreaTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string NodeTypePath = "ACME/Project/Todo";

    // Stable cache directory so compiled NodeType DLLs survive across runs.
    // The timestamped-subdir cache (a3ab9909e) prevents file-lock collisions
    // since each compile writes to its own {nodeName}_{ticks_hex}/ subdir.
    private static readonly string SharedCacheDirectory = Path.Combine(
        Path.GetTempPath(),
        "MeshWeaverNodeTypeProgressTests",
        ".mesh-cache");

    // Per-test-instance IAssemblyStore root. Must be a fresh GUID-tagged path per
    // test instance — without this, AddPartitionedFileSystemPersistence falls back
    // to the production default (MeshWeaver-AssemblyStore-pid{PID}) which is shared
    // across every test in the testhost process, so other tests' compile artifacts
    // leak in and break the cold-cycle "no v{N}.dll" precondition.
    //
    // Why we don't chain ConfigureMeshBase: the base configures AddInMemoryPersistence
    // which registers the CoreAndWrapperServicesMarker sentinel, and our subsequent
    // AddPartitionedFileSystemPersistence then bails out of its provider-registration
    // pass on the second hit of that sentinel — leaving the ACME data files
    // unreachable. We register the per-test IAssemblyStore manually instead.
    private readonly string _assemblyStoreRoot = Path.Combine(
        Path.GetTempPath(),
        $"MeshWeaverNodeTypeProgressTests-store-{Environment.ProcessId}-{Guid.NewGuid():N}");

    // Local copy of samples/Graph — keeps Cycle 1's "no prior compile" precondition
    // sound when other tests in the testhost mutate the shared SamplesGraph copy
    // via FileSystem persistence write-backs.
    private string? _localTestDataPath;

    private string GetLocalTestDataPath()
    {
        if (_localTestDataPath != null)
            return _localTestDataPath;

        _localTestDataPath = Path.Combine(
            Path.GetTempPath(),
            "MeshWeaverTests",
            $"NodeTypeProgress_{Guid.NewGuid():N}");
        CopyDirectory(TestPaths.SamplesGraph, _localTestDataPath);
        // After copy: strip compile state from the NodeType JSON files we'll exercise.
        // The test bin/Debug copy gets persisted Status=Ok + compiledFrameworkVersion
        // written back by prior test runs (FileSystem persistence is a real write-back
        // store). If we don't strip, the cold cycle starts with HasUsableBuild=true:
        // kickoff skips, no Pending/Compiling emission, ColdCycle's transition assertion
        // fails. The strip is targeted at the NodeType paths this test class touches —
        // peer tests in the suite stay unaffected.
        SanitizeNodeTypeDefinition(Path.Combine(_localTestDataPath, "Data", "ACME", "Project", "Todo.json"));
        SanitizeNodeTypeDefinition(Path.Combine(_localTestDataPath, "Data", "ACME", "Project.json"));
        return _localTestDataPath;
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(sourceDir))
            CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
    }

    /// <summary>
    /// Remove every persisted compile-lifecycle field from a NodeType MeshNode JSON
    /// file so the local copy starts in a never-compiled state. Mirrors the set the
    /// compile-activity write-back stamps (see <c>NodeTypeCompileActivityHandler</c>
    /// + <c>RunCompile</c> in <c>NodeTypeCompilationHelpers</c>). Idempotent — the
    /// file is rewritten only if at least one of the target keys was present.
    /// </summary>
    private static void SanitizeNodeTypeDefinition(string jsonPath)
    {
        if (!File.Exists(jsonPath)) return;
        var raw = File.ReadAllText(jsonPath);
        // AllowTrailingCommas: a previous compile-write-back may leave a
        // trailing comma in the JSON if the writer is lenient (rare but
        // observed once on CI as 'trailing comma at end LineNumber: 17').
        // Be tolerant on read; the rewrite always emits strict JSON.
        var parseOptions = new System.Text.Json.Nodes.JsonNodeOptions();
        var docOptions = new System.Text.Json.JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = System.Text.Json.JsonCommentHandling.Skip
        };
        var root = System.Text.Json.Nodes.JsonNode.Parse(raw, parseOptions, docOptions)?.AsObject();
        if (root?["content"] is not System.Text.Json.Nodes.JsonObject content) return;
        var keysToStrip = new[]
        {
            "compilationStatus", "compilationError", "lastCompileStartedAt",
            "lastCompileSucceededAt", "lastCompiledVersion", "lastCompilationActivityPath",
            "latestReleasePath", "latestAssemblyCollection", "latestAssemblyPath",
            "compiledSources", "compiledFrameworkVersion", "requestedReleasePath",
            "releaseNotes"
        };
        var stripped = false;
        foreach (var key in keysToStrip)
            stripped |= content.Remove(key);
        if (!stripped) return;
        // Also reset the top-level version + lastModified — they were bumped by
        // each compile write-back and don't reflect a never-compiled state.
        root["version"] = 1;
        root.Remove("lastModified");
        File.WriteAllText(jsonPath, root.ToJsonString(
            new System.Text.Json.JsonSerializerOptions { WriteIndented = false }));
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var localCopy = GetLocalTestDataPath();
        var dataDirectory = Path.Combine(localCopy, "Data");
        Directory.CreateDirectory(SharedCacheDirectory);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Graph:Storage:SourceType"] = "FileSystem",
                ["Graph:Storage:BasePath"] = localCopy
            })
            .Build();

        return builder
            .UseMonolithMesh()
            // Register the per-test FileSystemAssemblyStore BEFORE persistence so
            // AddPartitionedFileSystemPersistence's RegisterDefaultAssemblyStore
            // (TryAddSingleton, pid-only path → leaks across the testhost) doesn't
            // win. Without this we'd inherit other tests' compile output and the
            // cold-cycle "no v{N}.dll" precondition would fail spuriously.
            .ConfigureServices(s => s.AddFileSystemAssemblyStore(_assemblyStoreRoot))
            .AddPartitionedFileSystemPersistence(dataDirectory)
            .AddAcme()
            .AddSpaceType()
            .ConfigureServices(services =>
            {
                services.Configure<CompilationCacheOptions>(o =>
                {
                    o.CacheDirectory = SharedCacheDirectory;
                    o.EnableDiskCache = true;
                });
                services.AddSingleton<IConfiguration>(configuration);
                return services;
            })
            .AddGraph();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_localTestDataPath != null && Directory.Exists(_localTestDataPath))
        {
            try { Directory.Delete(_localTestDataPath, recursive: true); }
            catch { /* ignore cleanup races */ }
        }
    }

    /// <summary>
    /// Client hub for these tests subscribes to layout areas + opens remote
    /// MeshNode streams against the per-NodeType hub — both require AddData on
    /// the client. AddLayoutClient calls AddData internally.
    /// </summary>
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    /// <summary>
    /// Cold cycle: starting from a clean assembly store, subscribing to the
    /// NodeType hub triggers a compile, the NodeType MeshNode stream surfaces
    /// the Pending → Compiling → Ok transitions, the Progress layout area emits
    /// at least one non-null UiControl, and a v{N}.dll lands in the
    /// IAssemblyStore on disk.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ColdCycle_TriggersCompile_StreamsTransitions_LandsAssemblyOnDisk()
    {
        var ct = TestContext.Current.CancellationToken;
        var assemblyStore = Mesh.ServiceProvider.GetRequiredService<IAssemblyStore>();
        var nodeTypeAddress = new Address(NodeTypePath);
        // We open subscriptions via the CLIENT workspace — the mesh hub itself
        // does not have AddData configured (it's a router/registry, not a data
        // store), so Mesh.GetWorkspace() throws "AddData was not called". Every
        // GetClient() returns a hub with AddLayoutClient/AddData configured.
        var client = GetClient();
        var clientWorkspace = client.GetWorkspace();

        // Pre-condition assertion: this test-class instance has its OWN
        // IAssemblyStore directory (per-instance GUID, registered before
        // AddPartitionedFileSystemPersistence), so no prior compile output for
        // this NodeType can be present. Probe versions 1..10 to catch any leftover.
        for (long v = 1; v <= 10; v++)
        {
            var leftover = await assemblyStore.TryGetAssemblyPath(NodeTypePath, v)
                .FirstAsync().ToTask(ct);
            leftover.Should().BeNull(
                $"per-test IAssemblyStore must not contain a v{v} for {NodeTypePath} at test start");
        }

        // Capture every NodeTypeDefinition emission BEFORE the kickoff can fire.
        // Subscribing to clientWorkspace.GetMeshNodeStream(NodeTypePath) opens a
        // remote SubscribeRequest to the per-NodeType hub — that activation runs
        // the compile-watcher kickoff (Pending → Compiling → Ok). Capturing from
        // the very first emission is what lets us assert "we saw a compiling state."
        var transitions = new ConcurrentQueue<CompilationStatus?>();
        var rawNodes = new ConcurrentQueue<MeshNode>();
        using var typeSub = clientWorkspace.GetMeshNodeStream(NodeTypePath)
            .Where(n => n is not null)
            .Subscribe(n =>
            {
                rawNodes.Enqueue(n!);
                if (n!.Content is NodeTypeDefinition def)
                    transitions.Enqueue(def.CompilationStatus);
            });

        // Open the Progress layout area against the same NodeType hub. The
        // subscription drives the hub's layout-area machinery — if the Stack
        // emission renders, GetControlStream's onNext fires. We just collect
        // non-null UiControls; structural-content assertions live inline below.
        var progressRef = new LayoutAreaReference(NodeTypeLayoutAreas.ProgressArea);
        var progressEmissions = new ConcurrentQueue<UiControl?>();
        using var progressSub = clientWorkspace
            .GetRemoteStream<JsonElement, LayoutAreaReference>(nodeTypeAddress, progressRef)
            .GetControlStream(progressRef.Area!)
            .Subscribe(c => progressEmissions.Enqueue(c));

        Output.WriteLine($"Subscribed to typeStream + Progress area for {NodeTypePath}");

        // Wait for terminal Ok state on the NodeType MeshNode stream — this is
        // the canonical "compile finished" signal. The Progress area's last
        // emission tracks the same state (it subscribes to the same stream
        // internally), so by the time this completes the area has emitted at
        // least the terminal control.
        var terminal = await clientWorkspace.GetMeshNodeStream(NodeTypePath)
            .Where(n => n?.Content is NodeTypeDefinition d
                        && d.CompilationStatus == CompilationStatus.Ok
                        && !string.IsNullOrEmpty(d.LatestAssemblyCollection)
                        && !string.IsNullOrEmpty(d.LatestAssemblyPath))
            .Take(1)
            .Timeout(30.Seconds())
            .FirstAsync()
            .ToTask(ct);

        var terminalDef = (NodeTypeDefinition)terminal!.Content!;
        Output.WriteLine($"Cold cycle settled: Status={terminalDef.CompilationStatus} " +
                         $"Collection={terminalDef.LatestAssemblyCollection} " +
                         $"Path={terminalDef.LatestAssemblyPath} " +
                         $"Version={terminalDef.LastCompiledVersion} " +
                         $"Framework={terminalDef.CompiledFrameworkVersion}");

        // Transition assertion — we must have observed at least one in-flight
        // state (Pending or Compiling) before terminating at Ok. Without this,
        // the test could "pass" even if the kickoff silently never ran (which
        // would mean the cold-start contract is broken — instances would never
        // get their HubConfiguration). Snapshot the queue so a late emission
        // landing between Wait and assert doesn't poison the test.
        var observedStates = transitions.ToArray();
        Output.WriteLine($"NodeType CompilationStatus transitions ({observedStates.Length}): "
                         + string.Join(" → ", observedStates.Select(s => s?.ToString() ?? "null")));
        observedStates.Should().Contain(
            s => s == CompilationStatus.Pending || s == CompilationStatus.Compiling,
            "cold cycle must transition through Pending and/or Compiling before Ok " +
            "— if the only state ever observed is Ok, the kickoff didn't actually run and " +
            "the system silently relied on a preseeded assembly that shouldn't be there");
        observedStates.Should().Contain(s => s == CompilationStatus.Ok,
            "cold cycle must terminate at Ok");

        // The Progress layout area must have emitted at least one non-null
        // control: that means the per-NodeType hub was activated, the layout
        // machinery ran, and the area's reactive subscription is alive. We
        // don't assert on the exact markdown text — the structure is nested
        // (Stack → NamedAreaControl refs into the EntityStore) and string
        // sniffing would couple this test to the area's rendering details.
        // The "the area is wired up and reacting to typeStream" signal is
        // sufficient; the rendering itself is unit-tested elsewhere via the
        // helper that builds the Stack from a NodeTypeDefinition.
        progressEmissions.Should().NotBeEmpty(
            "the Progress layout area must emit at least one UiControl " +
            "across the compile lifecycle");
        progressEmissions.Should().Contain(c => c != null,
            "at least one emission must be non-null — the Stack with status header");

        // Disk assertion — the compile produced a v{LastCompiledVersion}.dll
        // and put it through the IAssemblyStore. TryGetAssemblyPath probes the
        // exact same path FileSystemAssemblyStore.Put wrote to.
        var version = terminalDef.LastCompiledVersion ?? 0;
        version.Should().BeGreaterThan(0, "compile must stamp LastCompiledVersion");
        var dllPath = await assemblyStore.TryGetAssemblyPath(NodeTypePath, version)
            .FirstAsync().ToTask(ct);
        dllPath.Should().NotBeNullOrEmpty(
            $"v{version}.dll must exist in the IAssemblyStore after a successful compile");
        File.Exists(dllPath).Should().BeTrue(
            $"the returned path '{dllPath}' must actually exist on disk");
    }

    /// <summary>
    /// Warm cycle: after a successful compile, a fresh subscription on a new
    /// client must NOT trigger another Pending/Compiling transition. The
    /// kickoff's <see cref="NodeTypeCompilationHelpers.HasUsableBuild"/> check
    /// (assembly fields populated + CompiledFrameworkVersion matches) returns
    /// true and the kickoff returns early — the NodeType's first observable
    /// emission to the new subscriber is the terminal Ok state.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task WarmCycle_AfterCompile_NoRecompile_FirstEmissionIsCachedOk()
    {
        var ct = TestContext.Current.CancellationToken;
        var assemblyStore = Mesh.ServiceProvider.GetRequiredService<IAssemblyStore>();
        var nodeTypeAddress = new Address(NodeTypePath);
        // Use client workspace — Mesh hub does not configure AddData; see
        // ColdCycle test for the rationale.
        var driverClient = GetClient();

        // === Phase 1 — drive a compile so the warm precondition holds ===
        // We don't bother capturing emissions here; the cold-cycle test
        // already covers that. Just wait for the terminal Ok and capture
        // the compiled version + dll path for the no-recompile assertion.
        Output.WriteLine("Warm cycle: triggering initial compile…");
        var firstCompile = await driverClient.GetWorkspace().GetMeshNodeStream(NodeTypePath)
            .Where(n => n?.Content is NodeTypeDefinition d
                        && d.CompilationStatus == CompilationStatus.Ok
                        && !string.IsNullOrEmpty(d.LatestAssemblyPath))
            .Take(1)
            .Timeout(30.Seconds())
            .FirstAsync()
            .ToTask(ct);

        var firstDef = (NodeTypeDefinition)firstCompile!.Content!;
        var firstVersion = firstDef.LastCompiledVersion ?? 0;
        var firstDllPath = await assemblyStore.TryGetAssemblyPath(NodeTypePath, firstVersion)
            .FirstAsync().ToTask(ct);
        var firstDllMtime = File.GetLastWriteTimeUtc(firstDllPath!);
        Output.WriteLine($"Initial compile: version={firstVersion} path={firstDllPath} mtime={firstDllMtime:O}");

        // === Phase 2 — warm subscription, expect NO new compile ===
        // Create a fresh client (separate from the one whose subscription
        // drove Phase 1) and capture transitions from its first emission.
        // The mesh-node cache returns the current Ok state immediately;
        // the kickoff sees HasUsableBuild=true (assembly fields populated +
        // CompiledFrameworkVersion matches) and bails before flipping Pending.
        Output.WriteLine("Warm cycle: opening fresh subscription on cached NodeType…");
        var warmClient = GetClient();
        var warmWorkspace = warmClient.GetWorkspace();
        var warmTransitions = new ConcurrentQueue<CompilationStatus?>();
        var warmFirstEmissionTcs = new TaskCompletionSource<CompilationStatus?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var warmSub = warmWorkspace.GetMeshNodeStream(NodeTypePath)
            .Where(n => n?.Content is NodeTypeDefinition)
            .Subscribe(n =>
            {
                var status = (n!.Content as NodeTypeDefinition)?.CompilationStatus;
                warmTransitions.Enqueue(status);
                warmFirstEmissionTcs.TrySetResult(status);
            });

        // Open the Progress area on the same fresh client — same as a user
        // navigating to a page whose layout area lives behind a NodeType that's
        // already compiled in this process.
        var progressRef = new LayoutAreaReference(NodeTypeLayoutAreas.ProgressArea);
        using var warmProgressSub = warmWorkspace
            .GetRemoteStream<JsonElement, LayoutAreaReference>(nodeTypeAddress, progressRef)
            .GetControlStream(progressRef.Area!)
            .Where(c => c != null)
            .Take(1)
            .Timeout(5.Seconds())
            .FirstAsync()
            .ToTask(ct);

        await warmProgressSub;
        var warmFirstStatus = await warmFirstEmissionTcs.Task;

        Output.WriteLine($"Warm cycle: first observed status = {warmFirstStatus}");

        // The PRIMARY warm-cycle assertion. If the first emission is Ok, the
        // kickoff didn't bounce us through Pending → Compiling — it correctly
        // recognised the cached assembly. Anything else (Pending, Compiling,
        // null, Error) means HasUsableBuild misfired or the persisted state
        // didn't survive between subscriptions.
        warmFirstStatus.Should().Be(CompilationStatus.Ok,
            "warm-cycle subscription must observe Status=Ok immediately; " +
            "any Pending/Compiling emission means the kickoff failed to detect the cached assembly");

        // Give the system a moment to see if a stray compile fires anyway.
        // The kickoff's Pending flip is synchronous on first emission; if it
        // didn't happen by now, it won't. 250 ms is plenty for any reactive
        // chain in the workspace to settle.
        await Task.Delay(250, ct);
        var warmSnapshot = warmTransitions.ToArray();
        warmSnapshot.Should().NotContain(s => s == CompilationStatus.Pending,
            "no Pending transition is allowed during the warm cycle — " +
            $"observed transitions: [{string.Join(", ", warmSnapshot.Select(s => s?.ToString() ?? "null"))}]");
        warmSnapshot.Should().NotContain(s => s == CompilationStatus.Compiling,
            "no Compiling transition is allowed during the warm cycle either");

        // Disk-level recompile assertion: the v{firstVersion}.dll must still
        // be the exact same file, byte-for-byte unchanged. A second compile
        // would either rewrite this path (FileSystemAssemblyStore.Put
        // short-circuits but the mtime would still update through any
        // attempted write) or bump the version (mint a v{N+1}.dll).
        var stillFirstDllPath = await assemblyStore.TryGetAssemblyPath(NodeTypePath, firstVersion)
            .FirstAsync().ToTask(ct);
        stillFirstDllPath.Should().Be(firstDllPath,
            "the warm cycle must not have moved the cached assembly path");
        File.GetLastWriteTimeUtc(stillFirstDllPath!).Should().Be(firstDllMtime,
            "the warm cycle must not have rewritten v{Version}.dll".Replace("{Version}", firstVersion.ToString()));

        // And no v{N+1}.dll should have appeared.
        var nextVersionPath = await assemblyStore.TryGetAssemblyPath(NodeTypePath, firstVersion + 1)
            .FirstAsync().ToTask(ct);
        nextVersionPath.Should().BeNull(
            $"no v{firstVersion + 1}.dll should exist — the warm cycle must not have minted a new release");
    }
}
