using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Service for compiling C# view functions at runtime using Roslyn.
/// </summary>
public interface IViewCompilationService
{
    /// <summary>
    /// Compiles the ViewSource from a LayoutAreaConfig and returns the compiled view delegate.
    /// </summary>
    /// <param name="config">The LayoutAreaConfig containing the view source</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The compiled view delegate</returns>
    Task<Func<LayoutAreaHost, RenderingContext, UiControl>> CompileViewAsync(
        LayoutAreaConfig config,
        CancellationToken ct = default);

    /// <summary>
    /// Compiles all LayoutAreaConfigs with ViewSource and returns their compiled delegates.
    /// </summary>
    /// <param name="configs">The LayoutAreaConfigs to compile</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Dictionary mapping config Id to compiled view delegate</returns>
    Task<IReadOnlyDictionary<string, Func<LayoutAreaHost, RenderingContext, UiControl>>> CompileAllAsync(
        IEnumerable<LayoutAreaConfig> configs,
        CancellationToken ct = default);

    /// <summary>
    /// Gets a previously compiled view delegate by its configuration Id.
    /// </summary>
    /// <param name="id">The configuration Id</param>
    /// <returns>The compiled view delegate, or null if not found</returns>
    Func<LayoutAreaHost, RenderingContext, UiControl>? GetCompiledView(string id);
}
