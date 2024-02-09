using Microsoft.EntityFrameworkCore;

namespace OpenSmc.DataSource.EntityFramework;

public class EntityFrameworkContext(Action<ModelBuilder> modelBuilderConfig) : DbContext
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilderConfig.Invoke(modelBuilder);
    }
}