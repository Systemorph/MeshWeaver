using System.Collections.Immutable;
using System.ComponentModel;
using System.Linq.Expressions;
using System.Reflection;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataBinding;
using MeshWeaver.Reflection;

namespace MeshWeaver.Layout.DataGrid;

public static class DataGridControlExtensions
{
    [ReplaceToDataGrid]
    public static DataGridControl ToDataGrid<T>(
        this LayoutAreaHost area, 
            IReadOnlyCollection<T> elements,
        Func<GenericDataGridControlBuilder<T>, GenericDataGridControlBuilder<T>> configuration
    ) => configuration.Invoke(new(area.GetTypeSource(typeof(T)), elements)).Build();

    private static ITypeSource GetTypeSource(this LayoutAreaHost area, Type type) => area.Workspace.DataContext.GetTypeSource(type);

    private static readonly MethodInfo ToDataGridMethod = ReflectionHelper.GetStaticMethodGeneric(
        () => ToDataGrid<object>(null, (object)null, default)
    );

    [ReplaceToDataGrid]
    public static DataGridControl ToDataGrid<T>(this LayoutAreaHost area, IReadOnlyCollection<T> elements) =>
        ToDataGrid(area, elements, x => x.AutoMapColumns());

    public static DataGridControl ToDataGrid(this LayoutAreaHost area, object elements, Type elementType, Func<DataGridControlBuilder, DataGridControlBuilder> config) =>
        config.Invoke(new(area.GetTypeSource(elementType), elementType, elements)).Build();



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

    public static DataGridControl ToDataGrid<T>(
        this LayoutAreaHost area, 
            object elements,
        Func<GenericDataGridControlBuilder<T>, GenericDataGridControlBuilder<T>> configuration
    ) => configuration.Invoke(new GenericDataGridControlBuilder<T>(area.GetTypeSource(typeof(T)), elements)).Build();

    private static readonly MethodInfo ToDataGridMethodOne =
        ReflectionHelper.GetStaticMethodGeneric(() => ToDataGrid<object>(null, (object)null));

    public static DataGridControl ToDataGrid<T>(this LayoutAreaHost area, object elements) =>
        area.ToDataGrid<T>(elements, x => x.AutoMapColumns());
    #endregion
}


public record DataGridControlBuilder<TBuilder>(ITypeSource TypeSource, Type ElementType, object Elements)
where TBuilder : DataGridControlBuilder<TBuilder>
{
    public TBuilder This => (TBuilder)this;
    public TBuilder AutoMapColumns() =>
        This with
        {
            Columns = ElementType
                .GetProperties()
                .Where(x =>
                    !x.HasAttribute<NotVisibleAttribute>()
                    && x.GetCustomAttribute<BrowsableAttribute>() is not { Browsable: false }
                )
                .Select(x => new PropertyColumnBuilder(TypeSource, x).Column)
                .ToImmutableList()
        };

    public ImmutableList<DataGridColumn> Columns { get; init; } =
        ImmutableList<DataGridColumn>.Empty;

    public TBuilder WithColumnForProperty(
        PropertyInfo property,
        Func<PropertyColumnBuilder, PropertyColumnBuilder> config
    ) =>
        This with
        {
            Columns = Columns.Add(config.Invoke(new PropertyColumnBuilder(TypeSource, property)).Column)
        };

    public DataGridControl Build() => new(Elements) { Columns = Columns };

}

public record DataGridControlBuilder(ITypeSource TypeSource, Type ElementType, object Elements) : DataGridControlBuilder<DataGridControlBuilder>(TypeSource, ElementType, Elements);

public record GenericDataGridControlBuilder<T>(ITypeSource TypeSource, object Elements) : 
    DataGridControlBuilder<GenericDataGridControlBuilder<T>>(TypeSource, typeof(T), Elements)
{


    public GenericDataGridControlBuilder<T> WithColumn<TProp>(Expression<Func<T, TProp>> expression) =>
        WithColumn(expression, x => x);

    public GenericDataGridControlBuilder<T> WithColumn<TProp>(
        Expression<Func<T, TProp>> expression,
        Func<PropertyColumnBuilder, PropertyColumnBuilder> config
    ) => WithColumnForProperty((PropertyInfo)((MemberExpression)expression.Body).Member, config);

}
