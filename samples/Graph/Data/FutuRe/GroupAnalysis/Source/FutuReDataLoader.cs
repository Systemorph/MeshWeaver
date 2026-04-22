// <meshweaver>
// Id: FutuReDataLoader
// DisplayName: FutuRe Data Loader
// </meshweaver>

using System.Globalization;
using System.IO;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Loads profitability data for the FutuRe sample.
/// Local hubs read CSV via IContentService; the group hub aggregates
/// from local hubs applying transaction mapping rules.
/// </summary>
public static class FutuReDataLoader
{
    // ---------------------------------------------------------------
    // Local Data Cube: CSV + Local LoB Enrichment
    // ---------------------------------------------------------------

    /// <summary>
    /// Loads the local data cube for a business unit hub.
    /// Reads datacube.csv from "attachments" and enriches with
    /// local LoB display names from mesh queries.
    /// </summary>
    public static IObservable<IEnumerable<FutuReDataCube>> LoadLocalDataCube(IWorkspace workspace)
    {
        var contentService = workspace.Hub.ServiceProvider.GetRequiredService<IContentService>();
        var meshQuery = workspace.Hub.ServiceProvider.GetRequiredService<IMeshService>();
        var address = workspace.Hub.Address.ToString();
        var segments = address.Split('/');
        var businessUnit = segments.Length > 1 ? segments[1] : address;
        var buPath = segments.Length > 1 ? $"{segments[0]}/{segments[1]}" : segments[0];

        return Observable.FromAsync<(List<FutuReDataCube> Rows, string Currency)>(async ct =>
        {
            // Look up the BU currency from the mesh node
            var buCurrency = "CHF";
            var buNode = await meshQuery.QueryAsync<MeshNode>($"path:{buPath}", ct: ct).FirstOrDefaultAsync(ct);
            if (buNode?.Content is BusinessUnit bu)
                buCurrency = bu.Currency;
            else if (buNode?.Content is JsonElement json
                     && json.TryGetProperty("currency", out var val)
                     && val.ValueKind == JsonValueKind.String)
                buCurrency = val.GetString() ?? "CHF";

            var stream = await contentService.GetContentAsync("attachments", "datacube.csv", ct);
            if (stream == null)
                return (new List<FutuReDataCube>(), buCurrency);
            using var reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync(ct);
            return (ParseLocalCsvContent(content, businessUnit), buCurrency);
        }).CombineLatest(
            LoadLocalLinesOfBusiness(workspace),
            (csvResult, lobs) =>
            {
                var lobLookup = lobs.ToDictionary(l => l.SystemName, l => l.DisplayName);
                return csvResult.Rows.Select(row => row with
                {
                    LineOfBusinessName = lobLookup.GetValueOrDefault(row.LineOfBusiness, row.LineOfBusiness),
                    LocalLineOfBusinessName = lobLookup.GetValueOrDefault(row.LocalLineOfBusiness, row.LocalLineOfBusiness),
                    Currency = csvResult.Currency
                }).AsEnumerable();
            }
        ).DistinctUntilChanged();
    }

    /// <summary>
    /// Parses local CSV content into FutuReDataCube rows.
    /// Local CSV columns: Month,Quarter,Year,LineOfBusiness,AmountType,Estimate,Actual
    /// </summary>
    private static List<FutuReDataCube> ParseLocalCsvContent(string content, string businessUnit)
    {
        var rows = new List<FutuReDataCube>();
        var lines = content.Split('\n');

        foreach (var rawLine in lines.Skip(1))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = SplitCsvLine(line);
            if (parts.Length < 6) continue;

            var month = parts[0];
            var lineOfBusiness = parts[3];
            var amountType = parts[4];

            rows.Add(new FutuReDataCube
            {
                Id = $"{month}-{lineOfBusiness}-{amountType}-{businessUnit}",
                Month = month,
                Quarter = parts[1],
                Year = int.Parse(parts[2], CultureInfo.InvariantCulture),
                LineOfBusiness = lineOfBusiness,
                LocalLineOfBusiness = lineOfBusiness,
                AmountType = amountType,
                BusinessUnit = businessUnit,
                Estimate = double.Parse(parts[5], CultureInfo.InvariantCulture),
                Actual = parts.Length > 6 && !string.IsNullOrWhiteSpace(parts[6])
                    ? double.Parse(parts[6], CultureInfo.InvariantCulture)
                    : null
            });
        }

        return rows;
    }

    // ---------------------------------------------------------------
    // Group Aggregation: Apply Transaction Mapping Rules
    // ---------------------------------------------------------------

    /// <summary>
    /// Aggregates local data cube rows to group level by applying
    /// transaction mapping rules and FX conversion.
    /// Each local row is split by percentage into one or more group LoB rows,
    /// with amounts converted to group reporting currency (CHF).
    /// </summary>
    public static IEnumerable<FutuReDataCube> AggregateToGroupLevel(
        IEnumerable<FutuReDataCube> localRows,
        IEnumerable<TransactionMapping> mappings,
        IEnumerable<LineOfBusiness> groupLobs,
        IEnumerable<ExchangeRate> exchangeRates,
        IEnumerable<BusinessUnit> businessUnits,
        string currencyMode = CurrencyModes.PlanChf)
    {
        var mappingLookup = mappings
            .GroupBy(m => (m.BusinessUnit, m.LocalLineOfBusiness))
            .ToDictionary(g => g.Key, g => g.ToList());
        var lobLookup = groupLobs
            .ToDictionary(l => l.SystemName, l => l.DisplayName);
        var buCurrencyLookup = businessUnits
            .ToDictionary(bu => bu.Id, bu => bu.Currency);
        var planFxLookup = exchangeRates
            .ToDictionary(fx => fx.FromCurrency, fx => fx.PlanRate);
        var actualFxLookup = exchangeRates
            .ToDictionary(fx => fx.FromCurrency, fx => fx.ActualRate);

        var isOriginal = currencyMode == CurrencyModes.OriginalCurrency;
        var useActualRateForBoth = currencyMode == CurrencyModes.ActualsChf;

        return localRows.SelectMany(row =>
        {
            var key = (row.BusinessUnit, row.LocalLineOfBusiness);
            if (!mappingLookup.TryGetValue(key, out var rules))
                return Enumerable.Empty<FutuReDataCube>();

            var buCurrency = buCurrencyLookup.GetValueOrDefault(row.BusinessUnit, "CHF");

            double estimateFxRate, actualFxRate;
            string currency;

            if (isOriginal)
            {
                estimateFxRate = 1.0;
                actualFxRate = 1.0;
                currency = buCurrency;
            }
            else if (useActualRateForBoth)
            {
                var rate = actualFxLookup.GetValueOrDefault(buCurrency, 1.0);
                estimateFxRate = rate;
                actualFxRate = rate;
                currency = "CHF";
            }
            else // Plan (CHF) — default
            {
                var rate = planFxLookup.GetValueOrDefault(buCurrency, 1.0);
                estimateFxRate = rate;
                actualFxRate = rate;
                currency = "CHF";
            }

            return rules.Select(rule => row with
            {
                Id = $"{row.Month}-{rule.GroupLineOfBusiness}-{row.AmountType}-{row.BusinessUnit}-{row.LocalLineOfBusiness}",
                LineOfBusiness = rule.GroupLineOfBusiness,
                LineOfBusinessName = lobLookup.GetValueOrDefault(
                    rule.GroupLineOfBusiness, rule.GroupLineOfBusiness),
                Currency = currency,
                Estimate = row.Estimate * rule.Percentage * estimateFxRate,
                Actual = row.Actual.HasValue
                    ? row.Actual.Value * rule.Percentage * actualFxRate
                    : null
            });
        });
    }

    private static string[] SplitCsvLine(string line)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;

        foreach (char c in line)
        {
            if (c == '"')
                inQuotes = !inQuotes;
            else if (c == ',' && !inQuotes)
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            else
                current.Append(c);
        }
        parts.Add(current.ToString());
        return parts.ToArray();
    }

    // ---------------------------------------------------------------
    // Local LoB Loading (from BU namespace)
    // ---------------------------------------------------------------

    /// <summary>
    /// Loads local LineOfBusiness instances for the current business unit.
    /// Derives the BU namespace from the workspace address.
    /// </summary>
    public static IObservable<IEnumerable<LineOfBusiness>> LoadLocalLinesOfBusiness(IWorkspace workspace)
    {
        var address = workspace.Hub.Address.ToString();
        var segments = address.Split('/');
        var buNamespace = segments.Length > 1
            ? $"{segments[0]}/{segments[1]}"
            : segments[0];

        var meshQuery = workspace.Hub.ServiceProvider.GetRequiredService<IMeshService>();
        return meshQuery
            .ObserveQuery<MeshNode>(
                MeshQueryRequest.FromQuery(
                    $"nodeType:FutuRe/LineOfBusiness namespace:{buNamespace}/LineOfBusiness state:Active"))
            .Select(change => change.Items
                .Select(ConvertToLineOfBusiness)
                .Where(lob => lob != null)
                .Cast<LineOfBusiness>()
                .OrderBy(lob => lob.Order));
    }

    // ---------------------------------------------------------------
    // Reference Data Loading (from Mesh Nodes)
    // ---------------------------------------------------------------

    /// <summary>
    /// Loads AmountType reference data from MeshNodes via IMeshService.
    /// </summary>
    public static IObservable<IEnumerable<AmountType>> LoadAmountTypes(IWorkspace workspace)
    {
        var meshQuery = workspace.Hub.ServiceProvider.GetRequiredService<IMeshService>();
        return meshQuery
            .ObserveQuery<MeshNode>(
                MeshQueryRequest.FromQuery("nodeType:FutuRe/AmountType namespace:FutuRe/AmountType state:Active"))
            .Select(change => change.Items
                .Select(ConvertToAmountType)
                .Where(a => a != null)
                .Cast<AmountType>()
                .OrderBy(a => a.Order));
    }

    /// <summary>
    /// Loads Currency reference data from MeshNodes via IMeshService.
    /// </summary>
    public static IObservable<IEnumerable<Currency>> LoadCurrencies(IWorkspace workspace)
    {
        var meshQuery = workspace.Hub.ServiceProvider.GetRequiredService<IMeshService>();
        return meshQuery
            .ObserveQuery<MeshNode>(
                MeshQueryRequest.FromQuery("nodeType:FutuRe/Currency namespace:FutuRe/Currency state:Active"))
            .Select(change => change.Items
                .Select(ConvertToCurrency)
                .Where(c => c != null)
                .Cast<Currency>()
                .OrderBy(c => c.Order));
    }

    /// <summary>
    /// Loads Country reference data from MeshNodes via IMeshService.
    /// </summary>
    public static IObservable<IEnumerable<Country>> LoadCountries(IWorkspace workspace)
    {
        var meshQuery = workspace.Hub.ServiceProvider.GetRequiredService<IMeshService>();
        return meshQuery
            .ObserveQuery<MeshNode>(
                MeshQueryRequest.FromQuery("nodeType:FutuRe/Country namespace:FutuRe/Country state:Active"))
            .Select(change => change.Items
                .Select(ConvertToCountry)
                .Where(c => c != null)
                .Cast<Country>()
                .OrderBy(c => c.Order));
    }

    /// <summary>
    /// Loads TransactionMapping instances from MeshNode graph via IMeshService.
    /// </summary>
    public static IObservable<IEnumerable<TransactionMapping>> LoadTransactionMappingsFromNodes(IWorkspace workspace)
    {
        var meshQuery = workspace.Hub.ServiceProvider.GetRequiredService<IMeshService>();

        return meshQuery
            .ObserveQuery<MeshNode>(
                MeshQueryRequest.FromQuery("nodeType:FutuRe/TransactionMapping namespace:FutuRe scope:descendants"))
            .Select(change => change.Items
                .Select(ConvertToTransactionMapping)
                .Where(m => m != null)
                .Cast<TransactionMapping>());
    }

    /// <summary>
    /// Loads ExchangeRate reference data from MeshNodes via IMeshService.
    /// </summary>
    public static IObservable<IEnumerable<ExchangeRate>> LoadExchangeRates(IWorkspace workspace)
    {
        var meshQuery = workspace.Hub.ServiceProvider.GetRequiredService<IMeshService>();
        return meshQuery
            .ObserveQuery<MeshNode>(
                MeshQueryRequest.FromQuery("nodeType:FutuRe/ExchangeRate namespace:FutuRe/ExchangeRate state:Active"))
            .Select(change => change.Items
                .Select(ConvertToExchangeRate)
                .Where(fx => fx != null)
                .Cast<ExchangeRate>()
                .OrderBy(fx => fx.Order));
    }

    /// <summary>
    /// Loads BusinessUnit reference data from MeshNodes via IMeshService.
    /// </summary>
    public static IObservable<IEnumerable<BusinessUnit>> LoadBusinessUnits(IWorkspace workspace)
    {
        var meshQuery = workspace.Hub.ServiceProvider.GetRequiredService<IMeshService>();
        return meshQuery
            .ObserveQuery<MeshNode>(
                MeshQueryRequest.FromQuery("nodeType:FutuRe/BusinessUnit namespace:FutuRe state:Active"))
            .Select(change => change.Items
                .Select(ConvertToBusinessUnit)
                .Where(bu => bu != null)
                .Cast<BusinessUnit>());
    }

    /// <summary>
    /// Loads group-level LineOfBusiness instances from MeshNode graph via IMeshService.
    /// </summary>
    public static IObservable<IEnumerable<LineOfBusiness>> LoadLinesOfBusinessFromNodes(IWorkspace workspace)
    {
        var meshQuery = workspace.Hub.ServiceProvider.GetRequiredService<IMeshService>();

        return meshQuery
            .ObserveQuery<MeshNode>(
                MeshQueryRequest.FromQuery("nodeType:FutuRe/LineOfBusiness namespace:FutuRe/LineOfBusiness state:Active"))
            .Select(change => change.Items
                .Select(ConvertToLineOfBusiness)
                .Where(lob => lob != null)
                .Cast<LineOfBusiness>()
                .OrderBy(lob => lob.Order));
    }

    // ---------------------------------------------------------------
    // MeshNode → Record Converters
    // ---------------------------------------------------------------

    private static TransactionMapping? ConvertToTransactionMapping(MeshNode node)
    {
        if (node.Content is TransactionMapping tm)
            return tm;
        if (node.Content is not JsonElement json)
            return null;

        return new TransactionMapping
        {
            Id = GetString(json, "id") ?? node.Id,
            BusinessUnit = GetString(json, "businessUnit") ?? string.Empty,
            LocalLineOfBusiness = GetString(json, "localLineOfBusiness") ?? string.Empty,
            LocalLineOfBusinessName = GetString(json, "localLineOfBusinessName") ?? string.Empty,
            GroupLineOfBusiness = GetString(json, "groupLineOfBusiness") ?? string.Empty,
            GroupLineOfBusinessName = GetString(json, "groupLineOfBusinessName") ?? string.Empty,
            Percentage = GetDouble(json, "percentage")
        };
    }

    private static LineOfBusiness? ConvertToLineOfBusiness(MeshNode node)
    {
        if (node.Content is LineOfBusiness lob)
            return lob;
        if (node.Content is not JsonElement json)
            return null;

        return new LineOfBusiness
        {
            SystemName = node.Id,
            DisplayName = node.Name ?? node.Id,
            Description = GetString(json, "description"),
            Order = node.Order ?? GetInt(json, "order"),
            ProductExamples = GetString(json, "productExamples")
        };
    }

    private static AmountType? ConvertToAmountType(MeshNode node)
    {
        if (node.Content is AmountType at)
            return at;
        if (node.Content is not JsonElement json)
            return null;

        return new AmountType
        {
            SystemName = GetString(json, "systemName") ?? node.Id,
            DisplayName = GetString(json, "displayName") ?? node.Name ?? node.Id,
            Order = GetInt(json, "order"),
            Sign = GetInt(json, "sign"),
            HasActuals = GetBool(json, "hasActuals")
        };
    }

    private static Currency? ConvertToCurrency(MeshNode node)
    {
        if (node.Content is Currency c)
            return c;
        if (node.Content is not JsonElement json)
            return null;

        return new Currency
        {
            Id = GetString(json, "id") ?? node.Id,
            Name = GetString(json, "name") ?? node.Name ?? node.Id,
            Symbol = GetString(json, "symbol"),
            DecimalPlaces = GetInt(json, "decimalPlaces"),
            Order = GetInt(json, "order")
        };
    }

    private static Country? ConvertToCountry(MeshNode node)
    {
        if (node.Content is Country co)
            return co;
        if (node.Content is not JsonElement json)
            return null;

        return new Country
        {
            Id = GetString(json, "id") ?? node.Id,
            Name = GetString(json, "name") ?? node.Name ?? node.Id,
            Alpha3Code = GetString(json, "alpha3Code"),
            Region = GetString(json, "region"),
            Order = GetInt(json, "order")
        };
    }

    private static ExchangeRate? ConvertToExchangeRate(MeshNode node)
    {
        if (node.Content is ExchangeRate fx)
            return fx;
        if (node.Content is not JsonElement json)
            return null;

        return new ExchangeRate
        {
            SystemName = node.Id,
            DisplayName = node.Name ?? node.Id,
            FromCurrency = GetString(json, "fromCurrency") ?? string.Empty,
            ToCurrency = GetString(json, "toCurrency") ?? string.Empty,
            PlanRate = GetDouble(json, "planRate"),
            ActualRate = GetDouble(json, "actualRate"),
            Order = node.Order ?? GetInt(json, "order")
        };
    }

    private static BusinessUnit? ConvertToBusinessUnit(MeshNode node)
    {
        if (node.Content is BusinessUnit bu)
            return bu;
        if (node.Content is not JsonElement json)
            return null;

        return new BusinessUnit
        {
            Id = GetString(json, "id") ?? node.Id,
            Name = GetString(json, "name") ?? node.Name ?? node.Id,
            Description = GetString(json, "description"),
            Currency = GetString(json, "currency") ?? "USD",
            Region = GetString(json, "region"),
            Icon = GetString(json, "icon") ?? "Building"
        };
    }

    // ---------------------------------------------------------------
    // JSON Helpers
    // ---------------------------------------------------------------

    private static string? GetString(JsonElement json, string property) =>
        json.TryGetProperty(property, out var val) && val.ValueKind == JsonValueKind.String
            ? val.GetString()
            : null;

    private static int GetInt(JsonElement json, string property) =>
        json.TryGetProperty(property, out var val) && val.ValueKind == JsonValueKind.Number
            ? val.GetInt32()
            : 0;

    private static double GetDouble(JsonElement json, string property) =>
        json.TryGetProperty(property, out var val) && val.ValueKind == JsonValueKind.Number
            ? val.GetDouble()
            : 0.0;

    private static bool GetBool(JsonElement json, string property) =>
        json.TryGetProperty(property, out var val) &&
        (val.ValueKind == JsonValueKind.True || val.ValueKind == JsonValueKind.False)
            && val.GetBoolean();
}
