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
        var address = workspace.Hub.Address.ToString();
        var segments = address.Split('/');
        var businessUnit = segments.Length > 1 ? segments[1] : address;

        return Observable.FromAsync(async ct =>
        {
            var stream = await contentService.GetContentAsync("attachments", "datacube.csv", ct);
            if (stream == null)
                return new List<FutuReDataCube>();
            using var reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync(ct);
            return ParseLocalCsvContent(content, businessUnit);
        }).CombineLatest(
            LoadLocalLinesOfBusiness(workspace),
            (csvRows, lobs) =>
            {
                var lobLookup = lobs.ToDictionary(l => l.SystemName, l => l.DisplayName);
                return csvRows.Select(row => row with
                {
                    LineOfBusinessName = lobLookup.GetValueOrDefault(row.LineOfBusiness, row.LineOfBusiness),
                    LocalLineOfBusinessName = lobLookup.GetValueOrDefault(row.LocalLineOfBusiness, row.LocalLineOfBusiness)
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
    /// transaction mapping rules. Each local row is split by percentage
    /// into one or more group LoB rows.
    /// </summary>
    public static IEnumerable<FutuReDataCube> AggregateToGroupLevel(
        IEnumerable<FutuReDataCube> localRows,
        IEnumerable<TransactionMapping> mappings,
        IEnumerable<LineOfBusiness> groupLobs)
    {
        var mappingLookup = mappings
            .GroupBy(m => (m.BusinessUnit, m.LocalLineOfBusiness))
            .ToDictionary(g => g.Key, g => g.ToList());
        var lobLookup = groupLobs
            .ToDictionary(l => l.SystemName, l => l.DisplayName);

        return localRows.SelectMany(row =>
        {
            var key = (row.BusinessUnit, row.LocalLineOfBusiness);
            if (!mappingLookup.TryGetValue(key, out var rules))
                return Enumerable.Empty<FutuReDataCube>();

            return rules.Select(rule => row with
            {
                Id = $"{row.Month}-{rule.GroupLineOfBusiness}-{row.AmountType}-{row.BusinessUnit}-{row.LocalLineOfBusiness}",
                LineOfBusiness = rule.GroupLineOfBusiness,
                LineOfBusinessName = lobLookup.GetValueOrDefault(
                    rule.GroupLineOfBusiness, rule.GroupLineOfBusiness),
                Estimate = row.Estimate * rule.Percentage,
                Actual = row.Actual.HasValue
                    ? row.Actual.Value * rule.Percentage
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

        var meshQuery = workspace.Hub.ServiceProvider.GetRequiredService<IMeshQuery>();
        return meshQuery
            .ObserveQuery<MeshNode>(
                MeshQueryRequest.FromQuery(
                    $"nodeType:FutuRe/LineOfBusiness namespace:{buNamespace}/LineOfBusiness scope:children state:Active"))
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
    /// Loads AmountType reference data from MeshNodes via IMeshQuery.
    /// </summary>
    public static IObservable<IEnumerable<AmountType>> LoadAmountTypes(IWorkspace workspace)
    {
        var meshQuery = workspace.Hub.ServiceProvider.GetRequiredService<IMeshQuery>();
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
    /// Loads Currency reference data from MeshNodes via IMeshQuery.
    /// </summary>
    public static IObservable<IEnumerable<Currency>> LoadCurrencies(IWorkspace workspace)
    {
        var meshQuery = workspace.Hub.ServiceProvider.GetRequiredService<IMeshQuery>();
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
    /// Loads Country reference data from MeshNodes via IMeshQuery.
    /// </summary>
    public static IObservable<IEnumerable<Country>> LoadCountries(IWorkspace workspace)
    {
        var meshQuery = workspace.Hub.ServiceProvider.GetRequiredService<IMeshQuery>();
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
    /// Loads TransactionMapping instances from MeshNode graph via IMeshQuery.
    /// </summary>
    public static IObservable<IEnumerable<TransactionMapping>> LoadTransactionMappingsFromNodes(IWorkspace workspace)
    {
        var meshQuery = workspace.Hub.ServiceProvider.GetRequiredService<IMeshQuery>();

        return meshQuery
            .ObserveQuery<MeshNode>(
                MeshQueryRequest.FromQuery("nodeType:FutuRe/TransactionMapping namespace:FutuRe scope:descendants"))
            .Select(change => change.Items
                .Select(ConvertToTransactionMapping)
                .Where(m => m != null)
                .Cast<TransactionMapping>());
    }

    /// <summary>
    /// Loads group-level LineOfBusiness instances from MeshNode graph via IMeshQuery.
    /// </summary>
    public static IObservable<IEnumerable<LineOfBusiness>> LoadLinesOfBusinessFromNodes(IWorkspace workspace)
    {
        var meshQuery = workspace.Hub.ServiceProvider.GetRequiredService<IMeshQuery>();

        return meshQuery
            .ObserveQuery<MeshNode>(
                MeshQueryRequest.FromQuery("nodeType:FutuRe/LineOfBusiness namespace:FutuRe/LineOfBusiness scope:children state:Active"))
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
        if (node.Content is not JsonElement json)
            return null;

        return new LineOfBusiness
        {
            SystemName = GetString(json, "systemName") ?? node.Id,
            DisplayName = GetString(json, "displayName") ?? node.Name ?? node.Id,
            Description = GetString(json, "description"),
            Order = GetInt(json, "order"),
            ProductExamples = GetString(json, "productExamples")
        };
    }

    private static AmountType? ConvertToAmountType(MeshNode node)
    {
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
