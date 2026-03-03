// <meshweaver>
// Id: FutuReDataLoader
// DisplayName: FutuRe Data Loader
// </meshweaver>

using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Mesh.Contract;
using MeshWeaver.Mesh.Contract.Services;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Loads profitability data for the FutuRe sample.
/// Generates a rolling 18-month window of monthly estimates and actuals
/// from hard-coded base amounts keyed by local (business unit) lines of business.
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
    // Base monthly amounts per LOCAL LoB (in thousands)
    // Format: [Premium, Claims, InternalCost, ExternalCost, CapitalCost, ExpectedProfit]
    // ---------------------------------------------------------------
    // EuropeRe local lines of business
    // ---------------------------------------------------------------
    private static readonly Dictionary<string, double[]> EuropeReBaseAmounts = new()
    {
        ["HOUSEHOLD"]      = [13000, 7800, 1040, 1300, 910, 1950],
        ["MOTOR"]          = [8500,  5100, 680,  850,  595, 1275],
        ["COMM_FIRE"]      = [6500,  3900, 520,  650,  455, 975],
        ["LIABILITY"]      = [7200,  4320, 576,  720,  504, 1080],
        ["TRANSPORT"]      = [4200,  2520, 336,  420,  294, 630],
        ["TECH_RISK"]      = [5000,  3000, 400,  500,  350, 750],
        ["LIFE_HEALTH_EU"] = [8200,  4920, 656,  820,  574, 1230],
        ["SPECIALTY_AVTN"] = [4800,  2880, 384,  480,  336, 720],
    };

    // ---------------------------------------------------------------
    // AmericasIns local lines of business
    // ---------------------------------------------------------------
    private static readonly Dictionary<string, double[]> AmericasInsBaseAmounts = new()
    {
        ["HOMEOWNERS"]       = [16000, 9600, 1280, 1600, 1120, 2400],
        ["WORKERS_COMP"]     = [9000,  5400, 720,  900,  630,  1350],
        ["COMMERCIAL"]       = [10000, 6000, 800,  1000, 700,  1500],
        ["ENERGY_MINING"]    = [7500,  4500, 600,  750,  525,  1125],
        ["LIFE_ANN"]         = [9800,  5880, 784,  980,  686,  1470],
        ["CYBER_TECH"]       = [6000,  3600, 480,  600,  420,  900],
        ["SPECIALTY_AVTN_US"]= [5200,  3120, 416,  520,  364,  780],
        ["AGRICULTURE"]      = [3800,  2280, 304,  380,  266,  570],
    };

    // ---------------------------------------------------------------
    // Seasonal multipliers by month (1=Jan..12=Dec)
    // Keyed by local LoB + amount type for lines with distinct patterns.
    // Lines without a specific key use the default factor of 1.0.
    // ---------------------------------------------------------------
    private static readonly Dictionary<string, double[]> SeasonalFactors = new()
    {
        // Month:                           Jan   Feb   Mar   Apr   May   Jun   Jul   Aug   Sep   Oct   Nov   Dec
        // Property-like lines
        ["HOUSEHOLD_Premium"]             = [0.95, 0.92, 1.00, 1.02, 1.05, 1.08, 1.10, 1.08, 1.05, 1.00, 0.95, 0.90],
        ["HOUSEHOLD_Claims"]              = [1.15, 1.10, 0.95, 0.90, 0.85, 0.90, 1.05, 1.20, 1.35, 1.25, 1.10, 1.15],
        ["HOMEOWNERS_Premium"]            = [0.93, 0.90, 0.98, 1.00, 1.04, 1.08, 1.12, 1.10, 1.06, 1.02, 0.95, 0.88],
        ["HOMEOWNERS_Claims"]             = [1.10, 1.05, 0.90, 0.85, 0.88, 0.95, 1.10, 1.30, 1.40, 1.30, 1.10, 1.08],
        // Casualty-like lines
        ["MOTOR_Claims"]                  = [0.90, 0.85, 0.95, 1.00, 1.05, 1.10, 1.15, 1.10, 1.05, 1.00, 0.95, 0.90],
        ["WORKERS_COMP_Claims"]           = [0.95, 0.90, 1.00, 1.05, 1.08, 1.10, 1.12, 1.10, 1.05, 1.00, 0.92, 0.88],
        // Marine/transport
        ["TRANSPORT_Claims"]              = [1.10, 1.05, 0.95, 0.90, 0.85, 0.90, 0.95, 1.00, 1.05, 1.10, 1.15, 1.20],
        ["COMMERCIAL_Claims"]             = [1.05, 1.00, 0.95, 0.90, 0.92, 1.00, 1.05, 1.08, 1.10, 1.05, 1.00, 0.98],
        // Energy
        ["COMM_FIRE_Claims"]              = [1.05, 1.00, 0.90, 0.85, 0.90, 1.10, 1.15, 1.20, 1.15, 1.00, 0.95, 1.05],
        ["ENERGY_MINING_Claims"]          = [1.08, 1.02, 0.92, 0.88, 0.90, 1.08, 1.12, 1.18, 1.12, 1.02, 0.98, 1.05],
        // Agriculture
        ["AGRICULTURE_Claims"]            = [0.70, 0.75, 0.85, 0.95, 1.10, 1.20, 1.30, 1.25, 1.15, 1.00, 0.85, 0.70],
        // Cyber/tech
        ["TECH_RISK_Claims"]              = [1.20, 1.10, 1.00, 0.90, 0.95, 1.00, 1.05, 0.95, 1.00, 1.10, 1.15, 1.30],
        ["CYBER_TECH_Claims"]             = [1.18, 1.08, 0.98, 0.92, 0.95, 1.02, 1.08, 0.98, 1.02, 1.12, 1.18, 1.28],
    };

    /// <summary>
    /// Hard-coded actual variance factors by month offset (0 = start month).
    /// Represents realistic deviations from estimates.
    /// Actuals only exist for past months (before current month).
    /// </summary>
    private static readonly double[] ActualVarianceFactors =
    [
        1.02, 0.97, 1.05, 0.98, 1.01, 0.96, 1.03, 0.99, 1.04, 0.95,
        1.06, 0.98, 1.01, 0.97, 1.03, 1.00, 0.96, 1.02
    ];

    private static readonly string[] AmountTypeNames =
        ["Premium", "Claims", "InternalCost", "ExternalCost", "CapitalCost", "ExpectedProfit"];

    private static readonly bool[] AmountTypeHasActuals =
        [true, true, false, true, false, false];

    /// <summary>
    /// Loads the full data cube as an observable stream.
    /// Used by the Analysis hub's virtual data source configuration.
    /// </summary>
    public static IObservable<IEnumerable<FutuReDataCube>> LoadDataCube(IWorkspace workspace)
    {
        return Observable.Return(GenerateDataCube());
    }

    /// <summary>
    /// Generates the full data cube from static arrays with dynamic date computation.
    /// Base amounts are keyed by local (business unit) LoB codes.
    /// TransactionMapping is applied to split each local LoB row into one or more
    /// group LoB rows with percentage-weighted amounts.
    /// </summary>
    public static IEnumerable<FutuReDataCube> GenerateDataCube()
    {
        var start = StartDate;
        var end = EndDate;
        var now = DateTime.UtcNow;
        var results = new List<FutuReDataCube>();

        foreach (var (buName, baseAmounts) in new[]
        {
            ("EuropeRe", EuropeReBaseAmounts),
            ("AmericasIns", AmericasInsBaseAmounts)
        })
        {
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
                    var mappings = TransactionMapping.GetMappings(buName, localLobId);
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
                        if (SeasonalFactors.TryGetValue(seasonKey, out var factors))
                            seasonalFactor = factors[monthIndex - 1];

                        var localEstimate = baseAmount * seasonalFactor;

                        // Compute actual for past months with amount types that have actuals
                        double? localActual = null;
                        if (isPastMonth && AmountTypeHasActuals[i])
                        {
                            var varianceIdx = monthOffset % ActualVarianceFactors.Length;
                            localActual = localEstimate * ActualVarianceFactors[varianceIdx];
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
}
