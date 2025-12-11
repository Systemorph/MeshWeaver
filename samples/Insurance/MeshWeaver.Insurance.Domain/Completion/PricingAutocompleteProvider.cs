using MeshWeaver.Data.Completion;
using MeshWeaver.Insurance.Domain.Services;

namespace MeshWeaver.Insurance.Domain.Completion;

/// <summary>
/// Provides autocomplete items for insurance pricing submissions.
/// Returns all available pricing IDs from the pricing catalog.
/// Pricing format: pricing/company/year
/// </summary>
public class PricingAutocompleteProvider(IPricingService pricingService) : IAutocompleteProvider
{
    /// <inheritdoc />
    public Task<IEnumerable<AutocompleteItem>> GetItemsAsync(string query, CancellationToken ct = default)
    {
        var pricings = pricingService.GetCatalog();

        // p.Id is now in format "company/year" (e.g., "Microsoft/2026")
        var items = pricings.Select(p => new AutocompleteItem(
            Label: $"@{InsuranceApplicationAttribute.PricingType}/{p.Id}/",
            InsertText: $"@{InsuranceApplicationAttribute.PricingType}/{p.Id}/",
            Description: p.InsuredName ?? p.Id,
            Category: "Pricing",
            Priority: 1000,
            Kind: AutocompleteKind.Other
        ));

        return Task.FromResult(items);
    }
}
