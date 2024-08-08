using MeshWeaver.Pivot.Grouping;
using MeshWeaver.Pivot.Models;

namespace MeshWeaver.Charting.Pivot;

public static class PivotChartModelExtensions
{
    public static PivotChartModel WithLabelsFromLevels(
        this PivotChartModel model,
        params int[] levels
    )
    {
        model = model with
        {
            ColumnDescriptors = model
                .ColumnDescriptors.Select(d =>
                    d with
                    {
                        DisplayName = String.Join(
                            ".",
                            levels.Select(i => d.Coordinates[i].DisplayName)
                        )
                    }
                )
                .ToList()
        };
        return model;
    }

    public static PivotChartModel WithLabels(this PivotChartModel model, params string[] labels)
    {
        model = model with
        {
            ColumnDescriptors = model
                .ColumnDescriptors.Select((d, i) => d with { DisplayName = labels[i] })
                .ToList()
        };
        return model;
    }

    public static PivotChartModel WithLegendItems(
        this PivotChartModel model,
        params string[] legendItems
    )
    {
        model = model with
        {
            Rows = model
                .Rows.Select(
                    (row, i) =>
                        row with
                        {
                            Descriptor = row.Descriptor with { DisplayName = legendItems[i] }
                        }
                )
                .ToList()
        };
        return model;
    }

    public static PivotChartModel WithLegendItemsFromLevels(
        this PivotChartModel model,
        string separator,
        params int[] levels
    )
    {
        model = model with
        {
            Rows = model
                .Rows.Select(
                    (row, i) =>
                    {
                        return row with
                        {
                            Descriptor = row.Descriptor with
                            {
                                DisplayName = String.Join(
                                    separator,
                                    levels.Select(lev =>
                                        row.Descriptor.Coordinates[lev].DisplayName
                                    )
                                )
                            }
                        };
                    }
                )
                .ToList()
        };
        return model;
    }

    public static bool IsTotalForSlice(this PivotElementDescriptor descriptor, object grouperName)
    {
        return descriptor.Coordinates.Any(c =>
            c.GrouperName.Equals(grouperName)
            && c.Id == IPivotGrouper<object, ColumnGroup>.TotalGroup.Id
        );
    }

    public static bool IsLevel(this PivotElementDescriptor descriptor, int level)
    {
        return descriptor.Coordinates.Count == level;
    }

    public static PivotChartModel OrderByValue(this PivotChartModel model, Func<PivotChartRow, bool> dataSetSelector = null) 
        => OrderByValue(model, dataSetSelector, false);

    public static PivotChartModel OrderByValueDescending(this PivotChartModel model, Func<PivotChartRow, bool> dataSetSelector = null) 
        => OrderByValue(model, dataSetSelector, true);

    private static PivotChartModel OrderByValue(
        PivotChartModel model,
        Func<PivotChartRow, bool> dataSetSelector,
        bool descending)
    {
        var dataSet = dataSetSelector != null ? model.Rows.FirstOrDefault(dataSetSelector)
            : model.Rows.FirstOrDefault();

        if (dataSet == null)
        {
            return model;
        }

        var keySelector = new Func<PivotElementDescriptor, double>(d =>
        {
            var value = dataSet.DataByColumns
                .FirstOrDefault(c => c.ColSystemName.Equals(d.Id)).Value;
            return value ?? 0;
        });

        var reorderedColumns = descending
            ? model.ColumnDescriptors.OrderByDescending(keySelector)
            : model.ColumnDescriptors.OrderBy(keySelector);

        return model with
        {
            ColumnDescriptors = reorderedColumns.ToList()
        };
    }

    public static PivotChartModel TopValues(this PivotChartModel model, int n)
    {
        return model with
        {
            ColumnDescriptors = model.ColumnDescriptors
                .Take(n)
                .ToList()
        };
    }
}
