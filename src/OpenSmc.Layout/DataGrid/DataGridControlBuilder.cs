using System.Collections.Immutable;
using System.ComponentModel;
using System.Linq.Expressions;
using System.Reflection;
using OpenSmc.Domain;
using OpenSmc.Layout.DataBinding;
using OpenSmc.Reflection;

namespace OpenSmc.Layout.DataGrid;

public static class DataGridControlBuilder
{
    [ReplaceToDataGrid]
    public static DataGridControl ToDataGrid<T>(
        this IReadOnlyCollection<T> elements,
        Func<DataGridControlBuilder<T>, DataGridControlBuilder<T>> configuration
    ) => configuration.Invoke(new(elements)).Build();

    private static readonly MethodInfo ToDataGridMethod = ReflectionHelper.GetStaticMethodGeneric(
        () => ToDataGrid<object>((object)null, default)
    );

    [ReplaceToDataGrid]
    public static DataGridControl ToDataGrid<T>(this IReadOnlyCollection<T> elements) =>
        ToDataGrid(elements, x => x.AutomapColumns());

    #region ReplaceMethodInTemplateAttribute
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
        Func<DataGridControlBuilder<T>, DataGridControlBuilder<T>> configuration
    ) => configuration.Invoke(new DataGridControlBuilder<T>(elements)).Build();

    private static readonly MethodInfo ToDataGridMethodOne =
        ReflectionHelper.GetStaticMethodGeneric(() => ToDataGrid<object>((object)null));

    public static DataGridControl ToDataGrid<T>(this object elements) =>
        ToDataGrid<T>(elements, x => x.AutomapColumns());
    #endregion
}

public record DataGridControlBuilder<T>
{
    public DataGridControlBuilder(object elements)
    {
        Elements = elements;
    }

    public DataGridControlBuilder<T> AutomapColumns() =>
        this with
        {
            Columns = typeof(T)
                .GetProperties()
                .Where(x =>
                    !x.HasAttribute<NotVisibleAttribute>()
                    && x.GetCustomAttribute<BrowsableAttribute>()
                        is not BrowsableAttribute { Browsable: false }
                )
                .Select(x => new PropertyColumnBuilder(x).Column)
                .ToImmutableList()
        };

    public ImmutableList<DataGridColumn> Columns { get; init; } =
        ImmutableList<DataGridColumn>.Empty;
    public object Elements { get; }

    public DataGridControlBuilder<T> WithColumnForProperty(
        PropertyInfo property,
        Func<PropertyColumnBuilder, PropertyColumnBuilder> config
    ) =>
        this with
        {
            Columns = Columns.Add(config.Invoke(new PropertyColumnBuilder(property)).Column)
        };

    public DataGridControlBuilder<T> WithColumn<TProp>(Expression<Func<T, TProp>> expression) =>
        WithColumn(expression, x => x);

    public DataGridControlBuilder<T> WithColumn<TProp>(
        Expression<Func<T, TProp>> expression,
        Func<PropertyColumnBuilder, PropertyColumnBuilder> config
    ) => WithColumnForProperty((PropertyInfo)((MemberExpression)expression.Body).Member, config);

    public DataGridControl Build() => new(Elements) { Columns = Columns };
}
