using MeshWeaver.Import.Configuration;

namespace MeshWeaver.Insurance.Domain.Services;

/// <summary>
/// Service for loading and managing insurance pricings.
/// </summary>
public interface IPricingService
{
    /// <summary>
    /// Gets all pricing headers for the catalog.
    /// </summary>
    IReadOnlyCollection<Pricing> GetCatalog();

    /// <summary>
    /// Gets a single pricing header by ID.
    /// </summary>
    Task<Pricing?> GetHeaderAsync(string id);

    /// <summary>
    /// Gets all property risks for a specific pricing.
    /// </summary>
    Task<IReadOnlyCollection<PropertyRisk>> GetRisksAsync(string pricingId, CancellationToken ct = default);

    /// <summary>
    /// Gets import configurations for a specific pricing.
    /// </summary>
    IAsyncEnumerable<ExcelImportConfiguration> GetImportConfigurationsAsync(string pricingId);

    /// <summary>
    /// Updates a pricing header.
    /// </summary>
    void UpdatePricingHeader(Pricing pricing);

    /// <summary>
    /// Updates risks for a specific pricing.
    /// </summary>
    void UpdateRisks(string pricingId, IEnumerable<PropertyRisk> updatedRisks);

    /// <summary>
    /// Upserts an import configuration.
    /// </summary>
    void UpsertImportConfiguration(ExcelImportConfiguration configuration);
}
