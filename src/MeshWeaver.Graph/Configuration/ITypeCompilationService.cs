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
    /// Compiles multiple DataModels with caching support.
    /// Generates a MeshNodeAttribute in the assembly for registration via InstallAssemblies.
    /// Supports HubConfiguration-only scenarios (empty DataModels list).
    /// </summary>
    /// <param name="dataModels">The DataModels containing type sources (can be empty)</param>
    /// <param name="layoutAreas">The LayoutAreaConfigs for this type</param>
    /// <param name="node">The MeshNode (provides path for cache key)</param>
    /// <param name="nodeTypeConfig">Optional NodeTypeConfig for additional settings</param>
    /// <param name="hubFeatures">Optional HubFeatureConfig for hub configuration</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The compiled Types (empty if no DataModels)</returns>
    Task<IReadOnlyList<Type>> CompileTypeWithCacheAsync(
        IReadOnlyList<DataModel> dataModels,
        IReadOnlyList<LayoutAreaConfig> layoutAreas,
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
