using AspectCore.Extensions.Reflection;
using MeshWeaver.Domain;
using MeshWeaver.Pivot.Models;
using MeshWeaver.Pivot.Processors;
using MeshWeaver.Reflection;

namespace MeshWeaver.Pivot.Grouping
{
    public record PivotColumnsConfiguration<TAggregate>
        : IPivotConfiguration<TAggregate, ColumnGroup>
    {
        public IReadOnlyCollection<Column> Columns { get; init; }
        private Type ValueType { get; }

        public PivotColumnsConfiguration(Type valueType, params string[] propertiesToHide)
        {
            ValueType = valueType;
            Columns = CreateColumns(propertiesToHide).ToArray();
        }

        public IEnumerable<Column> GetValueColumns()
        {
            // TODO V10: introduce AutoGroupColumnDef = new Column { DisplayName = "Name", SystemName = "Name", CellRendererParams = new CellRendererParams { SuppressCount = true } } in the GridModel instead (2021/10/06, Ekaterina Mishina)
            foreach (var c in Columns)
                yield return c;
        }

        private IEnumerable<Column> CreateColumns(params string[] propertiesToHide)
        {
            var propertiesToHideHashSet = new HashSet<string>(propertiesToHide);
            var propertiesForProcessing = ValueType
                .GetPropertiesForProcessing(null!)
                .Where(p =>
                    !propertiesToHideHashSet.Contains(p.SystemName)
                    && !p.Property.HasAttribute<DimensionAttribute>()
                )
                .ToArray();
            if (propertiesForProcessing.Length == 0)
            {
                var defaultValueName = PivotConst.DefaultValueName;
                yield return new Column(defaultValueName, defaultValueName);
            }

            foreach (var property in propertiesForProcessing)
            {
                yield return new Column(property.SystemName, property.DisplayName);
            }
        }

        public IEnumerable<(ColumnGroup group, Func<TAggregate, object?> accessor)> GetAccessors()
        {
            foreach (var column in Columns)
            {
                var prop = ValueType.GetProperty(column.Id!.ToString()!);
                if (prop != null)
                    yield return (
                        new ColumnGroup(column.Id, column.DisplayName!, prop.Name),
                        x =>
                        {
                            if (x == null)
                                return null;
                            var reflector = prop.GetReflector();
                            return reflector.GetValue(x);
                        }
                    );
                else
                    yield return (
                        new ColumnGroup(
                            column.Id,
                            column.DisplayName!,
                            PivotConst.PropertyPivotGrouperName
                        ),
                        x => x
                    );
            }
        }
    }
}
