using MeshWeaver.Data;
using MeshWeaver.Messaging;

namespace MeshWeaver.Layout;

/// <summary>
/// Handler for area: prefix paths.
/// Maps to LayoutAreaReference.
/// </summary>
public class AreaPrefixHandler : IUnifiedReferenceHandler
{
    /// <inheritdoc />
    public Address GetAddress(ContentReference reference)
    {
        var areaRef = (LayoutAreaContentReference)reference;
        return new Address(areaRef.AddressType, areaRef.AddressId);
    }

    /// <inheritdoc />
    public WorkspaceReference CreateWorkspaceReference(ContentReference reference)
    {
        var areaRef = (LayoutAreaContentReference)reference;
        return new LayoutAreaReference(areaRef.AreaName) { Id = areaRef.AreaId };
    }
}
