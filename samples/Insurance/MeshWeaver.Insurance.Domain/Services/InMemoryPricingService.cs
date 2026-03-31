using System.Collections.Concurrent;
using MeshWeaver.Import.Configuration;

namespace MeshWeaver.Insurance.Domain.Services;

/// <summary>
/// In-memory implementation of the pricing service.
/// </summary>
public class InMemoryPricingService : IPricingService
{
    private readonly ConcurrentDictionary<string, Pricing> _pricings = new();
    private readonly ConcurrentDictionary<string, List<PropertyRisk>> _risks = new();
    private readonly ConcurrentDictionary<string, List<ExcelImportConfiguration>> _importConfigs = new();

    public InMemoryPricingService()
    {
        // Initialize with sample data
        foreach (var pricing in SampleDataProvider.GetSamplePricings())
        {
            _pricings[pricing.Id!] = pricing;
            _risks[pricing.Id!] = new List<PropertyRisk>();
            _importConfigs[pricing.Id!] = new List<ExcelImportConfiguration>();
        }
    }

    public IReadOnlyCollection<Pricing> GetCatalog()
    {
        return _pricings.Values.ToList();
    }

    public Task<Pricing?> GetHeaderAsync(string id)
    {
        _pricings.TryGetValue(id, out var pricing);
        return Task.FromResult(pricing);
    }

    public Task<IReadOnlyCollection<PropertyRisk>> GetRisksAsync(string pricingId, CancellationToken ct = default)
    {
        if (_risks.TryGetValue(pricingId, out var risks))
        {
            return Task.FromResult<IReadOnlyCollection<PropertyRisk>>(risks.ToList());
        }
        return Task.FromResult<IReadOnlyCollection<PropertyRisk>>(Array.Empty<PropertyRisk>());
    }

    public async IAsyncEnumerable<ExcelImportConfiguration> GetImportConfigurationsAsync(string pricingId)
    {
        if (_importConfigs.TryGetValue(pricingId, out var configs))
        {
            foreach (var config in configs)
            {
                yield return config;
            }
        }
        await Task.CompletedTask;
    }

    public void UpdatePricingHeader(Pricing pricing)
    {
        if (pricing.Id != null)
        {
            _pricings[pricing.Id] = pricing;
        }
    }

    public void UpdateRisks(string pricingId, IEnumerable<PropertyRisk> updatedRisks)
    {
        if (!_risks.ContainsKey(pricingId))
        {
            _risks[pricingId] = new List<PropertyRisk>();
        }

        var riskList = _risks[pricingId];
        foreach (var risk in updatedRisks)
        {
            var existing = riskList.FirstOrDefault(r => r.Id == risk.Id);
            if (existing != null)
            {
                riskList.Remove(existing);
            }
            riskList.Add(risk);
        }
    }

    public void UpsertImportConfiguration(ExcelImportConfiguration configuration)
    {
        if (!_importConfigs.ContainsKey(configuration.Address))
        {
            _importConfigs[configuration.Address] = new List<ExcelImportConfiguration>();
        }

        var configList = _importConfigs[configuration.Address];
        var existing = configList.FirstOrDefault(c => c.Name == configuration.Name);
        if (existing != null)
        {
            configList.Remove(existing);
        }
        configList.Add(configuration);
    }
}
