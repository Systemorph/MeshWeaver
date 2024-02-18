namespace OpenSmc.Data;

public static class DataSourceDomainTypesExtensions
{
    public static DataSource ConfigureCategory(this DataSource dataSource, IDictionary<Type, IEnumerable<object>> values)
        => values.Aggregate(dataSource, (ds, t) => ds.WithType(t.Key, type => type.WithInitialData(t.Value)));
}
