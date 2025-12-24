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

    public async Task<Type> CompileTypeWithCacheAsync(
        DataModel model,
        MeshNode node,
        NodeTypeConfig? nodeTypeConfig,
        HubFeatureConfig? hubFeatures,
        CancellationToken ct = default)
    {
        // If caching is disabled or cache service not available, fall back to in-memory compilation
        if (!_cacheOptions.EnableCompilationCache)
        {
            return await CompileTypeAsync(model, ct);
        }

        var nodeName = cacheService.SanitizeNodeName(node.Path);

        // Check if we have it in memory cache
        if (_compiledTypes.TryGetValue(model.Id, out var existingType))
        {
            model.CompiledType = existingType;
            return existingType;
        }

        // Check if disk cache is valid
        if (cacheService.IsCacheValid(nodeName, node.LastModified))
        {
            logger.LogDebug("Using cached assembly for {NodeName}", nodeName);

            try
            {
                // Load assembly using the cache service's isolated load context
                var cachedAssembly = cacheService.LoadAssembly(nodeName);
                if (cachedAssembly != null)
                {
                    var typeName = _attributeGenerator.ExtractTypeName(model.TypeSource);
                    var fullTypeName = $"{DynamicNamespace}.{typeName}";

                    var compiledType = cachedAssembly.GetType(fullTypeName);
                    if (compiledType != null)
                    {
                        // Register in type registry
                        typeRegistry.WithType(compiledType, typeName);
                        _compiledTypes[model.Id] = compiledType;
                        model.CompiledType = compiledType;

                        logger.LogInformation("Loaded cached type {TypeName} for DataModel {Id}", typeName, model.Id);
                        return compiledType;
                    }

                    logger.LogWarning("Cached assembly exists but type {FullTypeName} not found, recompiling", fullTypeName);
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

        // Generate full source with MeshNodeAttribute
        var source = _attributeGenerator.GenerateAttributeSource(node, model, nodeTypeConfig, hubFeatures);

        // Write source file for debugging
        var sourcePath = cacheService.GetSourcePath(nodeName);
        if (_cacheOptions.EnableSourceDebugging)
        {
            await File.WriteAllTextAsync(sourcePath, source, ct);
            logger.LogDebug("Wrote source file for debugging: {SourcePath}", sourcePath);
        }

        logger.LogInformation("Compiling assembly for {NodeName} (cache miss or stale)", nodeName);

        // Parse with source path and encoding embedded (critical for PDB source linking)
        // SourceText with encoding is REQUIRED for debug information emission
        // DocumentationMode.Diagnose enables XML documentation generation
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
                .WithOptimizationLevel(OptimizationLevel.Debug) // Debug for PDB
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
            // Clean up failed artifacts
            cacheService.InvalidateCache(nodeName);

            var errors = emitResult.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.GetMessage())
                .ToList();

            var errorMessage = $"Type compilation failed for '{model.Id}':\n{string.Join('\n', errors)}";
            logger.LogError("{ErrorMessage}", errorMessage);
            throw new TypeCompilationException(model.Id, errorMessage);
        }

        // Close streams before loading
        await dllStream.DisposeAsync();
        await pdbStream.DisposeAsync();
        await xmlDocStream.DisposeAsync();

        // Load using the cache service's isolated load context (required for debugger to find PDB)
        var assembly = cacheService.LoadAssembly(nodeName);
        if (assembly == null)
        {
            throw new TypeCompilationException(model.Id, $"Failed to load compiled assembly from {dllPath}");
        }

        // Extract the type
        var extractedTypeName = _attributeGenerator.ExtractTypeName(model.TypeSource);
        var extractedFullTypeName = $"{DynamicNamespace}.{extractedTypeName}";

        var resultType = assembly.GetType(extractedFullTypeName);

        if (resultType == null)
        {
            var availableTypes = assembly.GetTypes().Select(t => t.FullName);
            throw new TypeCompilationException(model.Id,
                $"Type '{extractedFullTypeName}' not found in compiled assembly. Available types: {string.Join(", ", availableTypes)}");
        }

        // Register in type registry
        typeRegistry.WithType(resultType, extractedTypeName);
        _compiledTypes[model.Id] = resultType;
        model.CompiledType = resultType;

        logger.LogInformation("Successfully compiled and cached type {TypeName} for DataModel {Id} at {DllPath}",
            extractedTypeName, model.Id, dllPath);

        return resultType;
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
