using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeshWeaver.ContentCollections;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Service that compiles MeshNode configurations using CSharpScript.
/// Unlike MeshNodeCompilationService which generates assemblies, this service
/// evaluates scripts directly and caches the compiled Script objects.
/// </summary>
internal class ScriptCompilationService : IDisposable
{
    private readonly ScriptCodeGenerator _generator = new();
    private readonly ILogger<ScriptCompilationService> _logger;
    private readonly CompilationCacheOptions _cacheOptions;
    private readonly ScriptOptions _scriptOptions;

    // In-memory cache of compiled scripts by cache key
    private readonly ConcurrentDictionary<string, Script<MeshNode>> _compiledScripts = new();

    // Track script source files on disk for debugging
    private readonly ConcurrentDictionary<string, string> _sourceFiles = new();

    public ScriptCompilationService(
        ILogger<ScriptCompilationService> logger,
        IOptions<CompilationCacheOptions> cacheOptions)
    {
        _logger = logger;
        _cacheOptions = cacheOptions.Value ?? new CompilationCacheOptions();
        _scriptOptions = CreateScriptOptions();
    }

    /// <summary>
    /// Compiles and executes a script to get a MeshNode with HubConfiguration.
    /// Uses cached compiled script if available.
    /// </summary>
    /// <param name="node">The MeshNode template (provides path, name, etc.).</param>
    /// <param name="codeFile">Optional user code configuration.</param>
    /// <param name="hubConfiguration">Optional hub configuration lambda as string.</param>
    /// <param name="contentCollections">Optional content collections to register.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A MeshNode with HubConfiguration set.</returns>
    public async Task<MeshNode?> CompileAndExecuteAsync(
        MeshNode node,
        CodeConfiguration? codeFile,
        string? hubConfiguration,
        IReadOnlyList<ContentCollectionConfig>? contentCollections,
        CancellationToken ct = default)
    {
        var cacheKey = ComputeCacheKey(node, codeFile, hubConfiguration, contentCollections);

        _logger.LogDebug("Compiling script for {NodePath} with cache key {CacheKey}", node.Path, cacheKey);

        // Try to get cached compiled script
        if (!_compiledScripts.TryGetValue(cacheKey, out var script))
        {
            // Generate script source
            var source = _generator.GenerateScriptSource(node, codeFile, hubConfiguration, contentCollections);

            // Save source to disk for debugging if enabled
            if (_cacheOptions.EnableDiskCache && _cacheOptions.EnableSourceDebugging)
            {
                await SaveSourceToDiskAsync(node.Path, cacheKey, source, ct);
            }

            // Compile the script
            script = CSharpScript.Create<MeshNode>(source, _scriptOptions);

            // Validate compilation
            var diagnostics = script.Compile(ct);
            var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            if (errors.Any())
            {
                var errorMessage = string.Join("\n", errors.Select(e => e.ToString()));
                _logger.LogError("Script compilation failed for {NodePath}:\n{Errors}", node.Path, errorMessage);
                throw new CompilationException(node.Path, errorMessage);
            }

            // Cache the compiled script
            _compiledScripts[cacheKey] = script;
            _logger.LogDebug("Script compiled and cached for {NodePath}", node.Path);
        }
        else
        {
            _logger.LogDebug("Using cached script for {NodePath}", node.Path);
        }

        // Execute the script
        try
        {
            var result = await script.RunAsync(cancellationToken: ct);
            return result.ReturnValue;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Script execution failed for {NodePath}", node.Path);
            throw;
        }
    }

    /// <summary>
    /// Gets the HubConfiguration for a node by compiling and executing its script.
    /// </summary>
    public async Task<Func<MessageHubConfiguration, MessageHubConfiguration>?> GetHubConfigurationAsync(
        MeshNode node,
        CodeConfiguration? codeFile,
        string? hubConfiguration,
        IReadOnlyList<ContentCollectionConfig>? contentCollections,
        CancellationToken ct = default)
    {
        var resultNode = await CompileAndExecuteAsync(node, codeFile, hubConfiguration, contentCollections, ct);
        return resultNode?.HubConfiguration;
    }

    /// <summary>
    /// Invalidates the cached script for a specific cache key.
    /// </summary>
    public void InvalidateCache(string cacheKey)
    {
        _compiledScripts.TryRemove(cacheKey, out _);

        if (_sourceFiles.TryRemove(cacheKey, out var sourcePath) && File.Exists(sourcePath))
        {
            try
            {
                File.Delete(sourcePath);
                var metaPath = Path.ChangeExtension(sourcePath, ".meta.json");
                if (File.Exists(metaPath))
                    File.Delete(metaPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete cached source file {SourcePath}", sourcePath);
            }
        }
    }

    /// <summary>
    /// Invalidates all cached scripts for a node path.
    /// </summary>
    public void InvalidateCacheForNode(string nodePath)
    {
        var keysToRemove = _compiledScripts.Keys
            .Where(k => k.StartsWith(nodePath + ":"))
            .ToList();

        foreach (var key in keysToRemove)
        {
            InvalidateCache(key);
        }
    }

    /// <summary>
    /// Clears all cached scripts.
    /// </summary>
    public void ClearCache()
    {
        _compiledScripts.Clear();
        _sourceFiles.Clear();
    }

    /// <summary>
    /// Computes a cache key based on all compilation inputs.
    /// </summary>
    private string ComputeCacheKey(
        MeshNode node,
        CodeConfiguration? codeFile,
        string? hubConfiguration,
        IReadOnlyList<ContentCollectionConfig>? contentCollections)
    {
        var sb = new StringBuilder();
        sb.Append(node.Path);
        sb.Append(':');
        sb.Append(node.LastModified.ToString("O"));
        sb.Append(':');

        if (codeFile?.Code != null)
        {
            sb.Append(ComputeHash(codeFile.Code));
        }
        sb.Append(':');

        if (hubConfiguration != null)
        {
            sb.Append(ComputeHash(hubConfiguration));
        }
        sb.Append(':');

        if (contentCollections is { Count: > 0 })
        {
            foreach (var cc in contentCollections.OrderBy(c => c.Name))
            {
                sb.Append(cc.Name).Append(',');
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Computes a short hash for a string.
    /// </summary>
    private static string ComputeHash(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash)[..16]; // First 8 bytes = 16 hex chars
    }

    /// <summary>
    /// Saves the script source to disk for debugging.
    /// </summary>
    private async Task SaveSourceToDiskAsync(string nodePath, string cacheKey, string source, CancellationToken ct)
    {
        try
        {
            var cacheDir = _cacheOptions.CacheDirectory ?? ".mesh-cache";
            Directory.CreateDirectory(cacheDir);

            var safeName = _generator.SanitizeName(nodePath);
            var sourcePath = Path.Combine(cacheDir, $"{safeName}.cs");

            await File.WriteAllTextAsync(sourcePath, source, ct);
            _sourceFiles[cacheKey] = sourcePath;

            // Write metadata
            var metaPath = Path.ChangeExtension(sourcePath, ".meta.json");
            var meta = $"{{\"nodePath\":\"{nodePath}\",\"cacheKey\":\"{cacheKey}\",\"timestamp\":\"{DateTimeOffset.UtcNow:O}\"}}";
            await File.WriteAllTextAsync(metaPath, meta, ct);

            _logger.LogDebug("Saved script source to {SourcePath}", sourcePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save script source to disk for {NodePath}", nodePath);
        }
    }

    /// <summary>
    /// Creates the ScriptOptions with all necessary references and imports.
    /// </summary>
    private static ScriptOptions CreateScriptOptions()
    {
        // Get assemblies to reference
        var references = new List<MetadataReference>();

        // Add trusted platform assemblies
        var trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (!string.IsNullOrEmpty(trustedAssemblies))
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

        return ScriptOptions.Default
            .WithReferences(references)
            .WithImports(
                "System",
                "System.Collections.Generic",
                "System.ComponentModel",
                "System.ComponentModel.DataAnnotations",
                "System.Linq",
                "System.Text.Json.Serialization",
                "MeshWeaver.Mesh",
                "MeshWeaver.Messaging",
                "MeshWeaver.Data",
                "MeshWeaver.Graph",
                "MeshWeaver.Graph.Configuration",
                "MeshWeaver.Layout",
                "MeshWeaver.Layout.Composition",
                "MeshWeaver.Layout.Domain",
                "MeshWeaver.Layout.Views",
                "MeshWeaver.Application.Styles",
                "MeshWeaver.ContentCollections",
                "MeshWeaver.Mesh.Services",
                "Microsoft.Extensions.DependencyInjection",
                "Microsoft.Extensions.Configuration"
            )
            .WithOptimizationLevel(OptimizationLevel.Release)
            .WithEmitDebugInformation(true);
    }

    public void Dispose()
    {
        _compiledScripts.Clear();
        _sourceFiles.Clear();
    }
}
