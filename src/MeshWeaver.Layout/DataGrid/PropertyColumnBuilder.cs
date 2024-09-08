using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Reflection;
using MeshWeaver.Domain;
using MeshWeaver.Reflection;
using MeshWeaver.Utils;

namespace MeshWeaver.Layout.DataGrid;

public record  PropertyColumnBuilder(ITypeDefinition TypeDefinition, DataGridControl Grid)
{
    private static PropertyColumnControl CreateControl(PropertyInfo property)
    {
        return (PropertyColumnControl)
            Activator.CreateInstance(typeof(PropertyColumn<>).MakeGenericType(property.PropertyType));
    }


    public PropertyColumnBuilder AddColumn(PropertyInfo property)
        => AddColumn(property, x => x);

    public PropertyColumnBuilder AddColumn(PropertyInfo property, Func<PropertyColumnControl, PropertyColumnControl> config)
    {
        var displayAttribute = property.GetCustomAttribute<DisplayAttribute>();
        var displayFormat = property.GetCustomAttribute<DisplayFormatAttribute>();
        var description = TypeDefinition.GetDescription(property.Name);
        var ret = CreateControl(property);
        return this with
        {
            Grid = Grid.WithView(config.Invoke(ret with
                {
                    Property = property.Name.ToCamelCase(),
                    Title = displayAttribute?.Name ?? property.Name.Wordify(),
                    Format = displayFormat?.DataFormatString ?? ret.Format,
                    Tooltip = description == null ? null : true,
                    TooltipText = description,
                })
            )
        };
    }
    public PropertyColumnBuilder AutoMapColumns() =>
        TypeDefinition.Type
            .GetProperties()
            .Where(x =>
                !x.HasAttribute<NotVisibleAttribute>()
                && x.GetCustomAttribute<BrowsableAttribute>() is not { Browsable: false }
            )
            .Aggregate(new PropertyColumnBuilder(TypeDefinition, Grid), (g, c) => g.AddColumn(c))
    ;

}

public record PropertyColumnBuilder<T>(ITypeDefinition TypeDefinition,DataGridControl Grid) : PropertyColumnBuilder(TypeDefinition, Grid)
{
    public PropertyColumnBuilder<T> WithColumn<TProp>(Expression<Func<T, TProp>> propertySelector)
        => (PropertyColumnBuilder<T>)AddColumn(propertySelector.GetProperty());
    public PropertyColumnBuilder<T> WithColumn<TProp>(Expression<Func<T, TProp>> propertySelector, Func<PropertyColumnControl,PropertyColumnControl> configuration)
        => (PropertyColumnBuilder<T>)AddColumn(propertySelector.GetProperty(), configuration);
}
