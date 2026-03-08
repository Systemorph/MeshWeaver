using System.Text.Json;
using System.Text.RegularExpressions;
using MeshWeaver.ContentCollections;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
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
    ILogger<MeshNodeCompilationService> logger)
    : IMeshNodeCompilationService
{
    private readonly CompilationCacheOptions _cacheOptions = cacheOptions.Value ?? new CompilationCacheOptions();
    private JsonSerializerOptions JsonOptions => hub.JsonSerializerOptions;
    private readonly DynamicMeshNodeAttributeGenerator _attributeGenerator = new();
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
    /// For example, @@FutuRe/LineOfBusiness/Code/LineOfBusiness resolves to that node's code content.
    /// Resolution is transitive: if a resolved include itself contains @@references, those are resolved too.
    /// </summary>
    internal async Task<string> ResolveCodeIncludesAsync(
        string code, IMeshService meshQuery, CancellationToken ct)
    {
        var resolved = new HashSet<string>();
        return await ResolveCodeIncludesAsync(code, meshQuery, resolved, ct);
    }

    private async Task<string> ResolveCodeIncludesAsync(
        string code, IMeshService meshQuery, HashSet<string> resolved, CancellationToken ct)
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
                // Already resolved this path — remove the duplicate reference
                result = result.Replace(match.Value, string.Empty);
                continue;
            }

            string? resolvedCode = null;

            await foreach (var referencedNode in meshQuery
                               .QueryAsync<MeshNode>($"path:{path}", ct: ct)
                               .WithCancellation(ct))
            {
                if (referencedNode.Content is CodeConfiguration cf && !string.IsNullOrWhiteSpace(cf.Code))
                {
                    resolvedCode = cf.Code;
                }
                break;
            }

            if (resolvedCode != null)
            {
                logger.LogDebug("Resolved code include @@{Path}", path);
                // Transitively resolve nested includes
                resolvedCode = await ResolveCodeIncludesAsync(resolvedCode, meshQuery, resolved, ct);
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
    public async Task<string?> GetAssemblyLocationAsync(MeshNode node, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(node.NodeType))
        {
            logger.LogDebug("Node {NodePath} has no NodeType, skipping assembly compilation", node.Path);
            return null;
        }

        var nodeName = cacheService.SanitizeNodeName(node.Path);
        var dllPath = cacheService.GetDllPath(nodeName);

        // Check cache validity first (only for disk cache)
        if (cacheService.IsDiskCacheEnabled && cacheService.IsCacheValid(nodeName, node.LastModified))
        {
            logger.LogDebug("Using cached assembly for {NodePath}", node.Path);
            return dllPath;
        }

        // Get CodeConfiguration from child MeshNodes under the Code path
        // For NodeType nodes (where Content is NodeTypeDefinition), use the node's own path
        // For instance nodes, use the NodeType's path (e.g., "Person/Code" for Alice with NodeType="Person")
        // Collect ALL CodeConfiguration files and combine them
        var meshQuery = hub.ServiceProvider.GetService<IMeshService>();
        var codeFiles = new List<CodeConfiguration>();
        var codeParentPath = node.Content is NodeTypeDefinition
            ? $"{node.Path}/Code"    // NodeType node - use its own Code path
            : $"{node.NodeType}/Code"; // Instance node - use NodeType's Code path
        if (meshQuery != null)
        {
            var codeQuery = $"namespace:{codeParentPath}";
            await foreach (var codeNode in meshQuery.QueryAsync<MeshNode>(codeQuery, ct: ct).WithCancellation(ct))
            {
                if (codeNode.Content is CodeConfiguration cf && !string.IsNullOrWhiteSpace(cf.Code))
                {
                    codeFiles.Add(cf);
                }
            }
        }

        // Resolve @@ include references in code files (e.g., @@FutuRe/LineOfBusiness/Code/LineOfBusiness)
        if (meshQuery != null)
        {
            for (int i = 0; i < codeFiles.Count; i++)
            {
                var resolved = await ResolveCodeIncludesAsync(codeFiles[i].Code!, meshQuery, ct);
                if (resolved != codeFiles[i].Code)
                    codeFiles[i] = codeFiles[i] with { Code = resolved };
            }
        }

        // Combine all code files into a single CodeConfiguration
        CodeConfiguration? codeFile = codeFiles.Count switch
        {
            0 => null,
            1 => codeFiles[0],
            _ => new CodeConfiguration { Code = string.Join("\n\n", codeFiles.Select(cf => cf.Code)) }
        };

        // Get Configuration and ContentCollections from the NodeTypeDefinition content
        // Configuration is the source code that gets compiled into HubConfiguration
        // For NodeType nodes (where node.Content is NodeTypeDefinition), use the node's own content
        // For instance nodes, look up the NodeType node to get its Configuration
        string? configuration = null;
        List<ContentCollectionConfig>? contentCollections = null;
        if (node.Content is NodeTypeDefinition selfDef)
        {
            // Node is itself a NodeType definition - use its own Configuration
            configuration = selfDef.Configuration;
            contentCollections = selfDef.ContentCollections;
        }
        else if (meshQuery != null)
        {
            // Instance node - look up the NodeType to get its Configuration
            await foreach (var nodeTypeNode in meshQuery.QueryAsync<MeshNode>($"path:{node.NodeType}", ct: ct).WithCancellation(ct))
            {
                if (nodeTypeNode?.Content is NodeTypeDefinition ntd)
                {
                    configuration = ntd.Configuration;
                    contentCollections = ntd.ContentCollections;
                }
                break;
            }
        }

        try
        {
            // Compile using CodeConfiguration, Configuration, and ContentCollections
            await CompileAsync(codeFile, configuration, contentCollections, node, ct);

            // For disk cache, return the DLL path if it exists
            if (cacheService.IsDiskCacheEnabled)
            {
                if (File.Exists(dllPath))
                {
                    logger.LogInformation(
                        "Compiled assembly for node {NodePath} at {DllPath}",
                        node.Path, dllPath);
                    return dllPath;
                }

                logger.LogWarning("Assembly compilation succeeded but DLL not found at {DllPath}", dllPath);
                return null;
            }

            // For in-memory cache, return a virtual path (assembly is already loaded)
            logger.LogInformation("Compiled assembly for node {NodePath} (in-memory)", node.Path);
            return $"memory://{nodeName}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to compile assembly for node {NodePath}", node.Path);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<NodeCompilationResult?> CompileAndGetConfigurationsAsync(MeshNode node, CancellationToken ct = default)
    {
        var assemblyLocation = await GetAssemblyLocationAsync(node, ct);
        if (string.IsNullOrEmpty(assemblyLocation))
            return null;

        var nodeName = cacheService.SanitizeNodeName(node.Path);

        try
        {
            // Load assembly using isolated context
            var assembly = cacheService.LoadAssembly(nodeName);
            if (assembly == null)
            {
                logger.LogWarning("Failed to load assembly for {NodePath}", node.Path);
                return new NodeCompilationResult(assemblyLocation, []);
            }

            // Extract NodeTypeConfigurations from MeshNodeProviderAttribute.Nodes
            var configurations = new List<NodeTypeConfiguration>();
            foreach (var type in assembly.GetTypes())
            {
                if (typeof(MeshNodeProviderAttribute).IsAssignableFrom(type) && !type.IsAbstract)
                {
                    var attribute = (MeshNodeProviderAttribute?)Activator.CreateInstance(type);
                    if (attribute != null)
                    {
                        // Extract configurations from Nodes property
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
    }

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
        var source = _attributeGenerator.GenerateAttributeSource(node, codeFile, hubConfiguration, contentCollections);

        // Write source file for debugging (only for disk cache)
        var sourcePath = cacheService.GetSourcePath(nodeName);
        if (cacheService.IsDiskCacheEnabled && _cacheOptions.EnableSourceDebugging)
        {
            await File.WriteAllTextAsync(sourcePath, source, ct);
            logger.LogDebug("Wrote source file for debugging: {SourcePath}", sourcePath);
        }

        logger.LogInformation("Compiling assembly for {NodeName} ({Mode})",
            nodeName, cacheService.IsDiskCacheEnabled ? "disk" : "in-memory");

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
            references: _references,
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
        var source = _attributeGenerator.GenerateAttributeSource(node, codeConfig, release.HubConfiguration, release.ContentCollections);

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
            references: _references,
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
