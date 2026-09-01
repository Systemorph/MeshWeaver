namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// The built-in node-type NAMES both halves of the graph/compiler split speak.
///
/// <para>A name is vocabulary, not model: the compile pipeline writes an <c>Activity</c> node and
/// reads a <c>CompletionMemory</c> node without owning — or being able to reference — the
/// registration extensions that declare those types, which live with the model in
/// MeshWeaver.Graph. Hoisting the literals here keeps ONE definition of each string; the
/// registration classes derive their own <c>NodeType</c> const from these, so a rename still has a
/// single site.</para>
/// </summary>
public static class GraphNodeTypeNames
{
    /// <summary>The node-type identifier for Activity nodes (<c>ActivityNodeType</c>).</summary>
    public const string Activity = "Activity";

    /// <summary>The node-type identifier for completion-memory nodes
    /// (<c>CompletionMemoryNodeType</c>).</summary>
    public const string CompletionMemory = "CompletionMemory";

    /// <summary>The node-type identifier for Release nodes (<c>ReleaseNodeType</c>).</summary>
    public const string Release = "Release";

    /// <summary>The path segment Release nodes are filed under,
    /// <c>{nodeTypePath}/Release/{version}</c> (<c>ReleaseNodeType.ReleaseSegment</c>).</summary>
    public const string ReleaseSegment = "Release";
}
