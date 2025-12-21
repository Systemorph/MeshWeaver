using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using MeshWeaver.Domain;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Compiles C# type definitions at runtime using Roslyn CSharpCompilation.
/// This is significantly faster than using Microsoft.DotNet.Interactive's CSharpKernel.
/// </summary>
public class TypeCompilationService : ITypeCompilationService
{
    private readonly ITypeRegistry _typeRegistry;
    private readonly ILogger<TypeCompilationService>? _logger;
    private readonly ConcurrentDictionary<string, Type> _compiledTypes = new();
    private readonly List<MetadataReference> _references;

    private const string DynamicNamespace = "MeshWeaver.Graph.Dynamic";

    public TypeCompilationService(ITypeRegistry typeRegistry, ILogger<TypeCompilationService>? logger = null)
    {
        _typeRegistry = typeRegistry;
        _logger = logger;
        _references = GetDefaultReferences();
    }

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

        _logger?.LogDebug("Compiling type for {Id}", model.Id);

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
            _logger?.LogError("{ErrorMessage}", errorMessage);
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
        _typeRegistry.WithType(compiledType, typeName);

        // Cache the type
        _compiledTypes[model.Id] = compiledType;
        model.CompiledType = compiledType;

        _logger?.LogInformation("Successfully compiled type {TypeName} for DataModel {Id}", typeName, model.Id);

        return Task.FromResult(compiledType);
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
                _logger?.LogWarning(ex, "Failed to compile type for {Id}, skipping. Error: {Message}", model.Id, ex.Message);
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
