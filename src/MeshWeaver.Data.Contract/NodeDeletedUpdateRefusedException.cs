namespace MeshWeaver.Mesh;

/// <summary>
/// An update of a MeshNode committed at its owner AFTER the node was deleted, so its durable write
/// was refused: a delete wins, and persisting the update would re-create the row the delete just
/// removed. Raised by the owner's post-commit flush (<c>StoragePostCommitFlush</c>) and by nothing
/// else.
///
/// <para>A type rather than a sentence so the patch handler classifies it structurally:
/// <c>DataExtensions.ClassifyPatchException</c> maps it to <see cref="MeshNodeErrorCode.NotFound"/>,
/// and the writer receives a <see cref="MeshNodeStreamException"/> carrying that code. NotFound is
/// terminal, never auto-retried, which matches the case: the node is gone, and retrying the update
/// cannot make it land.</para>
///
/// <para>It lives in <c>MeshWeaver.Data.Contract</c> because the layer that raises it
/// (<c>MeshWeaver.Hosting</c>) and the layer that classifies it (<c>MeshWeaver.Data</c>) both
/// reference this assembly and neither references the other.</para>
/// </summary>
public sealed class NodeDeletedUpdateRefusedException : System.InvalidOperationException
{
    /// <summary>The path of the deleted node whose update was refused.</summary>
    public string Path { get; }

    /// <summary>Creates the refusal for <paramref name="path"/>.</summary>
    /// <param name="path">The path of the deleted node.</param>
    public NodeDeletedUpdateRefusedException(string path)
        : base($"Update of '{path}' was not persisted: the node was deleted after this update "
               + "committed at its owner, and a delete wins — writing it would re-create the row the "
               + "delete just removed.")
        => Path = path;
}
