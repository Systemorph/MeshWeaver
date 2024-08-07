using MeshWeaver.Pivot.Models;
using MeshWeaver.Pivot.Models.Interfaces;

namespace MeshWeaver.Pivot.Grouping
{
    public interface IPivotConfiguration<TAggregate, TGroup>
        where TGroup : IGroup
    {
        IEnumerable<Column> GetValueColumns();
        IEnumerable<(TGroup group, Func<TAggregate, object> accessor)> GetAccessors();
    }
}
