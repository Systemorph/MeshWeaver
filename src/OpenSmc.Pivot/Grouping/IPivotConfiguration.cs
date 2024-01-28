using OpenSmc.Pivot.Models;
using OpenSmc.Pivot.Models.Interfaces;

namespace OpenSmc.Pivot.Grouping
{
    public interface IPivotConfiguration<TAggregate, TGroup>
        where TGroup : IGroup
    {
        IEnumerable<Column> GetValueColumns();
        IEnumerable<(TGroup group, Func<TAggregate, object> accessor)> GetAccessors();
    }
}
