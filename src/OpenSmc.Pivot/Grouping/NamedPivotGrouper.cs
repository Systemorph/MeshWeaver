using OpenSmc.Domain;
using OpenSmc.Pivot.Models.Interfaces;

namespace OpenSmc.Pivot.Grouping
{
    public class NamedPivotGrouper<T, TGroup> : DirectPivotGrouper<T, TGroup>
        where TGroup : class, IGroup, new()
        where T : INamed
    {
        public NamedPivotGrouper(string name, Func<object, object> keySelector)
            : base(
                x =>
                    x.GroupBy(o =>
                    {
                        var id = keySelector(o);
                        return new TGroup
                        {
                            Id = id.ToString(),
                            DisplayName = o.DisplayName,
                            GrouperName = name,
                            Coordinates = [id.ToString()]
                        };
                    }),
                name
            ) { }
    }
}
