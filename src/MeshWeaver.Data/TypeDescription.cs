using System.Runtime.CompilerServices;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.Data
{
    /// <summary>
    /// Description of a domain type.
    /// </summary>
    /// <param name="Name">The name of the type</param>
    /// <param name="DisplayName">The display name of the type</param>
    /// <param name="Description">Optional description of the type</param>
    /// <param name="Address">Address on which the data type lives.</param>
    public record TypeDescription(string Name, string DisplayName, string Description, Address? Address);
}


public record SynchronizationAddress(string? Id = null) : Address(AddressType, Id ?? Guid.NewGuid().AsString())
{
    public const string AddressType = "sync";
}
