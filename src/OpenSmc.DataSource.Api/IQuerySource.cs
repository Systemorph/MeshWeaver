using System.Collections.Generic;
using System.Linq;

namespace OpenSmc.DataSource.Api
{
    public interface IQuerySource
    {
        IQueryable<T> Query<T>();
    }

}