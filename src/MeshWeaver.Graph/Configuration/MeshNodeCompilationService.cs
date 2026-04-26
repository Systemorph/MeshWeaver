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

    // Query expansion lives in CodeQueryResolver now so the NodeType Configuration
    // side menu can evaluate the *same* queries the compiler uses — the Sources /
    // Tests lists displayed in the UI are guaranteed to match the files compiled.
    private readonly List<MetadataReference> _references = GetDefaultReferences();

    private static List<MetadataReference> GetDefaultReferences()
    {
        var references = new List<MetadataReference>();

        // Add runtime assemblies
        var trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (trustedAssemblies != null)
        {
            foreach (var path in trustedAssemblies.Split(Path.PathSeparator))
            {
                if (File.Exists(path))
                {
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
        }

        // Also add specific assemblies we need
        var additionalAssemblies = new[]
        {
            typeof(object).Assembly,                                           // System.Runtime
            typeof(System.ComponentModel.DataAnnotations.KeyAttribute).Assembly, // DataAnnotations
            typeof(System.Text.Json.Serialization.JsonPropertyNameAttribute).Assembly, // System.Text.Json
        };

        foreach (var assembly in additionalAssemblies)
        {
            if (!string.IsNullOrEmpty(assembly.Location) && File.Exists(assembly.Location))
            {
                try
                {
                    var reference = MetadataReference.CreateFromFile(assembly.Location);
                    if (!references.Any(r => r.Display == assembly.Location))
                        references.Add(reference);
                }
                catch
                {
                    // Skip if already added or can't be loaded
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
    /// Resolves @@path references in code content by fetching the referenced node's CodeConfiguration.
    /// For example, @@FutuRe/LineOfBusiness/Source/LineOfBusiness resolves to that node's code content.
    /// Resolution is transitive: if a resolved include itself contains @@references, those are resolved too.
    /// </summary>
    internal async Task<string> ResolveCodeIncludesAsync(
        string code, IMeshService meshQuery, CancellationToken ct)
    {
        var resolved = new HashSet<string>();
        return await ResolveCodeIncludesAsync(code, meshQuery, resolved, ct);
    }

    /// <summary>
    /// Overload that uses IMeshStorage directly to bypass routing.
    /// Used by NodeTypeService during compilation to avoid circular hub creation.
    /// </summary>
    internal async Task<string> ResolveCodeIncludesAsync(
        string code, IMeshStorage meshStorage, CancellationToken ct)
    {
        var resolved = new HashSet<string>();
        return await ResolveCodeIncludesAsync(code, meshStorage, resolved, ct);
    }

    private IObservable<string> ResolveCodeIncludes(
        string code, IMeshService meshQuery, HashSet<string> resolved)
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
                            return ResolveCodeIncludes(cf.Code, meshQuery, resolved)
                                .Select(resolvedInner => current.Replace(matchValue, resolvedInner));
                        }
                        logger.LogWarning("Could not resolve code include @@{Path}", path);
                        return Observable.Return(current);
                    });
            });
        }

        return chain;
    }

    [Obsolete("Bridge for the storage-only path; production callers use ResolveCodeIncludes (IObservable).")]
    private async Task<string> ResolveCodeIncludesAsync(
        string code, IMeshService meshQuery, HashSet<string> resolved, CancellationToken ct)
        => await ResolveCodeIncludes(code, meshQuery, resolved).ToTask(ct);

    private async Task<string> ResolveCodeIncludesAsync(
        string code, IMeshStorage meshStorage, HashSet<string> resolved, CancellationToken ct)
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

            // Use IMeshStorage directly to bypass routing
            var referencedNode = await meshStorage.GetNode(path).FirstAsync().ToTask(ct);
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
    {
        if (string.IsNullOrEmpty(node.NodeType))
        {
            logger.LogDebug("Node {NodePath} has no NodeType, skipping assembly compilation", node.Path);
            return Observable.Return<string?>(null);
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
            // compiled DLL is newer than the most recent source change. Without this
            // check, editing a Source/*.cs node would not trigger recompilation
            // because the NodeType node's own LastModified hasn't changed.
            DiscoverSourceMaxLastModified(ntDef, selfPath)
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
                        return Observable.Return<string?>(dllPath);
                    }

                    return CompileCore(node, ntDef, selfPath);
                }));
    }

    /// <summary>
    /// Returns the maximum <c>LastModified</c> across all source Code nodes that
    /// would feed a compile of <paramref name="ntDef"/>. Uses the same storage
    /// enumeration as <see cref="CompileCore"/> so cache invalidation tracks the
    /// exact same set of files the compile reads. Returns
    /// <see cref="DateTimeOffset.MinValue"/> if there are no sources.
    /// </summary>
    private IObservable<DateTimeOffset> DiscoverSourceMaxLastModified(
        NodeTypeDefinition? ntDef, string selfPath)
    {
        var meshStorage = hub.ServiceProvider.GetService<IMeshStorage>();
        if (meshStorage == null)
            return Observable.Return(DateTimeOffset.MinValue);

        var queries = CodeQueryResolver
            .ExpandAll(ntDef?.Sources, CodeQueryResolver.DefaultSources, selfPath)
            .Concat(CodeQueryResolver.ExpandAll(ntDef?.Tests, CodeQueryResolver.DefaultTests, selfPath))
            .ToArray();

        if (queries.Length == 0)
            return Observable.Return(DateTimeOffset.MinValue);

        IObservable<DateTimeOffset> chain = Observable.Return(DateTimeOffset.MinValue);
        foreach (var q in queries)
        {
            var (queryPath, isSubtree, queryNodeType) = ParseSourceQuery(q);
            chain = chain.SelectMany(currentMax =>
                EnumerateStorageSources(meshStorage, queryPath, isSubtree, queryNodeType)
                    .Aggregate(currentMax, (acc, n) => n.LastModified > acc ? n.LastModified : acc));
        }
        return chain;
    }

    /// <summary>
    /// Parses a <see cref="CodeQueryResolver"/>-shaped query string into the
    /// (path, scope, nodeType) tuple needed for storage-direct enumeration.
    /// CodeQueryResolver always emits one of three shapes:
    /// <list type="bullet">
    ///   <item><description><c>namespace:X scope:subtree nodeType:Y</c></description></item>
    ///   <item><description><c>path:X nodeType:Y</c></description></item>
    ///   <item><description><c>{namespace:X scope:subtree} nodeType:Y</c></description></item>
    /// </list>
    /// </summary>
    private static (string Path, bool IsSubtree, string? NodeType) ParseSourceQuery(string query)
    {
        string path = "";
        bool isSubtree = false;
        string? nodeType = null;
        foreach (var token in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith("namespace:", StringComparison.Ordinal))
            {
                path = token["namespace:".Length..];
                isSubtree = true;
            }
            else if (token.StartsWith("path:", StringComparison.Ordinal))
            {
                path = token["path:".Length..];
            }
            else if (token.Equals("scope:subtree", StringComparison.Ordinal)
                     || token.Equals("scope:descendants", StringComparison.Ordinal))
            {
                isSubtree = true;
            }
            else if (token.StartsWith("nodeType:", StringComparison.Ordinal))
            {
                nodeType = token["nodeType:".Length..];
            }
        }
        return (path, isSubtree, nodeType);
    }

    /// <summary>
    /// Enumerates source nodes via <see cref="IMeshStorage"/> directly.
    /// Storage is the canonical source of truth; the read-side query index is
    /// eventually consistent and may lag fresh writes (see
    /// <c>Doc/Architecture/CqrsAndContentAccess.md</c>) — the compile pipeline
    /// is one of the two sanctioned places to read storage directly.
    /// </summary>
    private static IObservable<MeshNode> EnumerateStorageSources(
        IMeshStorage storage, string path, bool isSubtree, string? nodeType)
    {
        if (string.IsNullOrEmpty(path))
            return Observable.Empty<MeshNode>();

        var self = storage.GetNode(path)
            .Where(n => n != null
                && (nodeType == null
                    || string.Equals(n.NodeType, nodeType, StringComparison.Ordinal)))
            .Select(n => n!);

        if (!isSubtree)
            return self;

        var descendants = Observable.Create<MeshNode>(async (observer, ct) =>
        {
            try
            {
                await foreach (var d in storage.GetAllDescendantsAsync(path).WithCancellation(ct))
                {
                    if (nodeType == null
                        || string.Equals(d.NodeType, nodeType, StringComparison.Ordinal))
                        observer.OnNext(d);
                }
                observer.OnCompleted();
            }
            catch (Exception ex)
            {
                observer.OnError(ex);
            }
        });

        return self.Concat(descendants);
    }

    /// <summary>
    /// IObservable end-to-end. The only Task→Observable bridge in this method is the
    /// Roslyn compile call itself — every other leaf already exposes an IObservable
    /// surface (<c>meshService.ObserveQuery</c> for source discovery,
    /// <c>ResolveCodeIncludes</c> for @@ resolution). No <c>Observable.FromAsync</c>,
    /// no <c>await</c> on hub round-trips — both are the canonical deadlock patterns
    /// documented in <c>Doc/Architecture/AsynchronousCalls.md</c>.
    /// </summary>
    private IObservable<string?> CompileCore(
        MeshNode node, NodeTypeDefinition? ntDef, string selfPath)
    {
        var nodeName = cacheService.SanitizeNodeName(node.Path);
        var dllPath = cacheService.GetDllPath(nodeName);
        var meshQuery = hub.ServiceProvider.GetService<IMeshService>();
        var meshStorage = hub.ServiceProvider.GetService<IMeshStorage>();
        var executedQueries = new List<string>();
        var matchedCodePaths = new List<string>();

        IObservable<List<CodeConfiguration>> discoverCodeFiles;
        if (meshStorage == null)
        {
            discoverCodeFiles = Observable.Return(new List<CodeConfiguration>());
        }
        else
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queriesToRun = CodeQueryResolver
                .ExpandAll(ntDef?.Sources, CodeQueryResolver.DefaultSources, selfPath)
                .Concat(CodeQueryResolver.ExpandAll(ntDef?.Tests, CodeQueryResolver.DefaultTests, selfPath))
                .ToArray();

            // Source discovery: enumerate via IMeshStorage directly. The read-side
            // query index is eventually consistent (see Doc/Architecture/CqrsAndContentAccess.md);
            // a Code node written milliseconds before the compile request may not be
            // visible through ObserveQuery yet. Storage is the canonical source of
            // truth and is one of the two sanctioned places to read directly from.
            // Wrapped in Observable.Create so the IAsyncEnumerable enumeration is the
            // leaf — the chain stays IObservable end-to-end above this point.
            IObservable<List<CodeConfiguration>> chain = Observable.Return(new List<CodeConfiguration>());
            foreach (var finalQuery in queriesToRun)
            {
                var q = finalQuery;
                executedQueries.Add(q);
                var (queryPath, isSubtree, queryNodeType) = ParseSourceQuery(q);

                chain = chain.SelectMany(acc =>
                    EnumerateStorageSources(meshStorage, queryPath, isSubtree, queryNodeType)
                        .ToList()
                        .Select(matches =>
                        {
                            var matched = 0;
                            foreach (var n in matches)
                            {
                                if (string.IsNullOrEmpty(n.Path) || !seen.Add(n.Path))
                                    continue;
                                if (n.Content is CodeConfiguration cf
                                    && !string.IsNullOrWhiteSpace(cf.Code))
                                {
                                    acc.Add(cf);
                                    matchedCodePaths.Add(n.Path);
                                    matched++;
                                }
                            }
                            logger.LogInformation(
                                "Source discovery for {NodePath}: query '{Query}' matched {Count} Code nodes",
                                node.Path, q, matched);
                            return acc;
                        }));
            }
            discoverCodeFiles = chain;
        }

        return discoverCodeFiles
            .SelectMany(codeFiles =>
            {
                // Stage: resolve @@ include references reactively. Each include lookup
                // composes via ResolveCodeIncludes (already an IObservable<string>). No await.
                if (meshQuery == null || codeFiles.Count == 0)
                    return Observable.Return(codeFiles);

                IObservable<List<CodeConfiguration>> includeChain =
                    Observable.Return(new List<CodeConfiguration>(codeFiles.Count));
                foreach (var codeFile in codeFiles)
                {
                    var cf = codeFile;
                    includeChain = includeChain.SelectMany(acc =>
                        ResolveCodeIncludes(cf.Code!, meshQuery, new HashSet<string>())
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
                // the only Task→Observable bridge in this whole method (see method-level
                // doc-comment). Wrapping in Observable.Defer makes the Task lazy so it
                // only runs on subscribe — composes safely inside the outer chain.
                CodeConfiguration? codeFile = codeFiles.Count switch
                {
                    0 => null,
                    1 => codeFiles[0],
                    _ => new CodeConfiguration { Code = string.Join("\n\n", codeFiles.Select(cf => cf.Code)) }
                };
                var configuration = ntDef?.Configuration;
                var contentCollections = ntDef?.ContentCollections;

                return Observable.Defer(() =>
                        CompileAsync(codeFile, configuration, contentCollections, node, CancellationToken.None)
                            .ToObservable())
                    .Select(_ =>
                    {
                        if (cacheService.IsDiskCacheEnabled)
                        {
                            if (File.Exists(dllPath))
                            {
                                logger.LogInformation(
                                    "Compiled assembly for node {NodePath} at {DllPath}",
                                    node.Path, dllPath);
                                return (string?)dllPath;
                            }
                            logger.LogWarning(
                                "Assembly compilation succeeded but DLL not found at {DllPath}", dllPath);
                            return (string?)null;
                        }
                        logger.LogInformation("Compiled assembly for node {NodePath} (in-memory)", node.Path);
                        return (string?)$"memory://{nodeName}";
                    })
                    .Catch<string?, CompilationException>(ex =>
                    {
                        // Enrich the error with the actual queries that ran + which Code nodes
                        // matched, so the error overlay can tell the user *why* references are
                        // missing (usually: 0 Code nodes matched the configured sources).
                        var diag = BuildSourceDiscoveryReport(executedQueries, matchedCodePaths);
                        logger.LogError(ex, "Failed to compile assembly for node {NodePath}. {Diagnostics}",
                            node.Path, diag);
                        return Observable.Throw<string?>(new CompilationException(
                            ex.NodePath,
                            $"{ex.Message}\n\n--- Source discovery ---\n{diag}",
                            ex));
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
    public IObservable<NodeCompilationResult?> CompileAndGetConfigurations(MeshNode node)
        => GetAssemblyLocation(node).Select(assemblyLocation =>
        {
            if (string.IsNullOrEmpty(assemblyLocation))
                return (NodeCompilationResult?)null;

            var nodeName = cacheService.SanitizeNodeName(node.Path);

            try
            {
                var assembly = cacheService.LoadAssembly(nodeName);
                if (assembly == null)
                {
                    logger.LogWarning("Failed to load assembly for {NodePath}", node.Path);
                    return new NodeCompilationResult(assemblyLocation, []);
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

                return new NodeCompilationResult(assemblyLocation, configurations);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to extract NodeTypeConfigurations from {AssemblyLocation}", assemblyLocation);
                return new NodeCompilationResult(assemblyLocation, []);
            }
        });

    /// <summary>
    /// Compiles CodeConfiguration into an assembly using Roslyn.
    /// Supports both disk-based and in-memory compilation.
    /// </summary>
    private async Task CompileAsync(
        CodeConfiguration? codeFile,
        string? hubConfiguration,
        IReadOnlyList<ContentCollectionConfig>? contentCollections,
        MeshNode node,
        CancellationToken ct)
    {
        var nodeName = cacheService.SanitizeNodeName(node.Path);

        // Invalidate old cache and prepare for recompilation
        cacheService.InvalidateCache(nodeName);

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

        if (cacheService.IsDiskCacheEnabled)
        {
            // Emit to disk
            await CompileToDiskAsync(compilation, nodeName, node.Path, ct);
        }
        else
        {
            // Emit to memory and load immediately
            CompileToMemory(compilation, nodeName, node.Path, ct);
        }

        // The preparatory InvalidateCache above set a sticky flag so the NEXT IsCacheValid
        // returns false; now that we've just written fresh artifacts, clear it so the
        // immediately following call short-circuits on the new DLL.
        cacheService.MarkCacheFresh(nodeName);

        logger.LogInformation("Successfully compiled assembly for {NodePath}", node.Path);
    }

    /// <summary>
    /// Compiles and emits assembly to disk.
    /// </summary>
    private async Task CompileToDiskAsync(CSharpCompilation compilation, string nodeName, string nodePath, CancellationToken ct)
    {
        var dllPath = cacheService.GetDllPath(nodeName);
        var pdbPath = cacheService.GetPdbPath(nodeName);
        var xmlDocPath = cacheService.GetXmlDocPath(nodeName);

        await using var dllStream = File.Create(dllPath);
        await using var pdbStream = File.Create(pdbPath);
        await using var xmlDocStream = File.Create(xmlDocPath);

        var emitOptions = new EmitOptions(
            debugInformationFormat: DebugInformationFormat.PortablePdb,
            pdbFilePath: pdbPath);

        var emitResult = compilation.Emit(dllStream, pdbStream, xmlDocumentationStream: xmlDocStream, options: emitOptions, cancellationToken: ct);

        if (!emitResult.Success)
        {
            cacheService.InvalidateCache(nodeName);

            var errors = emitResult.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.GetMessage())
                .ToList();

            var errorMessage = $"Compilation failed for '{nodePath}':\n{string.Join('\n', errors)}";
            logger.LogError("{ErrorMessage}", errorMessage);
            throw new CompilationException(nodePath, errorMessage);
        }

        // Close streams before loading
        await dllStream.DisposeAsync();
        await pdbStream.DisposeAsync();
        await xmlDocStream.DisposeAsync();
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
