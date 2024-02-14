using System.ComponentModel.DataAnnotations;
using OpenSmc.Data;

namespace OpenSmc.Import.Test;

/// <summary>
/// This is structuring element for sub-dividing a data domain into several groups.
/// You can perceive this as the building plan for how everyone starts.
/// For tests, it is handy to ship initial values. Can be also hosted in separate file.
/// </summary>
public static class ImportTestDomain
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
            }
        };

    public static DataSource ConfigureReferenceData(this DataSource dataSource)
        => dataSource.ConfigureCategory(ReferenceDataDomain);

    public static DataSource ConfigureTransactionalData(this DataSource dataSource)
        => dataSource.ConfigureCategory(TransactionalDataDomain);
    public static DataSource ConfigureComputedData(this DataSource dataSource)
        => dataSource.ConfigureCategory(ComputedDataDomain);
    public static DataSource ConfigureCategory(this DataSource dataSource, IDictionary<Type,IEnumerable<object>> values)
        => values.Aggregate(dataSource, (ds, t) => ds.WithType(t.Key, type => type.WithInitialData(t.Value)));

}