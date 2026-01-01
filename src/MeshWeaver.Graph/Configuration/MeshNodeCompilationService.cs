using MeshWeaver.ContentCollections;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Service that compiles and caches MeshNode assemblies on-demand.
/// Combines code generation (via DynamicMeshNodeAttributeGenerator) with Roslyn compilation.
/// Implements IMeshNodeCompilationService from MeshWeaver.Mesh.Contract.
/// </summary>
internal class MeshNodeCompilationService(
    IPersistenceService persistence,
    ICompilationCacheService cacheService,
    IOptions<CompilationCacheOptions> cacheOptions,
    ILogger<MeshNodeCompilationService> logger)
    : IMeshNodeCompilationService
{
    private readonly CompilationCacheOptions _cacheOptions = cacheOptions.Value ?? new CompilationCacheOptions();
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

        // Get CodeConfiguration from the Code sub-partition
        // For NodeType nodes (where Content is NodeTypeDefinition), use the node's own path
        // For instance nodes, use the NodeType's path (e.g., "Person/Code" for Alice with NodeType="Person")
        CodeConfiguration? codeFile = null;
        var codePartition = node.Content is NodeTypeDefinition
            ? $"{node.Path}/Code"    // NodeType node - use its own Code partition
            : $"{node.NodeType}/Code"; // Instance node - use NodeType's Code partition
        await foreach (var obj in persistence.GetPartitionObjectsAsync(codePartition).WithCancellation(ct))
        {
            if (obj is CodeConfiguration cf)
            {
                codeFile = cf;
                break;
            }
        }

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
        else
        {
            // Instance node - look up the NodeType to get its Configuration
            var nodeTypeNode = await persistence.GetNodeAsync(node.NodeType, ct);
            if (nodeTypeNode?.Content is NodeTypeDefinition ntd)
            {
                configuration = ntd.Configuration;
                contentCollections = ntd.ContentCollections;
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

            // Extract NodeTypeConfigurations from MeshNodeAttribute.Nodes
            var configurations = new List<NodeTypeConfiguration>();
            foreach (var type in assembly.GetTypes())
            {
                if (typeof(MeshNodeAttribute).IsAssignableFrom(type) && !type.IsAbstract)
                {
                    var attribute = (MeshNodeAttribute?)Activator.CreateInstance(type);
                    if (attribute != null)
                    {
                        // Extract configurations from Nodes property
                        foreach (var meshNode in attribute.Nodes)
                        {
                            // Get HubConfiguration by subscribing to Observable (it emits immediately via Observable.Return)
                            Func<MessageHubConfiguration, MessageHubConfiguration>? hubConfig = null;
                            if (meshNode.HubConfiguration != null)
                            {
                                meshNode.HubConfiguration.Subscribe(config => hubConfig = config);
                            }

                            if (hubConfig != null)
                            {
                                configurations.Add(new NodeTypeConfiguration
                                {
                                    NodeType = meshNode.NodeType ?? meshNode.Path,
                                    DataType = typeof(object),
                                    HubConfiguration = hubConfig,
                                    DisplayName = meshNode.Name,
                                    Description = meshNode.Description,
                                    IconName = meshNode.IconName,
                                    DisplayOrder = meshNode.DisplayOrder
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

        // Generate full source with MeshNodeAttribute (including content collections)
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
