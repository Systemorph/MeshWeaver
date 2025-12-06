using MeshWeaver.Data;
using MeshWeaver.Messaging;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// Handler for content: prefix paths.
/// Maps to FileReference (file-based workspace reference).
/// </summary>
public class ContentPrefixHandler : IUnifiedReferenceHandler
{
    /// <inheritdoc />
    public Address GetAddress(ContentReference reference)
    {
        var fileRef = (FileContentReference)reference;
        return new Address(fileRef.AddressType, fileRef.AddressId);
    }

    /// <inheritdoc />
    public WorkspaceReference CreateWorkspaceReference(ContentReference reference)
    {
        var fileRef = (FileContentReference)reference;
        return new FileReference(fileRef.Collection, fileRef.FilePath, fileRef.Partition);
    }
}
