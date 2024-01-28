namespace OpenSmc.Domain.Abstractions;

public interface IWithParent
{
    string Parent { get; init; }
}