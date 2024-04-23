using OpenSmc.Pivot.Grouping;
using OpenSmc.Pivot.Models;

namespace OpenSmc.Charting.Pivot;

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

    public static bool IsTotalForSlice(this PivotElementDescriptor descriptor, string grouperName)
    {
        return descriptor.Coordinates.Any(c =>
            c.GrouperName.Equals(grouperName)
            && c.Id == IPivotGrouper<object, ColumnGroup>.TotalGroup.SystemName
        );
    }

    public static bool IsLevel(this PivotElementDescriptor descriptor, int level)
    {
        return descriptor.Coordinates.Count == level;
    }
}
