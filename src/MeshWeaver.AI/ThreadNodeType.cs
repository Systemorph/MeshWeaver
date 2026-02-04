namespace MeshWeaver.AI;

/// <summary>
/// Constants and path helpers for Thread node types.
/// </summary>
public static class ThreadNodeType
{
    /// <summary>
    /// The NodeType value used to identify thread nodes.
    /// </summary>
    public const string NodeType = "Thread";

    /// <summary>
    /// Gets the user's thread catalog path.
    /// </summary>
    /// <param name="userId">The user's unique identifier.</param>
    /// <returns>Path to the user's threads namespace.</returns>
    public static string GetUserThreadsPath(string userId) => $"User/{userId}/Threads";

    /// <summary>
    /// Gets the threads path for a context (e.g., ACME/ProductLaunch/Threads).
    /// Used for storing threads under a specific MeshNode context.
    /// </summary>
    /// <param name="contextPath">The context path (e.g., "ACME/ProductLaunch").</param>
    /// <returns>Path to the context's threads namespace.</returns>
    public static string GetContextThreadsPath(string contextPath) => $"{contextPath}/Threads";
}
