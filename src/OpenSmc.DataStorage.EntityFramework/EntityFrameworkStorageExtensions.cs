using Microsoft.EntityFrameworkCore;
using OpenSmc.Data;

namespace OpenSmc.DataStorage.EntityFramework;

public static class EntityFrameworkStorageExtensions
{
    public static EntityFrameworkDataSource FromEntityFramework(this Data.DataSource dataSource, Action<DbContextOptionsBuilder> database) =>
        new(dataSource.Id, database) ;


}

public record EntityFrameworkDataSource(object Id, Action<DbContextOptionsBuilder> Database) : DataSourceWithStorage<EntityFrameworkDataSource>(Id)
{

    public override IDataStorage CreateStorage()
    {
        return new EntityFrameworkDataStorage(ModelBuilder ?? ConvertDataSourceMappings, Database);
    }

    public EntityFrameworkDataSource WithModel(Action<ModelBuilder> modelBuilder)
        => this with { ModelBuilder = modelBuilder };

    public Action<ModelBuilder> ModelBuilder { get; init; }

    private void ConvertDataSourceMappings(ModelBuilder builder)
    {
        foreach (var type in MappedTypes)
            builder.Model.AddEntityType(type);
    }

}