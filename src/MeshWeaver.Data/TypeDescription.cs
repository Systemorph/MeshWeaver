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

    public static class SynchronizationAddress
    {
        public const string AddressType = "sync";
        public static Address Create(string? id = null) => new(AddressType, id ?? Guid.NewGuid().AsString());
    }
}
