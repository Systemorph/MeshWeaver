using System.Reactive.Linq;
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
    public IObservable<IReadOnlyCollection<AutocompleteItem>> GetItems(string query, string? contextPath = null) =>
        // Pure in-memory enumeration of the pricing catalog — no I/O, no async. One settled snapshot.
        // p.Id is in format "company/year" (e.g., "Microsoft/2026").
        Observable.Return<IReadOnlyCollection<AutocompleteItem>>(
            pricingService.GetCatalog()
                .Select(p => new AutocompleteItem(
                    Label: $"@{InsuranceApplicationAttribute.PricingType}/{p.Id}/",
                    InsertText: $"@{InsuranceApplicationAttribute.PricingType}/{p.Id}/",
                    Description: p.InsuredName ?? p.Id,
                    Category: "Pricing",
                    Priority: 1000,
                    Kind: AutocompleteKind.Other))
                .ToArray());
}
