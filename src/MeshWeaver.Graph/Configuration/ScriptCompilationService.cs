using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeshWeaver.ContentCollections;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.NuGet;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Service that compiles MeshNode configurations using CSharpScript.
/// Unlike MeshNodeCompilationService which generates assemblies, this service
/// evaluates scripts directly and caches the compiled executors.
///
/// <para>🚨 Each cached entry's assembly is emitted by US into a <b>collectible</b>
/// <see cref="AssemblyLoadContext"/>, never run through <c>Script.RunAsync</c> — Roslyn's own
/// script loader is permanently non-collectible (verified against Roslyn 5.3), and the cache
/// key includes <c>node.LastModified</c>, so with RunAsync every node UPDATE stranded the
/// previous entry's assembly for the process lifetime (a co-driver of the portal's
/// memory-fatigue wall alongside the kernel's per-cell leak — see <c>ScriptSession</c> in
/// MeshWeaver.Kernel.Hub for the same cure on the REPL path). Invalidation now unloads the
/// entry's context; a <c>HubConfiguration</c> delegate still referenced by a running hub keeps
/// its context alive until the hub dies (unloading is cooperative), then everything is
/// reclaimed.</para>
/// </summary>
internal class ScriptCompilationService : IDisposable
{
    private readonly ScriptCodeGenerator _generator = new();
    private readonly ILogger<ScriptCompilationService> _logger;
    private readonly CompilationCacheOptions _cacheOptions;
    private readonly ScriptOptions _scriptOptions;
    private readonly INuGetAssemblyResolver _nugetResolver;

    // In-memory cache of compiled config executors by cache key. The entry owns its
    // collectible load context; the Factory delegate (→ its target method → assembly) is what
    // keeps the context alive while cached — Unload() on eviction lets it die.
    private sealed record CompiledConfig(Func<object?[], Task<MeshNode>> Factory, AssemblyLoadContext LoadContext)
    {
        /// <summary>The generated submission entry point: slot 0 = globals (none here), slot 1
        /// is written by the submission itself — a fresh 2-slot array per execution.</summary>
        public Task<MeshNode> ExecuteAsync() => Factory(new object?[2]);
    }

    private readonly ConcurrentDictionary<string, CompiledConfig> _compiledScripts = new();

    // Track script source files on disk for debugging
    private readonly ConcurrentDictionary<string, string> _sourceFiles = new();

    public ScriptCompilationService(
        ILogger<ScriptCompilationService> logger,
        IOptions<CompilationCacheOptions> cacheOptions,
        INuGetAssemblyResolver nugetResolver)
    {
        _logger = logger;
        _cacheOptions = cacheOptions.Value ?? new CompilationCacheOptions();
        _nugetResolver = nugetResolver;
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

        // Try to get cached compiled executor
        if (!_compiledScripts.TryGetValue(cacheKey, out var compiled))
        {
            // Generate script source
            var rawSource = _generator.GenerateScriptSource(node, codeFile, hubConfiguration, contentCollections);

            // Strip #r "nuget:..." directives and resolve the packages in-process.
            var (source, nugetRefs) = NuGetDirectiveParser.Extract(rawSource);
            var scriptOptions = _scriptOptions;
            if (nugetRefs.Length > 0)
            {
                var resolved = await _nugetResolver.ResolveAsync(nugetRefs, targetFramework: null, ct);
                scriptOptions = scriptOptions.AddReferences(
                    resolved.AssemblyPaths.Select(p => MetadataReference.CreateFromFile(p)));
            }

            // Save source to disk for debugging if enabled
            if (_cacheOptions.EnableDiskCache && _cacheOptions.EnableSourceDebugging)
            {
                await SaveSourceToDiskAsync(node.Path, cacheKey, source, ct);
            }

            var fresh = CompileToCollectibleContext(node.Path, source, scriptOptions, ct);
            // On a lost publication race the winner stays cached; shed the duplicate's context
            // (unloading is cooperative — this call can still execute from it safely).
            compiled = _compiledScripts.GetOrAdd(cacheKey, fresh);
            if (!ReferenceEquals(compiled, fresh))
                fresh.LoadContext.Unload();
            _logger.LogDebug("Script compiled and cached for {NodePath}", node.Path);
        }
        else
        {
            _logger.LogDebug("Using cached script for {NodePath}", node.Path);
        }

        // Execute the script
        try
        {
            return await compiled.ExecuteAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Script execution failed for {NodePath}", node.Path);
            throw;
        }
    }

    /// <summary>
    /// Compiles the config script through Roslyn's script API but emits + loads it into a
    /// fresh collectible context and binds the generated <c>&lt;Factory&gt;</c> entry point —
    /// the submission protocol of the scripting host, minus its permanent loader.
    /// </summary>
    private CompiledConfig CompileToCollectibleContext(
        string nodePath, string source, ScriptOptions scriptOptions, CancellationToken ct)
    {
        var script = CSharpScript.Create<MeshNode>(source, scriptOptions);
        var compilation = script.GetCompilation();

        using var peStream = new MemoryStream();
        using var pdbStream = new MemoryStream();
        var emitted = compilation.Emit(
            peStream,
            pdbStream,
            options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb),
            cancellationToken: ct);
        if (!emitted.Success)
        {
            var errorMessage = string.Join("\n", emitted.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(e => e.ToString()));
            _logger.LogError("Script compilation failed for {NodePath}:\n{Errors}", nodePath, errorMessage);
            throw new CompilationException(nodePath, errorMessage);
        }

        // One context per entry; a single submission never needs sibling resolution, so every
        // bind falls through to the default context. 🚨 Never store the Assembly on the
        // context itself — a strong self-reference is a GC-handle cycle that can never unload.
        var loadContext = new AssemblyLoadContext($"node-config-script:{nodePath}", isCollectible: true);
        peStream.Position = 0;
        pdbStream.Position = 0;
        var assembly = loadContext.LoadFromStream(peStream, pdbStream);
        var scriptClass = compilation.ScriptClass!.MetadataName;
        var factory = assembly.GetType(scriptClass, throwOnError: true)!
                          .GetMethod("<Factory>", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                      ?? throw new InvalidOperationException($"Submission type '{scriptClass}' has no <Factory> entry point.");
        return new CompiledConfig(factory.CreateDelegate<Func<object?[], Task<MeshNode>>>(), loadContext);
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
        if (_compiledScripts.TryRemove(cacheKey, out var evicted))
            evicted.LoadContext.Unload();

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
        foreach (var key in _compiledScripts.Keys.ToList())
            if (_compiledScripts.TryRemove(key, out var evicted))
                evicted.LoadContext.Unload();
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
        ClearCache();
    }
}
