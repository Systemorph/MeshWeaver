using MeshWeaver.DataCubes;
using MeshWeaver.Domain;

namespace MeshWeaver.TestDomain
{
    public record LineOfBusiness : Dimension
    {
        public static LineOfBusiness[] Data =
        {
            new() { SystemName = "P", DisplayName = "Property" },
            new() { SystemName = "C", DisplayName = "Casualty" },
            new() { SystemName = "L", DisplayName = "Life" },
            new() { SystemName = "H", DisplayName = "Health" },
        };
    }

    public record Country : Dimension
    {
        public static Country[] Data =
        {
            new() { SystemName = "CH", DisplayName = "Switzerland" },
            new() { SystemName = "RU", DisplayName = "Russia" },
            new() { SystemName = "PT", DisplayName = "Portugal" },
            new() { SystemName = "D", DisplayName = "Germany" },
        };
    }

    public record AmountType : Dimension, IOrdered
    {
        public int Order { get; set; }

        public static AmountType[] Data =
        {
            new()
            {
                SystemName = "P",
                DisplayName = "Premium",
                Order = 20
            },
            new()
            {
                SystemName = "E",
                DisplayName = "Expenses",
                Order = 10
            },
            new()
            {
                SystemName = "C",
                DisplayName = "Cost",
                Order = 30
            },
            new()
            {
                SystemName = "B",
                DisplayName = "Benefit",
                Order = 40
            },
        };
    }

    public record Scenario : Dimension, IOrdered
    {
        public int Order { get; set; }

        public static Scenario[] Data =
        {
            new()
            {
                SystemName = "B",
                DisplayName = "Best Estimate",
                Order = 10
            },
            new()
            {
                SystemName = "SL",
                DisplayName = "Low Stress",
                Order = 20
            },
            new()
            {
                SystemName = "SH",
                DisplayName = "HighStress",
                Order = 30
            },
        };
    }

    public record Split : Dimension
    {
        public static Split[] Data =
        {
            new() { SystemName = "B", DisplayName = "Broked" },
            new() { SystemName = "D", DisplayName = "Direct" },
        };
    }

    public record Currency : Dimension
    {
        public static Currency[] Data =
        {
            new() { SystemName = "CHF", DisplayName = "Swiss Franc" },
            new() { SystemName = "RUB", DisplayName = "Russian rouble" },
            new() { SystemName = "EUR", DisplayName = "Euro" },
            new() { SystemName = "USD", DisplayName = "US Dollar" },
        };
    }

    public record Company : Dimension
    {
        public static Company[] Data =
        {
            new() { SystemName = "CH", DisplayName = "Velaro Switzerland" },
            new() { SystemName = "RU", DisplayName = "Velaro Russia" },
            new() { SystemName = "PT", DisplayName = "Velaro Portugal" },
            new() { SystemName = "D", DisplayName = "Velaro Germany" },
        };
    }
}
