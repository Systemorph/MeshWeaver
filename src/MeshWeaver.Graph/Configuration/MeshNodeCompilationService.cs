using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
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
    INodeTypeService nodeTypeService,
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

        // Check cache validity first
        if (cacheService.IsCacheValid(nodeName, node.LastModified))
        {
            logger.LogDebug("Using cached assembly for {NodePath}", node.Path);
            return dllPath;
        }

        // Get CodeFile from the NodeType's partition
        var codeFile = await nodeTypeService.GetCodeFileAsync(node.NodeType, node.Path, ct);

        // Get Configuration from the NodeTypeDefinition content
        // Configuration is the source code that gets compiled into HubConfiguration
        string? configuration = null;
        var nodeTypeNode = await nodeTypeService.GetNodeTypeNodeAsync(node.NodeType, node.Path, ct);
        if (nodeTypeNode?.Content is NodeTypeDefinition ntd)
        {
            configuration = ntd.Configuration;
        }

        try
        {
            // Compile using CodeFile and Configuration
            await CompileAsync(codeFile, configuration, node, ct);

            // Return the DLL path if it exists
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

            // Extract NodeTypeConfigurations from MeshNodeAttribute
            var configurations = new List<NodeTypeConfiguration>();
            foreach (var type in assembly.GetTypes())
            {
                if (typeof(MeshNodeAttribute).IsAssignableFrom(type) && !type.IsAbstract)
                {
                    var attribute = (MeshNodeAttribute?)Activator.CreateInstance(type);
                    if (attribute != null)
                    {
                        configurations.AddRange(attribute.NodeTypeConfigurations);
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
    /// Compiles CodeFile into an assembly using Roslyn.
    /// </summary>
    private async Task CompileAsync(
        CodeFile? codeFile,
        string? hubConfiguration,
        MeshNode node,
        CancellationToken ct)
    {
        var nodeName = cacheService.SanitizeNodeName(node.Path);

        // Invalidate old cache and prepare for recompilation
        cacheService.InvalidateCache(nodeName);
        cacheService.EnsureCacheDirectoryExists();

        ct.ThrowIfCancellationRequested();

        // Generate full source with MeshNodeAttribute
        var source = _attributeGenerator.GenerateAttributeSource(node, codeFile, hubConfiguration);

        // Write source file for debugging
        var sourcePath = cacheService.GetSourcePath(nodeName);
        if (_cacheOptions.EnableSourceDebugging)
        {
            await File.WriteAllTextAsync(sourcePath, source, ct);
            logger.LogDebug("Wrote source file for debugging: {SourcePath}", sourcePath);
        }

        logger.LogInformation("Compiling assembly for {NodeName}", nodeName);

        // Parse with source path and encoding embedded (critical for PDB source linking)
        var sourceText = Microsoft.CodeAnalysis.Text.SourceText.From(source, System.Text.Encoding.UTF8);
        var parseOptions = new CSharpParseOptions(documentationMode: DocumentationMode.Diagnose);
        var syntaxTree = CSharpSyntaxTree.ParseText(
            sourceText,
            parseOptions,
            path: _cacheOptions.EnableSourceDebugging ? sourcePath : "",
            cancellationToken: ct);

        var assemblyName = $"DynamicNode_{nodeName}";

        var compilation = CSharpCompilation.Create(
            assemblyName,
            syntaxTrees: [syntaxTree],
            references: _references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithOptimizationLevel(OptimizationLevel.Debug)
                .WithPlatform(Platform.AnyCpu));

        // Emit DLL, PDB, and XML documentation to disk
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

            var errorMessage = $"Compilation failed for '{node.Path}':\n{string.Join('\n', errors)}";
            logger.LogError("{ErrorMessage}", errorMessage);
            throw new CompilationException(node.Path, errorMessage);
        }

        // Close streams before loading
        await dllStream.DisposeAsync();
        await pdbStream.DisposeAsync();
        await xmlDocStream.DisposeAsync();

        logger.LogInformation("Successfully compiled assembly for {NodePath} at {DllPath}", node.Path, dllPath);
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
