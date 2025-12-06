using MeshWeaver.Messaging;

namespace MeshWeaver.Data;

/// <summary>
/// Registry for unified reference prefix handlers.
/// Allows registering how each prefix (data:, area:, content:) maps to addresses and workspace references.
/// Each module registers its own handler (e.g., AddData registers "data:", AddLayout registers "area:").
/// </summary>
public interface IUnifiedReferenceRegistry
{
    /// <summary>
    /// Register a handler for a specific prefix.
    /// </summary>
    /// <param name="prefix">The prefix (e.g., "data", "area", "content") without colon</param>
    /// <param name="handler">The handler for this prefix</param>
    void Register(string prefix, IUnifiedReferenceHandler handler);

    /// <summary>
    /// Try to get a handler for the given prefix.
    /// </summary>
    /// <param name="prefix">The prefix to look up</param>
    /// <param name="handler">The handler if found</param>
    /// <returns>True if a handler was found</returns>
    bool TryGetHandler(string prefix, out IUnifiedReferenceHandler? handler);

    /// <summary>
    /// Get all registered prefixes.
    /// </summary>
    IEnumerable<string> Prefixes { get; }
}

/// <summary>
/// Handler for a specific unified reference prefix.
/// Responsible for extracting address information and creating workspace references.
/// </summary>
public interface IUnifiedReferenceHandler
{
    /// <summary>
    /// Extract the target address from the parsed content reference.
    /// </summary>
    /// <param name="reference">The parsed content reference</param>
    /// <returns>The target address for routing</returns>
    Address GetAddress(ContentReference reference);

    /// <summary>
    /// Create the appropriate workspace reference from the parsed content reference.
    /// </summary>
    /// <param name="reference">The parsed content reference</param>
    /// <returns>A workspace reference (e.g., EntityReference, CollectionReference, LayoutAreaReference, FileReference)</returns>
    WorkspaceReference CreateWorkspaceReference(ContentReference reference);
}
