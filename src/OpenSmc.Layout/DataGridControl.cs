using System.Collections.Immutable;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenSmc.Layout;

public record DataGridControl()
    : UiControl<DataGridControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null)
{
    public ImmutableList<object> Columns { get; init; } = ImmutableList<object>.Empty;
}

public abstract record DataGridColumn
{
    public string Property { get; init; }
    public bool Sortable { get; init; } = true;
    public string Format { get; init; }

    public abstract Type GetPropertyType();
}

public record DataGridColumn<TProperty> : DataGridColumn
{
    public override Type GetPropertyType() => typeof(TProperty);
}

public record PropertyColumnBuilder<TGridItem, TProperty>
{
    internal DataGridColumn<TProperty> Column { get; init; } = new();

    public PropertyColumnBuilder<TGridItem, TProperty> WithProperty(string property) =>
        this with
        {
            Column = Column with { Property = property }
        };

    public PropertyColumnBuilder<TGridItem, TProperty> IsSortable(bool sortable = true) =>
        this with
        {
            Column = Column with { Sortable = sortable }
        };

    public PropertyColumnBuilder<TGridItem, TProperty> WithFormat(string format) =>
        this with
        {
            Column = Column with { Format = format }
        };
}

public static class DataGridControlBuilder
{
    //public static DataGridControl<T> ToDataGrid<T>(this IReadOnlyCollection<T> elements) =>
    //    elements.ToDataGrid(x => x);

    public static DataGridControl ToDataGrid<T>(
        this IReadOnlyCollection<T> elements,
        Func<DataGridControlBuilder<T>, DataGridControlBuilder<T>> configuration
    ) => configuration.Invoke(new() { DataContext = elements }).Build();
}

public record DataGridControlBuilder<T>
{
    public IReadOnlyCollection<T> DataContext { get; init; }
    public ImmutableList<object> Columns { get; init; } = ImmutableList<object>.Empty;

    public DataGridControlBuilder<T> WithColumn<TProp>(Expression<Func<T, TProp>> expression) =>
        WithColumn(expression, x => x);

    public DataGridControlBuilder<T> WithColumn<TProp>(
        Expression<Func<T, TProp>> expression,
        Func<PropertyColumnBuilder<T, TProp>, PropertyColumnBuilder<T, TProp>> config
    ) =>
        this with
        {
            Columns = Columns.Add(
                config
                    .Invoke(
                        new PropertyColumnBuilder<T, TProp>().WithProperty(
                            ((PropertyInfo)((MemberExpression)expression.Body).Member).Name
                        )
                    )
                    .Column
            )
        };

    public DataGridControl Build() => new() { Columns = Columns };
}
