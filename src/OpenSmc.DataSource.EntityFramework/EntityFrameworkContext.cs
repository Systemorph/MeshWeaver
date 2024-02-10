using Microsoft.EntityFrameworkCore;

namespace OpenSmc.DataStorage.EntityFramework;

public class EntityFrameworkContext(Action<ModelBuilder> modelBuilderConfig, Action<DbContextOptionsBuilder> dbContextOptionsBuilder) : DbContext
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilderConfig.Invoke(modelBuilder);
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        dbContextOptionsBuilder.Invoke(optionsBuilder);
    }
}