namespace OpenSmc.Domain.Abstractions;

public interface IWithParent
{
    object Parent { get; init; }
}
