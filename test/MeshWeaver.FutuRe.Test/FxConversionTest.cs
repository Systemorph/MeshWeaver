using Xunit;

namespace MeshWeaver.FutuRe.Test;

public class FxConversionTest
{
    private static readonly FutuReDataCube[] LocalRows =
    [
        new()
        {
            Id = "2025-01-HOUSEHOLD-Premium-EuropeRe",
            Month = "2025-01", Quarter = "Q1-2025", Year = 2025,
            LineOfBusiness = "HOUSEHOLD", LocalLineOfBusiness = "HOUSEHOLD",
            AmountType = "Premium", BusinessUnit = "EuropeRe",
            Estimate = 1000, Actual = 900
        },
        new()
        {
            Id = "2025-01-CASUALTY-Premium-AmericasIns",
            Month = "2025-01", Quarter = "Q1-2025", Year = 2025,
            LineOfBusiness = "CASUALTY", LocalLineOfBusiness = "CASUALTY",
            AmountType = "Premium", BusinessUnit = "AmericasIns",
            Estimate = 500, Actual = 450
        },
        new()
        {
            Id = "2025-01-HOUSEHOLD-Claims-EuropeRe",
            Month = "2025-01", Quarter = "Q1-2025", Year = 2025,
            LineOfBusiness = "HOUSEHOLD", LocalLineOfBusiness = "HOUSEHOLD",
            AmountType = "Claims", BusinessUnit = "EuropeRe",
            Estimate = 600, Actual = null
        }
    ];

    private static readonly TransactionMapping[] Mappings =
    [
        new()
        {
            Id = "EUR-HOUSEHOLD-PROPERTY",
            BusinessUnit = "EuropeRe", LocalLineOfBusiness = "HOUSEHOLD",
            GroupLineOfBusiness = "PROPERTY", Percentage = 1.0
        },
        new()
        {
            Id = "AME-CASUALTY-CASUALTY",
            BusinessUnit = "AmericasIns", LocalLineOfBusiness = "CASUALTY",
            GroupLineOfBusiness = "CASUALTY", Percentage = 1.0
        }
    ];

    private static readonly LineOfBusiness[] GroupLobs =
    [
        new() { SystemName = "PROPERTY", DisplayName = "Property" },
        new() { SystemName = "CASUALTY", DisplayName = "Casualty" }
    ];

    private static readonly ExchangeRate[] ExchangeRates =
    [
        new() { Id = "EUR-CHF", FromCurrency = "EUR", ToCurrency = "CHF", PlanRate = 0.94, ActualRate = 0.92 },
        new() { Id = "USD-CHF", FromCurrency = "USD", ToCurrency = "CHF", PlanRate = 0.89, ActualRate = 0.87 }
    ];

    private static readonly BusinessUnit[] BusinessUnits =
    [
        new() { Id = "EuropeRe", Name = "Europe Re", Currency = "EUR" },
        new() { Id = "AmericasIns", Name = "Americas Ins", Currency = "USD" }
    ];

    [Fact]
    public void PlanMode_BothAmountsUsePlanRate()
    {
        // Act — Plan (CHF): Estimate × PlanRate, Actual × PlanRate
        var result = FutuReDataLoader.AggregateToGroupLevel(
            LocalRows, Mappings, GroupLobs, ExchangeRates, BusinessUnits,
            CurrencyModes.PlanChf
        ).ToList();

        Assert.Equal(3, result.Count);

        // EuropeRe Premium: Estimate 1000 * 0.94 = 940, Actual 900 * 0.94 = 846
        var eurPremium = result.Single(r => r.BusinessUnit == "EuropeRe" && r.AmountType == "Premium");
        Assert.Equal(940, eurPremium.Estimate, precision: 2);
        Assert.NotNull(eurPremium.Actual);
        Assert.Equal(846, eurPremium.Actual!.Value, precision: 2);
        Assert.Equal("CHF", eurPremium.Currency);
        Assert.Equal("PROPERTY", eurPremium.LineOfBusiness);
        Assert.Equal("Property", eurPremium.LineOfBusinessName);

        // AmericasIns Premium: Estimate 500 * 0.89 = 445, Actual 450 * 0.89 = 400.5
        var usdPremium = result.Single(r => r.BusinessUnit == "AmericasIns" && r.AmountType == "Premium");
        Assert.Equal(445, usdPremium.Estimate, precision: 2);
        Assert.NotNull(usdPremium.Actual);
        Assert.Equal(400.5, usdPremium.Actual!.Value, precision: 2);
        Assert.Equal("CHF", usdPremium.Currency);

        // EuropeRe Claims: Estimate 600 * 0.94 = 564, Actual stays null
        var eurClaims = result.Single(r => r.BusinessUnit == "EuropeRe" && r.AmountType == "Claims");
        Assert.Equal(564, eurClaims.Estimate, precision: 2);
        Assert.Null(eurClaims.Actual);
        Assert.Null(eurClaims.Variance);
        Assert.Equal("CHF", eurClaims.Currency);
    }

    [Fact]
    public void ActualsMode_BothAmountsUseActualRate()
    {
        // Act — Actuals (CHF): Estimate × ActualRate, Actual × ActualRate
        var result = FutuReDataLoader.AggregateToGroupLevel(
            LocalRows, Mappings, GroupLobs, ExchangeRates, BusinessUnits,
            CurrencyModes.ActualsChf
        ).ToList();

        Assert.Equal(3, result.Count);

        // EuropeRe Premium: Estimate 1000 * 0.92 = 920, Actual 900 * 0.92 = 828
        var eurPremium = result.Single(r => r.BusinessUnit == "EuropeRe" && r.AmountType == "Premium");
        Assert.Equal(920, eurPremium.Estimate, precision: 2);
        Assert.NotNull(eurPremium.Actual);
        Assert.Equal(828, eurPremium.Actual!.Value, precision: 2);
        Assert.Equal("CHF", eurPremium.Currency);

        // AmericasIns Premium: Estimate 500 * 0.87 = 435, Actual 450 * 0.87 = 391.5
        var usdPremium = result.Single(r => r.BusinessUnit == "AmericasIns" && r.AmountType == "Premium");
        Assert.Equal(435, usdPremium.Estimate, precision: 2);
        Assert.NotNull(usdPremium.Actual);
        Assert.Equal(391.5, usdPremium.Actual!.Value, precision: 2);
        Assert.Equal("CHF", usdPremium.Currency);

        // EuropeRe Claims: Estimate 600 * 0.92 = 552, Actual stays null
        var eurClaims = result.Single(r => r.BusinessUnit == "EuropeRe" && r.AmountType == "Claims");
        Assert.Equal(552, eurClaims.Estimate, precision: 2);
        Assert.Null(eurClaims.Actual);
        Assert.Equal("CHF", eurClaims.Currency);
    }

    [Fact]
    public void OriginalMode_NoFxConversion()
    {
        // Act — Original Currency: no conversion, amounts stay in local currency
        var result = FutuReDataLoader.AggregateToGroupLevel(
            LocalRows, Mappings, GroupLobs, ExchangeRates, BusinessUnits,
            CurrencyModes.OriginalCurrency
        ).ToList();

        Assert.Equal(3, result.Count);

        // EuropeRe Premium: Estimate 1000 (no FX), Actual 900 (no FX), Currency = "EUR"
        var eurPremium = result.Single(r => r.BusinessUnit == "EuropeRe" && r.AmountType == "Premium");
        Assert.Equal(1000, eurPremium.Estimate, precision: 2);
        Assert.NotNull(eurPremium.Actual);
        Assert.Equal(900, eurPremium.Actual!.Value, precision: 2);
        Assert.Equal("EUR", eurPremium.Currency);
        Assert.Equal("PROPERTY", eurPremium.LineOfBusiness);

        // AmericasIns Premium: Estimate 500, Actual 450, Currency = "USD"
        var usdPremium = result.Single(r => r.BusinessUnit == "AmericasIns" && r.AmountType == "Premium");
        Assert.Equal(500, usdPremium.Estimate, precision: 2);
        Assert.NotNull(usdPremium.Actual);
        Assert.Equal(450, usdPremium.Actual!.Value, precision: 2);
        Assert.Equal("USD", usdPremium.Currency);

        // EuropeRe Claims: Estimate 600, Actual null, Currency = "EUR"
        var eurClaims = result.Single(r => r.BusinessUnit == "EuropeRe" && r.AmountType == "Claims");
        Assert.Equal(600, eurClaims.Estimate, precision: 2);
        Assert.Null(eurClaims.Actual);
        Assert.Equal("EUR", eurClaims.Currency);
    }

    [Fact]
    public void DefaultCurrencyMode_UsesPlanRate()
    {
        // Act — default parameter (no currencyMode) should use Plan (CHF)
        var result = FutuReDataLoader.AggregateToGroupLevel(
            LocalRows, Mappings, GroupLobs, ExchangeRates, BusinessUnits
        ).ToList();

        var eurPremium = result.Single(r => r.BusinessUnit == "EuropeRe" && r.AmountType == "Premium");
        Assert.Equal(940, eurPremium.Estimate, precision: 2);
        Assert.Equal(846, eurPremium.Actual!.Value, precision: 2);
        Assert.Equal("CHF", eurPremium.Currency);
    }

    [Fact]
    public void PartialPercentageMapping_SplitsRowCorrectly()
    {
        // 1 local row: HOUSEHOLD Premium, Estimate=1000, Actual=900, BU=EuropeRe
        var rows = new[]
        {
            new FutuReDataCube
            {
                Id = "2025-01-HOUSEHOLD-Premium-EuropeRe",
                Month = "2025-01", Quarter = "Q1-2025", Year = 2025,
                LineOfBusiness = "HOUSEHOLD", LocalLineOfBusiness = "HOUSEHOLD",
                AmountType = "Premium", BusinessUnit = "EuropeRe",
                Estimate = 1000, Actual = 900
            }
        };

        // 2 mappings: HOUSEHOLD → PROPERTY at 60%, HOUSEHOLD → CASUALTY at 40%
        var mappings = new[]
        {
            new TransactionMapping
            {
                Id = "EUR-HOUSEHOLD-PROPERTY", BusinessUnit = "EuropeRe",
                LocalLineOfBusiness = "HOUSEHOLD", GroupLineOfBusiness = "PROPERTY",
                Percentage = 0.6
            },
            new TransactionMapping
            {
                Id = "EUR-HOUSEHOLD-CASUALTY", BusinessUnit = "EuropeRe",
                LocalLineOfBusiness = "HOUSEHOLD", GroupLineOfBusiness = "CASUALTY",
                Percentage = 0.4
            }
        };

        var result = FutuReDataLoader.AggregateToGroupLevel(
            rows, mappings, GroupLobs, ExchangeRates, BusinessUnits,
            CurrencyModes.PlanChf
        ).ToList();

        Assert.Equal(2, result.Count);

        // PROPERTY: Estimate = 1000 × 0.6 × 0.94 = 564, Actual = 900 × 0.6 × 0.94 = 507.6
        var property = result.Single(r => r.LineOfBusiness == "PROPERTY");
        Assert.Equal(564, property.Estimate, precision: 2);
        Assert.NotNull(property.Actual);
        Assert.Equal(507.6, property.Actual!.Value, precision: 2);

        // CASUALTY: Estimate = 1000 × 0.4 × 0.94 = 376, Actual = 900 × 0.4 × 0.94 = 338.4
        var casualty = result.Single(r => r.LineOfBusiness == "CASUALTY");
        Assert.Equal(376, casualty.Estimate, precision: 2);
        Assert.NotNull(casualty.Actual);
        Assert.Equal(338.4, casualty.Actual!.Value, precision: 2);
    }

    [Fact]
    public void MissingMapping_DropsRow()
    {
        var rows = new[]
        {
            new FutuReDataCube
            {
                Id = "2025-01-UNKNOWN-Premium-EuropeRe",
                Month = "2025-01", Quarter = "Q1-2025", Year = 2025,
                LineOfBusiness = "UNKNOWN", LocalLineOfBusiness = "UNKNOWN",
                AmountType = "Premium", BusinessUnit = "EuropeRe",
                Estimate = 1000, Actual = 900
            }
        };

        var result = FutuReDataLoader.AggregateToGroupLevel(
            rows, Mappings, GroupLobs, ExchangeRates, BusinessUnits,
            CurrencyModes.PlanChf
        ).ToList();

        Assert.Empty(result);
    }

    [Fact]
    public void MissingExchangeRate_FallsBackToOne()
    {
        // BU with currency "JPY" — no JPY exchange rate in the rates array
        var rows = new[]
        {
            new FutuReDataCube
            {
                Id = "2025-01-MARINE-Premium-AsiaRe",
                Month = "2025-01", Quarter = "Q1-2025", Year = 2025,
                LineOfBusiness = "MARINE", LocalLineOfBusiness = "MARINE",
                AmountType = "Premium", BusinessUnit = "AsiaRe",
                Estimate = 5000, Actual = 4500
            }
        };

        var mappings = new[]
        {
            new TransactionMapping
            {
                Id = "ASIA-MARINE-MARINE", BusinessUnit = "AsiaRe",
                LocalLineOfBusiness = "MARINE", GroupLineOfBusiness = "MARINE",
                Percentage = 1.0
            }
        };

        var lobs = new[] { new LineOfBusiness { SystemName = "MARINE", DisplayName = "Marine" } };
        var bus = new[] { new BusinessUnit { Id = "AsiaRe", Name = "Asia Re", Currency = "JPY" } };

        // No JPY rate — only EUR and USD rates provided
        var result = FutuReDataLoader.AggregateToGroupLevel(
            rows, mappings, lobs, ExchangeRates, bus,
            CurrencyModes.PlanChf
        ).ToList();

        Assert.Single(result);
        var row = result[0];
        // Fallback rate = 1.0, so amounts unchanged
        Assert.Equal(5000, row.Estimate, precision: 2);
        Assert.NotNull(row.Actual);
        Assert.Equal(4500, row.Actual!.Value, precision: 2);
        Assert.Equal("CHF", row.Currency);
    }

    // ---------------------------------------------------------------
    // Variance computed property
    // ---------------------------------------------------------------

    [Fact]
    public void Variance_PositiveWhenActualExceedsEstimate()
    {
        var row = new FutuReDataCube { Estimate = 1000, Actual = 1200 };
        Assert.NotNull(row.Variance);
        Assert.Equal(200, row.Variance!.Value, precision: 2);
    }

    [Fact]
    public void Variance_NegativeWhenActualBelowEstimate()
    {
        var row = new FutuReDataCube { Estimate = 1000, Actual = 800 };
        Assert.NotNull(row.Variance);
        Assert.Equal(-200, row.Variance!.Value, precision: 2);
    }

    [Fact]
    public void Variance_NullWhenActualIsNull()
    {
        var row = new FutuReDataCube { Estimate = 1000, Actual = null };
        Assert.Null(row.Variance);
    }

    // ---------------------------------------------------------------
    // Multi-amount-type aggregation
    // ---------------------------------------------------------------

    [Fact]
    public void AllAmountTypes_ConvertedCorrectly()
    {
        // One BU, one month, all 6 amount types
        var rows = new[]
        {
            AmountTypes.Premium, AmountTypes.Claims, AmountTypes.InternalCost,
            AmountTypes.ExternalCost, AmountTypes.CapitalCost, AmountTypes.ExpectedProfit
        }.Select(at => new FutuReDataCube
        {
            Id = $"2025-01-HOUSEHOLD-{at}-EuropeRe",
            Month = "2025-01", Quarter = "Q1-2025", Year = 2025,
            LineOfBusiness = "HOUSEHOLD", LocalLineOfBusiness = "HOUSEHOLD",
            AmountType = at, BusinessUnit = "EuropeRe",
            Estimate = 1000, Actual = at is AmountTypes.InternalCost or AmountTypes.CapitalCost or AmountTypes.ExpectedProfit ? null : 900
        }).ToArray();

        var result = FutuReDataLoader.AggregateToGroupLevel(
            rows, Mappings, GroupLobs, ExchangeRates, BusinessUnits,
            CurrencyModes.PlanChf
        ).ToList();

        // All 6 amount types should be present — no rows lost
        Assert.Equal(6, result.Count);

        foreach (var row in result)
        {
            Assert.Equal("CHF", row.Currency);
            Assert.Equal("PROPERTY", row.LineOfBusiness);
            // Estimate = 1000 * 1.0 (mapping %) * 0.94 (EUR→CHF plan) = 940
            Assert.Equal(940, row.Estimate, precision: 2);
        }

        // Rows with actuals: Premium, Claims, ExternalCost
        var withActuals = result.Where(r =>
            r.AmountType is AmountTypes.Premium or AmountTypes.Claims or AmountTypes.ExternalCost).ToList();
        Assert.Equal(3, withActuals.Count);
        foreach (var row in withActuals)
        {
            Assert.NotNull(row.Actual);
            Assert.Equal(846, row.Actual!.Value, precision: 2); // 900 * 0.94
        }

        // Rows without actuals: InternalCost, CapitalCost, ExpectedProfit
        var withoutActuals = result.Where(r =>
            r.AmountType is AmountTypes.InternalCost or AmountTypes.CapitalCost or AmountTypes.ExpectedProfit).ToList();
        Assert.Equal(3, withoutActuals.Count);
        foreach (var row in withoutActuals)
        {
            Assert.Null(row.Actual);
            Assert.Null(row.Variance);
        }
    }

    // ---------------------------------------------------------------
    // CHF business unit — no FX conversion needed
    // ---------------------------------------------------------------

    [Fact]
    public void ChfBusinessUnit_NoConversionApplied()
    {
        var rows = new[]
        {
            new FutuReDataCube
            {
                Id = "2025-01-PROPERTY-Premium-SwissRe",
                Month = "2025-01", Quarter = "Q1-2025", Year = 2025,
                LineOfBusiness = "PROPERTY", LocalLineOfBusiness = "PROPERTY",
                AmountType = "Premium", BusinessUnit = "SwissRe",
                Estimate = 2000, Actual = 1800
            }
        };

        var mappings = new[]
        {
            new TransactionMapping
            {
                Id = "CHE-PROPERTY-PROPERTY", BusinessUnit = "SwissRe",
                LocalLineOfBusiness = "PROPERTY", GroupLineOfBusiness = "PROPERTY",
                Percentage = 1.0
            }
        };

        var bus = new[] { new BusinessUnit { Id = "SwissRe", Name = "Swiss Re", Currency = "CHF" } };

        // No CHF→CHF exchange rate in the table — rate should default to 1.0
        var result = FutuReDataLoader.AggregateToGroupLevel(
            rows, mappings, GroupLobs, ExchangeRates, bus,
            CurrencyModes.PlanChf
        ).ToList();

        Assert.Single(result);
        var row = result[0];
        Assert.Equal(2000, row.Estimate, precision: 2);
        Assert.NotNull(row.Actual);
        Assert.Equal(1800, row.Actual!.Value, precision: 2);
        Assert.Equal("CHF", row.Currency);
        Assert.Equal(-200, row.Variance!.Value, precision: 2);
    }

    // ---------------------------------------------------------------
    // Multi-month data — preserves monthly granularity
    // ---------------------------------------------------------------

    [Fact]
    public void MultipleMonths_PreservesMonthlyGranularity()
    {
        var rows = new[]
        {
            new FutuReDataCube
            {
                Id = "2025-01-HOUSEHOLD-Premium-EuropeRe",
                Month = "2025-01", Quarter = "Q1-2025", Year = 2025,
                LineOfBusiness = "HOUSEHOLD", LocalLineOfBusiness = "HOUSEHOLD",
                AmountType = "Premium", BusinessUnit = "EuropeRe",
                Estimate = 1000, Actual = 900
            },
            new FutuReDataCube
            {
                Id = "2025-02-HOUSEHOLD-Premium-EuropeRe",
                Month = "2025-02", Quarter = "Q1-2025", Year = 2025,
                LineOfBusiness = "HOUSEHOLD", LocalLineOfBusiness = "HOUSEHOLD",
                AmountType = "Premium", BusinessUnit = "EuropeRe",
                Estimate = 1100, Actual = 1050
            },
            new FutuReDataCube
            {
                Id = "2025-03-HOUSEHOLD-Premium-EuropeRe",
                Month = "2025-03", Quarter = "Q1-2025", Year = 2025,
                LineOfBusiness = "HOUSEHOLD", LocalLineOfBusiness = "HOUSEHOLD",
                AmountType = "Premium", BusinessUnit = "EuropeRe",
                Estimate = 1200, Actual = null
            }
        };

        var result = FutuReDataLoader.AggregateToGroupLevel(
            rows, Mappings, GroupLobs, ExchangeRates, BusinessUnits,
            CurrencyModes.PlanChf
        ).ToList();

        // 3 months, each producing 1 row = 3 rows total (no cross-month merging)
        Assert.Equal(3, result.Count);

        var jan = result.Single(r => r.Month == "2025-01");
        Assert.Equal(940, jan.Estimate, precision: 2);    // 1000 * 0.94
        Assert.Equal(846, jan.Actual!.Value, precision: 2); // 900 * 0.94

        var feb = result.Single(r => r.Month == "2025-02");
        Assert.Equal(1034, feb.Estimate, precision: 2);      // 1100 * 0.94
        Assert.Equal(987, feb.Actual!.Value, precision: 2);   // 1050 * 0.94

        var mar = result.Single(r => r.Month == "2025-03");
        Assert.Equal(1128, mar.Estimate, precision: 2);       // 1200 * 0.94
        Assert.Null(mar.Actual);
        Assert.Null(mar.Variance);
    }
}
