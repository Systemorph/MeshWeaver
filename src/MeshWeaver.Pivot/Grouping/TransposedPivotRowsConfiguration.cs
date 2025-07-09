using AspectCore.Extensions.Reflection;
using MeshWeaver.Pivot.Models;
using MeshWeaver.Pivot.Processors;

namespace MeshWeaver.Pivot.Grouping
{
    public record TransposedPivotRowsConfiguration<TAggregate> : IPivotConfiguration<TAggregate, RowGroup>
    {
        private Type TransposedValue { get; }

        public TransposedPivotRowsConfiguration(Type transposedValue)
        {
            TransposedValue = transposedValue;
        }

        public virtual IEnumerable<Column> GetValueColumns()
        {
            throw new NotImplementedException();
        }

        public IEnumerable<(RowGroup group, Func<TAggregate, object?> accessor)> GetAccessors()
        {
            var propertiesForProcessing = typeof(TAggregate).GetPropertiesForProcessing(string.Empty);
            if (TransposedValue != typeof(TAggregate))
                propertiesForProcessing =
                    propertiesForProcessing.Where(p => TransposedValue.IsAssignableFrom(p.Property.PropertyType));

            foreach (var property in propertiesForProcessing)
            {
                var reflector = property.Property.GetReflector();
                yield return (new RowGroup(property.SystemName, property.DisplayName, PivotConst.PropertyPivotGrouperName), x => new Dictionary<string, object?>
                                                                                                                                 {
                                                                                                                                     { PivotConst.DefaultValueName, reflector.GetValue(x) }
                                                                                                                                 });
            }
        }
    }
}
