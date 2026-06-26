using System.Collections.Immutable;
using System.Reflection;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataBinding;
using MeshWeaver.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Layout.DataGrid;

/// <summary>
/// Extension methods for converting typed collections to <see cref="DataGridControl"/> instances.
/// </summary>
public static class DataGridControlExtensions
{
    /// <summary>
    /// Converts a typed collection to a <see cref="DataGridControl"/> with auto-mapped columns for <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The element type whose properties are auto-mapped to columns.</typeparam>
    /// <param name="area">The layout area host providing service access.</param>
    /// <param name="elements">The collection of elements to display.</param>
    /// <returns>A <see cref="DataGridControl"/> with auto-generated columns.</returns>
    [ReplaceToDataGrid]
    public static DataGridControl ToDataGrid<T>(this LayoutAreaHost area, IReadOnlyCollection<T> elements)
    {
        return ToDataGrid(area, elements, typeof(T), x => x.AutoMapProperties());
    }
    /// <summary>
    /// Converts a typed collection to a <see cref="DataGridControl"/> with a custom column configuration.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="area">The layout area host providing service access.</param>
    /// <param name="elements">The collection of elements to display.</param>
    /// <param name="configuration">Optional delegate to configure columns; null defaults to auto-mapping.</param>
    /// <returns>A <see cref="DataGridControl"/> with columns configured by <paramref name="configuration"/>.</returns>
    [ReplaceToDataGrid]
    public static DataGridControl ToDataGrid<T>(this LayoutAreaHost area, IReadOnlyCollection<T> elements, Func<PropertyViewBuilder<T>, PropertyViewBuilder>? configuration)
    {
        return ToDataGrid(area, elements, typeof(T), configuration == null ? null : c => configuration.Invoke((PropertyViewBuilder<T>)c));
    }

    /// <summary>
    /// Converts an untyped elements object to a <see cref="DataGridControl"/> with auto-mapped columns for <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The element type whose properties are auto-mapped to columns.</typeparam>
    /// <param name="area">The layout area host providing service access.</param>
    /// <param name="elements">The elements to display, typically a collection of <typeparamref name="T"/>.</param>
    /// <returns>A <see cref="DataGridControl"/> with auto-generated columns.</returns>
    public static DataGridControl ToDataGrid<T>(this LayoutAreaHost area, object elements)
    {
        return ToDataGrid(area, elements, typeof(T), x => x.AutoMapProperties());
    }
    /// <summary>
    /// Converts an untyped elements object to a <see cref="DataGridControl"/> with a custom column configuration for <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The element type whose properties define the available columns.</typeparam>
    /// <param name="area">The layout area host providing service access.</param>
    /// <param name="elements">The elements to display.</param>
    /// <param name="configuration">Optional delegate to configure columns; null defaults to auto-mapping.</param>
    /// <returns>A <see cref="DataGridControl"/> with the specified column configuration.</returns>
    public static DataGridControl ToDataGrid<T>(this LayoutAreaHost area, object elements, Func<PropertyViewBuilder, PropertyViewBuilder>? configuration)
    {
        return ToDataGrid(area, elements, typeof(T), configuration);
    }

    /// <summary>
    /// Converts elements to a <see cref="DataGridControl"/> using the type definition looked up from the type registry.
    /// </summary>
    /// <param name="area">The layout area host providing service access.</param>
    /// <param name="elements">The elements to display.</param>
    /// <param name="type">The CLR type used to look up the type definition and generate columns.</param>
    /// <param name="configuration">Optional delegate to configure columns; null defaults to auto-mapping.</param>
    /// <returns>A <see cref="DataGridControl"/> with columns derived from <paramref name="type"/>.</returns>
    public static DataGridControl ToDataGrid(this LayoutAreaHost area, object elements, Type type, Func<PropertyViewBuilder, PropertyViewBuilder>? configuration = null) =>
        area.Hub.ServiceProvider.GetRequiredService<ITypeRegistry>().GetTypeDefinition(type)!.ToDataGrid(elements, configuration);

    /// <summary>
    /// Builds a <see cref="DataGridControl"/> from a type definition and an elements collection.
    /// </summary>
    /// <param name="typeDefinition">The type definition that provides property metadata for column generation.</param>
    /// <param name="elements">The elements to display in the grid.</param>
    /// <param name="configuration">Optional delegate to configure columns; null defaults to auto-mapping via AutoMapProperties.</param>
    /// <returns>A <see cref="DataGridControl"/> with columns generated from the type definition's properties.</returns>
    public static DataGridControl ToDataGrid(this ITypeDefinition typeDefinition, object elements,  Func<PropertyViewBuilder, PropertyViewBuilder>? configuration = null)
    {
        var builder = (configuration ?? (x => x.AutoMapProperties()))
            .Invoke((PropertyViewBuilder)Activator.CreateInstance(
                typeof(PropertyViewBuilder<>).MakeGenericType(typeDefinition.Type), typeDefinition)!);

        return builder.Properties.Aggregate(new DataGridControl(elements), (grid, p) => grid.WithColumn(p));
    }


    #region ReplaceToDataGridAttribute
    private class ReplaceToDataGridAttribute : ReplaceMethodInTemplateAttribute
    {
        public override MethodInfo Replace(MethodInfo method) =>
            method.GetParameters().Length switch
            {
                2 => ToDataGridMethodOne.MakeGenericMethod(method.GetGenericArguments()),
                3 => ToDataGridMethod.MakeGenericMethod(method.GetGenericArguments()),
                _ => throw new NotSupportedException()
            };
    }


    private static readonly MethodInfo ToDataGridMethod =
        ReflectionHelper.GetStaticMethodGeneric(() => ToDataGrid<object>(null!, (object)null!, null!));
    private static readonly MethodInfo ToDataGridMethodOne =
        ReflectionHelper.GetStaticMethodGeneric(() => ToDataGrid<object>(null!, (object)null!));

    #endregion
}


/// <summary>
/// Fluent builder base record for constructing a <see cref="DataGridControl"/> from a typed element collection.
/// </summary>
/// <typeparam name="TBuilder">The concrete builder type, enabling fluent self-returning <c>with</c> expressions.</typeparam>
/// <param name="TypeSource">The source that provides type metadata for column generation.</param>
/// <param name="ElementType">The CLR type of the grid's elements.</param>
/// <param name="Elements">The collection of elements to render in the grid.</param>
public record DataGridControlBuilder<TBuilder>(ITypeSource TypeSource, Type ElementType, object Elements)
where TBuilder : DataGridControlBuilder<TBuilder>
{
    /// <summary>Gets the concrete builder instance cast to <typeparamref name="TBuilder"/>, for fluent chaining.</summary>
    public TBuilder This => (TBuilder)this;

    /// <summary>The ordered list of column definitions to include in the built grid.</summary>
    public ImmutableList<PropertyColumnControl> Columns { get; init; } =
        ImmutableList<PropertyColumnControl>.Empty;


    /// <summary>Constructs a <see cref="DataGridControl"/> from the current column definitions and element collection.</summary>
    /// <returns>A <see cref="DataGridControl"/> with the configured columns.</returns>
    public DataGridControl Build() => Columns.Aggregate(new DataGridControl(Elements), (g,c) => g.WithColumn(c));

}


