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

    /// <summary>
    /// Schema information returned by SchemaReference.
    /// </summary>
    /// <param name="Type">The type name</param>
    /// <param name="Schema">The JSON schema string</param>
    public record SchemaInfo(string Type, string Schema);

    /// <summary>
    /// Factory helpers for addresses of transient synchronization streams.
    /// </summary>
    public static class SynchronizationAddress
    {
        /// <summary>The address-type discriminator used for synchronization addresses.</summary>
        public const string AddressType = "sync";
        /// <summary>
        /// Creates a synchronization address, generating a fresh identifier when none is supplied.
        /// </summary>
        /// <param name="id">Optional explicit identifier; a new GUID-based id is used when <c>null</c>.</param>
        /// <returns>An <see cref="Address"/> of type <see cref="AddressType"/>.</returns>
        public static Address Create(string? id = null) => new(AddressType, id ?? Guid.NewGuid().AsString());
    }
}
