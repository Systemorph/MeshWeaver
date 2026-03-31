#nullable enable

namespace MeshWeaver.Data.Completion;

/// <summary>
/// Service to query known address IDs for a given address type.
/// Used for the second stage of hierarchical autocomplete (addressType/ → addressId completion).
/// </summary>
public interface IAddressCatalogService
{
    /// <summary>
    /// Gets known address IDs for a given address type.
    /// </summary>
    /// <param name="addressType">The address type (e.g., "pricing", "host").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Collection of known address IDs for this type.</returns>
    Task<IEnumerable<string>> GetAddressIdsAsync(string addressType, CancellationToken ct = default);
}
