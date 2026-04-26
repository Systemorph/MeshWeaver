namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Public interface for resolving URL paths to mesh node addresses.
/// Used by Blazor navigation to map browser URLs to hub addresses.
///
/// <para>
/// 100% reactive — emits <see cref="AddressResolution"/> via <see cref="IObservable{T}"/>.
/// Compose with <c>.Select</c> / <c>.SelectMany</c> / <c>.Subscribe</c>. NEVER bridge to
/// <c>Task</c> (that's a 100% deadlock surface; see
/// <c>Doc/Architecture/AsynchronousCalls.md</c>).
/// </para>
/// </summary>
public interface IPathResolver
{
    /// <summary>
    /// Resolves a full URL path to an address using score-based matching.
    /// Emits the best matching node's address and the remaining path segments,
    /// or <c>null</c> if no match is found.
    /// </summary>
    IObservable<AddressResolution?> ResolvePath(string path);
}
