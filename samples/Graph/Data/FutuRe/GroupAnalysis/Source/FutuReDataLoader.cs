using System.Globalization;
using System.IO;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
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
        var hub = workspace.Hub;
        var address = workspace.Hub.Address.ToString();
        var segments = address.Split('/');
        var businessUnit = segments.Length > 1 ? segments[1] : address;
        var buPath = segments.Length > 1 ? $"{segments[0]}/{segments[1]}" : segments[0];

        // BU node lookup goes through the per-node MeshNodeReference reducer (authoritative,
        // no read-side index lag). CSV I/O stays on the bounded FileSystem IoPool at the file
        // boundary. Both compose into the final tuple.
        //
        // Cross-ALC type-identity is fragile here: BusinessUnit gets compiled into
        // FutuRe_BusinessUnit's NodeAssemblyLoadContext while this loader lives in
        // FutuRe_LocalAnalysis's ALC, so `Content is BusinessUnit` returns false even though the
        // runtime types match by full name. ContentAs<T> is precisely the accessor for that: it
        // covers the already-typed value, the degraded JsonElement/JsonNode (the
        // deserialized-to-JSON path), AND the same-short-named foreign type by JSON round-trip —
        // which is what the hand-rolled casing probe plus a reflected `Currency` property were
        // approximating, one shape at a time and with no diagnosis when all of them missed.
        var buOptions = hub.JsonSerializerOptions;
        var buCurrencyObs = hub.GetMeshNode(buPath, TimeSpan.FromSeconds(10))
            .Select(buNode =>
            {
                if (buNode.ContentAs<BusinessUnit>(buOptions)?.Currency is { Length: > 0 } currency)
                    return currency;

                // Fallback by hub address — every BU's local currency is known statically;
                // this keeps the local Analysis dashboard labelling correct when the
                // cross-hub MeshNode lookup fails (e.g. BU hub not yet activated, or
                // running in a partial test context).
                return businessUnit switch
                {
                    "EuropeRe" => "EUR",
                    "AmericasIns" => "USD",
                    "AsiaRe" => "JPY",
                    _ => "CHF"
                };
            });

        // CSV I/O on the bounded FileSystem IoPool — no Observable.FromAsync, no async/await.
        // The content fetch runs on the collection's own pool (the whole IContentService surface
        // is pooled observables). InvokeBlocking() runs the sync StreamReader read + CSV parse on
        // the pool's dedicated limited-concurrency scheduler, so the blocking read can't trigger
        // ThreadPool thread-injection that starves the grain schedulers.
        // Both are cold IObservables — reactive end-to-end, no async state machine.
        var ioPool = workspace.Hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.FileSystem)
                     ?? IoPool.Unbounded;
        var csvRowsObs = contentService
            .GetContent("attachments", "datacube.csv")
            .SelectMany(stream => stream is null
                ? Observable.Return(new List<FutuReDataCube>())
                : ioPool.InvokeBlocking(_ =>
                {
                    using var reader = new StreamReader(stream);
                    return ParseLocalCsvContent(reader.ReadToEnd(), businessUnit);
                }));

        return buCurrencyObs
            .SelectMany(currency => csvRowsObs.Select(rows => (Rows: rows, Currency: currency)))
            .CombineLatest(
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
        var options = workspace.Hub.JsonSerializerOptions;
        return meshQuery
            .Query<MeshNode>(
                MeshQueryRequest.FromQuery(
                    $"nodeType:FutuRe/LineOfBusiness namespace:{buNamespace}/LineOfBusiness state:Active"))
            .Select(change => change.Items
                .Select(node => ConvertToLineOfBusiness(node, options))
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
        var options = workspace.Hub.JsonSerializerOptions;
        return meshQuery
            .Query<MeshNode>(
                MeshQueryRequest.FromQuery("nodeType:FutuRe/AmountType namespace:FutuRe/AmountType state:Active"))
            .Select(change => change.Items
                .Select(node => ConvertToAmountType(node, options))
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
        var options = workspace.Hub.JsonSerializerOptions;
        return meshQuery
            .Query<MeshNode>(
                MeshQueryRequest.FromQuery("nodeType:FutuRe/Currency namespace:FutuRe/Currency state:Active"))
            .Select(change => change.Items
                .Select(node => ConvertToCurrency(node, options))
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
        var options = workspace.Hub.JsonSerializerOptions;
        return meshQuery
            .Query<MeshNode>(
                MeshQueryRequest.FromQuery("nodeType:FutuRe/Country namespace:FutuRe/Country state:Active"))
            .Select(change => change.Items
                .Select(node => ConvertToCountry(node, options))
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
        var options = workspace.Hub.JsonSerializerOptions;

        return meshQuery
            .Query<MeshNode>(
                MeshQueryRequest.FromQuery("nodeType:FutuRe/TransactionMapping namespace:FutuRe scope:descendants"))
            .Select(change => change.Items
                .Select(node => ConvertToTransactionMapping(node, options))
                .Where(m => m != null)
                .Cast<TransactionMapping>());
    }

    /// <summary>
    /// Loads ExchangeRate reference data from MeshNodes via IMeshService.
    /// </summary>
    public static IObservable<IEnumerable<ExchangeRate>> LoadExchangeRates(IWorkspace workspace)
    {
        var meshQuery = workspace.Hub.ServiceProvider.GetRequiredService<IMeshService>();
        var options = workspace.Hub.JsonSerializerOptions;
        return meshQuery
            .Query<MeshNode>(
                MeshQueryRequest.FromQuery("nodeType:FutuRe/ExchangeRate namespace:FutuRe/ExchangeRate state:Active"))
            .Select(change => change.Items
                .Select(node => ConvertToExchangeRate(node, options))
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
        var options = workspace.Hub.JsonSerializerOptions;
        return meshQuery
            .Query<MeshNode>(
                MeshQueryRequest.FromQuery("nodeType:FutuRe/BusinessUnit namespace:FutuRe state:Active"))
            .Select(change => change.Items
                .Select(node => ConvertToBusinessUnit(node, options))
                .Where(bu => bu != null)
                .Cast<BusinessUnit>());
    }

    /// <summary>
    /// Loads group-level LineOfBusiness instances from MeshNode graph via IMeshService.
    /// </summary>
    public static IObservable<IEnumerable<LineOfBusiness>> LoadLinesOfBusinessFromNodes(IWorkspace workspace)
    {
        var meshQuery = workspace.Hub.ServiceProvider.GetRequiredService<IMeshService>();
        var options = workspace.Hub.JsonSerializerOptions;

        return meshQuery
            .Query<MeshNode>(
                MeshQueryRequest.FromQuery("nodeType:FutuRe/LineOfBusiness namespace:FutuRe/LineOfBusiness state:Active"))
            .Select(change => change.Items
                .Select(node => ConvertToLineOfBusiness(node, options))
                .Where(lob => lob != null)
                .Cast<LineOfBusiness>()
                .OrderBy(lob => lob.Order));
    }

    // ---------------------------------------------------------------
    // MeshNode → Record Converters
    // ---------------------------------------------------------------
    //
    // 🚨 Every one of these reads the node's content through ContentAs<T>(hub options) — NEVER
    // `Content is T` plus a hand-rolled JsonElement branch. That hand-rolled shape was correct for
    // the two cases it enumerated and silently wrong for the one that bites hardest HERE: a
    // same-short-named record from another build. These converters run in FutuRe/GroupAnalysis's
    // NodeAssemblyLoadContext while BusinessUnit, LineOfBusiness et al. are compiled into their OWN
    // NodeTypes' ALCs, and every recompile mints a fresh collectible assembly — so a content value
    // the owning hub's registry DID resolve arrives typed as a foreign CLR identity, fails
    // `Content is T`, is not a JsonElement either, and fell through to `null`. The row then simply
    // vanished from the cube with no exception and no log line. ContentAs recovers exactly that by
    // JSON round-trip (and the degraded JsonElement/JsonNode, and the already-typed value).
    //
    // The node-level overlays below are NOT fallbacks for a failed cast — they are the node's own
    // identity (Id / Name / Order) taking precedence over, or standing in for, what the content
    // carries, exactly as the JSON branch did.

    private static TransactionMapping? ConvertToTransactionMapping(MeshNode node, JsonSerializerOptions options)
    {
        var mapping = node.ContentAs<TransactionMapping>(options);
        if (mapping is null)
            return null;

        return string.IsNullOrEmpty(mapping.Id) ? mapping with { Id = node.Id } : mapping;
    }

    private static LineOfBusiness? ConvertToLineOfBusiness(MeshNode node, JsonSerializerOptions options)
    {
        var lob = node.ContentAs<LineOfBusiness>(options);
        if (lob is null)
            return null;

        return lob with
        {
            SystemName = node.Id,
            DisplayName = node.Name ?? node.Id,
            Order = node.Order ?? lob.Order
        };
    }

    private static AmountType? ConvertToAmountType(MeshNode node, JsonSerializerOptions options)
    {
        var amountType = node.ContentAs<AmountType>(options);
        if (amountType is null)
            return null;

        return amountType with
        {
            SystemName = string.IsNullOrEmpty(amountType.SystemName) ? node.Id : amountType.SystemName,
            DisplayName = string.IsNullOrEmpty(amountType.DisplayName)
                ? node.Name ?? node.Id
                : amountType.DisplayName
        };
    }

    private static Currency? ConvertToCurrency(MeshNode node, JsonSerializerOptions options)
    {
        var currency = node.ContentAs<Currency>(options);
        if (currency is null)
            return null;

        return currency with
        {
            Id = string.IsNullOrEmpty(currency.Id) ? node.Id : currency.Id,
            Name = string.IsNullOrEmpty(currency.Name) ? node.Name ?? node.Id : currency.Name
        };
    }

    private static Country? ConvertToCountry(MeshNode node, JsonSerializerOptions options)
    {
        var country = node.ContentAs<Country>(options);
        if (country is null)
            return null;

        return country with
        {
            Id = string.IsNullOrEmpty(country.Id) ? node.Id : country.Id,
            Name = string.IsNullOrEmpty(country.Name) ? node.Name ?? node.Id : country.Name
        };
    }

    private static ExchangeRate? ConvertToExchangeRate(MeshNode node, JsonSerializerOptions options)
    {
        var fx = node.ContentAs<ExchangeRate>(options);
        if (fx is null)
            return null;

        return fx with
        {
            SystemName = node.Id,
            DisplayName = node.Name ?? node.Id,
            Order = node.Order ?? fx.Order
        };
    }

    private static BusinessUnit? ConvertToBusinessUnit(MeshNode node, JsonSerializerOptions options)
    {
        var bu = node.ContentAs<BusinessUnit>(options);
        if (bu is null)
            return null;

        return bu with
        {
            Id = string.IsNullOrEmpty(bu.Id) ? node.Id : bu.Id,
            Name = string.IsNullOrEmpty(bu.Name) ? node.Name ?? node.Id : bu.Name
        };
    }
}