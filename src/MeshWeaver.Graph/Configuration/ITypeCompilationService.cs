using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Internal service for compiling C# type definitions at runtime.
/// Supports disk-based caching with PDB generation for debugging.
/// </summary>
internal interface ITypeCompilationService
{
    /// <summary>
    /// Compiles the TypeSource from a DataModel and returns the compiled Type.
    /// Registers the type in the TypeRegistry.
    /// </summary>
    /// <param name="model">The DataModel containing the type source</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The compiled Type</returns>
    Task<Type> CompileTypeAsync(DataModel model, CancellationToken ct = default);

    /// <summary>
    /// Compiles a DataModel with caching support using MeshNode.LastModified for cache validation.
    /// Generates a MeshNodeAttribute in the assembly for registration via InstallAssemblies.
    /// </summary>
    /// <param name="model">The DataModel containing the type source</param>
    /// <param name="node">The MeshNode (provides LastModified for cache validation)</param>
    /// <param name="nodeTypeConfig">Optional NodeTypeConfig for additional settings</param>
    /// <param name="hubFeatures">Optional HubFeatureConfig for hub configuration</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The compiled Type</returns>
    Task<Type> CompileTypeWithCacheAsync(
        DataModel model,
        MeshNode node,
        NodeTypeConfig? nodeTypeConfig,
        HubFeatureConfig? hubFeatures,
        CancellationToken ct = default);

    /// <summary>
    /// Compiles all DataModels and registers their types.
    /// </summary>
    /// <param name="models">The DataModels to compile</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Dictionary mapping DataModel.Id to compiled Type</returns>
    Task<IReadOnlyDictionary<string, Type>> CompileAllAsync(
        IEnumerable<DataModel> models,
        CancellationToken ct = default);

    /// <summary>
    /// Gets a previously compiled type by its DataModel.Id.
    /// </summary>
    /// <param name="id">The DataModel.Id</param>
    /// <returns>The compiled Type or null if not found</returns>
    Type? GetCompiledType(string id);
}
