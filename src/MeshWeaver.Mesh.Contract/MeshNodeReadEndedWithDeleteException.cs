namespace MeshWeaver.Mesh;

/// <summary>
/// The node stream cache ended an existing reader because its node was deleted. A fresh read
/// may be opened if the path is created again; this signal says nothing about other paths.
/// </summary>
public sealed class MeshNodeReadEndedWithDeleteException : InvalidOperationException
{
    /// <summary>The exact path whose cached read ended.</summary>
    public string NodePath { get; }

    /// <summary>Creates the delete outcome for an existing node-stream reader.</summary>
    public MeshNodeReadEndedWithDeleteException(string nodePath)
        : base($"No node found at '{nodePath}': the node was deleted, and this read of it ended "
               + "with the delete. Read the path again if it is re-created.")
    {
        NodePath = nodePath;
    }
}
