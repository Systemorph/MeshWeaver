// <meshweaver>
// Id: FutuReDataLoader
// DisplayName: FutuRe Data Loader
// </meshweaver>

using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Loads profitability data for the FutuRe sample.
/// Generates a rolling 18-month window of monthly estimates and actuals
/// from CSV-based base amounts keyed by local (business unit) lines of business.
/// Applies TransactionMapping percentage splits to produce group LoB rows.
/// </summary>
public static class FutuReDataLoader
{
    /// <summary>
    /// End of the data window: last day of the previous month.
    /// </summary>
    private static DateTime EndDate
    {
        get
        {
            var now = DateTime.UtcNow;
            return new DateTime(now.Year, now.Month, 1).AddDays(-1);
        }
    }

    /// <summary>
    /// Start of the data window: 18 months before the end date.
    /// </summary>
    private static DateTime StartDate => EndDate.AddMonths(-17).AddDays(1 - EndDate.Day);

    /// <summary>
    /// Gets the quarter label for a given date, e.g. "Q1-2025".
    /// </summary>
    private static string GetQuarter(DateTime date) =>
        $"Q{(date.Month - 1) / 3 + 1}-{date.Year}";

    // ---------------------------------------------------------------
    // CSV-based data loading
    // ---------------------------------------------------------------
    private static string _basePath = "";
    private static string BasePath => _basePath;

    private static readonly ConcurrentDictionary<string, Dictionary<string, double[]>> BaseAmountsCache = new();
    private static readonly ConcurrentDictionary<string, Dictionary<string, double[]>> SeasonalCache = new();

    private static Dictionary<string, double[]> GetBaseAmounts(string businessUnit)
        => BaseAmountsCache.GetOrAdd(businessUnit, LoadBaseAmounts);

    private static Dictionary<string, double[]> GetSeasonalFactors(string businessUnit)
        => SeasonalCache.GetOrAdd(businessUnit, bu =>
        {
            var path = Path.Combine(BasePath, bu, "Profitability", "SeasonalFactors.csv");
            return File.Exists(path) ? LoadSeasonalFactors(bu) : new Dictionary<string, double[]>();
        });

    private static readonly Lazy<double[]> VarianceFactors =
        new(LoadActualVarianceFactors);

    private static Dictionary<string, double[]> LoadBaseAmounts(string businessUnit)
    {
        var path = Path.Combine(BasePath, businessUnit, "Profitability", "BaseAmounts.csv");
        var lines = File.ReadAllLines(path);
        var result = new Dictionary<string, double[]>();
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = SplitCsvLine(line);
            var lob = parts[0];
            var amounts = new double[parts.Length - 1];
            for (int i = 1; i < parts.Length; i++)
                amounts[i - 1] = double.Parse(parts[i], CultureInfo.InvariantCulture);
            result[lob] = amounts;
        }
        return result;
    }

    private static Dictionary<string, double[]> LoadSeasonalFactors(string businessUnit)
    {
        var path = Path.Combine(BasePath, businessUnit, "Profitability", "SeasonalFactors.csv");
        var lines = File.ReadAllLines(path);
        var result = new Dictionary<string, double[]>();
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = SplitCsvLine(line);
            var key = parts[0];
            var factors = new double[12];
            for (int i = 0; i < 12; i++)
                factors[i] = double.Parse(parts[i + 1], CultureInfo.InvariantCulture);
            result[key] = factors;
        }
        return result;
    }

    private static double[] LoadActualVarianceFactors()
    {
        var path = Path.Combine(BasePath, "Profitability", "ActualVarianceFactors.csv");
        var lines = File.ReadAllLines(path);
        return lines.Skip(1)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => double.Parse(l.Trim(), CultureInfo.InvariantCulture))
            .ToArray();
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

    private static readonly string[] AmountTypeNames =
        ["Premium", "Claims", "InternalCost", "ExternalCost", "CapitalCost", "ExpectedProfit"];

    private static readonly bool[] AmountTypeHasActuals =
        [true, true, false, true, false, false];

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
    /// Queries nodes with nodeType:FutuRe/TransactionMapping.
    /// </summary>
    public static IObservable<IEnumerable<TransactionMapping>> LoadTransactionMappingsFromNodes(IWorkspace workspace)
    {
        var meshQuery = workspace.Hub.ServiceProvider.GetRequiredService<IMeshQuery>();

        return meshQuery
            .ObserveQuery<MeshNode>(
                MeshQueryRequest.FromQuery("nodeType:FutuRe/TransactionMapping namespace:FutuRe scope:descendants state:Active"))
            .Select(change => change.Items
                .Select(ConvertToTransactionMapping)
                .Where(m => m != null)
                .Cast<TransactionMapping>());
    }

    /// <summary>
    /// Loads the full data cube as an observable stream.
    /// Combines TransactionMapping from MeshNodes with static base amounts.
    /// </summary>
    public static IObservable<IEnumerable<FutuReDataCube>> LoadDataCube(IWorkspace workspace)
    {
        // Resolve attachment base path from storage configuration
        if (string.IsNullOrEmpty(_basePath))
        {
            var config = workspace.Hub.ServiceProvider.GetService<IConfiguration>();
            var graphRoot = config?["Storage:BasePath"] ?? config?["Graph:Storage:BasePath"] ?? "";
            var combined = Path.Combine(graphRoot, "attachments", "FutuRe");
            // Resolve to absolute path for consistent file access
            _basePath = Path.IsPathRooted(combined)
                ? combined
                : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), combined));
        }

        return LoadTransactionMappingsFromNodes(workspace)
            .Select(mappings => GenerateDataCube(mappings.ToArray()));
    }

    /// <summary>
    /// Generates the full data cube from CSV-loaded data with dynamic date computation.
    /// Base amounts are keyed by local (business unit) LoB codes.
    /// TransactionMapping is applied to split each local LoB row into one or more
    /// group LoB rows with percentage-weighted amounts.
    /// </summary>
    public static IEnumerable<FutuReDataCube> GenerateDataCube(TransactionMapping[] allMappings)
    {
        var start = StartDate;
        var end = EndDate;
        var now = DateTime.UtcNow;
        var results = new List<FutuReDataCube>();

        TransactionMapping[] GetMappings(string bu, string localLoB) =>
            allMappings.Where(m => m.BusinessUnit == bu && m.LocalLineOfBusiness == localLoB).ToArray();

        var businessUnits = allMappings.Select(m => m.BusinessUnit).Distinct();

        foreach (var buName in businessUnits)
        {
            var baseAmounts = GetBaseAmounts(buName);
            var seasonalFactors = GetSeasonalFactors(buName);
            int monthOffset = 0;
            var current = new DateTime(start.Year, start.Month, 1);

            while (current <= end)
            {
                var month = current.ToString("yyyy-MM");
                var quarter = GetQuarter(current);
                var year = current.Year;
                var monthIndex = current.Month; // 1-12
                var isPastMonth = current < new DateTime(now.Year, now.Month, 1);

                foreach (var (localLobId, amounts) in baseAmounts)
                {
                    // Get the TransactionMapping entries for this BU + local LoB
                    var mappings = GetMappings(buName, localLobId);
                    if (mappings.Length == 0)
                        continue;

                    var localLobName = mappings[0].LocalLineOfBusinessName;

                    for (int i = 0; i < AmountTypeNames.Length; i++)
                    {
                        var amountType = AmountTypeNames[i];
                        var baseAmount = amounts[i] * 1000.0; // Convert from thousands

                        // Apply seasonal factor if available
                        var seasonKey = $"{localLobId}_{amountType}";
                        var seasonalFactor = 1.0;
                        if (seasonalFactors.TryGetValue(seasonKey, out var factors))
                            seasonalFactor = factors[monthIndex - 1];

                        var localEstimate = baseAmount * seasonalFactor;

                        // Compute actual for past months with amount types that have actuals
                        double? localActual = null;
                        if (isPastMonth && AmountTypeHasActuals[i])
                        {
                            var varianceIdx = monthOffset % VarianceFactors.Value.Length;
                            localActual = localEstimate * VarianceFactors.Value[varianceIdx];
                        }

                        // Split across group LoBs via TransactionMapping
                        foreach (var mapping in mappings)
                        {
                            var estimate = localEstimate * mapping.Percentage;
                            double? actual = localActual.HasValue
                                ? localActual.Value * mapping.Percentage
                                : null;

                            results.Add(new FutuReDataCube
                            {
                                Id = $"{month}-{localLobId}-{mapping.GroupLineOfBusiness}-{amountType}-{buName}",
                                Month = month,
                                Quarter = quarter,
                                Year = year,
                                LineOfBusiness = mapping.GroupLineOfBusiness,
                                LineOfBusinessName = mapping.GroupLineOfBusinessName,
                                LocalLineOfBusiness = localLobId,
                                LocalLineOfBusinessName = localLobName,
                                AmountType = amountType,
                                BusinessUnit = buName,
                                Estimate = Math.Round(estimate, 0),
                                Actual = actual.HasValue ? Math.Round(actual.Value, 0) : null
                            });
                        }
                    }
                }

                current = current.AddMonths(1);
                monthOffset++;
            }
        }

        return results;
    }

    /// <summary>
    /// Loads LineOfBusiness instances from MeshNode graph via IMeshQuery.
    /// Queries nodes with nodeType:FutuRe/LineOfBusiness in the FutuRe/LineOfBusiness namespace.
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
}
