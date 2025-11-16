using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
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
/// Builder for creating PivotConfiguration from type metadata
/// </summary>
public class PivotConfigurationBuilder<T> where T : class
{
    private readonly List<string> rowDimensionFields = new();
    private readonly List<string> columnDimensionFields = new();
    private readonly List<(string Field, AggregateFunction Function, string? Format, SortOrder? SortOrder)> aggregateFields = new();
    private readonly Dictionary<string, AvailableDimension> availableDimensions = new();
    private readonly Dictionary<string, AvailableAggregate> availableAggregates = new();

    private bool showRowTotals = true;
    private bool showColumnTotals = true;

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
            // Skip properties marked as NotVisible
            if (property.GetCustomAttribute<NotVisibleAttribute>() != null)
                continue;

            var propertyType = property.PropertyType;
            var underlyingType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;

            // Check if it's a dimension (has DimensionAttribute or is a string/value type that could be a dimension)
            var dimensionAttr = property.GetCustomAttributes<DimensionAttribute>().FirstOrDefault();
            if (dimensionAttr != null || IsValidDimensionType(underlyingType))
            {
                var displayName = property.GetCustomAttribute<DisplayAttribute>()?.Name
                    ?? property.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName
                    ?? SplitCamelCase(property.Name);

                availableDimensions[property.Name] = new AvailableDimension
                {
                    Field = property.Name,
                    DisplayName = displayName,
                    PropertyPath = property.Name,
                    TypeName = underlyingType.FullName ?? underlyingType.Name,
                    Width = GetDefaultWidth(underlyingType)
                };
            }

            // Check if it's a numeric type that could be aggregated
            if (IsNumericType(underlyingType) && dimensionAttr == null)
            {
                var displayName = property.GetCustomAttribute<DisplayAttribute>()?.Name
                    ?? property.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName
                    ?? SplitCamelCase(property.Name);

                availableAggregates[property.Name] = new AvailableAggregate
                {
                    Field = property.Name,
                    DisplayName = displayName,
                    PropertyPath = property.Name,
                    TypeName = underlyingType.FullName ?? underlyingType.Name,
                    DefaultFormat = GetDefaultFormat(underlyingType)
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

    private static string? GetDefaultWidth(Type type)
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

    /// <summary>
    /// Adds a row dimension to the pivot configuration
    /// </summary>
    public PivotConfigurationBuilder<T> WithRowDimension(string fieldName, string? displayName = null, string? width = null)
    {
        rowDimensionFields.Add(fieldName);

        if (availableDimensions.TryGetValue(fieldName, out var dimension))
        {
            if (displayName != null)
                dimension.DisplayName = displayName;
            if (width != null)
                dimension.Width = width;
        }

        return this;
    }

    /// <summary>
    /// Adds a column dimension to the pivot configuration
    /// </summary>
    public PivotConfigurationBuilder<T> WithColumnDimension(string fieldName, string? displayName = null, string? width = null)
    {
        columnDimensionFields.Add(fieldName);

        if (availableDimensions.TryGetValue(fieldName, out var dimension))
        {
            if (displayName != null)
                dimension.DisplayName = displayName;
            if (width != null)
                dimension.Width = width;
        }

        return this;
    }

    /// <summary>
    /// Adds an aggregate to the pivot configuration
    /// </summary>
    public PivotConfigurationBuilder<T> WithAggregate(
        string fieldName,
        AggregateFunction function = AggregateFunction.Sum,
        string? displayName = null,
        string? format = null,
        SortOrder? sortOrder = null)
    {
        aggregateFields.Add((fieldName, function, format, sortOrder));

        if (availableAggregates.TryGetValue(fieldName, out var aggregate))
        {
            if (displayName != null)
                aggregate.DisplayName = displayName;
            if (format != null)
                aggregate.DefaultFormat = format;
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
    /// Builds the final PivotConfiguration
    /// </summary>
    public PivotConfiguration Build()
    {
        var rowDimensions = rowDimensionFields
            .Select(field => availableDimensions.TryGetValue(field, out var dim)
                ? new PivotDimension
                {
                    Field = dim.Field,
                    DisplayName = dim.DisplayName,
                    PropertyPath = dim.PropertyPath,
                    TypeName = dim.TypeName,
                    Width = dim.Width
                }
                : new PivotDimension
                {
                    Field = field,
                    DisplayName = SplitCamelCase(field),
                    PropertyPath = field,
                    TypeName = "System.String",
                    Width = "120px"
                })
            .ToList();

        var columnDimensions = columnDimensionFields
            .Select(field => availableDimensions.TryGetValue(field, out var dim)
                ? new PivotDimension
                {
                    Field = dim.Field,
                    DisplayName = dim.DisplayName,
                    PropertyPath = dim.PropertyPath,
                    TypeName = dim.TypeName,
                    Width = dim.Width
                }
                : new PivotDimension
                {
                    Field = field,
                    DisplayName = SplitCamelCase(field),
                    PropertyPath = field,
                    TypeName = "System.String",
                    Width = "120px"
                })
            .ToList();

        var aggregates = aggregateFields
            .Select(agg => availableAggregates.TryGetValue(agg.Field, out var aggDef)
                ? new PivotAggregate
                {
                    Field = aggDef.Field,
                    DisplayName = aggDef.DisplayName,
                    PropertyPath = aggDef.PropertyPath,
                    TypeName = aggDef.TypeName,
                    Function = agg.Function,
                    Format = agg.Format ?? aggDef.DefaultFormat,
                    TextAlign = TextAlign.Right,
                    SortOrder = agg.SortOrder
                }
                : new PivotAggregate
                {
                    Field = agg.Field,
                    DisplayName = SplitCamelCase(agg.Field),
                    PropertyPath = agg.Field,
                    TypeName = "System.Double",
                    Function = agg.Function,
                    Format = agg.Format,
                    TextAlign = TextAlign.Right,
                    SortOrder = agg.SortOrder
                })
            .ToList();

        return new PivotConfiguration
        {
            RowDimensions = rowDimensions,
            ColumnDimensions = columnDimensions,
            Aggregates = aggregates,
            ShowRowTotals = showRowTotals,
            ShowColumnTotals = showColumnTotals
        };
    }

    private class AvailableDimension
    {
        public required string Field { get; init; }
        public required string DisplayName { get; set; }
        public required string PropertyPath { get; init; }
        public required string TypeName { get; init; }
        public string? Width { get; set; }
    }

    private class AvailableAggregate
    {
        public required string Field { get; init; }
        public required string DisplayName { get; set; }
        public required string PropertyPath { get; init; }
        public required string TypeName { get; init; }
        public string? DefaultFormat { get; set; }
    }
}
