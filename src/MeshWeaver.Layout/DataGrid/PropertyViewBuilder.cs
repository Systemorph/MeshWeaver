using System.Collections.Immutable;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Reflection;
using MeshWeaver.Domain;
using MeshWeaver.Reflection;
using MeshWeaver.Utils;

namespace MeshWeaver.Layout.DataGrid;

public record  PropertyViewBuilder(ITypeDefinition TypeDefinition)
{
    private static PropertyControl CreateControl(PropertyInfo property)
    {
        return (PropertyControl)
            Activator.CreateInstance(typeof(PropertyControl<>).MakeGenericType(property.PropertyType));
    }


    public PropertyViewBuilder AddColumn(PropertyInfo property)
        => AddColumn(property, x => x);


    public ImmutableList<PropertyControl> Properties { get; init; } = [];

    public PropertyViewBuilder AddColumn(PropertyInfo property, Func<PropertyControl, PropertyControl> config)
    {
        var displayAttribute = property.GetCustomAttribute<DisplayAttribute>();
        var displayFormat = property.GetCustomAttribute<DisplayFormatAttribute>();
        var description = TypeDefinition.GetDescription(property.Name);
        var ret = CreateControl(property);
        return this with
        {
            Properties = Properties.Add(config.Invoke(ret with
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
    public PropertyViewBuilder AutoMapProperties() =>
        TypeDefinition.Type
            .GetProperties()
            .Where(x =>
                !x.HasAttribute<NotVisibleAttribute>()
                && x.GetCustomAttribute<BrowsableAttribute>() is not { Browsable: false }
            )
            .Aggregate(new PropertyViewBuilder(TypeDefinition), (g, c) => g.AddColumn(c))
    ;

}

public record PropertyViewBuilder<T>(ITypeDefinition TypeDefinition) : PropertyViewBuilder(TypeDefinition)
{
    public PropertyViewBuilder<T> WithColumn<TProp>(Expression<Func<T, TProp>> propertySelector)
        => (PropertyViewBuilder<T>)AddColumn(propertySelector.GetProperty());
    public PropertyViewBuilder<T> WithColumn<TProp>(Expression<Func<T, TProp>> propertySelector, Func<PropertyControl,PropertyControl> configuration)
        => (PropertyViewBuilder<T>)AddColumn(propertySelector.GetProperty(), configuration);
}
