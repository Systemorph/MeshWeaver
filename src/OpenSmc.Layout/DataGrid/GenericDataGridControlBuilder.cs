using System.Collections.Immutable;
using System.ComponentModel;
using System.Linq.Expressions;
using System.Reflection;
using OpenSmc.Domain;
using OpenSmc.Layout.DataBinding;
using OpenSmc.Reflection;

namespace OpenSmc.Layout.DataGrid;

public static class DataGridControlExtensions
{
    [ReplaceToDataGrid]
    public static DataGridControl ToDataGrid<T>(
        this IReadOnlyCollection<T> elements,
        Func<GenericDataGridControlBuilder<T>, GenericDataGridControlBuilder<T>> configuration
    ) => configuration.Invoke(new(elements)).Build();

    private static readonly MethodInfo ToDataGridMethod = ReflectionHelper.GetStaticMethodGeneric(
        () => ToDataGrid<object>((object)null, default)
    );

    [ReplaceToDataGrid]
    public static DataGridControl ToDataGrid<T>(this IReadOnlyCollection<T> elements) =>
        ToDataGrid(elements, x => x.AutoMapColumns());

    public static DataGridControl ToDataGrid(this object elements, Type elementType, Func<DataGridControlBuilder, DataGridControlBuilder> config) =>
        config.Invoke(new(elementType, elements)).Build();



    #region ReplaceToDataGridAttribute
    private class ReplaceToDataGridAttribute : ReplaceMethodInTemplateAttribute
    {
        public override MethodInfo Replace(MethodInfo method) =>
            method.GetParameters().Length switch
            {
                1 => ToDataGridMethodOne.MakeGenericMethod(method.GetGenericArguments()),
                2 => ToDataGridMethod.MakeGenericMethod(method.GetGenericArguments()),
                _ => throw new NotSupportedException()
            };
    }

    public static DataGridControl ToDataGrid<T>(
        this object elements,
        Func<GenericDataGridControlBuilder<T>, GenericDataGridControlBuilder<T>> configuration
    ) => configuration.Invoke(new GenericDataGridControlBuilder<T>(elements)).Build();

    private static readonly MethodInfo ToDataGridMethodOne =
        ReflectionHelper.GetStaticMethodGeneric(() => ToDataGrid<object>((object)null));

    public static DataGridControl ToDataGrid<T>(this object elements) =>
        ToDataGrid<T>(elements, x => x.AutoMapColumns());
    #endregion
}


public record DataGridControlBuilder<TBuilder>(Type ElementType, object Elements)
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
                .Select(x => new PropertyColumnBuilder(x).Column)
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
            Columns = Columns.Add(config.Invoke(new PropertyColumnBuilder(property)).Column)
        };

    public DataGridControl Build() => new(Elements) { Columns = Columns };

}

public record DataGridControlBuilder(Type ElementType, object Elements) : DataGridControlBuilder<DataGridControlBuilder>(ElementType, Elements);

public record GenericDataGridControlBuilder<T>(object Elements) : 
    DataGridControlBuilder<GenericDataGridControlBuilder<T>>(typeof(T), Elements)
{


    public GenericDataGridControlBuilder<T> WithColumn<TProp>(Expression<Func<T, TProp>> expression) =>
        WithColumn(expression, x => x);

    public GenericDataGridControlBuilder<T> WithColumn<TProp>(
        Expression<Func<T, TProp>> expression,
        Func<PropertyColumnBuilder, PropertyColumnBuilder> config
    ) => WithColumnForProperty((PropertyInfo)((MemberExpression)expression.Body).Member, config);

}
