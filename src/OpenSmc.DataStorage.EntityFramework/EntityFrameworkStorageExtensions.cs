using Microsoft.EntityFrameworkCore;
using OpenSmc.Data;

namespace OpenSmc.DataStorage.EntityFramework;

public static class EntityFrameworkStorageExtensions
{
    public static DataContext FromEntityFramework(this DataContext context, string name,
        Action<DbContextOptionsBuilder> database,
        Func<EntityFrameworkDataSource, EntityFrameworkDataSource> dataSource) =>
        context.WithDataSourceBuilder(name, hub => dataSource.Invoke(new EntityFrameworkDataSource(name, context.Workspace, new EntityFrameworkDataStorage(database))));


}
