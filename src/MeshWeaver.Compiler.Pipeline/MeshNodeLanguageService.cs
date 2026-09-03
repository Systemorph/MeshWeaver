using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Services.LanguageServer;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.QuickInfo;
using Microsoft.CodeAnalysis.Tags;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RoslynDiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;
using LspDiagnosticSeverity = MeshWeaver.Mesh.Services.LanguageServer.DiagnosticSeverity;

using MeshWeaver.Compiler;
namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// In-process Roslyn language services over a NodeType's live <c>CSharpCompilation</c>.
/// Wraps Roslyn's <see cref="CompletionService"/> / <see cref="QuickInfoService"/> in an
/// <see cref="AdhocWorkspace"/> per NodeType — cached, keyed by source-versions hash.
/// <para>
/// Stage 1 of LSP integration. Consumed by the <c>lsp_*_for_node</c> MCP tools so the
/// code-authoring agents can hover / complete / diagnose without a full <c>Compile</c> round-trip.
/// </para>
/// </summary>
internal sealed class MeshNodeLanguageService : IMeshLanguageService
{
    private readonly MeshNodeCompilationService compilationService;
    private readonly SpeculativeCompilation speculativeCompilation;
    private readonly IMessageHub hub;
    private readonly ILogger<MeshNodeLanguageService> logger;

    // Path used as the SyntaxTree.FilePath for the generated skeleton tree.
    // Distinct from any user MeshNode path so callers can never address it accidentally.
    // Aliases the toolchain's sentinel — the failure-diagnostics filter keys on the SAME value.
    private const string SkeletonDocumentPath = MeshWeaver.Compiler.CompileDiagnostics.SkeletonDiagnosticsPath;
    private const string GlobalUsingsDocumentPath = MeshWeaver.Compiler.SpeculativeCompilation.GlobalUsingsDocumentPath;

    // Per-NodeType cached workspace, invalidated when source versions change.
    // Concurrent because hub-message handlers may invoke language-service queries
    // for the same NodeType from multiple threads.
    private readonly ConcurrentDictionary<string, CachedWorkspace> _cache =
        new(StringComparer.Ordinal);

    // The ONE script workspace — for Code nodes with no NodeType compilation behind them
    // (course lesson cells). Its reference set is process-shared and fixed for the service's
    // lifetime, so a single lazily-built project serves every request: each suggest forks the
    // immutable solution with the in-flight text, nothing is applied back, and concurrent
    // requests never contend. Bounded: one AdhocWorkspace total, not one per cell.
    //
    // 🚨 Task, not a bare value (issues #2480/#2578). Building it touches
    // MeshScriptEnvironment.ReferencesAsync, whose FIRST-ever call in the process can be
    // genuinely slow (materializing ~350 assemblies' metadata) — it used to run synchronously,
    // inline, inside this leaf's Compile-pool gate, with no cancellation participation at all,
    // so a silo/mesh teardown racing the first caller could not reclaim that gate permit within
    // the drain budget. Every caller now races its OWN `ct` against this shared Task via
    // Task.WaitAsync (see GetScriptCompletionsAsync/GetScriptDiagnosticsAsync) — the FIRST
    // caller's own token never cancels the build (it is shared by every request against this
    // mesh instance), it only governs how long THAT caller waits.
    private readonly Lazy<Task<ScriptWorkspace>> _scriptWorkspace;

    // Path used as the script document's FilePath — never a real MeshNode path.
    private const string ScriptDocumentPath = "__script__.csx";

    // The likely-usage prior (how often each identifier occurs in the Code nodes this mesh
    // already runs). Built lazily in the BACKGROUND and never awaited on a request — see
    // CompletionUsageIndex; until it is ready, ranking simply proceeds without it.
    private readonly CompletionUsageIndex usageIndex;

    // This user's acceptance history — the per-user half of "likely usage" (VS Code's suggest
    // memory). Used ONLY to preselect; it never reorders what the matcher decided.
    private readonly CompletionMemoryStore memoryStore;

    // Compile pool: Roslyn LSP work is CPU-bound, so it routes through the bounded
    // Compile pool which caps concurrency so it can't starve other schedulers.
    // A bare Observable.FromAsync deadlocks under a blocking subscriber (SubscribeOn
    // only moves the subscribe, not the await continuation); the pool runs the leaf
    // with ConfigureAwait(false) behind a gate. Falls back to the unbounded pool
    // when no registry is wired (e.g. tests constructing the service outside DI).
    private readonly IIoPool _ioPool;

    public MeshNodeLanguageService(
        MeshNodeCompilationService compilationService,
        SpeculativeCompilation speculativeCompilation,
        IMessageHub hub,
        ILogger<MeshNodeLanguageService> logger)
    {
        this.compilationService = compilationService;
        this.speculativeCompilation = speculativeCompilation;
        this.hub = hub;
        this.logger = logger;
        _scriptWorkspace = new Lazy<Task<ScriptWorkspace>>(
            () => BuildScriptWorkspaceAsync(hub.ServiceProvider),
            LazyThreadSafetyMode.ExecutionAndPublication);
        usageIndex = new CompletionUsageIndex(hub, logger);
        memoryStore = new CompletionMemoryStore(hub, logger);
        _ioPool = hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Compile)
            ?? IoPool.Unbounded;

        // 🚨 The two helpers own subscriptions that outlive the request that started them — the
        // usage index's mesh-wide corpus scan (its own 30 s Timeout, a 1 s Throttle) and the memory
        // store's debounced save. Unowned, the scan ran into mesh teardown, where its Throttle timer
        // met the pool drain and deadlocked the drain's cancel (IoPoolDrainCancelJoinTest —
        // `pools=[Query=1]` on MeshNodeLanguageServiceTest, 2026-08-28 → 09-03). This service is a
        // mesh singleton with no IDisposable of its own, and the service provider disposes AFTER the
        // drain anyway — so the owner lifetime is the hub's: RegisterForDisposal runs in DisposeImpl,
        // strictly before DisposalCompleted and therefore before IoPoolRegistry.DrainAll(). A hub
        // already disposing disposes the registrant immediately.
        hub.RegisterForDisposal(usageIndex);
        hub.RegisterForDisposal(memoryStore);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 🚨 The <see cref="NodeDiagnosticsOutcome"/> exists because this method used to map "no
    /// workspace" to <c>Array.Empty&lt;DiagnosticInfo&gt;()</c> — byte-identical to "compiled
    /// clean" — so an invented path answered <c>{"ok":true,"diagnostics":[]}</c> and the pre-prod
    /// sweep AGENTS.md mandates ran on no evidence (#1592). The distinction was never missing from
    /// the framework: the read underneath is <see cref="MeshNodeStreamExtensions.GetMeshNodeOutcome"/>,
    /// which already separates Present / Absent / Unavailable. It was being discarded here.
    /// </remarks>
    public IObservable<NodeDiagnosticsOutcome> GetDiagnostics(string nodeTypePath)
        => ResolveNodeOutcome(nodeTypePath)
            .SelectMany(outcome => outcome.Status switch
            {
                NodeReadStatus.Present => WorkspaceFor(nodeTypePath, outcome.Node!)
                    .SelectMany(cached => cached is null
                        // The node resolved but no compilation inputs could be assembled — which
                        // GetCompilationInputsAsync does for exactly one shape, a node whose
                        // NodeType is unset. 🚨 NOT the case for a node with some other NodeType:
                        // a Markdown node assembles an EMPTY compilation and reports Compiled with
                        // no diagnostics, which is outside #1592's failure (the mandated sweep
                        // enumerates nodeType:NodeType) but is not what this branch means.
                        ? Observable.Return(NodeDiagnosticsOutcome.NotCompilable)
                        : _ioPool.Run(ct => GetDiagnosticsAsync(cached, ct))
                            .Select(NodeDiagnosticsOutcome.Compiled)),

                // Genuinely not there, or its delete is in flight — either way nothing was checked.
                NodeReadStatus.Absent or NodeReadStatus.DeleteInProgress =>
                    Observable.Return(NodeDiagnosticsOutcome.Absent),

                // 🚨 The case that makes this a gate rather than a nicety: a per-node hub that does
                // not answer inside the budget. That is precisely when a sweep most needs to fail,
                // and precisely when the old shape was most confidently green.
                _ => Observable.Return(NodeDiagnosticsOutcome.Unavailable(outcome.Failure)),
            });

    public IObservable<HoverInfo?> GetHover(string nodeTypePath, string sourcePath, SourcePosition position)
        => GetOrBuildWorkspace(nodeTypePath)
            .SelectMany(cached =>
            {
                if (cached is null || !cached.DocumentsByPath.TryGetValue(sourcePath, out var docId))
                    return Observable.Return<HoverInfo?>(null);
                return _ioPool.Run(ct => GetHoverAsync(cached, docId, position, ct));
            });

    public IObservable<IReadOnlyList<CompletionEntry>> GetCompletions(
        string nodeTypePath, string sourcePath, SourcePosition position, int maxResults = 20)
        => GetOrBuildWorkspace(nodeTypePath)
            .SelectMany(cached =>
            {
                if (cached is null || !cached.DocumentsByPath.TryGetValue(sourcePath, out var docId))
                    return Observable.Return<IReadOnlyList<CompletionEntry>>(Array.Empty<CompletionEntry>());
                return _ioPool.Run(ct => GetCompletionsAsync(cached, docId, position, maxResults, usageIndex, CurrentMemory(), ct));
            });

    /// <inheritdoc />
    public IObservable<IReadOnlyList<CompletionEntry>> GetCompletions(
        string nodeTypePath, string sourcePath, string proposedCode, SourcePosition position, int maxResults = 20)
        // Kick the likely-usage prior's background refresh (never awaited — a cold or wedged
        // index just means this request ranks without the corpus signal).
        => Observable.Defer(() => { usageIndex.EnsureFresh(); return ResolveNode(nodeTypePath); })
            .SelectMany(node => Environment(node, nodeTypePath) switch
            {
                CompletionEnvironment.NodeType => GetOrBuildWorkspace(nodeTypePath)
                    .SelectMany(cached => cached is null
                        ? Observable.Return<IReadOnlyList<CompletionEntry>>(Array.Empty<CompletionEntry>())
                        : _ioPool.Run(ct => GetOverlayCompletionsAsync(
                            cached, sourcePath, proposedCode, position, maxResults, usageIndex,
                            CurrentMemory(), ct))),
                // A non-NodeType owner that EXISTS → a standalone SCRIPT Code node (e.g. a course
                // lesson cell): complete in the kernel's script environment.
                CompletionEnvironment.Script =>
                    _ioPool.Run(ct => GetScriptCompletionsAsync(proposedCode, position, maxResults, ct)),
                // Owner unresolvable → we do not know which language environment applies, and
                // guessing "script" would suggest globals (Mesh/Log/Ct) that do not exist in a
                // NodeType source. No suggestions beats wrong ones.
                _ => Observable.Return<IReadOnlyList<CompletionEntry>>(Array.Empty<CompletionEntry>()),
            });

    public IObservable<IReadOnlyList<DiagnosticInfo>> CheckSpeculative(
        string nodeTypePath, string sourcePath, string proposedCode)
        => ResolveNode(nodeTypePath)
            .SelectMany(node => Environment(node, nodeTypePath) switch
            {
                CompletionEnvironment.NodeType => compilationService.GetCompilationInputsAsync(node!)
                    .SelectMany(inputs => inputs is null
                        ? Observable.Return<IReadOnlyList<DiagnosticInfo>>(Array.Empty<DiagnosticInfo>())
                        : _ioPool.Run(ct =>
                            speculativeCompilation.GetDiagnosticsAsync(inputs, sourcePath, proposedCode, ct))),
                // A non-NodeType owner that EXISTS → a standalone script Code node: diagnose in
                // the script environment so lesson cells get live squiggles too (this used to
                // fall through to a compilation that parses a cell as REGULAR C#, reporting the
                // spurious "top-level statements must be in an executable" on every cell).
                CompletionEnvironment.Script => _ioPool.Run(ct => GetScriptDiagnosticsAsync(proposedCode, ct)),
                // Owner unresolvable → no honest environment to diagnose in; stay silent rather
                // than paint squiggles computed under the wrong language rules.
                _ => Observable.Return<IReadOnlyList<DiagnosticInfo>>(Array.Empty<DiagnosticInfo>()),
            });

    /// <summary>Which language environment a Code node's text belongs to.</summary>
    private enum CompletionEnvironment
    {
        /// <summary>The owner could not be read — the environment is genuinely unknown.</summary>
        Unknown,
        /// <summary>The owner is a NodeType: its sources compile as a library.</summary>
        NodeType,
        /// <summary>The owner exists and is not a NodeType: the kernel runs this text as a script.</summary>
        Script,
    }

    /// <summary>
    /// Classifies the owner, and it must key on what the node IS: a plain Code node still
    /// produces compilation inputs, so "inputs resolved" never distinguished the two
    /// environments. An owner that cannot be READ is <see cref="CompletionEnvironment.Unknown"/>
    /// rather than script — inferring an environment from a failed read would offer script
    /// globals inside a NodeType source. The one exception is an owner we have already compiled:
    /// a cached workspace proves it is a NodeType, so a transient read failure does not
    /// interrupt completions in the file the user is actually editing.
    /// </summary>
    private CompletionEnvironment Environment(MeshNode? node, string nodeTypePath)
    {
        if (node is not null)
            return string.Equals(node.NodeType, MeshNode.NodeTypePath, StringComparison.Ordinal)
                ? CompletionEnvironment.NodeType
                : CompletionEnvironment.Script;
        return _cache.ContainsKey(nodeTypePath)
            ? CompletionEnvironment.NodeType
            : CompletionEnvironment.Unknown;
    }

    /// <inheritdoc />
    public void RecordCompletionAccepted(string prefix, string label, CompletionKind kind)
    {
        var viewer = memoryStore.Viewer();
        if (viewer is null || string.IsNullOrEmpty(label))
            return;
        memoryStore.Record(viewer, prefix, label, (int)kind);
    }

    /// <inheritdoc />
    public void Evict(string nodeTypePath)
    {
        // Drop + dispose the cached AdhocWorkspace so its CSharpCompilation / SyntaxTrees /
        // symbol graph are released the moment the NodeType hub disposes, rather than lingering
        // for the life of this singleton. Dispose() is the same release BuildOrReuseWorkspace does
        // on a version change; here it is triggered by node deletion / hub teardown instead.
        if (_cache.TryRemove(nodeTypePath, out var cached))
        {
            cached.Workspace.Dispose();
            logger.LogDebug("Evicted cached AdhocWorkspace for disposed NodeType {NodeTypePath}", nodeTypePath);
        }
    }

    /// <summary>Test seam (InternalsVisibleTo): is a workspace currently cached for this NodeType?</summary>
    internal bool IsWorkspaceCached(string nodeTypePath) => _cache.ContainsKey(nodeTypePath);

    /// <summary>
    /// Resolves the NodeType MeshNode, fetches <see cref="CompilationInputs"/>, and builds
    /// (or reuses) an <see cref="AdhocWorkspace"/> whose Documents mirror the input sources.
    /// Returns <c>null</c> when the node or its compilation cannot be resolved.
    /// </summary>
    private IObservable<CachedWorkspace?> GetOrBuildWorkspace(string nodeTypePath)
        => ResolveNodeOutcome(nodeTypePath)
            .SelectMany(outcome =>
            {
                if (outcome.Node is null)
                {
                    // 🚨 Hover and completions keep their "null / empty means nothing here"
                    // contracts (Monaco's shape), so for THEM the reason has to reach the log or it
                    // reaches nobody: a wedged hub and a mistyped path both render as a silently
                    // dead editor, which is how #1601 presents. Diagnostics no longer degrade this
                    // way — it returns an interrogable NodeDiagnosticsOutcome instead.
                    logger.LogWarning(
                        "[LSP] No workspace for '{Path}' — read status {Status}{Failure}. "
                        + "Hover/completions will answer empty; this is NOT evidence the NodeType "
                        + "is healthy.",
                        nodeTypePath, outcome.Status,
                        outcome.Failure is null ? "" : $": {outcome.Failure.Message}");
                    return Observable.Return<CachedWorkspace?>(null);
                }
                return WorkspaceFor(nodeTypePath, outcome.Node);
            });

    // The workspace half, given an already-resolved node — shared by GetOrBuildWorkspace and by
    // GetDiagnostics, which needs the READ's outcome and so cannot go through the null-collapsing
    // path above.
    private IObservable<CachedWorkspace?> WorkspaceFor(string nodeTypePath, MeshNode node)
        => compilationService.GetCompilationInputsAsync(node)
            .Select(inputs => inputs is null ? null : BuildOrReuseWorkspace(nodeTypePath, inputs));

    // 🚨 GetMeshNodeOutcome, not GetMeshNode: the interrogable read. GetMeshNode is the same read
    // with the distinction discarded, and discarding it here is what let "the hub never answered"
    // and "there is no such node" both render as a clean bill of health (#1592).
    private IObservable<NodeReadOutcome> ResolveNodeOutcome(string nodeTypePath)
        => hub.GetMeshNodeOutcome(nodeTypePath, TimeSpan.FromSeconds(15), ReadTimeoutBehavior.EmitNull);

    // The same read with the distinction discarded — for the completion/speculative paths, whose
    // "no node ⇒ no suggestions" contract is deliberate and documented at their call sites.
    private IObservable<MeshNode?> ResolveNode(string nodeTypePath)
        => ResolveNodeOutcome(nodeTypePath).Select(outcome => outcome.Node);

    private CachedWorkspace BuildOrReuseWorkspace(string nodeTypePath, CompilationInputs inputs)
    {
        if (_cache.TryGetValue(nodeTypePath, out var existing)
            && VersionsEqual(existing.SourceVersions, inputs.SourceVersions))
        {
            return existing;
        }

        // Dispose the old workspace before replacing — Roslyn AdhocWorkspace
        // holds onto syntax trees / compilation state that we no longer need.
        existing?.Workspace.Dispose();

        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var projectInfo = ProjectInfo
            .Create(
                projectId,
                VersionStamp.Create(),
                name: inputs.AssemblyName,
                assemblyName: inputs.AssemblyName,
                language: LanguageNames.CSharp)
            .WithMetadataReferences(inputs.References)
            .WithCompilationOptions(inputs.CompilationOptions)
            .WithParseOptions(inputs.ParseOptions);

        workspace.AddProject(projectInfo);

        var pathToDocId = ImmutableDictionary.CreateBuilder<string, DocumentId>(StringComparer.OrdinalIgnoreCase);

        // Skeleton document — the assembly attribute + generated provider class.
        // 🚨 It does NOT put the framework usings in scope for user code: its `using`s are
        // FILE-SCOPED to this document (it is generated with codeFile:null, so it holds no user
        // code at all). The import scope comes from the global-usings document below (#1802).
        var skeletonDocId = DocumentId.CreateNewId(projectId);
        workspace.AddDocument(DocumentInfo.Create(
            skeletonDocId,
            name: SkeletonDocumentPath,
            filePath: SkeletonDocumentPath,
            loader: TextLoader.From(TextAndVersion.Create(
                SourceText.From(inputs.SkeletonSource), VersionStamp.Create()))));

        // The import scope, as `global using` — what the emit path gets by concatenating every
        // source into the skeleton file. Without it, each per-file Document below sees only its own
        // usings and the service reports phantom CS0246/CS0308 on source that compiles and ships.
        var globalUsingsDocId = DocumentId.CreateNewId(projectId);
        workspace.AddDocument(DocumentInfo.Create(
            globalUsingsDocId,
            name: GlobalUsingsDocumentPath,
            filePath: GlobalUsingsDocumentPath,
            loader: TextLoader.From(TextAndVersion.Create(
                SourceText.From(inputs.GlobalUsingsSource), VersionStamp.Create()))));

        // One Document per user source, with the MeshNode Path as FilePath so language-service
        // queries from Monaco / MCP tools can address each file independently.
        foreach (var (path, code) in inputs.Sources)
        {
            var docId = DocumentId.CreateNewId(projectId);
            workspace.AddDocument(DocumentInfo.Create(
                docId,
                name: path,
                filePath: path,
                loader: TextLoader.From(TextAndVersion.Create(
                    SourceText.From(code), VersionStamp.Create()))));
            pathToDocId[path] = docId;
        }

        var cached = new CachedWorkspace(
            Workspace: workspace,
            ProjectId: projectId,
            SourceVersions: inputs.SourceVersions,
            DocumentsByPath: pathToDocId.ToImmutable(),
            SkeletonDocumentId: skeletonDocId);

        _cache[nodeTypePath] = cached;
        logger.LogDebug("Built AdhocWorkspace for {NodeTypePath} with {DocCount} user documents",
            nodeTypePath, inputs.Sources.Length);
        return cached;
    }

    private static bool VersionsEqual(
        ImmutableDictionary<string, long> a,
        ImmutableDictionary<string, long> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var kv in a)
        {
            if (!b.TryGetValue(kv.Key, out var v) || v != kv.Value) return false;
        }
        return true;
    }

    private static async Task<IReadOnlyList<DiagnosticInfo>> GetDiagnosticsAsync(
        CachedWorkspace cached, CancellationToken ct)
    {
        var project = cached.Workspace.CurrentSolution.GetProject(cached.ProjectId);
        if (project is null) return Array.Empty<DiagnosticInfo>();

        var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
        if (compilation is null) return Array.Empty<DiagnosticInfo>();
        var diags = compilation.GetDiagnostics(ct);
        if (diags.IsDefaultOrEmpty) return Array.Empty<DiagnosticInfo>();

        var result = new List<DiagnosticInfo>(diags.Length);
        foreach (var d in diags)
        {
            // Skip diagnostics that fall inside the generated trees (skeleton, global usings) —
            // they're artifacts of the generated code, not actionable for the user editing source
            // files. (CS8019 "unnecessary using" on the global-usings document is expected: it
            // imports for the whole compilation, so any single file uses only part of it.)
            var path = d.Location.SourceTree?.FilePath;
            if (path == SkeletonDocumentPath || path == GlobalUsingsDocumentPath) continue;

            result.Add(ToDiagnosticInfo(d));
        }
        return result;
    }

    private static async Task<HoverInfo?> GetHoverAsync(
        CachedWorkspace cached, DocumentId docId, SourcePosition position, CancellationToken ct)
    {
        var document = cached.Workspace.CurrentSolution.GetDocument(docId);
        if (document is null) return null;

        var text = await document.GetTextAsync(ct).ConfigureAwait(false);
        var offset = TryGetOffset(text, position);
        if (offset is null) return null;

        var service = QuickInfoService.GetService(document);
        if (service is null) return null;

        var info = await service.GetQuickInfoAsync(document, offset.Value, ct).ConfigureAwait(false);
        if (info is null) return null;

        var markdown = RenderQuickInfo(info);
        var range = SpanToRange(text, info.Span);
        return new HoverInfo(markdown, range);
    }

    private static async Task<IReadOnlyList<CompletionEntry>> GetCompletionsAsync(
        CachedWorkspace cached, DocumentId docId, SourcePosition position, int maxResults,
        CompletionUsageIndex? usage, CompletionMemory? memory, CancellationToken ct)
    {
        var document = cached.Workspace.CurrentSolution.GetDocument(docId);
        if (document is null) return Array.Empty<CompletionEntry>();
        return await CompleteAsync(document, position, maxResults, usage, memory, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Overlay completions inside a NodeType's compilation: the CURRENT solution with the one
    /// document's text substituted by the editor's in-flight code (an unknown
    /// <paramref name="sourcePath"/> is added as a new document — same tolerance as
    /// <see cref="CheckSpeculative"/>). Forked solutions are immutable, so concurrent suggest
    /// requests never contend and nothing needs applying back.
    /// </summary>
    private static async Task<IReadOnlyList<CompletionEntry>> GetOverlayCompletionsAsync(
        CachedWorkspace cached, string sourcePath, string proposedCode, SourcePosition position,
        int maxResults, CompletionUsageIndex? usage, CompletionMemory? memory, CancellationToken ct)
    {
        var solution = cached.Workspace.CurrentSolution;
        Document? document;
        if (cached.DocumentsByPath.TryGetValue(sourcePath, out var docId))
        {
            document = solution
                .WithDocumentText(docId, SourceText.From(proposedCode))
                .GetDocument(docId);
        }
        else
        {
            var newId = DocumentId.CreateNewId(cached.ProjectId);
            document = solution
                .AddDocument(newId, sourcePath, SourceText.From(proposedCode), filePath: sourcePath)
                .GetDocument(newId);
        }
        if (document is null) return Array.Empty<CompletionEntry>();
        return await CompleteAsync(document, position, maxResults, usage, memory, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Completions for a standalone SCRIPT Code node in the environment the kernel executes
    /// it in — script-kind parsing, the kernel's default imports and process-shared reference
    /// set, and the script globals type in scope (see <c>MeshScriptEnvironment</c>), so the
    /// editor suggests exactly what a ▶ Run can use.
    /// </summary>
    private async Task<IReadOnlyList<CompletionEntry>> GetScriptCompletionsAsync(
        string proposedCode, SourcePosition position, int maxResults, CancellationToken ct)
    {
        var usage = usageIndex;
        var memory = CurrentMemory();
        var script = await _scriptWorkspace.Value.WaitAsync(ct).ConfigureAwait(false);
        var document = script.Workspace.CurrentSolution
            .WithDocumentText(script.DocumentId, SourceText.From(StripKernelDirectives(proposedCode)))
            .GetDocument(script.DocumentId);
        if (document is null) return Array.Empty<CompletionEntry>();
        return await CompleteAsync(document, position, maxResults, usage, memory, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Diagnostics for a standalone SCRIPT Code node — the script-environment sibling of
    /// <see cref="SpeculativeCompilation"/>, so lesson cells get live squiggles.
    /// </summary>
    private async Task<IReadOnlyList<DiagnosticInfo>> GetScriptDiagnosticsAsync(
        string proposedCode, CancellationToken ct)
    {
        var script = await _scriptWorkspace.Value.WaitAsync(ct).ConfigureAwait(false);
        var document = script.Workspace.CurrentSolution
            .WithDocumentText(script.DocumentId, SourceText.From(StripKernelDirectives(proposedCode)))
            .GetDocument(script.DocumentId);
        var compilation = document is null
            ? null
            : await document.Project.GetCompilationAsync(ct).ConfigureAwait(false);
        if (compilation is null) return Array.Empty<DiagnosticInfo>();


        var diags = compilation.GetDiagnostics(ct);
        if (diags.IsDefaultOrEmpty) return Array.Empty<DiagnosticInfo>();
        var result = new List<DiagnosticInfo>();
        foreach (var d in diags)
        {
            if (d.Severity == RoslynDiagnosticSeverity.Hidden) continue;
            result.Add(ToDiagnosticInfo(d));
        }
        return result;
    }

    /// <summary>
    /// The shared core: Roslyn <see cref="CompletionService"/> over one document at one
    /// position, flattened to <see cref="CompletionEntry"/>.
    /// </summary>
    private static async Task<IReadOnlyList<CompletionEntry>> CompleteAsync(
        Document document, SourcePosition position, int maxResults, CompletionUsageIndex? usage,
        CompletionMemory? memory, CancellationToken ct)
    {
        var text = await document.GetTextAsync(ct).ConfigureAwait(false);
        var offset = TryGetOffset(text, position);
        if (offset is null) return Array.Empty<CompletionEntry>();

        var service = CompletionService.GetService(document);
        if (service is null) return Array.Empty<CompletionEntry>();

        var list = await service.GetCompletionsAsync(document, offset.Value, cancellationToken: ct).ConfigureAwait(false);
        if (list is null || list.ItemsList.Count == 0) return Array.Empty<CompletionEntry>();

        // 🚨 Rank BEFORE truncating. Roslyn returns every symbol in scope, alphabetically — a
        // bare Take(maxResults) keeps the A's and drops everything after them, so typing "Mes"
        // could never reach "Mesh" no matter how the client filters afterwards. The word being
        // completed comes from the completion list's own span (the token Roslyn would replace),
        // so it is exactly what the user has typed at the caret.
        var prefix = list.Span.Length > 0 && list.Span.End <= text.Length
            ? text.ToString(list.Span)
            : string.Empty;

        // Two regimes, the same split every editor makes (VS Code: client fuzzy-scorer for the
        // typed word, server sortText/priority for ties, learned prior otherwise):
        //
        //   • The user has TYPED something → MATCH QUALITY decides, and Roslyn's own matcher is
        //     the authority (exact > prefix > CamelCase hump > substring, honouring the
        //     MatchPriority that target-typing sets). Popularity must NOT reorder across match
        //     quality here, or typing "Bad" would surface the popular "Stack" above "Badge".
        //   • The user has typed NOTHING (just pressed `.`) → there is no match quality to rank
        //     by and alphabetical is worthless. This is where LIKELY USAGE decides: how often
        //     each identifier occurs in the code this mesh already runs, plus a locality bonus
        //     for identifiers already in this very cell (VS Code's locality bonus, and the
        //     signal IntelliCode models over a public corpus — ours is better, it is the
        //     MeshWeaver idiom itself).
        IEnumerable<CompletionItem> ranked;
        if (prefix.Length > 0)
        {
            ranked = service.FilterItems(document, list.ItemsList.ToImmutableArray(), prefix);
        }
        else
        {
            var local = LocalIdentifierCounts(text.ToString());
            ranked = list.ItemsList
                .OrderByDescending(i => UsageScore(i, usage, local))
                .ThenByDescending(i => i.Rules.MatchPriority)
                .ThenBy(i => i.SortText ?? i.DisplayText, StringComparer.OrdinalIgnoreCase);
        }
        ranked = ranked.Take(maxResults);

        // 🚨 The emitted SortText is OUR RANK, never Roslyn's — and that is the whole point.
        // Roslyn's SortText is an ALPHABETICAL key (it exists to sort a list A→Z), and Monaco
        // orders its suggest widget by (fuzzy score, sortText, label). Passing Roslyn's key
        // through therefore threw away every ranking computed above the moment the widget
        // opened: with nothing typed there is no fuzzy score to order by, so the list the user
        // saw was strictly alphabetical — the usage and locality ranking, and the target-typing
        // priority, all discarded. Emitting the rank as a zero-padded ordinal keeps Monaco in
        // charge of relevance WHILE the user types (its score still dominates) and makes our
        // order the tiebreak, which is exactly what a `.`-triggered list needs.
        var result = new List<CompletionEntry>();
        var rank = 0;
        foreach (var item in ranked)
        {
            result.Add(new CompletionEntry(
                Label: item.DisplayText,
                Kind: MapTagsToKind(item.Tags),
                InsertText: item.DisplayText,
                Detail: item.InlineDescription is { Length: > 0 } ? item.InlineDescription : null,
                Documentation: null,
                SortText: RankKey(rank++)));
        }

        // Finally, this user's own history: what they accepted last time in this situation is
        // PRESELECTED — the order stays exactly as ranked above, only the highlighted row moves
        // (VS Code's suggest memory works this way, and it is why the memory can be aggressive
        // without ever hiding a better match).
        var remembered = memory?.Select(
            result.Select(r => (r.Label, (int)r.Kind)).ToList(), prefix);
        if (remembered is not null)
        {
            for (var i = 0; i < result.Count; i++)
            {
                if (!string.Equals(result[i].Label, remembered, StringComparison.Ordinal))
                    continue;
                result[i] = result[i] with { Preselect = true };
                break;
            }
        }
        return result;
    }

    /// <summary>This viewer's acceptance history, or null when nobody is signed in.</summary>
    private CompletionMemory? CurrentMemory()
    {
        var viewer = memoryStore.Viewer();
        return viewer is null ? null : memoryStore.For(viewer);
    }

    /// <summary>
    /// The likely-usage score for one candidate: how often the identifier appears in THIS cell
    /// (weighted heavily — the strongest signal about what the author is doing right now, and it
    /// works on a cold index) plus how often it appears across the mesh's existing Code nodes.
    /// </summary>
    private static int UsageScore(
        CompletionItem item, CompletionUsageIndex? usage, IReadOnlyDictionary<string, int> local)
    {
        var name = item.FilterText is { Length: > 0 } f ? f : item.DisplayText;
        var localCount = local.TryGetValue(name, out var n) ? n : 0;
        return (LocalityWeight * localCount) + (usage?.Frequency(name) ?? 0);
    }

    /// <summary>How much an occurrence in the current cell outweighs one in the corpus.</summary>
    private const int LocalityWeight = 5;

    /// <summary>Identifier occurrences within the cell being edited (VS Code's locality bonus).</summary>
    private static Dictionary<string, int> LocalIdentifierCounts(string code)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(code))
            return counts;
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(code, @"[A-Za-z_][A-Za-z0-9_]*"))
        {
            var token = m.Value;
            if (token.Length < 2) continue;
            counts[token] = counts.TryGetValue(token, out var n) ? n + 1 : 1;
        }
        return counts;
    }

    /// <summary>
    /// Blanks kernel-only directive lines (<c>#r "nuget: …"</c> — restored out of band by the
    /// kernel, unknown to a workspace compilation) while PRESERVING line numbers, so positions
    /// and diagnostic locations still line up with the editor's text.
    /// </summary>
    internal static string StripKernelDirectives(string code)
    {
        if (string.IsNullOrEmpty(code) || !code.Contains("#r", StringComparison.Ordinal))
            return code;
        var lines = code.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("#r", StringComparison.Ordinal)
                && trimmed.Contains("nuget:", StringComparison.OrdinalIgnoreCase))
                lines[i] = string.Empty;
        }
        return string.Join("\n", lines);
    }

    private static DiagnosticInfo ToDiagnosticInfo(Diagnostic d)
    {
        SourceLocation? location = null;
        if (d.Location.IsInSource && d.Location.SourceTree?.FilePath is { Length: > 0 } path)
        {
            var span = d.Location.GetLineSpan();
            location = new SourceLocation(
                path,
                new SourceRange(
                    new SourcePosition(span.StartLinePosition.Line, span.StartLinePosition.Character),
                    new SourcePosition(span.EndLinePosition.Line, span.EndLinePosition.Character)));
        }
        return new DiagnosticInfo(
            Id: d.Id,
            Severity: MapSeverity(d.Severity),
            Message: d.GetMessage(),
            Location: location);
    }

    private static LspDiagnosticSeverity MapSeverity(RoslynDiagnosticSeverity s) => s switch
    {
        RoslynDiagnosticSeverity.Hidden => LspDiagnosticSeverity.Hidden,
        RoslynDiagnosticSeverity.Info => LspDiagnosticSeverity.Info,
        RoslynDiagnosticSeverity.Warning => LspDiagnosticSeverity.Warning,
        RoslynDiagnosticSeverity.Error => LspDiagnosticSeverity.Error,
        _ => LspDiagnosticSeverity.Info,
    };

    private static int? TryGetOffset(SourceText text, SourcePosition position)
    {
        if (position.Line < 0 || position.Line >= text.Lines.Count) return null;
        var line = text.Lines[position.Line];
        var character = Math.Max(0, position.Character);
        if (character > line.End - line.Start) return null;
        return line.Start + character;
    }

    private static SourceRange? SpanToRange(SourceText text, TextSpan span)
    {
        if (span.IsEmpty && span.Start == 0) return null;
        var line = text.Lines.GetLinePositionSpan(span);
        return new SourceRange(
            new SourcePosition(line.Start.Line, line.Start.Character),
            new SourcePosition(line.End.Line, line.End.Character));
    }

    private static string RenderQuickInfo(QuickInfoItem info)
    {
        // Concatenate sections as markdown — Description as a C# code block, Documentation as plain markdown.
        var sb = new System.Text.StringBuilder();
        foreach (var section in info.Sections)
        {
            var text = section.Text;
            if (string.IsNullOrEmpty(text)) continue;

            if (section.Kind == QuickInfoSectionKinds.Description)
            {
                sb.AppendLine("```csharp");
                sb.AppendLine(text);
                sb.AppendLine("```");
            }
            else
            {
                sb.AppendLine(text);
            }
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// The sort key for a ranked completion: its position, zero-padded so a plain STRING compare
    /// (which is all an editor does with <c>sortText</c>) reproduces the numeric order —
    /// <c>"000002" &lt; "000010"</c>, where <c>"2" &gt; "10"</c> would invert it.
    ///
    /// <para>The width is what makes that total, so it is chosen to outrun any caller rather than
    /// any list a human reads: at the width boundary the padding STOPS and the contract inverts
    /// again (<c>"10000" &lt; "9999"</c> ordinally). Six digits is far past the few hundred a
    /// completion list is ever asked for, and past any plausible <c>maxResults</c> a caller
    /// invents. Pure, so the ordering contract is testable without Roslyn.</para>
    /// </summary>
    internal static string RankKey(int rank) => rank.ToString("D6", CultureInfo.InvariantCulture);

    private static CompletionKind MapTagsToKind(ImmutableArray<string> tags)
    {
        if (tags.IsDefaultOrEmpty) return CompletionKind.Text;
        foreach (var tag in tags)
        {
            switch (tag)
            {
                case WellKnownTags.Class: return CompletionKind.Class;
                case WellKnownTags.Constant: return CompletionKind.Constant;
                case WellKnownTags.Delegate: return CompletionKind.Function;
                case WellKnownTags.Enum: return CompletionKind.Enum;
                case WellKnownTags.EnumMember: return CompletionKind.EnumMember;
                case WellKnownTags.Event: return CompletionKind.Event;
                case WellKnownTags.ExtensionMethod: return CompletionKind.Method;
                case WellKnownTags.Field: return CompletionKind.Field;
                case WellKnownTags.Interface: return CompletionKind.Interface;
                case WellKnownTags.Keyword: return CompletionKind.Keyword;
                case WellKnownTags.Local: return CompletionKind.Variable;
                case WellKnownTags.Method: return CompletionKind.Method;
                case WellKnownTags.Module: return CompletionKind.Module;
                case WellKnownTags.Namespace: return CompletionKind.Module;
                case WellKnownTags.Operator: return CompletionKind.Operator;
                case WellKnownTags.Parameter: return CompletionKind.Variable;
                case WellKnownTags.Property: return CompletionKind.Property;
                case WellKnownTags.Snippet: return CompletionKind.Snippet;
                case WellKnownTags.Structure: return CompletionKind.Struct;
                case WellKnownTags.TypeParameter: return CompletionKind.TypeParameter;
            }
        }
        return CompletionKind.Text;
    }

    /// <summary>
    /// Builds the script workspace mirroring the kernel's execution environment
    /// (<c>MeshScriptEnvironment</c>): script-kind parsing, the default imports as the
    /// compilation's usings, the process-shared reference set, the shared metadata resolver,
    /// and the script globals as the submission's host object — so <c>Mesh</c>, <c>Log</c>,
    /// <c>Ct</c> and <c>Inputs</c> complete as bare identifiers exactly as they run.
    ///
    /// <para>🚨 Awaits the shared reference snapshot with <see cref="CancellationToken.None"/> —
    /// this build is cached (once) for every future request against THIS mesh instance's
    /// <c>_scriptWorkspace</c>, so it must never be torn down by whichever caller happens to be
    /// first; each caller's own cancellation is applied at the `_scriptWorkspace.Value.WaitAsync`
    /// call site instead (see the field's doc comment).</para>
    /// </summary>
    private static async Task<ScriptWorkspace> BuildScriptWorkspaceAsync(IServiceProvider serviceProvider)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var docId = DocumentId.CreateNewId(projectId);
        var references = await Kernel.Hub.MeshScriptEnvironment
            .ReferencesAsync(serviceProvider, CancellationToken.None).ConfigureAwait(false);
        var projectInfo = ProjectInfo
            .Create(
                projectId,
                VersionStamp.Create(),
                name: "MeshScript",
                assemblyName: "MeshScript",
                language: LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(
                        OutputKind.DynamicallyLinkedLibrary,
                        metadataReferenceResolver: Kernel.Hub.MeshScriptEnvironment.MetadataResolver)
                    .WithUsings(Kernel.Hub.MeshScriptEnvironment.Imports),
                parseOptions: new CSharpParseOptions(kind: SourceCodeKind.Script),
                metadataReferences: references,
                isSubmission: true,
                hostObjectType: Kernel.Hub.MeshScriptEnvironment.GlobalsType)
            .WithDocuments(
            [
                DocumentInfo.Create(
                    docId,
                    name: ScriptDocumentPath,
                    filePath: ScriptDocumentPath,
                    sourceCodeKind: SourceCodeKind.Script,
                    loader: TextLoader.From(TextAndVersion.Create(
                        SourceText.From(string.Empty), VersionStamp.Create()))),
            ]);
        workspace.AddProject(projectInfo);
        return new ScriptWorkspace(workspace, projectId, docId);
    }

    private sealed record ScriptWorkspace(
        AdhocWorkspace Workspace,
        ProjectId ProjectId,
        DocumentId DocumentId);

    private sealed record CachedWorkspace(
        AdhocWorkspace Workspace,
        ProjectId ProjectId,
        ImmutableDictionary<string, long> SourceVersions,
        ImmutableDictionary<string, DocumentId> DocumentsByPath,
        DocumentId SkeletonDocumentId);
}
