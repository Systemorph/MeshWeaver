namespace MeshWeaver.Mesh;

/// <summary>
/// A write was refused because its path lies at or under a subtree whose recursive deletion is in
/// flight. Raised by the outermost storage decorator (<c>SubtreeDeletionGuardStorageAdapter</c>) and
/// by nothing else.
///
/// <para>A type rather than a sentence so a caller can tell this refusal apart without parsing its
/// message. <c>DataExtensions.ClassifyPatchException</c> maps it to
/// <see cref="MeshNodeErrorCode.NotFound"/> when an owner's post-commit flush runs into it: the node
/// the update targeted is being deleted, so the update cannot land. It derives from
/// <see cref="System.InvalidOperationException"/>, the type the guard threw before, so existing catch
/// arms keep matching it.</para>
///
/// <para>It lives in <c>MeshWeaver.Data.Contract</c> because the layer that raises it
/// (<c>MeshWeaver.Hosting</c>) and the layer that classifies it (<c>MeshWeaver.Data</c>) both
/// reference this assembly and neither references the other.</para>
/// </summary>
public sealed class SubtreeDeletionInFlightException : System.InvalidOperationException
{
    /// <summary>The path whose write was refused.</summary>
    public string Path { get; }

    /// <summary>The root of the subtree whose deletion is in flight.</summary>
    public string? DeletionRoot { get; }

    /// <summary>Creates the refusal for a write to <paramref name="path"/>.</summary>
    /// <param name="path">The path whose write was refused.</param>
    /// <param name="deletionRoot">The root of the subtree being deleted.</param>
    public SubtreeDeletionInFlightException(string path, string? deletionRoot)
        : base($"Cannot write '{path}': the subtree '{deletionRoot}' is currently being deleted.")
    {
        Path = path;
        DeletionRoot = deletionRoot;
    }
}
