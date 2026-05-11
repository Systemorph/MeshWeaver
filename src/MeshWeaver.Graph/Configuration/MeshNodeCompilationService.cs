using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.NuGet;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Service that compiles and caches MeshNode assemblies on-demand.
/// Combines code generation (via DynamicMeshNodeAttributeGenerator) with Roslyn compilation.
/// Implements IMeshNodeCompilationService from MeshWeaver.Mesh.Contract.
/// </summary>
internal class MeshNodeCompilationService(
    ICompilationCacheService cacheService,
    IOptions<CompilationCacheOptions> cacheOptions,
    IMessageHub hub,
    INuGetAssemblyResolver nugetResolver,
    ILogger<MeshNodeCompilationService> logger)
    : IMeshNodeCompilationService
{
    private readonly CompilationCacheOptions _cacheOptions = cacheOptions.Value ?? new CompilationCacheOptions();
    private JsonSerializerOptions JsonOptions => hub.JsonSerializerOptions;
    private readonly DynamicMeshNodeAttributeGenerator _attributeGenerator = new();

    // Per-nodeName lock that serialises concurrent <see cref="CompileAsync"/> calls
    // for the same NodeType. Without it, multiple instances activating in parallel
    // (e.g. four FutuRe BusinessUnit hubs — EuropeRe, AmericasIns, AsiaRe, Group)
    // race File.Create on the same .dll and the loser gets
    // "The process cannot access the file because it is being used by another
    // process". Roslyn's emit + the AssemblyLoadContext load hold the file open
    // between the two, so the OS lock survives long enough for the second caller
    // to hit it. Inside the lock we double-check the cache so the runner-up
    // returns immediately instead of recompiling redundantly.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _compileLocks = new(StringComparer.Ordinal);

    // Query expansion lives in CodeQueryResolver now so the NodeType Configuration
    // side menu can evaluate the *same* queries the compiler uses — the Sources /
    // Tests lists displayed in the UI are guaranteed to match the files compiled.
    //
    // Default Roslyn references are process-wide: TPA list + a few well-known
    // additions never change at runtime. Eager static-field init runs once at
    // type load and the result is then a plain field read on every compile —
    // no Lazy property dispatch, no synchronization, zero per-compile cost.
    private static readonly IReadOnlyList<MetadataReference> _references = GetDefaultReferences();

    /// <summary>
    /// Builds the process-wide MetadataReference list — TPA assemblies plus a few
    /// well-known additions. Uses <see cref="MetadataReference.CreateFromFile(string)"/>
    /// (mmap, lazy read) — Roslyn typically reads only a small fraction of each
    /// assembly's metadata, so the upfront cost is tiny. An earlier attempt at
    /// <see cref="MetadataReference.CreateFromStream(Stream, MetadataReferenceProperties, DocumentationProvider, string?)"/>
    /// to avoid finalizer pressure ended up reading the whole DLL into managed
    /// memory eagerly — net 10%+ slower in the autocomplete-test CPU profile,
    /// since most of those bytes were never touched. The file-handle finalizer
    /// pressure those references add is also tiny in practice (the static field
    /// holds them for the process lifetime; finalizers only run at shutdown).
    /// </summary>
    private static List<MetadataReference> GetDefaultReferences()
    {
        var references = new List<MetadataReference>();

        var trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (trustedAssemblies != null)
        {
            foreach (var path in trustedAssemblies.Split(Path.PathSeparator))
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    continue;
                try
                {
                    references.Add(MetadataReference.CreateFromFile(path));
                }
                catch
                {
                    // Skip assemblies that can't be loaded
                }
            }
        }

        // Three well-known additions in case TPA didn't include them. Dedup
        // against TPA-derived set by Display path so we don't double-load.
        var seen = new HashSet<string>(
            references.Select(r => r.Display ?? string.Empty),
            StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in new[]
        {
            typeof(object).Assembly,                                           // System.Runtime
            typeof(System.ComponentModel.DataAnnotations.KeyAttribute).Assembly, // DataAnnotations
            typeof(System.Text.Json.Serialization.JsonPropertyNameAttribute).Assembly, // System.Text.Json
        })
        {
            if (!string.IsNullOrEmpty(assembly.Location)
                && File.Exists(assembly.Location)
                && seen.Add(assembly.Location))
            {
                try
                {
                    references.Add(MetadataReference.CreateFromFile(assembly.Location));
                }
                catch
                {
                    // Skip if it can't be loaded
                }
            }
        }

        return references;
    }

    /// <summary>
    /// Regex matching @@path references in code files, consistent with InlineReferenceResolver in MeshWeaver.AI.
    /// </summary>
    private static readonly Regex CodeIncludePattern = new(@"@@([^\s#\]]+)", RegexOptions.Compiled);

    /// <summary>
    /// Resolves @@path references in code content by fetching the referenced
    /// node's CodeConfiguration via <see cref="IStorageAdapter"/> directly. Used by
    /// <c>NodeTypeService</c> during compilation to bypass routing and avoid
    /// circular hub creation. Resolution is transitive: if a resolved include
    /// itself contains @@references, those are resolved too.
    /// For the production reactive path, see <see cref="ResolveCodeIncludes"/>.
    /// </summary>
    internal async Task<string> ResolveCodeIncludesAsync(
        string code, IStorageAdapter meshStorage, CancellationToken ct)
    {
        var resolved = new HashSet<string>();
        return await ResolveCodeIncludesAsync(code, meshStorage, resolved, ct);
    }

    private IObservable<string> ResolveCodeIncludes(string code, HashSet<string> resolved)
    {
        if (string.IsNullOrWhiteSpace(code) || !code.Contains("@@"))
            return Observable.Return(code);

        var matches = CodeIncludePattern.Matches(code);
        if (matches.Count == 0)
            return Observable.Return(code);

        // For each @@ match, fetch the referenced node via composed hub.GetMeshNode
        // (NEVER await — that's a 100% deadlock). Each result feeds the recursive
        // resolution; the final substituted string is built up in left-to-right order
        // by serially aggregating the per-match observables.
        IObservable<string> chain = Observable.Return(code);
        foreach (Match match in matches)
        {
            var path = match.Groups[1].Value;
            var matchValue = match.Value;
            chain = chain.SelectMany(current =>
            {
                if (!resolved.Add(path))
                    return Observable.Return(current.Replace(matchValue, string.Empty));

                return hub.GetMeshNode(path, TimeSpan.FromSeconds(15))
                    .SelectMany(referencedNode =>
                    {
                        if (referencedNode?.Content is CodeConfiguration cf
                            && !string.IsNullOrWhiteSpace(cf.Code))
                        {
                            logger.LogDebug("Resolved code include @@{Path}", path);
                            return ResolveCodeIncludes(cf.Code, resolved)
                                .Select(resolvedInner => current.Replace(matchValue, resolvedInner));
                        }
                        logger.LogWarning("Could not resolve code include @@{Path}", path);
                        return Observable.Return(current);
                    });
            });
        }

        return chain;
    }

    private async Task<string> ResolveCodeIncludesAsync(
        string code, IStorageAdapter meshStorage, HashSet<string> resolved, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code) || !code.Contains("@@"))
            return code;

        var matches = CodeIncludePattern.Matches(code);
        if (matches.Count == 0)
            return code;

        var result = code;
        foreach (Match match in matches)
        {
            var path = match.Groups[1].Value;
            if (!resolved.Add(path))
            {
                result = result.Replace(match.Value, string.Empty);
                continue;
            }

            // Use IStorageAdapter directly to bypass routing
            var referencedNode = await meshStorage.Read(path, hub.JsonSerializerOptions).FirstAsync().ToTask(ct);
            string? resolvedCode = null;
            if (referencedNode?.Content is CodeConfiguration cf && !string.IsNullOrWhiteSpace(cf.Code))
            {
                resolvedCode = cf.Code;
            }

            if (resolvedCode != null)
            {
                logger.LogDebug("Resolved code include @@{Path}", path);
                resolvedCode = await ResolveCodeIncludesAsync(resolvedCode, meshStorage, resolved, ct);
                result = result.Replace(match.Value, resolvedCode);
            }
            else
            {
                logger.LogWarning("Could not resolve code include @@{Path}", path);
            }
        }

        return result;
    }

    /// <inheritdoc />
    public IObservable<string?> GetAssemblyLocation(MeshNode node)
        => GetAssemblyLocationWithLog(node).Select(t => t.Path);

    /// <summary>
    /// Companion to <see cref="GetAssemblyLocation"/> that also surfaces the
    /// <see cref="ActivityLog"/> of the compile attempt — every executed source
    /// query, every matched Code path, the final compile result. The same chain
    /// runs underneath; this method is what <see cref="CompileAndGetConfigurations"/>
    /// uses so the response surfaced through <c>GetCompilationPathResponse.Log</c>
    /// reflects what actually happened (no double-compile to gather diagnostics).
    /// </summary>
    private IObservable<(string? Path, ActivityLog Log)> GetAssemblyLocationWithLog(
        MeshNode node, IReadOnlyList<MeshNode>? sourcesOverride = null)
    {
        var log = new ActivityLog(ActivityCategory.Compilation)
        {
            HubPath = node.Path,
            AffectedPaths = ImmutableList<string>.Empty.Add(node.Path)
        };

        if (string.IsNullOrEmpty(node.NodeType))
        {
            logger.LogDebug("Node {NodePath} has no NodeType, skipping assembly compilation", node.Path);
            return Observable.Return<(string?, ActivityLog)>((null,
                AppendInfo(log, $"Skipped — node '{node.Path}' has no NodeType.")
                    .Finish((int)hub.Version, ActivityStatus.Succeeded)));
        }

        var nodeName = cacheService.SanitizeNodeName(node.Path);
        var dllPath = cacheService.GetDllPath(nodeName);

        // Resolve the owning NodeTypeDefinition once — used for source discovery
        // (Sources / Source convention) and for Configuration / ContentCollections.
        IObservable<NodeTypeDefinition?> resolveDef;
        string selfPath;
        if (node.Content is NodeTypeDefinition selfDef)
        {
            resolveDef = Observable.Return<NodeTypeDefinition?>(selfDef);
            selfPath = node.Path;
        }
        else
        {
            resolveDef = hub.GetMeshNode(node.NodeType, TimeSpan.FromSeconds(15))
                .Select(typeNode => typeNode?.Content as NodeTypeDefinition);
            selfPath = node.NodeType;
        }

        return resolveDef.SelectMany(ntDef =>
            // Source-aware cache check: discover the LastModified of every source
            // Code node + the NodeType itself. The cache is valid only if the
            // compiled DLL is newer than the most recent source change.
            DiscoverSourceMaxLastModified(ntDef, selfPath, sourcesOverride)
                .SelectMany(maxSourceLastModified =>
                {
                    var effectiveLastModified = node.LastModified > maxSourceLastModified
                        ? node.LastModified
                        : maxSourceLastModified;

                    if (cacheService.IsDiskCacheEnabled
                        && cacheService.IsCacheValid(nodeName, effectiveLastModified))
                    {
                        logger.LogDebug(
                            "Using cached assembly for {NodePath} (effectiveLastModified={EffectiveLastModified})",
                            node.Path, effectiveLastModified);
                        return Observable.Return<(string?, ActivityLog)>((
                            dllPath,
                            AppendInfo(log,
                                $"Cache hit — returning {dllPath} (effective LastModified={effectiveLastModified:O}).")
                                .Finish((int)hub.Version, ActivityStatus.Succeeded)));
                    }

                    return CompileCore(node, ntDef, selfPath, log, sourcesOverride);
                }));
    }

    private static ActivityLog AppendInfo(ActivityLog log, string message)
        => log with { Messages = log.Messages.Add(new LogMessage(message, LogLevel.Information)) };

    private static ActivityLog AppendWarning(ActivityLog log, string message)
        => log with { Messages = log.Messages.Add(new LogMessage(message, LogLevel.Warning)) };

    private static ActivityLog AppendError(ActivityLog log, string message)
        => log with { Messages = log.Messages.Add(new LogMessage(message, LogLevel.Error)) };

    /// <summary>
    /// Source-set discovery via the workspace SyncedQuery registry — one
    /// long-lived, cached, replayed <see cref="IObservable{T}"/> per
    /// <paramref name="selfPath"/>. The first call spins up a single
    /// <see cref="IMeshQueryCore.ObserveQuery"/> per NodeType-resolved query
    /// (union of <c>Sources</c> + <c>Tests</c>); subsequent compiles for the
    /// same NodeType hit the registry's <c>Replay(1).RefCount()</c> cache and
    /// skip the Initial re-fetch entirely. Live updates flow through too —
    /// when a Source/Test Code node changes, the cached collection re-emits.
    /// </summary>
    private IObservable<IEnumerable<MeshNode>> GetSourceCollection(
        NodeTypeDefinition? ntDef, string selfPath)
    {
        var queries = CodeQueryResolver
            .ExpandAll(ntDef?.Sources, CodeQueryResolver.DefaultSources, selfPath)
            .Concat(CodeQueryResolver.ExpandAll(ntDef?.Tests, CodeQueryResolver.DefaultTests, selfPath))
            .ToArray();

        if (queries.Length == 0)
            return Observable.Return(Enumerable.Empty<MeshNode>());

        var workspace = hub.GetWorkspace();
        var id = $"compile-sources:{selfPath}";
        return workspace.GetQuery(id, queries);
    }

    /// <summary>
    /// Returns the maximum <c>LastModified</c> across all source Code nodes that
    /// would feed a compile of <paramref name="ntDef"/>. Reads from the cached
    /// SyncedQuery so cache invalidation tracks the exact same set of files the
    /// compile reads. Returns <see cref="DateTimeOffset.MinValue"/> if there are
    /// no sources.
    /// </summary>
    private IObservable<DateTimeOffset> DiscoverSourceMaxLastModified(
        NodeTypeDefinition? ntDef, string selfPath,
        IReadOnlyList<MeshNode>? sourcesOverride = null) =>
        ResolveSources(ntDef, selfPath, sourcesOverride)
            .Take(1)
            .Select(nodes => nodes.Aggregate(
                DateTimeOffset.MinValue,
                (acc, n) => n.LastModified > acc ? n.LastModified : acc));

    /// <summary>
    /// Captures <c>{path → MeshNode.LastModified.Ticks}</c> for every source Code/Test
    /// node that feeds a compile of <paramref name="ntDef"/>. Sibling to
    /// <see cref="DiscoverSourceMaxLastModified"/> — same SyncedQuery, different
    /// aggregation. Used by the compile watcher to populate
    /// <c>NodeTypeDefinition.CompiledSources</c> on success so a future
    /// recompile-needed check is a data comparison (added/removed/modified)
    /// instead of a max-LastModified timing guess.
    /// <para>
    /// Uses <c>LastModified.Ticks</c> (not <c>Version</c>) because the framework's
    /// <c>UpdateNodeRequest</c> handler reliably refreshes <c>LastModified</c>, while
    /// <c>MeshNode.Version</c> is bumped only by the local workspace's
    /// <c>MeshNodeTypeSource</c> stamp and may not propagate through the synced
    /// mesh-level query that <c>HandleCreateRelease</c> reads from.
    /// </para>
    /// </summary>
    public IObservable<ImmutableDictionary<string, long>> DiscoverSourceVersionSnapshot(
        NodeTypeDefinition? ntDef, string selfPath,
        IReadOnlyList<MeshNode>? sourcesOverride = null) =>
        ResolveSources(ntDef, selfPath, sourcesOverride)
            .Take(1)
            .Select(nodes => nodes
                .Where(n => !string.IsNullOrEmpty(n.Path))
                .Aggregate(
                    ImmutableDictionary<string, long>.Empty,
                    (acc, n) => acc.SetItem(n.Path, n.LastModified.UtcTicks)));

    /// <summary>
    /// Resolves the source set for a compile run. When the caller hands in a
    /// <paramref name="sourcesOverride"/> (the freshly-observed set from
    /// <c>HandleCreateRelease</c>'s uncached <c>IMeshService.ObserveQuery</c>),
    /// use that — it's the authoritative post-update snapshot the trigger
    /// already evaluated. Otherwise fall back to the cached SyncedQuery.
    /// </summary>
    private IObservable<IEnumerable<MeshNode>> ResolveSources(
        NodeTypeDefinition? ntDef, string selfPath, IReadOnlyList<MeshNode>? sourcesOverride)
    {
        if (sourcesOverride is not null)
            return Observable.Return<IEnumerable<MeshNode>>(sourcesOverride);
        return GetSourceCollection(ntDef, selfPath);
    }

    /// <summary>
    /// IObservable end-to-end. Source discovery rides the cached SyncedQuery
    /// registered for this NodeType; @@ include resolution composes via
    /// <see cref="ResolveCodeIncludes"/>. The only Task→Observable bridge is
    /// the Roslyn <see cref="CompileAsync"/> call. No <c>Observable.FromAsync</c>,
    /// no <c>await</c> on hub round-trips — both are the canonical deadlock
    /// patterns documented in <c>Doc/Architecture/AsynchronousCalls.md</c>.
    /// </summary>
    private IObservable<(string? Path, ActivityLog Log)> CompileCore(
        MeshNode node, NodeTypeDefinition? ntDef, string selfPath, ActivityLog log,
        IReadOnlyList<MeshNode>? sourcesOverride = null)
    {
        var nodeName = cacheService.SanitizeNodeName(node.Path);
        var executedQueries = CodeQueryResolver
            .ExpandAll(ntDef?.Sources, CodeQueryResolver.DefaultSources, selfPath)
            .Concat(CodeQueryResolver.ExpandAll(ntDef?.Tests, CodeQueryResolver.DefaultTests, selfPath))
            .ToList();
        var matchedCodePaths = new List<string>();

        // Source discovery: prefer the caller-supplied freshly-observed sources
        // (HandleCreateRelease's uncached IMeshService.ObserveQuery snapshot —
        // authoritative for the just-modified Code node), falling back to the
        // workspace SyncedQuery registry's Replay(1) cache when no override is
        // supplied. Without the override, the .Take(1) on the cached observable
        // could pick up the pre-update Initial emission and the V2 compile would
        // silently consume V1 source — that was the V1↔V2 mismatch root cause in
        // CodeEditRecompileTest.
        var discoverCodeFiles = ResolveSources(ntDef, selfPath, sourcesOverride)
            .Take(1)
            .Select(matches =>
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var acc = new List<CodeConfiguration>();
                foreach (var n in matches)
                {
                    if (string.IsNullOrEmpty(n.Path) || !seen.Add(n.Path))
                        continue;
                    if (n.Content is CodeConfiguration cf
                        && !string.IsNullOrWhiteSpace(cf.Code))
                    {
                        // Skip executable scripts — they run via the kernel
                        // (ExecuteScriptRequest), not folded into the parent
                        // NodeType's Roslyn unit. Top-level statements would
                        // collide with class declarations from Source/ siblings
                        // ("Top-level statements must precede namespace and
                        // type declarations"). Test/ commonly mixes both
                        // shapes; this filter lets both coexist.
                        if (cf.IsExecutable)
                        {
                            logger.LogDebug(
                                "Source discovery for {NodePath}: skipping executable Code {CodePath} — runs via kernel only",
                                node.Path, n.Path);
                            continue;
                        }
                        acc.Add(cf);
                        matchedCodePaths.Add(n.Path);
                    }
                }
                logger.LogDebug(
                    "Source discovery for {NodePath}: matched {Count} Code nodes from {QueryCount} queries",
                    node.Path, matchedCodePaths.Count, executedQueries.Count);
                return acc;
            });

        return discoverCodeFiles
            .SelectMany(codeFiles =>
            {
                // Stage: resolve @@ include references reactively. Each include lookup
                // composes via ResolveCodeIncludes (already an IObservable<string>). No await.
                if (codeFiles.Count == 0)
                    return Observable.Return(codeFiles);

                IObservable<List<CodeConfiguration>> includeChain =
                    Observable.Return(new List<CodeConfiguration>(codeFiles.Count));
                foreach (var codeFile in codeFiles)
                {
                    var cf = codeFile;
                    includeChain = includeChain.SelectMany(acc =>
                        ResolveCodeIncludes(cf.Code!, new HashSet<string>())
                            .Select(resolvedCode =>
                            {
                                acc.Add(resolvedCode != cf.Code ? cf with { Code = resolvedCode } : cf);
                                return acc;
                            }));
                }
                return includeChain;
            })
            .SelectMany(codeFiles =>
            {
                // Final stage: combine + compile. The Roslyn `Compile` call itself is
                // the only Task→Observable bridge in this whole method.
                CodeConfiguration? codeFile = codeFiles.Count switch
                {
                    0 => null,
                    1 => codeFiles[0],
                    _ => new CodeConfiguration { Code = string.Join("\n\n", codeFiles.Select(cf => cf.Code)) }
                };
                var configuration = ntDef?.Configuration;
                var contentCollections = ntDef?.ContentCollections;

                // Snapshot the discovery into the activity log: every executed query +
                // every matched Code path. Lets the response carry "compile saw N
                // source files from queries [Q1, Q2…]" without re-running the pipeline.
                var discoveryLog = log;
                foreach (var q in executedQueries)
                    discoveryLog = AppendInfo(discoveryLog, $"Source query: {q}");
                if (matchedCodePaths.Count == 0)
                {
                    discoveryLog = AppendWarning(discoveryLog,
                        $"Source discovery for '{node.Path}' matched 0 Code nodes — " +
                        "check that the Source Code nodes exist and the NodeType's " +
                        "`Sources` list points at them.");
                }
                else
                {
                    discoveryLog = AppendInfo(discoveryLog,
                        $"Source discovery matched {matchedCodePaths.Count} Code node(s): " +
                        string.Join(", ", matchedCodePaths));
                }

                return Observable.Defer(() =>
                        CompileAsync(codeFile, configuration, contentCollections, node, CancellationToken.None)
                            .ToObservable())
                    .Select(actualPath =>
                    {
                        ActivityLog finalLog;
                        string? finalPath;
                        if (cacheService.IsDiskCacheEnabled)
                        {
                            if (actualPath != null && File.Exists(actualPath))
                            {
                                logger.LogDebug(
                                    "Compiled assembly for node {NodePath} at {DllPath}",
                                    node.Path, actualPath);
                                finalPath = actualPath;
                                finalLog = AppendInfo(discoveryLog,
                                    $"Compiled assembly written to {actualPath}.");
                            }
                            else
                            {
                                logger.LogWarning(
                                    "Assembly compilation succeeded but DLL not found at {DllPath}", actualPath);
                                finalPath = null;
                                finalLog = AppendError(discoveryLog,
                                    $"Compilation succeeded but DLL not found at {actualPath}.");
                            }
                        }
                        else
                        {
                            logger.LogDebug("Compiled assembly for node {NodePath} (in-memory)", node.Path);
                            finalPath = actualPath;
                            finalLog = AppendInfo(discoveryLog,
                                $"Compiled assembly loaded in-memory ({actualPath}).");
                        }
                        return (finalPath, finalLog.Finish((int)hub.Version, ActivityStatus.Succeeded));
                    })
                    .Catch<(string?, ActivityLog), CompilationException>(ex =>
                    {
                        var diag = BuildSourceDiscoveryReport(executedQueries, matchedCodePaths);
                        logger.LogError(ex, "Failed to compile assembly for node {NodePath}. {Diagnostics}",
                            node.Path, diag);
                        var failedLog = AppendError(discoveryLog,
                                $"Compilation failed: {ex.Message}\n--- Source discovery ---\n{diag}")
                            .Finish((int)hub.Version, ActivityStatus.Failed);
                        return Observable.Return<(string?, ActivityLog)>((null, failedLog));
                    });
            });
    }

    private static string BuildSourceDiscoveryReport(IReadOnlyList<string> executedQueries, IReadOnlyList<string> matchedCodePaths)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Executed source queries ({executedQueries.Count}):");
        foreach (var q in executedQueries)
            sb.AppendLine($"  - {q}");
        sb.AppendLine($"Matched Code nodes ({matchedCodePaths.Count}):");
        if (matchedCodePaths.Count == 0)
            sb.AppendLine("  (none) — the configuration lambda cannot reference types because no source files were included. Check that your Source Code nodes exist and that the NodeType's `sources` list points at them.");
        else
            foreach (var p in matchedCodePaths)
                sb.AppendLine($"  - {p}");
        return sb.ToString();
    }

    /// <inheritdoc />
    public IObservable<NodeCompilationResult?> CompileAndGetConfigurations(
        MeshNode node,
        IReadOnlyList<MeshNode>? sourcesOverride = null)
        => GetAssemblyLocationWithLog(node, sourcesOverride).SelectMany(t =>
        {
            var (assemblyLocation, log) = t;
            if (string.IsNullOrEmpty(assemblyLocation))
                return Observable.Return((NodeCompilationResult?)new NodeCompilationResult(null, [], log));

            // Capture the per-source version snapshot AFTER the compile resolved
            // its source set so the snapshot reflects the same storage enumeration
            // the cache check uses. Compose via SelectMany so the observable chain
            // stays reactive (no Task bridges, no .Result deadlocks).
            var ntDef = node.Content as NodeTypeDefinition;
            var selfPath = ntDef != null ? node.Path : node.NodeType ?? node.Path;
            return DiscoverSourceVersionSnapshot(ntDef, selfPath ?? "", sourcesOverride)
                .Select(snapshot => CompileResultFromAssembly(node, assemblyLocation, log, snapshot));
        });

    /// <inheritdoc />
    public IObservable<NodeCompilationResult?> GetConfigurationsFromExistingAssembly(MeshNode node)
    {
        var assemblyLocation = node.AssemblyLocation;
        if (string.IsNullOrEmpty(assemblyLocation))
            return Observable.Return((NodeCompilationResult?)null);

        var log = new ActivityLog(ActivityCategory.Compilation) { HubPath = node.Path };

        // No source-version snapshot here — this path is taken on the instance hub's
        // GetCompilationPathRequest hot path, where DiscoverSourceVersionSnapshot's
        // SyncedQuery enumeration has timed out under load (see CodeEditRecompileTest
        // failures). The snapshot only matters for HandleCreateRelease's
        // "is up to date?" check; once the assembly exists, the instance hub just
        // needs the HubConfiguration to render. Pass empty so the result still has
        // a non-null CompiledSources dict for downstream consumers.
        return Observable.Return<NodeCompilationResult?>(
            CompileResultFromAssembly(node, assemblyLocation, log,
                ImmutableDictionary<string, long>.Empty));
    }

    private NodeCompilationResult? CompileResultFromAssembly(
        MeshNode node, string assemblyLocation, ActivityLog log,
        ImmutableDictionary<string, long> compiledSources)
    {

            var nodeName = cacheService.SanitizeNodeName(node.Path);

            try
            {
                // Load from the exact path recorded on the node, not from the shared
                // GetDllPath(nodeName) shorthand. Each release writes to a unique subdir
                // so V1 and V2 ALCs are separate; loading from the canonical shared path
                // would always return V1's assembly after V2 compiles. In-memory
                // assemblies keep the old keyed-by-nodeName path.
                var assembly = assemblyLocation.StartsWith("memory://", StringComparison.Ordinal)
                    ? cacheService.LoadAssembly(nodeName)
                    : cacheService.GetOrCreateLoadContextForPath(nodeName, assemblyLocation).LoadNodeAssembly();
                if (assembly == null)
                {
                    // Promoted from Warning → Error: this is the root cause that
                    // cascades into every downstream "SubscribeRequest timed out"
                    // for hubs of this NodeType. Log noise from the cascade was
                    // hiding this single offender — make it stand out so the
                    // operator sees the cause, not the symptoms.
                    logger.LogError(
                        "Failed to load assembly for {NodePath} — the per-node hub for this " +
                        "NodeType (and every instance of it) cannot activate. Subscribe / GetData " +
                        "calls to its grains will time out. Common causes: corrupt cached .dll " +
                        "(delete .mesh-cache to force recompile), source compilation error " +
                        "(check the Code node's diagnostics), or missing dependency.",
                        node.Path);
                    return new NodeCompilationResult(assemblyLocation, [],
                        AppendError(log, $"Failed to load assembly at {assemblyLocation}."),
                        compiledSources);
                }

                var configurations = new List<NodeTypeConfiguration>();
                foreach (var type in assembly.GetTypes())
                {
                    if (typeof(MeshNodeProviderAttribute).IsAssignableFrom(type) && !type.IsAbstract)
                    {
                        var attribute = (MeshNodeProviderAttribute?)Activator.CreateInstance(type);
                        if (attribute != null)
                        {
                            foreach (var meshNode in attribute.Nodes)
                            {
                                var hubConfig = meshNode.HubConfiguration;
                                if (hubConfig != null)
                                {
                                    configurations.Add(new NodeTypeConfiguration
                                    {
                                        NodeType = meshNode.Path,
                                        DataType = typeof(object),
                                        HubConfiguration = hubConfig,
                                        DisplayName = meshNode.Name,
                                        Icon = meshNode.Icon,
                                    });
                                }
                            }
                        }
                    }
                }

                logger.LogDebug("Extracted {Count} NodeTypeConfigurations from {AssemblyLocation}",
                    configurations.Count, assemblyLocation);

                return new NodeCompilationResult(assemblyLocation, configurations, log, compiledSources);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to extract NodeTypeConfigurations from {AssemblyLocation}", assemblyLocation);
                return new NodeCompilationResult(assemblyLocation, [],
                    AppendError(log, $"Failed to extract configurations: {ex.Message}"),
                    compiledSources);
            }
    }

    /// <summary>
    /// Compiles CodeConfiguration into an assembly using Roslyn.
    /// Supports both disk-based and in-memory compilation.
    /// </summary>
    private async Task<string?> CompileAsync(
        CodeConfiguration? codeFile,
        string? hubConfiguration,
        IReadOnlyList<ContentCollectionConfig>? contentCollections,
        MeshNode node,
        CancellationToken ct)
    {
        var nodeName = cacheService.SanitizeNodeName(node.Path);
        var sem = _compileLocks.GetOrAdd(nodeName, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            return await CompileAsyncCore(codeFile, hubConfiguration, contentCollections, node, nodeName, ct);
        }
        finally
        {
            sem.Release();
        }
    }

    private async Task<string?> CompileAsyncCore(
        CodeConfiguration? codeFile,
        string? hubConfiguration,
        IReadOnlyList<ContentCollectionConfig>? contentCollections,
        MeshNode node,
        string nodeName,
        CancellationToken ct)
    {
        if (cacheService.IsDiskCacheEnabled)
        {
            cacheService.EnsureCacheDirectoryExists();
        }

        ct.ThrowIfCancellationRequested();

        // Generate full source with MeshNodeProviderAttribute (including content collections)
        var rawSource = _attributeGenerator.GenerateAttributeSource(node, codeFile, hubConfiguration, contentCollections);

        // Strip #r "nuget:..." directives — Roslyn compilation (unlike scripting) does not process them.
        var (source, nugetRefs) = NuGetDirectiveParser.Extract(rawSource);
        IEnumerable<MetadataReference> references = _references;
        if (nugetRefs.Length > 0)
        {
            var resolved = await nugetResolver.ResolveAsync(nugetRefs, targetFramework: null, ct);
            references = _references.Concat(
                resolved.AssemblyPaths.Select(p => MetadataReference.CreateFromFile(p)));
            cacheService.RegisterProbingDirectories(nodeName, resolved.ProbingDirectories);
        }

        // Write source file for debugging (only for disk cache)
        var sourcePath = cacheService.GetSourcePath(nodeName);
        if (cacheService.IsDiskCacheEnabled && _cacheOptions.EnableSourceDebugging)
        {
            await File.WriteAllTextAsync(sourcePath, source, ct);
            logger.LogDebug("Wrote source file for debugging: {SourcePath}", sourcePath);
        }

        logger.LogInformation("Compiling assembly for {NodeName} ({Mode}, {NuGetRefs} NuGet refs)",
            nodeName, cacheService.IsDiskCacheEnabled ? "disk" : "in-memory", nugetRefs.Length);

        // Parse with source path and encoding embedded (critical for PDB source linking)
        var sourceText = Microsoft.CodeAnalysis.Text.SourceText.From(source, System.Text.Encoding.UTF8);
        var parseOptions = new CSharpParseOptions(documentationMode: DocumentationMode.Diagnose);
        var syntaxTree = CSharpSyntaxTree.ParseText(
            sourceText,
            parseOptions,
            path: cacheService.IsDiskCacheEnabled && _cacheOptions.EnableSourceDebugging ? sourcePath : "",
            cancellationToken: ct);

        var assemblyName = $"DynamicNode_{nodeName}";

        var compilation = CSharpCompilation.Create(
            assemblyName,
            syntaxTrees: [syntaxTree],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithOptimizationLevel(OptimizationLevel.Debug)
                .WithPlatform(Platform.AnyCpu));

        string? actualPath;
        if (cacheService.IsDiskCacheEnabled)
        {
            actualPath = await CompileToDiskAsync(compilation, nodeName, node.Path, ct);
        }
        else
        {
            CompileToMemory(compilation, nodeName, node.Path, ct);
            actualPath = $"memory://{nodeName}";
        }

        logger.LogInformation("Successfully compiled assembly for {NodePath} to {ActualPath}", node.Path, actualPath);
        return actualPath;
    }

    /// <summary>
    /// Compiles and emits assembly to a unique per-compile subdirectory.
    /// Each compile writes to {cacheDir}/{nodeName}_{ticks_hex}/ so V1 and V2
    /// DLLs coexist on disk without overwriting — no file-lock races, no hash
    /// collisions in TryCreateReleaseNode.
    /// </summary>
    private async Task<string> CompileToDiskAsync(CSharpCompilation compilation, string nodeName, string nodePath, CancellationToken ct)
    {
        var timestamp = DateTimeOffset.UtcNow.Ticks.ToString("x");
        var releaseDir = Path.Combine(cacheService.CacheDirectory, $"{nodeName}_{timestamp}");
        Directory.CreateDirectory(releaseDir);
        var dllPath = Path.Combine(releaseDir, $"{nodeName}.dll");
        var pdbPath = Path.Combine(releaseDir, $"{nodeName}.pdb");
        var xmlDocPath = Path.Combine(releaseDir, $"DynamicNode_{nodeName}.xml");

        await using var dllStream = File.Create(dllPath);
        await using var pdbStream = File.Create(pdbPath);
        await using var xmlDocStream = File.Create(xmlDocPath);

        var emitOptions = new EmitOptions(
            debugInformationFormat: DebugInformationFormat.PortablePdb,
            pdbFilePath: pdbPath);

        var emitResult = compilation.Emit(dllStream, pdbStream, xmlDocumentationStream: xmlDocStream, options: emitOptions, cancellationToken: ct);

        if (!emitResult.Success)
        {
            var errors = emitResult.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.GetMessage())
                .ToList();

            var errorMessage = $"Compilation failed for '{nodePath}':\n{string.Join('\n', errors)}";
            logger.LogError("{ErrorMessage}", errorMessage);
            throw new CompilationException(nodePath, errorMessage);
        }

        // Close streams before returning the path
        await dllStream.DisposeAsync();
        await pdbStream.DisposeAsync();
        await xmlDocStream.DisposeAsync();

        return dllPath;
    }

    /// <summary>
    /// Compiles and loads assembly directly to memory (no disk I/O).
    /// </summary>
    private void CompileToMemory(CSharpCompilation compilation, string nodeName, string nodePath, CancellationToken ct)
    {
        using var dllStream = new MemoryStream();
        using var pdbStream = new MemoryStream();

        var emitOptions = new EmitOptions(
            debugInformationFormat: DebugInformationFormat.PortablePdb);

        var emitResult = compilation.Emit(dllStream, pdbStream, options: emitOptions, cancellationToken: ct);

        if (!emitResult.Success)
        {
            var errors = emitResult.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.GetMessage())
                .ToList();

            var errorMessage = $"Compilation failed for '{nodePath}':\n{string.Join('\n', errors)}";
            logger.LogError("{ErrorMessage}", errorMessage);
            throw new CompilationException(nodePath, errorMessage);
        }

        // Load assembly from bytes immediately
        var assemblyBytes = dllStream.ToArray();
        var pdbBytes = pdbStream.ToArray();
        cacheService.LoadAssemblyFromBytes(nodeName, assemblyBytes, pdbBytes);
    }

    /// <summary>
    /// Compiles a node type to a specific release folder.
    /// This method is thread-safe and multi-process safe when used with CompilationLock.
    /// The caller is responsible for acquiring the lock before calling this method.
    /// </summary>
    /// <param name="release">The NodeTypeRelease containing all compilation inputs.</param>
    /// <param name="node">The MeshNode being compiled.</param>
    /// <param name="releaseFolder">Target folder for the compiled assembly.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Compilation result with assembly location and configurations.</returns>
    internal async Task<NodeCompilationResult?> CompileToReleaseAsync(
        NodeTypeRelease release,
        MeshNode node,
        string releaseFolder,
        CancellationToken ct = default)
    {
        var sanitizedPath = release.GetSanitizedPath();

        logger.LogInformation("Compiling {NodePath} to release folder {ReleaseFolder}", node.Path, releaseFolder);

        // Ensure release folder exists
        Directory.CreateDirectory(releaseFolder);

        var dllPath = Path.Combine(releaseFolder, $"{sanitizedPath}.dll");
        var pdbPath = Path.Combine(releaseFolder, $"{sanitizedPath}.pdb");
        var sourcePath = Path.Combine(releaseFolder, $"{sanitizedPath}.cs");
        var xmlDocPath = Path.Combine(releaseFolder, $"{sanitizedPath}.xml");

        ct.ThrowIfCancellationRequested();

        // Generate source code
        var codeConfig = string.IsNullOrEmpty(release.Code) ? null : new CodeConfiguration { Code = release.Code };
        var rawSource = _attributeGenerator.GenerateAttributeSource(node, codeConfig, release.HubConfiguration, release.ContentCollections);

        // Strip #r "nuget:..." directives — Roslyn compilation (unlike scripting) does not process them.
        var (source, nugetRefs) = NuGetDirectiveParser.Extract(rawSource);
        IEnumerable<MetadataReference> references = _references;
        IReadOnlyList<string> probingDirs = [];
        if (nugetRefs.Length > 0)
        {
            var resolved = await nugetResolver.ResolveAsync(nugetRefs, targetFramework: null, ct);
            references = _references.Concat(
                resolved.AssemblyPaths.Select(p => MetadataReference.CreateFromFile(p)));
            probingDirs = resolved.ProbingDirectories;
        }

        // Write source file for debugging
        if (_cacheOptions.EnableSourceDebugging)
        {
            await File.WriteAllTextAsync(sourcePath, source, ct);
            logger.LogDebug("Wrote source file: {SourcePath}", sourcePath);
        }

        // Parse with source path embedded for PDB source linking
        var sourceText = Microsoft.CodeAnalysis.Text.SourceText.From(source, System.Text.Encoding.UTF8);
        var parseOptions = new CSharpParseOptions(documentationMode: DocumentationMode.Diagnose);
        var syntaxTree = CSharpSyntaxTree.ParseText(
            sourceText,
            parseOptions,
            path: _cacheOptions.EnableSourceDebugging ? sourcePath : "",
            cancellationToken: ct);

        var assemblyName = sanitizedPath;

        var compilation = CSharpCompilation.Create(
            assemblyName,
            syntaxTrees: [syntaxTree],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithOptimizationLevel(OptimizationLevel.Debug)
                .WithPlatform(Platform.AnyCpu));

        // Emit to release folder
        await using var dllStream = File.Create(dllPath);
        await using var pdbStream = File.Create(pdbPath);
        await using var xmlDocStream = File.Create(xmlDocPath);

        var emitOptions = new EmitOptions(
            debugInformationFormat: DebugInformationFormat.PortablePdb,
            pdbFilePath: pdbPath);

        var emitResult = compilation.Emit(dllStream, pdbStream, xmlDocumentationStream: xmlDocStream, options: emitOptions, cancellationToken: ct);

        if (!emitResult.Success)
        {
            // Clean up partial files on failure
            await dllStream.DisposeAsync();
            await pdbStream.DisposeAsync();
            await xmlDocStream.DisposeAsync();

            try { Directory.Delete(releaseFolder, recursive: true); } catch { /* ignore */ }

            var errors = emitResult.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.GetMessage())
                .ToList();

            var errorMessage = $"Compilation failed for '{node.Path}':\n{string.Join('\n', errors)}";
            logger.LogError("{ErrorMessage}", errorMessage);
            throw new CompilationException(node.Path, errorMessage);
        }

        // Close streams before writing metadata
        await dllStream.DisposeAsync();
        await pdbStream.DisposeAsync();
        await xmlDocStream.DisposeAsync();

        // Write the NodeTypeRelease as release.json (contains all metadata)
        var metadataPath = Path.Combine(releaseFolder, "release.json");
        var metadataJson = JsonSerializer.Serialize(release, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(metadataPath, metadataJson, ct);

        // Persist NuGet probing directories alongside the release so the load context
        // can probe for transitive dependencies at load time.
        if (probingDirs.Count > 0)
        {
            var probingPath = Path.Combine(releaseFolder, "probing.json");
            var probingJson = JsonSerializer.Serialize(probingDirs);
            await File.WriteAllTextAsync(probingPath, probingJson, ct);
        }

        logger.LogInformation("Successfully compiled {NodePath} to {DllPath}", node.Path, dllPath);

        // Load and extract configurations
        return await LoadAndExtractConfigurationsFromReleaseAsync(release, releaseFolder, ct);
    }

    /// <summary>
    /// Loads an assembly from a release folder and extracts NodeTypeConfigurations.
    /// </summary>
    internal async Task<NodeCompilationResult?> LoadAndExtractConfigurationsFromReleaseAsync(
        NodeTypeRelease release,
        string releaseFolder,
        CancellationToken _)
    {
        var sanitizedPath = release.GetSanitizedPath();
        var dllPath = Path.Combine(releaseFolder, $"{sanitizedPath}.dll");

        try
        {
            var assembly = cacheService.LoadAssemblyFromRelease(release, releaseFolder);
            if (assembly == null)
            {
                logger.LogWarning("Failed to load assembly from {DllPath}", dllPath);
                return new NodeCompilationResult(dllPath, []);
            }

            var configurations = new List<NodeTypeConfiguration>();
            foreach (var type in assembly.GetTypes())
            {
                if (typeof(MeshNodeProviderAttribute).IsAssignableFrom(type) && !type.IsAbstract)
                {
                    var attribute = (MeshNodeProviderAttribute?)Activator.CreateInstance(type);
                    if (attribute != null)
                    {
                        foreach (var meshNode in attribute.Nodes)
                        {
                            var hubConfig = meshNode.HubConfiguration;
                            if (hubConfig != null)
                            {
                                configurations.Add(new NodeTypeConfiguration
                                {
                                    NodeType = meshNode.Path,
                                    DataType = typeof(object),
                                    HubConfiguration = hubConfig,
                                    DisplayName = meshNode.Name,
                                    Icon = meshNode.Icon,
                                });
                            }
                        }
                    }
                }
            }

            logger.LogDebug("Extracted {Count} NodeTypeConfigurations from {DllPath}", configurations.Count, dllPath);
            return new NodeCompilationResult(dllPath, configurations);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to extract NodeTypeConfigurations from {DllPath}", dllPath);
            return new NodeCompilationResult(dllPath, []);
        }
    }
}

/// <summary>
/// Exception thrown when compilation fails.
/// </summary>
public class CompilationException : Exception
{
    public string NodePath { get; }

    public CompilationException(string nodePath, string message)
        : base(message)
    {
        NodePath = nodePath;
    }

    public CompilationException(string nodePath, string message, Exception innerException)
        : base(message, innerException)
    {
        NodePath = nodePath;
    }
}
