using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Reflection;
using MeshWeaver.Domain;

namespace MeshWeaver.GridModel;

/// <summary>
/// Extension methods for creating PivotConfiguration from type metadata
/// </summary>
public static class PivotConfigurationExtensions
{
    /// <summary>
    /// Creates a PivotConfiguration builder from a data type, automatically discovering available dimensions and aggregates.
    /// </summary>
    /// <typeparam name="T">The data type to analyze</typeparam>
    /// <returns>A builder for configuring the pivot</returns>
    public static PivotConfigurationBuilder<T> ForType<T>() where T : class
    {
        return new PivotConfigurationBuilder<T>();
    }

    /// <summary>
    /// Creates a PivotConfiguration for the given data with specified row and column dimensions.
    /// </summary>
    /// <typeparam name="T">The data type</typeparam>
    /// <param name="data">The data collection</param>
    /// <param name="configure">Configuration action</param>
    /// <returns>A configured PivotGridControl</returns>
    public static PivotGridControl ToPivotGrid<T>(
        this IEnumerable<T> data,
        Action<PivotConfigurationBuilder<T>> configure
    ) where T : class
    {
        var builder = new PivotConfigurationBuilder<T>();
        configure(builder);
        var configuration = builder.Build();
        return new PivotGridControl(data.ToArray(), configuration);
    }
}

/// <summary>
/// Builder for configuring dimension properties
/// </summary>
public class DimensionBuilder
{
    internal string? DisplayName { get; private set; }
    internal string? Width { get; private set; }
    internal SortOrder? SortOrder { get; private set; }

    /// <summary>
    /// Sets the display name for the dimension
    /// </summary>
    public DimensionBuilder WithDisplayName(string displayName)
    {
        DisplayName = displayName;
        return this;
    }

    /// <summary>
    /// Sets the width for the dimension
    /// </summary>
    public DimensionBuilder WithWidth(string width)
    {
        Width = width;
        return this;
    }

    /// <summary>
    /// Sets the sort order for the dimension
    /// </summary>
    public DimensionBuilder WithSortOrder(SortOrder sortOrder)
    {
        SortOrder = sortOrder;
        return this;
    }
}

/// <summary>
/// Builder for configuring aggregate properties
/// </summary>
public class AggregateBuilder
{
    internal AggregateFunction Function { get; private set; } = AggregateFunction.Sum;
    internal string? DisplayName { get; private set; }
    internal string? Format { get; private set; }
    internal SortOrder? SortOrder { get; private set; }

    /// <summary>
    /// Sets the aggregate function
    /// </summary>
    public AggregateBuilder WithFunction(AggregateFunction function)
    {
        Function = function;
        return this;
    }

    /// <summary>
    /// Sets the display name for the aggregate
    /// </summary>
    public AggregateBuilder WithDisplayName(string displayName)
    {
        DisplayName = displayName;
        return this;
    }

    /// <summary>
    /// Sets the format for the aggregate
    /// </summary>
    public AggregateBuilder WithFormat(string format)
    {
        Format = format;
        return this;
    }

    /// <summary>
    /// Sets the sort order for the aggregate
    /// </summary>
    public AggregateBuilder WithSortOrder(SortOrder sortOrder)
    {
        SortOrder = sortOrder;
        return this;
    }
}

/// <summary>
/// Builder for creating PivotConfiguration from type metadata
/// </summary>
public class PivotConfigurationBuilder<T> where T : class
{
    private readonly List<(string Field, DimensionBuilder Builder)> rowDimensionFields = new();
    private readonly List<(string Field, DimensionBuilder Builder)> columnDimensionFields = new();
    private readonly List<(string Field, AggregateBuilder Builder)> aggregateFields = new();
    private readonly Dictionary<string, AvailableDimension> availableDimensions = new();
    private readonly Dictionary<string, AvailableAggregate> availableAggregates = new();

    private bool showRowTotals = true;
    private bool showColumnTotals = true;
    private bool allowFieldsPicking = true;

    public PivotConfigurationBuilder()
    {
        DiscoverDimensionsAndAggregates();
    }

    private void DiscoverDimensionsAndAggregates()
    {
        var type = typeof(T);
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var property in properties)
        {
            var propertyType = property.PropertyType;
            var underlyingType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
            var notVisible = property.GetCustomAttribute<NotVisibleAttribute>() != null;

            // Check if it's a dimension (has DimensionAttribute or is a string/value type that could be a dimension)
            var dimensionAttr = property.GetCustomAttributes<DimensionAttribute>().FirstOrDefault();
            if (dimensionAttr != null || (!notVisible && IsValidDimensionType(underlyingType)))
            {
                var displayName = property.GetCustomAttribute<DisplayAttribute>()?.Name
                    ?? property.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName
                    ?? SplitCamelCase(property.Name);

                var defaultSortOrderAttr = property.GetCustomAttribute<DefaultSortOrderAttribute>();

                availableDimensions[property.Name] = new AvailableDimension
                {
                    Field = property.Name,
                    DisplayName = displayName,
                    PropertyPath = property.Name,
                    TypeName = underlyingType.FullName ?? underlyingType.Name,
                    Width = GetDefaultWidth(underlyingType),
                    DefaultSortOrder = defaultSortOrderAttr?.SortOrder
                };
            }

            // Check if it's a numeric type that could be aggregated
            // Include if: has no dimension attribute AND (is not NotVisible OR is explicitly numeric)
            if (IsNumericType(underlyingType) && dimensionAttr == null && !notVisible)
            {
                var displayName = property.GetCustomAttribute<DisplayAttribute>()?.Name
                    ?? property.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName
                    ?? SplitCamelCase(property.Name);

                var formatAttr = property.GetCustomAttribute<DisplayFormatAttribute>();
                var defaultFormat = formatAttr?.DataFormatString ?? GetDefaultFormat(underlyingType);

                var defaultSortOrderAttr = property.GetCustomAttribute<DefaultSortOrderAttribute>();

                availableAggregates[property.Name] = new AvailableAggregate
                {
                    Field = property.Name,
                    DisplayName = displayName,
                    PropertyPath = property.Name,
                    TypeName = underlyingType.FullName ?? underlyingType.Name,
                    DefaultFormat = defaultFormat,
                    DefaultSortOrder = defaultSortOrderAttr?.SortOrder
                };
            }
        }
    }

    private static bool IsValidDimensionType(Type type)
    {
        return type == typeof(string)
            || type == typeof(int)
            || type == typeof(long)
            || type == typeof(DateTime)
            || type == typeof(DateTimeOffset)
            || type == typeof(bool)
            || type.IsEnum;
    }

    private static bool IsNumericType(Type type)
    {
        return type == typeof(int)
            || type == typeof(long)
            || type == typeof(decimal)
            || type == typeof(double)
            || type == typeof(float)
            || type == typeof(short)
            || type == typeof(byte);
    }

    private static string GetDefaultWidth(Type type)
    {
        if (type == typeof(string))
            return "200px";
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
            return "150px";
        return "120px";
    }

    private static string? GetDefaultFormat(Type type)
    {
        if (type == typeof(decimal) || type == typeof(double) || type == typeof(float))
            return "{0:N2}";
        return null;
    }

    private static string SplitCamelCase(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        return System.Text.RegularExpressions.Regex.Replace(
            input,
            "([a-z])([A-Z])",
            "$1 $2"
        );
    }

    private static string GetPropertyName<TProp>(Expression<Func<T, TProp>> propertyExpression)
    {
        if (propertyExpression.Body is MemberExpression memberExpression)
        {
            return memberExpression.Member.Name;
        }

        if (propertyExpression.Body is UnaryExpression { Operand: MemberExpression unaryMember })
        {
            return unaryMember.Member.Name;
        }

        throw new ArgumentException("Expression must be a property accessor", nameof(propertyExpression));
    }

    /// <summary>
    /// Groups rows by the specified dimension
    /// </summary>
    /// <param name="propertyExpression">Expression selecting the property to group by</param>
    /// <param name="configure">Optional configuration action for the dimension</param>
    public PivotConfigurationBuilder<T> GroupRowsBy<TProp>(
        Expression<Func<T, TProp>> propertyExpression,
        Action<DimensionBuilder>? configure = null)
    {
        var fieldName = GetPropertyName(propertyExpression);
        return GroupRowsBy(fieldName, configure);
    }

    /// <summary>
    /// Groups rows by the specified dimension
    /// </summary>
    /// <param name="fieldName">Name of the field to group by</param>
    /// <param name="configure">Optional configuration action for the dimension</param>
    public PivotConfigurationBuilder<T> GroupRowsBy(
        string fieldName,
        Action<DimensionBuilder>? configure = null)
    {
        var builder = new DimensionBuilder();
        configure?.Invoke(builder);

        rowDimensionFields.Add((fieldName, builder));

        // Apply any overrides to the available dimension
        if (availableDimensions.TryGetValue(fieldName, out var dimension))
        {
            if (builder.DisplayName != null)
                dimension.DisplayName = builder.DisplayName;
            if (builder.Width != null)
                dimension.Width = builder.Width;
        }

        return this;
    }

    /// <summary>
    /// Groups columns by the specified dimension
    /// </summary>
    /// <param name="propertyExpression">Expression selecting the property to group by</param>
    /// <param name="configure">Optional configuration action for the dimension</param>
    public PivotConfigurationBuilder<T> GroupColumnsBy<TProp>(
        Expression<Func<T, TProp>> propertyExpression,
        Action<DimensionBuilder>? configure = null)
    {
        var fieldName = GetPropertyName(propertyExpression);
        return GroupColumnsBy(fieldName, configure);
    }

    /// <summary>
    /// Groups columns by the specified dimension
    /// </summary>
    /// <param name="fieldName">Name of the field to group by</param>
    /// <param name="configure">Optional configuration action for the dimension</param>
    public PivotConfigurationBuilder<T> GroupColumnsBy(
        string fieldName,
        Action<DimensionBuilder>? configure = null)
    {
        var builder = new DimensionBuilder();
        configure?.Invoke(builder);

        columnDimensionFields.Add((fieldName, builder));

        // Apply any overrides to the available dimension
        if (availableDimensions.TryGetValue(fieldName, out var dimension))
        {
            if (builder.DisplayName != null)
                dimension.DisplayName = builder.DisplayName;
            if (builder.Width != null)
                dimension.Width = builder.Width;
        }

        return this;
    }

    /// <summary>
    /// Adds an aggregate to the pivot configuration
    /// </summary>
    /// <param name="propertyExpression">Expression selecting the property to aggregate</param>
    /// <param name="configure">Optional configuration action for the aggregate</param>
    public PivotConfigurationBuilder<T> Aggregate<TProp>(
        Expression<Func<T, TProp>> propertyExpression,
        Action<AggregateBuilder>? configure = null)
    {
        var fieldName = GetPropertyName(propertyExpression);
        return Aggregate(fieldName, configure);
    }

    /// <summary>
    /// Adds an aggregate to the pivot configuration
    /// </summary>
    /// <param name="fieldName">Name of the field to aggregate</param>
    /// <param name="configure">Optional configuration action for the aggregate</param>
    public PivotConfigurationBuilder<T> Aggregate(
        string fieldName,
        Action<AggregateBuilder>? configure = null)
    {
        var builder = new AggregateBuilder();
        configure?.Invoke(builder);

        aggregateFields.Add((fieldName, builder));

        // Apply any overrides to the available aggregate
        if (availableAggregates.TryGetValue(fieldName, out var aggregate))
        {
            if (builder.DisplayName != null)
                aggregate.DisplayName = builder.DisplayName;
            if (builder.Format != null)
                aggregate.DefaultFormat = builder.Format;
        }

        return this;
    }

    /// <summary>
    /// Sets whether to show row totals
    /// </summary>
    public PivotConfigurationBuilder<T> WithRowTotals(bool show = true)
    {
        showRowTotals = show;
        return this;
    }

    /// <summary>
    /// Sets whether to show column totals
    /// </summary>
    public PivotConfigurationBuilder<T> WithColumnTotals(bool show = true)
    {
        showColumnTotals = show;
        return this;
    }

    /// <summary>
    /// Sets whether to allow fields picking
    /// </summary>
    public PivotConfigurationBuilder<T> WithFieldsPicking(bool allow = true)
    {
        allowFieldsPicking = allow;
        return this;
    }

    /// <summary>
    /// Builds the final PivotConfiguration
    /// </summary>
    public PivotConfiguration Build()
    {
        var rowDimensions = rowDimensionFields
            .Select(fieldInfo => availableDimensions.TryGetValue(fieldInfo.Field, out var dim)
                ? new PivotDimension
                {
                    Field = dim.Field,
                    DisplayName = fieldInfo.Builder.DisplayName ?? dim.DisplayName,
                    PropertyPath = dim.PropertyPath,
                    TypeName = dim.TypeName,
                    Width = fieldInfo.Builder.Width ?? dim.Width,
                    SortOrder = fieldInfo.Builder.SortOrder ?? dim.DefaultSortOrder
                }
                : new PivotDimension
                {
                    Field = fieldInfo.Field,
                    DisplayName = fieldInfo.Builder.DisplayName ?? SplitCamelCase(fieldInfo.Field),
                    PropertyPath = fieldInfo.Field,
                    TypeName = "System.String",
                    Width = fieldInfo.Builder.Width ?? "120px",
                    SortOrder = fieldInfo.Builder.SortOrder
                })
            .ToList();

        var columnDimensions = columnDimensionFields
            .Select(fieldInfo => availableDimensions.TryGetValue(fieldInfo.Field, out var dim)
                ? new PivotDimension
                {
                    Field = dim.Field,
                    DisplayName = fieldInfo.Builder.DisplayName ?? dim.DisplayName,
                    PropertyPath = dim.PropertyPath,
                    TypeName = dim.TypeName,
                    Width = fieldInfo.Builder.Width ?? dim.Width,
                    SortOrder = fieldInfo.Builder.SortOrder ?? dim.DefaultSortOrder
                }
                : new PivotDimension
                {
                    Field = fieldInfo.Field,
                    DisplayName = fieldInfo.Builder.DisplayName ?? SplitCamelCase(fieldInfo.Field),
                    PropertyPath = fieldInfo.Field,
                    TypeName = "System.String",
                    Width = fieldInfo.Builder.Width ?? "120px",
                    SortOrder = fieldInfo.Builder.SortOrder
                })
            .ToList();

        var aggregates = aggregateFields
            .Select(agg => availableAggregates.TryGetValue(agg.Field, out var aggDef)
                ? new PivotAggregate
                {
                    Field = aggDef.Field,
                    DisplayName = agg.Builder.DisplayName ?? aggDef.DisplayName,
                    PropertyPath = aggDef.PropertyPath,
                    TypeName = aggDef.TypeName,
                    Function = agg.Builder.Function,
                    Format = agg.Builder.Format ?? aggDef.DefaultFormat,
                    TextAlign = TextAlign.Right,
                    SortOrder = agg.Builder.SortOrder ?? aggDef.DefaultSortOrder
                }
                : new PivotAggregate
                {
                    Field = agg.Field,
                    DisplayName = agg.Builder.DisplayName ?? SplitCamelCase(agg.Field),
                    PropertyPath = agg.Field,
                    TypeName = "System.Double",
                    Function = agg.Builder.Function,
                    Format = agg.Builder.Format,
                    TextAlign = TextAlign.Right,
                    SortOrder = agg.Builder.SortOrder
                })
            .ToList();

        // Convert available dimensions and aggregates to PivotDimension/PivotAggregate collections
        var allAvailableDimensions = availableDimensions.Values
            .Select(dim => new PivotDimension
            {
                Field = dim.Field,
                DisplayName = dim.DisplayName,
                PropertyPath = dim.PropertyPath,
                TypeName = dim.TypeName,
                Width = dim.Width
            })
            .ToList();

        var allAvailableAggregates = availableAggregates.Values
            .Select(agg => new PivotAggregate
            {
                Field = agg.Field,
                DisplayName = agg.DisplayName,
                PropertyPath = agg.PropertyPath,
                TypeName = agg.TypeName,
                Function = AggregateFunction.Sum, // Default function
                Format = agg.DefaultFormat,
                TextAlign = TextAlign.Right
            })
            .ToList();

        return new PivotConfiguration
        {
            RowDimensions = rowDimensions,
            ColumnDimensions = columnDimensions,
            Aggregates = aggregates,
            AvailableDimensions = allAvailableDimensions,
            AvailableAggregates = allAvailableAggregates,
            ShowRowTotals = showRowTotals,
            ShowColumnTotals = showColumnTotals,
            AllowFieldsPicking = allowFieldsPicking
        };
    }

    private class AvailableDimension
    {
        public required string Field { get; init; }
        public required string DisplayName { get; set; }
        public required string PropertyPath { get; init; }
        public required string TypeName { get; init; }
        public string? Width { get; set; }
        public SortOrder? DefaultSortOrder { get; set; }
    }

    private class AvailableAggregate
    {
        public required string Field { get; init; }
        public required string DisplayName { get; set; }
        public required string PropertyPath { get; init; }
        public required string TypeName { get; init; }
        public string? DefaultFormat { get; set; }
        public SortOrder? DefaultSortOrder { get; set; }
    }
}
