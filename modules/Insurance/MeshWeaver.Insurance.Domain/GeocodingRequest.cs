using MeshWeaver.Messaging;

namespace MeshWeaver.Insurance.Domain;

/// <summary>
/// Request to geocode property risks.
/// </summary>
public record GeocodingRequest : IRequest<GeocodingResponse>;

/// <summary>
/// Response from geocoding operation.
/// </summary>
public record GeocodingResponse
{
    /// <summary>
    /// Whether the geocoding operation was successful.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Number of risks successfully geocoded.
    /// </summary>
    public int GeocodedCount { get; init; }

    /// <summary>
    /// Error message if geocoding failed.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// List of updated risks with geocoded locations.
    /// </summary>
    public IReadOnlyList<PropertyRisk>? UpdatedRisks { get; init; }
}
