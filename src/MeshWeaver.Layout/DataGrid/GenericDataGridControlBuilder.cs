using System.Collections.Immutable;
using System.Reflection;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataBinding;
using MeshWeaver.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Layout.DataGrid;

public static class DataGridControlExtensions
{
    [ReplaceToDataGrid]
    public static DataGridControl ToDataGrid<T>(this LayoutAreaHost area, IReadOnlyCollection<T> elements)
    {
        return ToDataGrid(area, elements, typeof(T), x => x.AutoMapColumns());
    }
    [ReplaceToDataGrid]
    public static DataGridControl ToDataGrid<T>(this LayoutAreaHost area, IReadOnlyCollection<T> elements, Func<PropertyColumnBuilder<T>, PropertyColumnBuilder<T>> configuration)
    {
        return ToDataGrid(area, elements, typeof(T), configuration == null ? null : c => configuration.Invoke((PropertyColumnBuilder<T>)c));
    }

    public static DataGridControl ToDataGrid<T>(this LayoutAreaHost area, object elements)
    {
        return ToDataGrid(area, elements, typeof(T), x => x.AutoMapColumns());
    }
    public static DataGridControl ToDataGrid<T>(this LayoutAreaHost area, object elements, Func<PropertyColumnBuilder, PropertyColumnBuilder> configuration)
    {
        return ToDataGrid(area, elements, typeof(T), configuration);
    }

    public static DataGridControl ToDataGrid(this LayoutAreaHost area, object elements, Type type, Func<PropertyColumnBuilder, PropertyColumnBuilder> configuration = null) =>
        area.Hub.ServiceProvider.GetRequiredService<ITypeRegistry>().GetTypeDefinition(type).ToDataGrid(elements,  configuration);

    public static DataGridControl ToDataGrid(this ITypeDefinition typeDefinition, object elements,  Func<PropertyColumnBuilder, PropertyColumnBuilder> configuration = null) =>
            (configuration ?? (x => x.AutoMapColumns())).Invoke((PropertyColumnBuilder)Activator.CreateInstance(typeof(PropertyColumnBuilder<>).MakeGenericType(typeDefinition.Type), typeDefinition, new DataGridControl(elements))).Grid;






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
        ReflectionHelper.GetStaticMethodGeneric(() => ToDataGrid<object>(null, (object)null, null));
    private static readonly MethodInfo ToDataGridMethodOne =
        ReflectionHelper.GetStaticMethodGeneric(() => ToDataGrid<object>(null, (object)null));

    #endregion
}


public record DataGridControlBuilder<TBuilder>(ITypeSource TypeSource, Type ElementType, object Elements)
where TBuilder : DataGridControlBuilder<TBuilder>
{
    public TBuilder This => (TBuilder)this;

    public ImmutableList<PropertyColumnControl> Columns { get; init; } =
        ImmutableList<PropertyColumnControl>.Empty;


    public DataGridControl Build() => Columns.Aggregate(new DataGridControl(Elements), (g,c) => g.WithView(c));

}


