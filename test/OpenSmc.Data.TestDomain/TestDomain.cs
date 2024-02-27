using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using OpenSmc.Domain.Abstractions;
using OpenSmc.Domain.Abstractions.Attributes;

namespace OpenSmc.Data.TestDomain;

/// <summary>
/// This is structuring element for sub-dividing a data domain into several groups.
/// You can perceive this as the building plan for how everyone starts.
/// For tests, it is handy to ship initial values. Can be also hosted in separate file.
/// </summary>
public static class TestDomain
{
    public record TransactionalData([property: Key] string Id, string LoB, string BusinessUnit, double Value);
    public record ComputedData([property: Key] string Id, string LoB, string BusinessUnit, double Value);

    public record LineOfBusiness([property: Key] string SystemName, string DisplayName);
    public record BusinessUnit([property: Key] string SystemName, string DisplayName);

    public static readonly Dictionary<Type, IEnumerable<object>> ReferenceDataDomain
        =
        new()
        {
            { typeof(LineOfBusiness), new LineOfBusiness[] { new("1", "1"), new("2", "2") } },
            { typeof(BusinessUnit), new BusinessUnit[] { new("1", "1"), new("2", "2") } }
        };

    public static readonly Dictionary<Type, IEnumerable<object>> TransactionalDataDomain
        =
        new()
        {
            {
                typeof(TransactionalData), new TransactionalData[]
                {
                    new("1", "1", "1", 7),
                    new("2", "1", "3", 2),
                }
            }
        };
    public static readonly Dictionary<Type, IEnumerable<object>> ComputedDataDomain
        =
        new()
        {
            {
                typeof(ComputedData), new ComputedData[]
                {
                }
            }
        };

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

    public static TDataSource ConfigureReferenceData<TDataSource>(this TDataSource dataSource) where TDataSource : DataSource<TDataSource> 
        => dataSource.ConfigureCategory(ReferenceDataDomain);

    public static TDataSource ConfigureTransactionalData<TDataSource>(this TDataSource dataSource) where TDataSource : DataSource<TDataSource> 
        => dataSource.ConfigureCategory(TransactionalDataDomain);
    public static TDataSource ConfigureComputedData<TDataSource>(this TDataSource dataSource) where TDataSource : DataSource<TDataSource> 
        => dataSource.ConfigureCategory(ComputedDataDomain);

    public static TDataSource ConfigureTypesForImport<TDataSource>(this TDataSource dataSource) where TDataSource : DataSource<TDataSource> 
        => dataSource.ConfigureTransactionalData()
            .ConfigureComputedData()
            .ConfigureReferenceData();

    public static TDataSource ConfigureCategory<TDataSource>(this TDataSource dataSource, IDictionary<Type, IEnumerable<object>> typeAndInstance)
    where TDataSource: DataSource<TDataSource>
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