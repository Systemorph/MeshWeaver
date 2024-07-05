using System.ComponentModel.DataAnnotations;
using System.Reflection;
using OpenSmc.Utils;

namespace OpenSmc.Layout.DataGrid;

public record PropertyColumnBuilder(Type PropertyType, string Name)
{
    public DataGridColumn Column { get; init; } =
        (
            (DataGridColumn)
                Activator.CreateInstance(typeof(DataGridColumn<>).MakeGenericType(PropertyType))
        ) with
        {
            Property = Name,
            Title = Name.Wordify(),
        };

    public PropertyColumnBuilder(PropertyInfo property)
        : this(property.PropertyType, property.Name.ToCamelCase())
    {
        var displayAttribute = property.GetCustomAttribute<DisplayAttribute>();
        var displayFormat = property.GetCustomAttribute<DisplayFormatAttribute>();
        Column = Column with
        {
            Title = displayAttribute?.Name ?? property.Name.Wordify(),
            Format = displayFormat?.DataFormatString ?? Column.Format,
        };
    }

    public PropertyColumnBuilder WithProperty(string property) =>
        this with
        {
            Column = Column with { Property = property }
        };

    public PropertyColumnBuilder IsSortable(bool sortable = true) =>
        this with
        {
            Column = Column with { Sortable = sortable }
        };

    public PropertyColumnBuilder WithFormat(string format) =>
        this with
        {
            Column = Column with { Format = format }
        };

    public PropertyColumnBuilder WithTitle(string title) =>
        this with
        {
            Column = Column with { Title = title }
        };
}
