using MeshWeaver.Domain;
using MeshWeaver.Pivot.Models.Interfaces;

namespace MeshWeaver.Pivot.Grouping;

public class NamedPivotGrouper<T, TGroup>(string name, Func<object?, object?> keySelector)
    : DirectPivotGrouper<T, TGroup>(x =>
            x.GroupBy(o =>
            {
                var id = keySelector(o);
                return new TGroup
                {
                    Id = id?.ToString(),
                    DisplayName = o.DisplayName,
                    GrouperName = name,
                    Coordinates = [id!.ToString()!]
                };
            }),
        name)
    where TGroup : class, IGroup, new()
    where T : INamed;
