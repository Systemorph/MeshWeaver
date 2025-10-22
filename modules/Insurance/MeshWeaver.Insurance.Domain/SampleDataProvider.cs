namespace MeshWeaver.Insurance.Domain;

/// <summary>
/// Provides sample data for insurance dimensions and entities.
/// </summary>
public static class SampleDataProvider
{
    /// <summary>
    /// Gets sample line of business records.
    /// </summary>
    public static IEnumerable<LineOfBusiness> GetLinesOfBusiness()
    {
        return new[]
        {
            new LineOfBusiness
            {
                Id = "PROP",
                Name = "Property",
                Description = "Property insurance covering buildings, contents, and business interruption"
            },
            new LineOfBusiness
            {
                Id = "CAS",
                Name = "Casualty",
                Description = "Casualty insurance covering liability and workers compensation"
            },
            new LineOfBusiness
            {
                Id = "MARINE",
                Name = "Marine",
                Description = "Marine and cargo insurance"
            },
            new LineOfBusiness
            {
                Id = "AVIATION",
                Name = "Aviation",
                Description = "Aviation and aerospace insurance"
            },
            new LineOfBusiness
            {
                Id = "ENERGY",
                Name = "Energy",
                Description = "Energy sector insurance including oil & gas"
            }
        };
    }

    /// <summary>
    /// Gets sample country records.
    /// </summary>
    public static IEnumerable<Country> GetCountries()
    {
        return new[]
        {
            new Country
            {
                Id = "US",
                Name = "United States",
                Alpha3Code = "USA",
                Region = "North America"
            },
            new Country
            {
                Id = "GB",
                Name = "United Kingdom",
                Alpha3Code = "GBR",
                Region = "Europe"
            },
            new Country
            {
                Id = "DE",
                Name = "Germany",
                Alpha3Code = "DEU",
                Region = "Europe"
            },
            new Country
            {
                Id = "FR",
                Name = "France",
                Alpha3Code = "FRA",
                Region = "Europe"
            },
            new Country
            {
                Id = "JP",
                Name = "Japan",
                Alpha3Code = "JPN",
                Region = "Asia"
            },
            new Country
            {
                Id = "CN",
                Name = "China",
                Alpha3Code = "CHN",
                Region = "Asia"
            },
            new Country
            {
                Id = "AU",
                Name = "Australia",
                Alpha3Code = "AUS",
                Region = "Oceania"
            },
            new Country
            {
                Id = "CA",
                Name = "Canada",
                Alpha3Code = "CAN",
                Region = "North America"
            },
            new Country
            {
                Id = "CH",
                Name = "Switzerland",
                Alpha3Code = "CHE",
                Region = "Europe"
            },
            new Country
            {
                Id = "SG",
                Name = "Singapore",
                Alpha3Code = "SGP",
                Region = "Asia"
            }
        };
    }

    /// <summary>
    /// Gets sample legal entity records.
    /// </summary>
    public static IEnumerable<LegalEntity> GetLegalEntities()
    {
        return new[]
        {
            new LegalEntity
            {
                Id = "MW-US",
                Name = "MeshWeaver Insurance US Inc.",
                CountryOfIncorporation = "US",
                EntityType = "Corporation"
            },
            new LegalEntity
            {
                Id = "MW-UK",
                Name = "MeshWeaver Insurance UK Ltd.",
                CountryOfIncorporation = "GB",
                EntityType = "Limited Company"
            },
            new LegalEntity
            {
                Id = "MW-EU",
                Name = "MeshWeaver Insurance Europe AG",
                CountryOfIncorporation = "CH",
                EntityType = "Corporation"
            },
            new LegalEntity
            {
                Id = "MW-ASIA",
                Name = "MeshWeaver Insurance Asia Pte. Ltd.",
                CountryOfIncorporation = "SG",
                EntityType = "Private Limited"
            }
        };
    }

    /// <summary>
    /// Gets sample currency records.
    /// </summary>
    public static IEnumerable<Currency> GetCurrencies()
    {
        return new[]
        {
            new Currency
            {
                Id = "USD",
                Name = "US Dollar",
                Symbol = "$",
                DecimalPlaces = 2
            },
            new Currency
            {
                Id = "EUR",
                Name = "Euro",
                Symbol = "€",
                DecimalPlaces = 2
            },
            new Currency
            {
                Id = "GBP",
                Name = "British Pound",
                Symbol = "£",
                DecimalPlaces = 2
            },
            new Currency
            {
                Id = "JPY",
                Name = "Japanese Yen",
                Symbol = "¥",
                DecimalPlaces = 0
            },
            new Currency
            {
                Id = "CHF",
                Name = "Swiss Franc",
                Symbol = "CHF",
                DecimalPlaces = 2
            },
            new Currency
            {
                Id = "AUD",
                Name = "Australian Dollar",
                Symbol = "A$",
                DecimalPlaces = 2
            },
            new Currency
            {
                Id = "CAD",
                Name = "Canadian Dollar",
                Symbol = "C$",
                DecimalPlaces = 2
            }
        };
    }

    /// <summary>
    /// Gets sample pricing records for demonstration.
    /// </summary>
    public static IEnumerable<Pricing> GetSamplePricings()
    {
        return new[]
        {
            new Pricing
            {
                Id = "PRC-2024-001",
                InsuredName = "Global Manufacturing Corp",
                BrokerName = "Marsh McLennan",
                InceptionDate = new DateTime(2024, 1, 1),
                ExpirationDate = new DateTime(2024, 12, 31),
                UnderwritingYear = 2024,
                LineOfBusiness = "PROP",
                Country = "US",
                LegalEntity = "MW-US",
                Premium = 125000m,
                Currency = "USD",
                Status = "Bound"
            },
            new Pricing
            {
                Id = "PRC-2024-002",
                InsuredName = "European Logistics Ltd",
                BrokerName = "Aon",
                InceptionDate = new DateTime(2024, 3, 1),
                ExpirationDate = new DateTime(2025, 2, 28),
                UnderwritingYear = 2024,
                LineOfBusiness = "PROP",
                Country = "GB",
                LegalEntity = "MW-UK",
                Premium = 85000m,
                Currency = "GBP",
                Status = "Quoted"
            },
            new Pricing
            {
                Id = "PRC-2024-003",
                InsuredName = "Tech Industries GmbH",
                BrokerName = "Willis Towers Watson",
                InceptionDate = new DateTime(2024, 6, 1),
                ExpirationDate = new DateTime(2025, 5, 31),
                UnderwritingYear = 2024,
                LineOfBusiness = "PROP",
                Country = "DE",
                LegalEntity = "MW-EU",
                Premium = 95000m,
                Currency = "EUR",
                Status = "Draft"
            },
            new Pricing
            {
                Id = "Microsoft-2026",
                InsuredName = "Microsoft",
                BrokerName = "Marsh McLennan",
                InceptionDate = new DateTime(2026, 1, 1),
                ExpirationDate = new DateTime(2026, 12, 31),
                UnderwritingYear = 2026,
                LineOfBusiness = "PROP",
                Country = "US",
                LegalEntity = "MW-US",
                Premium = 2500000m,
                Currency = "USD",
                Status = "Bound"
            }
        };
    }
}
