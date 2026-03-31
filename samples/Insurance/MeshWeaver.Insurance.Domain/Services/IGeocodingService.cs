namespace MeshWeaver.Insurance.Domain.Services;

/// <summary>
/// Service for geocoding property risks.
/// </summary>
public interface IGeocodingService
{
    /// <summary>
    /// Observable stream of geocoding progress.
    /// </summary>
    IObservable<GeocodingProgress?> Progress { get; }

    /// <summary>
    /// Geocodes a collection of property risks.
    /// </summary>
    Task<GeocodingResponse> GeocodeRisksAsync(IReadOnlyCollection<PropertyRisk> risks, CancellationToken cancellationToken = default);
}

/// <summary>
/// Progress information for geocoding operations.
/// </summary>
public record GeocodingProgress(
    int TotalRisks,
    int ProcessedRisks,
    string? CurrentRiskId,
    string CurrentRiskName,
    bool IsComplete = false
);
