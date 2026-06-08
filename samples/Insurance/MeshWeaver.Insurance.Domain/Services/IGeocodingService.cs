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
    /// Geocodes a collection of property risks. Reactive — returns <see cref="IObservable{T}"/>
    /// (never Task); the HTTP leaf runs inside the bounded Http I/O queue.
    /// </summary>
    IObservable<GeocodingResponse> GeocodeRisks(IReadOnlyCollection<PropertyRisk> risks);
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
