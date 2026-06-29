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

/// <summary>
/// Builds an ordered list of <see cref="PropertyColumnControl"/> instances for a data-grid
/// view, driven by reflection over an <see cref="ITypeDefinition"/>. Supports manual
/// column addition (via <see cref="System.Reflection.PropertyInfo"/> or typed expressions) and automatic
/// mapping of all browsable, visible properties via <see cref="AutoMapProperties"/>.
/// </summary>
/// <param name="TypeDefinition">The type definition whose properties are reflected into columns.</param>
public record  PropertyViewBuilder(ITypeDefinition TypeDefinition)
{
    private static PropertyColumnControl CreateControl(PropertyInfo property)
    {
        return (PropertyColumnControl)
            Activator.CreateInstance(typeof(PropertyColumnControl<>).MakeGenericType(property.PropertyType))!;
    }


    /// <summary>
    /// Adds a column for <paramref name="property"/> using the default (identity) configuration.
    /// </summary>
    /// <param name="property">The property to map to a column.</param>
    /// <returns>A new builder with the column appended.</returns>
    public PropertyViewBuilder AddColumn(PropertyInfo property)
        => AddColumn(property, x => x);


    /// <summary>
    /// Ordered list of configured column controls accumulated by this builder.
    /// </summary>
    public ImmutableList<PropertyColumnControl> Properties { get; init; } = [];

    /// <summary>
    /// Adds a column for <paramref name="property"/>, applying <paramref name="config"/> to
    /// the default column control produced by reflection (title, format, sortability, editability,
    /// alignment all resolved from attributes on the property).
    /// </summary>
    /// <param name="property">The property to map to a column.</param>
    /// <param name="config">A function to further configure the generated column control.</param>
    /// <returns>A new builder with the column appended.</returns>
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
    /// <summary>
    /// Automatically maps all browsable, visible properties of <see cref="TypeDefinition"/>'s
    /// underlying type to columns, in declaration order. Properties decorated with
    /// <c>[NotVisible]</c> or <c>[Browsable(false)]</c> are skipped.
    /// </summary>
    /// <returns>A new builder populated with one column per visible property.</returns>
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

/// <summary>
/// Strongly-typed variant of <see cref="PropertyViewBuilder"/> that accepts lambda selectors
/// for compile-time-safe column registration.
/// </summary>
/// <typeparam name="T">The row type whose properties are mapped to columns.</typeparam>
/// <param name="TypeDefinition">The type definition for <typeparamref name="T"/>.</param>
public record PropertyViewBuilder<T>(ITypeDefinition TypeDefinition) : PropertyViewBuilder(TypeDefinition)
{
    /// <summary>
    /// Adds a column for the property selected by <paramref name="propertySelector"/> using
    /// the default configuration.
    /// </summary>
    /// <typeparam name="TProp">The property's value type.</typeparam>
    /// <param name="propertySelector">A lambda expression identifying the property, e.g. <c>x =&gt; x.Name</c>.</param>
    /// <returns>A new builder with the column appended.</returns>
    public PropertyViewBuilder<T> WithColumn<TProp>(Expression<Func<T, TProp>> propertySelector)
        => (PropertyViewBuilder<T>)AddColumn(propertySelector.GetProperty()!);
    /// <summary>
    /// Adds a column for the property selected by <paramref name="propertySelector"/>, applying
    /// <paramref name="configuration"/> to the generated column control.
    /// </summary>
    /// <typeparam name="TProp">The property's value type.</typeparam>
    /// <param name="propertySelector">A lambda expression identifying the property.</param>
    /// <param name="configuration">A function to further configure the column control.</param>
    /// <returns>A new builder with the column appended.</returns>
    public PropertyViewBuilder<T> WithColumn<TProp>(Expression<Func<T, TProp>> propertySelector, Func<PropertyColumnControl, PropertyColumnControl> configuration)
        => (PropertyViewBuilder<T>)AddColumn(propertySelector.GetProperty()!, configuration);
}
