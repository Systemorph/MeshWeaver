using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Compiles C# view functions at runtime using Roslyn CSharpCompilation.
/// View functions have the signature: static UiControl ViewName(LayoutAreaHost host, RenderingContext ctx)
/// </summary>
public class ViewCompilationService : IViewCompilationService
{
    private readonly ILogger<ViewCompilationService>? _logger;
    private readonly ConcurrentDictionary<string, Func<LayoutAreaHost, RenderingContext, UiControl>> _compiledViews = new();
    private readonly List<MetadataReference> _references;

    private const string DynamicNamespace = "MeshWeaver.Graph.DynamicViews";
    private const string WrapperClassName = "DynamicViewContainer";

    public ViewCompilationService(ILogger<ViewCompilationService>? logger = null)
    {
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

        // Add specific assemblies needed for view compilation
        var additionalAssemblies = new[]
        {
            typeof(object).Assembly,                    // System.Runtime
            typeof(UiControl).Assembly,                 // MeshWeaver.Layout
            typeof(LayoutAreaHost).Assembly,            // MeshWeaver.Layout (Composition)
            typeof(MeshWeaver.Data.EntityStore).Assembly, // MeshWeaver.Data
            typeof(MeshWeaver.Messaging.IMessageHub).Assembly, // MeshWeaver.Messaging
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

    public Task<Func<LayoutAreaHost, RenderingContext, UiControl>> CompileViewAsync(
        LayoutAreaConfig config,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.ViewSource))
            throw new ViewCompilationException(config.Id, "ViewSource cannot be null or empty");

        if (_compiledViews.TryGetValue(config.Id, out var existingView))
        {
            config.CompiledView = existingView;
            return Task.FromResult(existingView);
        }

        ct.ThrowIfCancellationRequested();

        // Wrap in namespace with common usings for layout views
        var code = $@"
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Data;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace {DynamicNamespace}
{{
    public static class {WrapperClassName}_{config.Id.Replace("-", "_")}
    {{
        {config.ViewSource}
    }}
}}";

        _logger?.LogDebug("Compiling view for {Id}", config.Id);

        var syntaxTree = CSharpSyntaxTree.ParseText(code, cancellationToken: ct);

        var assemblyName = $"DynamicView_{config.Id}_{Guid.NewGuid():N}";

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

            var errorMessage = $"View compilation failed for '{config.Id}':\n{string.Join('\n', errors)}";
            _logger?.LogError("{ErrorMessage}", errorMessage);
            throw new ViewCompilationException(config.Id, errorMessage);
        }

        ms.Seek(0, SeekOrigin.Begin);
        var assembly = Assembly.Load(ms.ToArray());

        // Find the wrapper class
        var wrapperClassName = $"{DynamicNamespace}.{WrapperClassName}_{config.Id.Replace("-", "_")}";
        var wrapperType = assembly.GetType(wrapperClassName);

        if (wrapperType == null)
        {
            var availableTypes = assembly.GetTypes().Select(t => t.FullName);
            throw new ViewCompilationException(config.Id,
                $"Wrapper type '{wrapperClassName}' not found in compiled assembly. Available types: {string.Join(", ", availableTypes)}");
        }

        // Find a static method with the correct signature
        var viewMethod = FindViewMethod(wrapperType, config.Id);

        // Create delegate from the method
        var compiledView = (Func<LayoutAreaHost, RenderingContext, UiControl>)
            Delegate.CreateDelegate(typeof(Func<LayoutAreaHost, RenderingContext, UiControl>), viewMethod);

        // Cache the compiled view
        _compiledViews[config.Id] = compiledView;
        config.CompiledView = compiledView;

        _logger?.LogInformation("Successfully compiled view for LayoutAreaConfig {Id}", config.Id);

        return Task.FromResult(compiledView);
    }

    private static MethodInfo FindViewMethod(Type wrapperType, string configId)
    {
        // Find all public static methods that match the view signature
        var viewMethods = wrapperType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m =>
            {
                var parameters = m.GetParameters();
                return parameters.Length == 2 &&
                       parameters[0].ParameterType == typeof(LayoutAreaHost) &&
                       parameters[1].ParameterType == typeof(RenderingContext) &&
                       typeof(UiControl).IsAssignableFrom(m.ReturnType);
            })
            .ToList();

        if (viewMethods.Count == 0)
        {
            throw new ViewCompilationException(configId,
                $"No view method found with signature: static UiControl MethodName(LayoutAreaHost host, RenderingContext ctx). " +
                $"Available methods: {string.Join(", ", wrapperType.GetMethods(BindingFlags.Public | BindingFlags.Static).Select(m => m.Name))}");
        }

        if (viewMethods.Count > 1)
        {
            throw new ViewCompilationException(configId,
                $"Multiple view methods found: {string.Join(", ", viewMethods.Select(m => m.Name))}. " +
                "ViewSource should contain exactly one view method.");
        }

        return viewMethods[0];
    }

    public async Task<IReadOnlyDictionary<string, Func<LayoutAreaHost, RenderingContext, UiControl>>> CompileAllAsync(
        IEnumerable<LayoutAreaConfig> configs,
        CancellationToken ct = default)
    {
        var results = new Dictionary<string, Func<LayoutAreaHost, RenderingContext, UiControl>>();

        foreach (var config in configs.Where(c => !string.IsNullOrWhiteSpace(c.ViewSource)))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var view = await CompileViewAsync(config, ct);
                results[config.Id] = view;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to compile view for {Id}, skipping. Error: {Message}", config.Id, ex.Message);
                // Continue with other views instead of failing completely
            }
        }

        return results;
    }

    public Func<LayoutAreaHost, RenderingContext, UiControl>? GetCompiledView(string id)
    {
        return _compiledViews.TryGetValue(id, out var view) ? view : null;
    }
}

/// <summary>
/// Exception thrown when view compilation fails.
/// </summary>
public class ViewCompilationException : Exception
{
    public string ConfigId { get; }

    public ViewCompilationException(string configId, string message)
        : base(message)
    {
        ConfigId = configId;
    }

    public ViewCompilationException(string configId, string message, Exception innerException)
        : base(message, innerException)
    {
        ConfigId = configId;
    }
}
