using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using MeshWeaver.Domain;
using MeshWeaver.Messaging;

namespace MeshWeaver.Data.TestDomain;

public static class TestDomain
{
    public record ImportAddress() : Address(TypeName, "1")
    {
        public const string TypeName = "import";
    }

    public static readonly Dictionary<Type, IEnumerable<object>> TestRecordsDomain =
        new()
        {
            { typeof(MyRecord), new MyRecord[] { } },
            { typeof(MyRecord2), new MyRecord2[] { } }
        };

    public static IUnpartitionedDataSource ConfigureCategory(
        this IUnpartitionedDataSource dataSource,
        IDictionary<Type, IEnumerable<object>> typeAndInstance
    ) =>
        typeAndInstance.Aggregate(
            dataSource,
            (ds, kvp) => ds.WithType(kvp.Key, t => t.WithInitialData(kvp.Value))
        );

    public static readonly Dictionary<Type, IEnumerable<object>> ContractDomain =
        new()
        {
            { typeof(Contract), new Contract[] { } },
            { typeof(Country), new Country[] { } },
            { typeof(StreetAddress), new StreetAddress[] { } },
            { typeof(Discount), new Discount[] { } },
        };

    public record StreetAddress() 
    {
        [Required]
        public string Street { get; set; } = null!;

        [Dimension<Country>]
        public string Country { get; set; } = null!;
    }

    public record Contract
    {
        [Key]
        public string SystemName { get; init; } = null!;

        [Range(1999, 2023)]
        public int FoundationYear { get; set; }

        [Category("ContractType")]
        public string ContractType { get; set; } = null!;
    }

    public record Country : INamed
    {
        [Key]
        public string SystemName { get; set; } = null!;
        public string DisplayName { get; set; } = null!;
    }

    public record Discount
    {
        public Guid Id { get; init; } = Guid.NewGuid();

        [Percentage]
        public double DoubleValue { get; init; }

        [Percentage(MinPercentage = 10, MaxPercentage = 20)]
        public decimal DecimalValue { get; init; }

        [Percentage]
        public decimal FloatValue { get; init; }

        [Percentage]
        public int IntValue { get; init; }
    }
}
