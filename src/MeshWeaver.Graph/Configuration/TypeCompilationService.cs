using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using MeshWeaver.Domain;
using MeshWeaver.Mesh;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Compiles C# type definitions at runtime using Roslyn CSharpCompilation.
/// Supports disk-based caching with PDB generation for debugging.
/// </summary>
internal class TypeCompilationService(
    ITypeRegistry typeRegistry,
    ICompilationCacheService cacheService,
    IOptions<CompilationCacheOptions> cacheOptions,
    ILogger<TypeCompilationService> logger)
    : ITypeCompilationService
{
    private readonly DynamicMeshNodeAttributeGenerator _attributeGenerator = new();
    private readonly CompilationCacheOptions _cacheOptions = cacheOptions.Value ?? new CompilationCacheOptions();
    private readonly ConcurrentDictionary<string, Type> _compiledTypes = new();
    private readonly List<MetadataReference> _references = GetDefaultReferences();

    private const string DynamicNamespace = "MeshWeaver.Graph.Dynamic";

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

    public Task<Type> CompileTypeAsync(DataModel model, CancellationToken ct = default)
    {
        if (_compiledTypes.TryGetValue(model.Id, out var existingType))
        {
            model.CompiledType = existingType;
            return Task.FromResult(existingType);
        }

        ct.ThrowIfCancellationRequested();

        // Wrap in namespace with common usings
        var code = $@"
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace {DynamicNamespace}
{{
    {model.TypeSource}
}}";

        logger.LogDebug("Compiling type for {Id}", model.Id);

        var syntaxTree = CSharpSyntaxTree.ParseText(code, cancellationToken: ct);

        var assemblyName = $"DynamicType_{model.Id}_{Guid.NewGuid():N}";

        var compilation = CSharpCompilation.Create(
            assemblyName,
            syntaxTrees: [syntaxTree],
            references: _references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithOptimizationLevel(OptimizationLevel.Release)
                .WithPlatform(Platform.AnyCpu));

        using var ms = new MemoryStream();
        var emitResult = compilation.Emit(ms, cancellationToken: ct);

        if (!emitResult.Success)
        {
            var errors = emitResult.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.GetMessage())
                .ToList();

            var errorMessage = $"Type compilation failed for '{model.Id}':\n{string.Join('\n', errors)}";
            logger.LogError("{ErrorMessage}", errorMessage);
            throw new TypeCompilationException(model.Id, errorMessage);
        }

        ms.Seek(0, SeekOrigin.Begin);
        var assembly = Assembly.Load(ms.ToArray());

        // Extract the type name from the source
        var typeName = ExtractTypeName(model.TypeSource);
        var fullTypeName = $"{DynamicNamespace}.{typeName}";

        var compiledType = assembly.GetType(fullTypeName);

        if (compiledType == null)
        {
            var availableTypes = assembly.GetTypes().Select(t => t.FullName);
            throw new TypeCompilationException(model.Id,
                $"Type '{fullTypeName}' not found in compiled assembly. Available types: {string.Join(", ", availableTypes)}");
        }

        // Register in type registry with short name
        typeRegistry.WithType(compiledType, typeName);

        // Cache the type
        _compiledTypes[model.Id] = compiledType;
        model.CompiledType = compiledType;

        logger.LogInformation("Successfully compiled type {TypeName} for DataModel {Id}", typeName, model.Id);

        return Task.FromResult(compiledType);
    }

    public async Task<IReadOnlyList<Type>> CompileTypeWithCacheAsync(
        IReadOnlyList<DataModel> dataModels,
        IReadOnlyList<LayoutAreaConfig> layoutAreas,
        MeshNode node,
        NodeTypeConfig? nodeTypeConfig,
        HubFeatureConfig? hubFeatures,
        CancellationToken ct = default)
    {
        var nodeName = cacheService.SanitizeNodeName(node.Path);

        // If caching is disabled, fall back to in-memory compilation for each DataModel
        if (!_cacheOptions.EnableCompilationCache)
        {
            var results = new List<Type>();
            foreach (var model in dataModels)
            {
                results.Add(await CompileTypeAsync(model, ct));
            }
            return results;
        }

        // Check if all types are already in memory cache
        var allCached = dataModels.Count > 0 && dataModels.All(m => _compiledTypes.ContainsKey(m.Id));
        if (allCached)
        {
            var cachedTypes = new List<Type>();
            foreach (var model in dataModels)
            {
                var cachedType = _compiledTypes[model.Id];
                model.CompiledType = cachedType;
                cachedTypes.Add(cachedType);
            }
            return cachedTypes;
        }

        // Try to load from disk cache
        var dllPath = cacheService.GetDllPath(nodeName);
        if (File.Exists(dllPath))
        {
            logger.LogDebug("Attempting to load cached assembly for {NodeName}", nodeName);

            try
            {
                var cachedAssembly = cacheService.LoadAssembly(nodeName);
                if (cachedAssembly != null)
                {
                    var loadedTypes = new List<Type>();
                    var allTypesFound = true;

                    foreach (var model in dataModels)
                    {
                        var typeName = _attributeGenerator.ExtractTypeName(model.TypeSource);
                        var fullTypeName = $"{DynamicNamespace}.{typeName}";

                        var compiledType = cachedAssembly.GetType(fullTypeName);
                        if (compiledType != null)
                        {
                            typeRegistry.WithType(compiledType, typeName);
                            _compiledTypes[model.Id] = compiledType;
                            model.CompiledType = compiledType;
                            loadedTypes.Add(compiledType);
                        }
                        else
                        {
                            logger.LogWarning("Cached assembly exists but type {FullTypeName} not found, recompiling", fullTypeName);
                            allTypesFound = false;
                            break;
                        }
                    }

                    if (allTypesFound)
                    {
                        logger.LogInformation("Loaded {Count} cached types for {NodeName}", loadedTypes.Count, nodeName);
                        return loadedTypes;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load cached assembly for {NodeName}, recompiling", nodeName);
            }
        }

        // Invalidate old cache and recompile
        cacheService.InvalidateCache(nodeName);
        cacheService.EnsureCacheDirectoryExists();

        ct.ThrowIfCancellationRequested();

        // Generate full source with MeshNodeAttribute for ALL DataModels and LayoutAreas
        var source = _attributeGenerator.GenerateAttributeSource(node, dataModels, layoutAreas, nodeTypeConfig, hubFeatures);

        // Write source file for debugging
        var sourcePath = cacheService.GetSourcePath(nodeName);
        if (_cacheOptions.EnableSourceDebugging)
        {
            await File.WriteAllTextAsync(sourcePath, source, ct);
            logger.LogDebug("Wrote source file for debugging: {SourcePath}", sourcePath);
        }

        logger.LogInformation("Compiling assembly for {NodeName} with {DataModelCount} DataModels (cache miss or stale)",
            nodeName, dataModels.Count);

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

            var nodeId = dataModels.FirstOrDefault()?.Id ?? node.Path;
            var errorMessage = $"Type compilation failed for '{nodeId}':\n{string.Join('\n', errors)}";
            logger.LogError("{ErrorMessage}", errorMessage);
            throw new TypeCompilationException(nodeId, errorMessage);
        }

        // Close streams before loading
        await dllStream.DisposeAsync();
        await pdbStream.DisposeAsync();
        await xmlDocStream.DisposeAsync();

        // Load using the cache service's isolated load context
        var assembly = cacheService.LoadAssembly(nodeName);
        if (assembly == null)
        {
            var nodeId = dataModels.FirstOrDefault()?.Id ?? node.Path;
            throw new TypeCompilationException(nodeId, $"Failed to load compiled assembly from {dllPath}");
        }

        // Extract and register all compiled types
        var compiledTypes = new List<Type>();
        foreach (var model in dataModels)
        {
            var extractedTypeName = _attributeGenerator.ExtractTypeName(model.TypeSource);
            var extractedFullTypeName = $"{DynamicNamespace}.{extractedTypeName}";

            var resultType = assembly.GetType(extractedFullTypeName);
            if (resultType == null)
            {
                var availableTypes = assembly.GetTypes().Select(t => t.FullName);
                throw new TypeCompilationException(model.Id,
                    $"Type '{extractedFullTypeName}' not found in compiled assembly. Available types: {string.Join(", ", availableTypes)}");
            }

            typeRegistry.WithType(resultType, extractedTypeName);
            _compiledTypes[model.Id] = resultType;
            model.CompiledType = resultType;
            compiledTypes.Add(resultType);
        }

        logger.LogInformation("Successfully compiled and cached {Count} types for {NodePath} at {DllPath}",
            compiledTypes.Count, node.Path, dllPath);

        return compiledTypes;
    }

    public async Task<IReadOnlyDictionary<string, Type>> CompileAllAsync(
        IEnumerable<DataModel> models,
        CancellationToken ct = default)
    {
        var results = new Dictionary<string, Type>();

        foreach (var model in models)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var type = await CompileTypeAsync(model, ct);
                results[model.Id] = type;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to compile type for {Id}, skipping. Error: {Message}", model.Id, ex.Message);
                // Continue with other types instead of failing completely
            }
        }

        return results;
    }

    public Type? GetCompiledType(string id)
    {
        return _compiledTypes.TryGetValue(id, out var type) ? type : null;
    }

    private static string ExtractTypeName(string typeSource)
    {
        // Match "public record Story", "public class Person", "public struct Point", etc.
        var match = Regex.Match(typeSource, @"(record|class|struct|interface)\s+(\w+)");
        if (match.Success)
            return match.Groups[2].Value;

        throw new ArgumentException($"Cannot extract type name from source: {typeSource}");
    }
}

/// <summary>
/// Exception thrown when type compilation fails.
/// </summary>
public class TypeCompilationException : Exception
{
    public string DataModelId { get; }

    public TypeCompilationException(string dataModelId, string message)
        : base(message)
    {
        DataModelId = dataModelId;
    }

    public TypeCompilationException(string dataModelId, string message, Exception innerException)
        : base(message, innerException)
    {
        DataModelId = dataModelId;
    }
}
