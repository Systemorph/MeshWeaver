﻿using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using OpenSmc.Domain.Abstractions;
using OpenSmc.Domain.Abstractions.Attributes;
using OpenSmc.Messaging;

namespace OpenSmc.Data.TestDomain;

public static class TestDomain
{
    public record ImportAddress(object Host) : IHostedAddress;

    public static readonly Dictionary<Type, IEnumerable<object>> TestRecordsDomain
        =
        new()
        {
            {
                typeof(MyRecord), new MyRecord[]
                {
                }
            },
            {
                typeof(MyRecord2), new MyRecord2[]
                {
                }
            }
        };

    public static TDataSource ConfigureCategory<TDataSource>(this TDataSource dataSource, IDictionary<Type, IEnumerable<object>> typeAndInstance)
        where TDataSource : DataSource<TDataSource>
        => typeAndInstance.Aggregate(dataSource,
            (ds, kvp) =>
                ds.WithType(kvp.Key, t => t.WithInitialData(kvp.Value)));

    public static readonly Dictionary<Type, IEnumerable<object>> ContractDomain
        =
        new()
        {
            { typeof(Contract), new Contract[] { } },
            { typeof(Country), new Country[] { } },
            { typeof(Discount), new Discount[] { } },
        };

    public record Address
    {
        [Required]
        public string Street { get; set; }
        [Category("Country")]
        public string Country { get; set; }
    }

    public record Contract
    {
        [Key]
        public string SystemName { get; init; }
        [Range(1999, 2023)]
        public int FoundationYear { get; set; }
        [Category("ContractType")]
        public string ContractType { get; set; }
    }

    public record Country : INamed
    {
        public string SystemName { get; set; }
        public string DisplayName { get; set; }
    }

    public record Discount
    {
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