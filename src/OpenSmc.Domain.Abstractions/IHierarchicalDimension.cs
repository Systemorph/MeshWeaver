using OpenSmc.Domain.Abstractions.Attributes;

namespace OpenSmc.Domain.Abstractions;

public interface IHierarchicalDimension : INamed, IWithParent
{
    [IdentityProperty]
    new string SystemName { get; init; }
}