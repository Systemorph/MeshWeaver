using Microsoft.EntityFrameworkCore;
using MeshWeaver.Data;

namespace MeshWeaver.DataStorage.EntityFramework;

public static class EntityFrameworkStorageExtensions
{
    public static DataContext FromEntityFramework(this DataContext context, string name,
        Action<DbContextOptionsBuilder> database,
        Func<EntityFrameworkUnpartitionedDataSource, EntityFrameworkUnpartitionedDataSource> dataSource) =>
        context.WithDataSourceBuilder(name, hub => dataSource.Invoke(new EntityFrameworkUnpartitionedDataSource(name, context.Workspace, new EntityFrameworkDataStorage(database))));


}
