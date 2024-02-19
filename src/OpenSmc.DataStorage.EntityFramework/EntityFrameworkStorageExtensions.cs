using Microsoft.EntityFrameworkCore;

namespace OpenSmc.DataStorage.EntityFramework;

public static class EntityFrameworkStorageExtensions
{
    public static EntityFrameworkDataStorage FromEntityFramework(this Data.DataSource dataSource, Action<ModelBuilder> modelBuilder, Action<DbContextOptionsBuilder> database) =>
        new(builder => modelBuilder.Invoke(ConvertDataSourceMappings(builder, dataSource)), database);
    public static EntityFrameworkDataStorage FromEntityFramework(this Data.DataSource dataSource, Action<DbContextOptionsBuilder> database) =>
        new(builder => ConvertDataSourceMappings(builder, dataSource), database);

    private static ModelBuilder ConvertDataSourceMappings(ModelBuilder builder, Data.DataSource dataSource)
    {
        foreach (var type in dataSource.MappedTypes)
            builder.Model.AddEntityType(type);
        return builder;
    }

}