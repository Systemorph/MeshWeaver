using System.Collections.Immutable;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Reflection;
using Json.More;
using MeshWeaver.Domain;
using MeshWeaver.Reflection;
using MeshWeaver.Utils;

namespace MeshWeaver.Layout.DataGrid;

public record  PropertyViewBuilder(ITypeDefinition TypeDefinition)
{
    private static PropertyColumnControl CreateControl(PropertyInfo property)
    {
        return (PropertyColumnControl)
            Activator.CreateInstance(typeof(PropertyColumnControl<>).MakeGenericType(property.PropertyType))!;
    }


    public PropertyViewBuilder AddColumn(PropertyInfo property)
        => AddColumn(property, x => x);


    public ImmutableList<PropertyColumnControl> Properties { get; init; } = [];

    public PropertyViewBuilder AddColumn(PropertyInfo property, Func<PropertyColumnControl, PropertyColumnControl> config)
    {
        var displayAttribute = property.GetCustomAttribute<DisplayAttribute>();
        var displayFormat = property.GetCustomAttribute<DisplayFormatAttribute>();
        var ret = CreateControl(property);
        var sortAttribute = property.GetCustomAttribute<SortAttribute>();
        
        return this with
        {
            Properties = Properties.Add(config.Invoke(ret with
                {
                    Property = property.Name.ToCamelCase(),
                    Title = displayAttribute?.Name ?? property.Name.Wordify(),
                    Format = displayFormat?.DataFormatString ?? ret.Format,
                    Sortable = sortAttribute?.Sortable ?? true,
                    IsDefaultSortColumn = sortAttribute?.IsDefaultSort ?? false,
                    InitialSortDirection = sortAttribute?.SortDirection ?? SortDirection.Ascending,
                    IsEditable = !property.HasAttribute<KeyAttribute>() && (property.GetCustomAttribute<EditableAttribute>()?.AllowEdit ?? true),
                    Align = property.GetCustomAttribute<AlignAttribute>()?.Align ?? (property.PropertyType.IsNumber() ? Align.End : Align.Start),
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
        => (PropertyViewBuilder<T>)AddColumn(propertySelector.GetProperty()!);
    public PropertyViewBuilder<T> WithColumn<TProp>(Expression<Func<T, TProp>> propertySelector, Func<PropertyColumnControl, PropertyColumnControl> configuration)
        => (PropertyViewBuilder<T>)AddColumn(propertySelector.GetProperty()!, configuration);
}
