using Microsoft.EntityFrameworkCore;

namespace OpenSmc.DataStorage.EntityFramework;

public static class EntityFrameworkStorageExtensions
{
    public static EntityFrameworkDataSource FromEntityFramework(this Data.DataSource dataSource, Action<DbContextOptionsBuilder> database) =>
        new(dataSource.Id, database) ;


}