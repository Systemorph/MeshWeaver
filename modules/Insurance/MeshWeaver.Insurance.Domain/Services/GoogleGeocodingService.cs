using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Reactive.Subjects;
using MeshWeaver.GoogleMaps;
using Microsoft.Extensions.Options;

namespace MeshWeaver.Insurance.Domain.Services;

/// <summary>
/// Google Maps-based geocoding service for property risks.
/// </summary>
public class GoogleGeocodingService(IOptions<GoogleMapsConfiguration> googleConfig) : IGeocodingService
{
    private readonly ReplaySubject<GeocodingProgress?> progressSubject = InitializeProgress();
    private readonly object progressLock = new();
    private readonly HttpClient http = new();

    private static ReplaySubject<GeocodingProgress?> InitializeProgress()
    {
        var ret = new ReplaySubject<GeocodingProgress?>(1);
        ret.OnNext(null);
        return ret;
    }

    public IObservable<GeocodingProgress?> Progress => progressSubject;

    public async Task<GeocodingResponse> GeocodeRisksAsync(IReadOnlyCollection<PropertyRisk> risks, CancellationToken cancellationToken = default)
    {
        try
        {
            lock (progressLock)
            {
                progressSubject.OnNext(new GeocodingProgress(0, 0, null, "Starting geocoding..."));
            }

            if (!risks.Any())
            {
                lock (progressLock)
                {
                    progressSubject.OnNext(new GeocodingProgress(0, 0, null, "No risks to geocode", true));
                }
                return new GeocodingResponse
                {
                    Success = true,
                    GeocodedCount = 0,
                    Error = "No risks found to geocode"
                };
            }

            // Check Google Maps API key
            if (string.IsNullOrEmpty(googleConfig.Value.ApiKey))
            {
                var error = "Google Maps API key not configured";
                lock (progressLock)
                {
                    progressSubject.OnNext(new GeocodingProgress(0, 0, null, "Configuration error", true));
                }
                return new GeocodingResponse
                {
                    Success = false,
                    GeocodedCount = 0,
                    Error = error
                };
            }

            // Filter risks that need geocoding
            var risksToGeocode = risks
                .Where(r => r.GeocodedLocation?.Latitude == null || r.GeocodedLocation?.Longitude == null)
                .ToList();

            if (!risksToGeocode.Any())
            {
                lock (progressLock)
                {
                    progressSubject.OnNext(new GeocodingProgress(risks.Count, risks.Count, null,
                        "All risks already geocoded", true));
                }
                return new GeocodingResponse
                {
                    Success = true,
                    GeocodedCount = 0,
                    Error = null
                };
            }

            lock (progressLock)
            {
                progressSubject.OnNext(new GeocodingProgress(risksToGeocode.Count, 0, null, "Initializing geocoding..."));
            }

            var geocodedCount = 0;
            var updatedRisks = new ConcurrentBag<PropertyRisk>();
            var processedCount = 0;

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 10),
                CancellationToken = cancellationToken
            };

            await Parallel.ForEachAsync(risksToGeocode, parallelOptions, async (risk, ct) =>
            {
                var riskName = risk.LocationName ?? risk.Address ?? $"Risk {risk.Id}";

                try
                {
                    var geocodedLocation = await GeocodeAsync(risk, ct);

                    if (geocodedLocation.Latitude.HasValue && geocodedLocation.Longitude.HasValue)
                    {
                        // Update the risk with geocoded data
                        var updatedRisk = risk with { GeocodedLocation = geocodedLocation };
                        updatedRisks.Add(updatedRisk);
                        Interlocked.Increment(ref geocodedCount);
                    }
                    else
                    {
                        // Still add the risk with the geocoding attempt result
                        updatedRisks.Add(risk with { GeocodedLocation = geocodedLocation });
                    }
                }
                catch (Exception)
                {
                    // Add the original risk unchanged
                    updatedRisks.Add(risk);
                }

                // Update progress after processing each risk
                var currentProcessed = Interlocked.Increment(ref processedCount);
                lock (progressLock)
                {
                    progressSubject.OnNext(new GeocodingProgress(
                        risksToGeocode.Count,
                        currentProcessed,
                        risk.Id,
                        $"Processing {currentProcessed}/{risksToGeocode.Count} risks..."
                    ));
                }
            });

            // Final progress update
            lock (progressLock)
            {
                progressSubject.OnNext(new GeocodingProgress(
                    risksToGeocode.Count,
                    risksToGeocode.Count,
                    null,
                    $"Completed processing {risksToGeocode.Count} risks",
                    true
                ));
            }

            return new GeocodingResponse
            {
                Success = true,
                GeocodedCount = geocodedCount,
                Error = null,
                UpdatedRisks = updatedRisks.ToList()
            };
        }
        catch (Exception ex)
        {
            var error = $"Geocoding failed: {ex.Message}";
            return new GeocodingResponse
            {
                Success = false,
                GeocodedCount = 0,
                Error = error
            };
        }
        finally
        {
            lock (progressLock)
            {
                progressSubject.OnNext(null);
            }
        }
    }

    private async Task<GeocodedLocation> GeocodeAsync(PropertyRisk risk, CancellationToken ct = default)
    {
        var query = BuildQuery(risk);
        var url = $"https://maps.googleapis.com/maps/api/geocode/json?address={Uri.EscapeDataString(query)}&key={googleConfig.Value.ApiKey}";

        var response = await http.GetFromJsonAsync<GoogleGeocodeResponse>(url, cancellationToken: ct);
        if (response == null)
        {
            return new GeocodedLocation { Status = "NoResponse" };
        }

        if (response.status != "OK" || response.results == null || response.results.Length == 0)
        {
            return new GeocodedLocation { Status = response.status };
        }

        var r = response.results[0];
        return new GeocodedLocation
        {
            Latitude = r.geometry.location.lat,
            Longitude = r.geometry.location.lng,
            FormattedAddress = r.formatted_address,
            PlaceId = r.place_id,
            Status = response.status
        };
    }

    private static string BuildQuery(PropertyRisk risk)
    {
        var parts = new[] { risk.LocationName, risk.Address, risk.City, risk.State, risk.ZipCode, risk.Country }
            .Where(s => !string.IsNullOrWhiteSpace(s));
        return string.Join(", ", parts);
    }

    private sealed class GoogleGeocodeResponse
    {
        public string status { get; set; } = string.Empty;
        public GoogleGeocodeResult[]? results { get; set; }
    }

    private sealed class GoogleGeocodeResult
    {
        public string formatted_address { get; set; } = string.Empty;
        public string place_id { get; set; } = string.Empty;
        public GoogleGeometry geometry { get; set; } = new();
    }

    private sealed class GoogleGeometry
    {
        public GoogleLocation location { get; set; } = new();
    }

    private sealed class GoogleLocation
    {
        public double lat { get; set; }
        public double lng { get; set; }
    }
}
