using System.Collections.Immutable;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;

namespace OpenSmc.Layout;

public record DataGridControl<TGridItem>()
    : UiControl<DataGridControl<TGridItem>>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null), IGenericType
{
    public Type BaseType { get; } = typeof(DataGridControl<>);
    public Type[] TypeArguments { get; } = new[] { typeof(TGridItem) };
    public ImmutableList<DataGridColumn<TGridItem>> Columns { get; init; } = ImmutableList<DataGridColumn<TGridItem>>.Empty;

    public DataGridControl<TGridItem> WithColumn<TProp>(Expression<Func<TGridItem, TProp>> expression)=> 
        WithColumn(expression, x =>x);
    public DataGridControl<TGridItem> WithColumn<TProp>(Expression<Func<TGridItem, TProp>> expression,
        Func<PropertyColumn<TGridItem, TProp>, PropertyColumn<TGridItem, TProp>> config) => this with
    {
        Columns = Columns.Add(
            config.Invoke(new PropertyColumn<TGridItem, TProp>
            {
                Property = ((PropertyInfo)((MemberExpression)expression.Body).Member).Name
            }))
    };

}

public record DataGridColumn<TGridItem>
{
    public string Property { get; init; }
    public bool Sortable { get; init; } = true;
    public string Format { get; init; }

    public Expression ColumnExpression
    {
        get{ var prm = Expression.Parameter(typeof(TGridItem), "p"); return Expression.Lambda(Expression.Property(prm,Property), prm);}
    }

}
public record PropertyColumn<TGridItem, TProperty> : DataGridColumn<TGridItem>
{
    public PropertyColumn<TGridItem, TProperty> WithProperty(string property) => this with { Property = property };
    public PropertyColumn<TGridItem, TProperty> IsSortable(bool sortable = true) => this with { Sortable = sortable };
    public PropertyColumn<TGridItem, TProperty> WithFormat(string format) => this with { Format = format };
}

public static class DataGridControlBuilder
{
    //public static DataGridControl<T> ToDataGrid<T>(this IReadOnlyCollection<T> elements) =>
    //    elements.ToDataGrid(x => x);

    public static DataGridControl<T> ToDataGrid<T>(this IReadOnlyCollection<T> elements,
        Func<DataGridControl<T>, DataGridControl<T>> configuration) =>
        configuration.Invoke(new(){DataContext = elements});
}


