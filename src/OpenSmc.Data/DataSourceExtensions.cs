using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Messaging;
using OpenSmc.Serialization;

namespace OpenSmc.Data;

public static class DataSourceExtensions
{
    public static DataSource ConfigureCategory(this DataSource dataSource, IDictionary<Type, IEnumerable<object>> values)
        => values.Aggregate(dataSource, (ds, t) => ds.WithType(t.Key, type => type.WithInitialData(t.Value)));

}


