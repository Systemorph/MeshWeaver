using Microsoft.EntityFrameworkCore;
using OpenSmc.Data;
using OpenSmc.Messaging;

namespace OpenSmc.DataStorage.EntityFramework;

public record EntityFrameworkDataSource(object Id, Action<DbContextOptionsBuilder> Database) : DataSourceWithStorage<EntityFrameworkDataSource>(Id)
{

    public override IDataStorage CreateStorage(IMessageHub hub)
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